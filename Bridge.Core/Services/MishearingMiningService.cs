using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 聽錯字探勘流程（DI singleton）：情境＋生效中詞彙卡的全部名詞＋最近字幕 → GLM 找出 Whisper 聽錯的寫法
/// → 逐筆驗證（termId 存在、wrong 長度、不是任何輸入名詞的正確寫法、evidence 真的出現在字幕中、wrong 出現在 evidence 裡）
/// → 通過的以 <see cref="MishearingStatus.Pending"/> 存進 MishearingFixes，等使用者到審核頁核准。
/// 每一步都記 Information log，不存其他資料表。
/// </summary>
public sealed class MishearingMiningService(
    ILorebookLlm llm,
    IGlossaryStore glossaryStore,
    ILogger<MishearingMiningService> logger)
{
    /// <summary>送給 GLM 的字幕句數上限（保留最新）。</summary>
    private const int MaxLines = 80;

    /// <summary>送給 GLM 的字幕總長上限（字），超過時從最舊的開始丟。</summary>
    private const int MaxTotalLineChars = 4000;

    /// <summary>送給 GLM 的名詞數上限（保留前面的）。</summary>
    private const int MaxTerms = 150;

    /// <summary>證據句的上限（字），存進 MishearingFixes.Evidence。</summary>
    private const int EvidenceMaxLength = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // GLM 偶爾把數字回成字串（"termId": "5"）
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>執行一次聽錯字探勘：名詞或字幕是空的時候不呼叫 GLM，直接回傳全 0 的結果。</summary>
    public async Task<MishearingMiningResult> MineAsync(string? situation, IReadOnlyList<MishearingTermInput> terms,
        IReadOnlyList<string> recentLines, CancellationToken ct)
    {
        // 聽錯字探勘沒有人在等結果：以最低的 Background 優先度排隊
        using var _ = LlmPriorityScope.Enter(LlmPriority.Background);
        var stopwatch = Stopwatch.StartNew();
        var lines = PrepareLines(recentLines);
        var termList = terms.Take(MaxTerms).ToList();
        if (termList.Count == 0 || lines.Count == 0)
        {
            logger.LogInformation("聽錯字探勘略過：名詞或字幕是空的（名詞 {Terms} 個，字幕 {Lines} 句）", termList.Count, lines.Count);
            return new MishearingMiningResult(0, 0, 0, 0, 0, stopwatch.ElapsedMilliseconds, null);
        }

        logger.LogInformation("聽錯字探勘開始：名詞 {Terms} 個，字幕 {Lines} 句", termList.Count, lines.Count);
        LlmJsonResult reply;
        using (var purpose = LlmCallPurposeScope.Enter(LlmCallPurposes.Mishearing))
        {
            reply = await llm.CompleteJsonAsync(
                MishearingPrompts.System, MishearingPrompts.BuildUser(situation, termList, lines), ct).ConfigureAwait(false);
        }
        var totalTokens = reply.PromptTokens + reply.CompletionTokens;

        var response = ParseFixes(reply.Content);
        var fixes = response?.Fixes?.Where(f => f is not null).ToList() ?? [];
        logger.LogInformation("聽錯字探勘：GLM 回覆 {Count} 筆（{Tokens} tokens）", fixes.Count, totalTokens);
        if (response is null)
        {
            logger.LogWarning("聽錯字探勘：AI 回覆無法解析");
            return new MishearingMiningResult(0, 0, 0, 0, totalTokens, stopwatch.ElapsedMilliseconds, "AI 回覆無法解析");
        }

        // 名詞的正確寫法（Source＋Alias）：wrong 等於任何一個都表示那是正確寫法，不是錯字
        var termById = termList.ToDictionary(t => t.TermId);
        var ownNames = termList.ToDictionary(t => t.TermId, t => new HashSet<string>(
            new[] { t.Source.Trim() }.Concat(MishearingPrompts.AliasList(t.Aliases)), StringComparer.Ordinal));
        var allNames = new HashSet<string>(ownNames.Values.SelectMany(names => names), StringComparer.Ordinal);

        var proposals = new List<MishearingProposal>();
        var seen = new HashSet<(long TermId, string Wrong)>();
        var dropped = 0;
        foreach (var fix in fixes)
        {
            var wrong = (fix.Wrong ?? string.Empty).Trim();
            var evidence = (fix.Evidence ?? string.Empty).Trim();
            if (fix.TermId is not { } termId || !termById.TryGetValue(termId, out var term))
            {
                dropped++;
                logger.LogInformation("聽錯字探勘：丟棄一筆（termId {TermId} 不在名詞清單中）", fix.TermId);
                continue;
            }
            if (wrong.Length < 2)
            {
                dropped++;
                logger.LogInformation("聽錯字探勘：丟棄一筆（「{Wrong}」少於 2 個字，名詞 {TermId}）", wrong, termId);
                continue;
            }
            if (ownNames[termId].Contains(wrong))
            {
                dropped++;
                logger.LogInformation("聽錯字探勘：丟棄一筆（「{Wrong}」等於名詞 {TermId} 的正確寫法或別名）", wrong, termId);
                continue;
            }
            if (allNames.Contains(wrong))
            {
                dropped++;
                logger.LogInformation("聽錯字探勘：丟棄一筆（「{Wrong}」等於其他名詞的正確寫法或別名）", wrong);
                continue;
            }
            var line = evidence.Length == 0
                ? null
                : lines.FirstOrDefault(l => l.Contains(evidence, StringComparison.Ordinal));
            if (line is null)
            {
                dropped++;
                logger.LogInformation("聽錯字探勘：丟棄一筆（證據「{Evidence}」不在最近的字幕中，名詞 {TermId}）", evidence, termId);
                continue;
            }
            // 正確寫法包含錯字時（例如錯字「クリ」、正確「ツクリ」），證據裡的錯字只出現在正確寫法裡面的話不算
            if (!ContainsOutsideNames(evidence, wrong, ownNames[termId]))
            {
                dropped++;
                logger.LogInformation("聽錯字探勘：丟棄一筆（「{Wrong}」沒有出現在證據「{Evidence}」中）", wrong, evidence);
                continue;
            }
            if (!seen.Add((termId, wrong)))
            {
                dropped++;
                logger.LogInformation("聽錯字探勘：丟棄一筆（「{Wrong}」重複提出，名詞 {TermId}）", wrong, termId);
                continue;
            }
            proposals.Add(new MishearingProposal(
                term.CardId, term.TermId, MishearingKind.Pre, wrong, term.Source,
                line.Length <= EvidenceMaxLength ? line : line[..EvidenceMaxLength]));
        }

        var inserted = glossaryStore.UpsertMishearingFixes(proposals);
        stopwatch.Stop();
        logger.LogInformation(
            "聽錯字探勘完成：提出 {Proposed}，新增 {Inserted}，累加 {Accumulated}，丟棄 {Dropped}（{Tokens} tokens，{ElapsedMs} ms）",
            fixes.Count, inserted, proposals.Count - inserted, dropped, totalTokens, stopwatch.ElapsedMilliseconds);
        return new MishearingMiningResult(fixes.Count, inserted, proposals.Count - inserted, dropped, totalTokens,
            stopwatch.ElapsedMilliseconds, null);
    }

    /// <summary>整理要送出的字幕：Trim、丟掉空字串、去除重複（保留第一次出現的順序），
    /// 最多保留最後 <see cref="MaxLines"/> 句，總長超過 <see cref="MaxTotalLineChars"/> 字時從最舊的開始丟。</summary>
    internal static List<string> PrepareLines(IReadOnlyList<string> recentLines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>();
        foreach (var raw in recentLines)
        {
            var line = (raw ?? string.Empty).Trim();
            if (line.Length > 0 && seen.Add(line))
            {
                unique.Add(line);
            }
        }

        var capped = unique.Count > MaxLines ? unique[^MaxLines..] : unique;

        // 從最新的往回收集，總長超過上限就停；至少保留最新的一句
        var kept = new List<string>();
        var total = 0;
        for (var i = capped.Count - 1; i >= 0; i--)
        {
            if (kept.Count > 0 && total + capped[i].Length > MaxTotalLineChars)
            {
                break;
            }
            total += capped[i].Length;
            kept.Add(capped[i]);
        }
        kept.Reverse();
        return kept;
    }

    /// <summary>wrong 是否在 text 中出現在「不被任何一個正確寫法完整包住」的位置：
    /// 錯字「クリ」只出現在「ツクリ」裡面時為 false；錯字「ツクリー」比正確寫法「ツクリ」長，不算被包住。</summary>
    public static bool ContainsOutsideNames(string text, string wrong, IEnumerable<string> names)
    {
        var nameList = names.Where(n => n.Length > wrong.Length).ToList();
        for (var i = text.IndexOf(wrong, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(wrong, i + 1, StringComparison.Ordinal))
        {
            var covered = false;
            foreach (var name in nameList)
            {
                // 正確寫法的出現位置 j 要滿足 j <= i 且 i + wrong.Length <= j + name.Length
                for (var j = Math.Max(0, i + wrong.Length - name.Length); j <= i && !covered; j++)
                {
                    covered = j + name.Length <= text.Length &&
                              string.CompareOrdinal(text, j, name, 0, name.Length) == 0;
                }
                if (covered) break;
            }
            if (!covered) return true;
        }
        return false;
    }

    /// <summary>從模型輸出中切出 JSON 物件本體（容忍 markdown 圍欄與前後文字）。</summary>
    private static string? ExtractJsonObject(string content)
    {
        var text = content.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static FixesResponse? ParseFixes(string content)
    {
        var json = ExtractJsonObject(content);
        if (json is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<FixesResponse>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class FixesResponse
    {
        public List<FixDto>? Fixes { get; set; }
    }

    private sealed class FixDto
    {
        public long? TermId { get; set; }

        public string? Wrong { get; set; }

        public string? Evidence { get; set; }
    }
}
