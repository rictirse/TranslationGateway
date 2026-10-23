using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>貼上資料整理（DI singleton）：把使用者貼上的網路資料切段 → 逐段依序請 GLM 整理成名詞提案
/// → 驗證與正規化。提案只回傳給呼叫端加進畫面待審核，存檔前完全不動資料庫。</summary>
public sealed class TermImportService(ILorebookLlm llm, ILogger<TermImportService> logger)
{
    /// <summary>貼上內容上限（字），超過時擲 <see cref="ArgumentException"/>（由 UI 先擋）。</summary>
    public const int MaxInputLength = 30_000;

    /// <summary>每段上限（字）。</summary>
    public const int ChunkLength = 4_000;

    /// <summary>每段最多行數：一行一個詞的密集對照表，GLM 輸出量跟行數成正比，限制行數才不會逾時。</summary>
    public const int MaxLinesPerChunk = 30;

    /// <summary>user 訊息裡「已有名詞」最多列出的數量。</summary>
    private const int MaxExistingSourcesInPrompt = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>整理貼上的資料：逐段呼叫 GLM、驗證正規化後回傳提案；某段失敗只記錄該段錯誤，其他段照常。</summary>
    public async Task<TermImportResult> ImportAsync(string cardName, string cardKind, string? instruction,
        IReadOnlyList<string> existingSources, string pastedText, IProgress<string>? progress, CancellationToken ct)
    {
        // 貼上整理是使用者在等的工作：以 Mining 優先度排進知識庫模型的隊伍
        using var _ = LlmPriorityScope.Enter(LlmPriority.Mining);
        var text = pastedText ?? string.Empty;
        if (text.Length > MaxInputLength)
        {
            throw new ArgumentException($"貼上內容超過上限 {MaxInputLength} 字（目前 {text.Length} 字）", nameof(pastedText));
        }

        var stopwatch = Stopwatch.StartNew();
        var chunks = SplitChunks(text);
        logger.LogInformation("貼上整理開始：卡片「{Card}」，{Length} 字，{Chunks} 段", cardName, text.Length, chunks.Count);

        // 重複判斷用（OrdinalIgnoreCase）：卡片既有名詞＋本次已收的提案
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownList = new List<string>();
        foreach (var raw in existingSources)
        {
            var source = (raw ?? string.Empty).Trim();
            if (source.Length == 0 || !seenSources.Add(source))
            {
                continue;
            }

            knownList.Add(source);
        }

        var proposals = new List<TermImportProposal>();
        var errors = new List<string>();
        var duplicates = 0;
        var dropped = 0;
        var totalTokens = 0;

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunkIndex = i + 1;
            progress?.Report($"整理中：第 {chunkIndex}/{chunks.Count} 段…");
            try
            {
                LlmJsonResult reply;
                using (var purpose = LlmCallPurposeScope.Enter(LlmCallPurposes.TermImport))
                {
                    reply = await llm.CompleteJsonAsync(
                        TermImportPrompts.System,
                        TermImportPrompts.BuildUser(cardName, cardKind, instruction, knownList, chunks[i], chunkIndex, chunks.Count),
                        ct).ConfigureAwait(false);
                }
                var chunkTokens = reply.PromptTokens + reply.CompletionTokens;
                totalTokens += chunkTokens;

                var added = 0;
                var response = ParseTerms(reply.Content);
                if (response is null)
                {
                    errors.Add($"第 {chunkIndex} 段：AI 回覆無法解析");
                }
                else
                {
                    var terms = response.Terms?.Where(t => t is not null).ToList() ?? [];
                    foreach (var term in terms)
                    {
                        var proposal = Normalize(term, text, seenSources);
                        if (proposal.Error == NormalizeError.Duplicate)
                        {
                            duplicates++;
                        }
                        else if (proposal.Error == NormalizeError.Dropped)
                        {
                            dropped++;
                        }
                        else if (proposal.Proposal is { } item)
                        {
                            proposals.Add(item);
                            knownList.Add(item.Source);
                            added++;
                        }
                    }
                }

                logger.LogInformation("貼上整理第 {ChunkIndex}/{ChunkCount} 段：{Count} 個名詞（{Tokens} tokens）",
                    chunkIndex, chunks.Count, added, chunkTokens);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LorebookLlmException ex)
            {
                errors.Add($"第 {chunkIndex} 段：{ex.Message}");
            }
        }

        stopwatch.Stop();
        logger.LogInformation(
            "貼上整理完成：提案 {Proposals}（推測日文 {Guessed}），重複 {Duplicates}，丟棄 {Dropped}，錯誤 {Errors}",
            proposals.Count, proposals.Count(p => p.JapaneseGuessed), duplicates, dropped, errors.Count);
        return new TermImportResult(proposals, duplicates, dropped, chunks.Count, totalTokens,
            stopwatch.ElapsedMilliseconds, errors);
    }

    /// <summary>依行切段：盡量在換行處切，每段 ≤ <paramref name="chunkLength"/> 字且 ≤ <paramref name="maxLines"/> 行；
    /// 單行超過上限時硬切；空白行不單獨成段、不算行數。</summary>
    public static IReadOnlyList<string> SplitChunks(string text, int chunkLength = ChunkLength, int maxLines = MaxLinesPerChunk)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var lineCount = 0;

        void Flush()
        {
            var chunk = current.ToString().Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            current.Clear();
            lineCount = 0;
        }

        foreach (var rawLine in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length > chunkLength)
            {
                // 單行超過上限：先收掉累積中的段落，再硬切這一行
                Flush();
                for (var start = 0; start < line.Length; start += chunkLength)
                {
                    chunks.Add(line.Substring(start, Math.Min(chunkLength, line.Length - start)));
                }
                continue;
            }

            if (current.Length > 0 && (current.Length + 1 + line.Length > chunkLength || lineCount >= maxLines))
            {
                Flush();
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(line);
            lineCount++;
        }

        Flush();
        return chunks;
    }

    private enum NormalizeError
    {
        None,
        Dropped,
        Duplicate,
    }

    /// <summary>驗證與正規化一個名詞：修剪、丟棄無效項、重複計數、別名正規化與日文推測註記。</summary>
    private static (TermImportProposal? Proposal, NormalizeError Error) Normalize(
        TermDto term, string originalText, HashSet<string> seenSources)
    {
        var source = (term.Source ?? string.Empty).Trim();
        var target = (term.Target ?? string.Empty).Trim();
        if (source.Length == 0 || target.Length == 0 ||
            (source.Equals(target, StringComparison.OrdinalIgnoreCase) && IsAscii(source) && IsAscii(target)))
        {
            return (null, NormalizeError.Dropped);
        }

        if (seenSources.Contains(source))
        {
            return (null, NormalizeError.Duplicate);
        }

        seenSources.Add(source);

        var aliases = NormalizeAliases(term.Aliases, source);
        var category = NullIfEmpty((term.Category ?? string.Empty).Trim());
        var note = NullIfEmpty((term.Note ?? string.Empty).Trim());

        // 寬鬆比對（不分大小寫、全半形、平假名片假名互通）確認原始全文裡有沒有這個 Source；
        // 沒有表示日文是模型自己推測的，保留但加註提醒審核時特別看
        var guessed = !LooseContains(originalText, source);
        if (guessed)
        {
            note = note is null ? "日文由模型推測" : $"日文由模型推測；{note}";
        }

        return (new TermImportProposal(source, target, aliases, category, note, guessed), NormalizeError.None);
    }

    /// <summary>別名正規化：修剪、去空、去掉和 Source 相同的、不分大小寫去重，用換行接起來；空的回 null。</summary>
    private static string? NormalizeAliases(List<string?>? aliases, string source)
    {
        if (aliases is null || aliases.Count == 0)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var raw in aliases)
        {
            var alias = (raw ?? string.Empty).Trim();
            if (alias.Length == 0 ||
                alias.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(alias))
            {
                continue;
            }

            normalized.Add(alias);
        }

        return normalized.Count > 0 ? string.Join("\n", normalized) : null;
    }

    /// <summary>比對方式與 GlossaryCatalog 相同：不分大小寫、全形半形互通、平假名片假名互通。</summary>
    private static bool LooseContains(string text, string query) =>
        query.Length > 0 &&
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType) >= 0;

    private static bool IsAscii(string value)
    {
        foreach (var c in value)
        {
            if (c > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>從模型輸出中切出 JSON 物件本體（容忍 markdown 圍欄與前後文字），比照 MishearingMiningService 的寫法。</summary>
    private static string? ExtractJsonObject(string content)
    {
        var text = content.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static TermsResponse? ParseTerms(string content)
    {
        var json = ExtractJsonObject(content);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TermsResponse>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class TermsResponse
    {
        public List<TermDto>? Terms { get; set; }
    }

    private sealed class TermDto
    {
        public string? Source { get; set; }

        public string? Target { get; set; }

        public List<string?>? Aliases { get; set; }

        public string? Category { get; set; }

        public string? Note { get; set; }
    }
}
