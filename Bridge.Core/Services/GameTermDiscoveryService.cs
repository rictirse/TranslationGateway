using System.Text.Json;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>遊戲用語探勘（DI singleton）：直播中重複出現、跨主題不普遍且詞彙表沒有的片假名詞
/// → GLM 判斷是不是這款遊戲的專有名詞並給台灣譯名 → 判斷「是」的以 Pending 寫進生效遊戲卡的附屬用語卡
/// （Kind=Common、ParentId=遊戲卡；附屬用語卡不生效，名詞只走關鍵字詞彙提示）。</summary>
public sealed class GameTermDiscoveryService(
    ILorebookLlm llm,
    IGlossaryStore glossaryStore,
    ILogger<GameTermDiscoveryService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>執行一次遊戲用語探勘：候選詞是空的時候不呼叫模型，直接回傳全 0 的結果。
    /// 模型或資料庫失敗時不往外擲例外，回傳 Error = 例外訊息（取消例外照常往外擲）。</summary>
    public async Task<TermDiscoveryResult> DiscoverAsync(GlossaryCard game,
        IReadOnlyList<TermDiscoveryCandidate> candidates, CancellationToken ct)
    {
        // 遊戲用語探勘沒有人在等結果：以最低的 Background 優先度排隊
        using var _ = LlmPriorityScope.Enter(LlmPriority.Background);
        var totalTokens = 0;
        if (candidates.Count == 0)
        {
            logger.LogInformation("遊戲用語探勘略過：沒有候選詞");
            return new TermDiscoveryResult(0, 0, [], null, 0, null);
        }

        try
        {
            logger.LogInformation("遊戲用語探勘開始：遊戲「{Game}」，候選 {Count} 個", game.Name, candidates.Count);
            LlmJsonResult reply;
            using (var purpose = LlmCallPurposeScope.Enter(LlmCallPurposes.TermDiscovery))
            {
                reply = await llm.CompleteJsonAsync(
                    TermDiscoveryPrompts.System, TermDiscoveryPrompts.BuildUser(game, candidates), ct).ConfigureAwait(false);
            }
            totalTokens = reply.PromptTokens + reply.CompletionTokens;

            var response = ParseTerms(reply.Content);
            var items = response?.Terms?.Where(t => t is not null).ToList() ?? [];
            logger.LogInformation("遊戲用語探勘：GLM 回覆 {Count} 筆（{Tokens} tokens）", items.Count, totalTokens);
            if (response is null)
            {
                logger.LogWarning("遊戲用語探勘：AI 回覆無法解析");
                return new TermDiscoveryResult(candidates.Count, 0, [], null, totalTokens, "AI 回覆無法解析");
            }

            // 逐筆過濾：只留候選詞本身、判「是」而且有譯名的（同一個詞只取第一筆）
            var candidateWords = candidates.Select(c => c.Word).ToHashSet(StringComparer.Ordinal);
            var examplesByWord = candidates.ToDictionary(c => c.Word, c => c.Examples, StringComparer.Ordinal);
            var accepted = new List<(string Word, string Target)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var word = (item.Word ?? string.Empty).Trim();
                if (!candidateWords.Contains(word))
                {
                    logger.LogInformation("遊戲用語探勘：丟棄一筆（「{Word}」不在候選清單中）", word);
                    continue;
                }
                if (!IsTerm(item.IsTerm))
                {
                    logger.LogInformation("遊戲用語探勘：丟棄一筆（「{Word}」判為不是遊戲用語）", word);
                    continue;
                }
                var target = (item.Target ?? string.Empty).Trim();
                if (target.Length == 0)
                {
                    logger.LogInformation("遊戲用語探勘：丟棄一筆（「{Word}」沒有譯名）", word);
                    continue;
                }
                if (!seen.Add(word))
                {
                    logger.LogInformation("遊戲用語探勘：丟棄一筆（「{Word}」重複判斷，只留第一筆）", word);
                    continue;
                }
                accepted.Add((word, target));
            }

            return WriteToSupplementCard(game, accepted, examplesByWord, candidates.Count, totalTokens);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // LorebookLlmException 與 store 的例外都算流程失敗：記 Warning、回傳 Error，不往外擲
            logger.LogWarning(ex, "遊戲用語探勘失敗：{Message}", ex.Message);
            return new TermDiscoveryResult(candidates.Count, 0, [], null, totalTokens, ex.Message);
        }
    }

    /// <summary>找或建附屬用語卡（Kind=Common、ParentId=遊戲卡、Name=「{遊戲名} 用語」）並寫入過濾後的名詞；
    /// 卡已被拒絕時不寫入；過濾後沒有名詞時不建卡。回傳結果的 Accepted 是過濾後的筆數（不含因重複而跳過的）。</summary>
    private TermDiscoveryResult WriteToSupplementCard(GlossaryCard game,
        List<(string Word, string Target)> accepted, Dictionary<string, IReadOnlyList<string>> examplesByWord,
        int judged, int totalTokens)
    {
        var supplementName = $"{game.Name} 用語";
        var supplement = glossaryStore.GetAllGlossaryCards().FirstOrDefault(card =>
            card.Kind == GlossaryKind.Common && card.ParentId == game.Id && card.Name == supplementName);
        if (supplement is { Status: "Rejected" })
        {
            logger.LogWarning("遊戲用語探勘：附屬用語卡「{Name}」已被拒絕，不寫入", supplementName);
            return new TermDiscoveryResult(judged, accepted.Count, [], supplement.Id, totalTokens, "附屬用語卡已被拒絕");
        }

        if (supplement is null)
        {
            if (accepted.Count == 0)
            {
                logger.LogInformation("遊戲用語探勘沒有要寫入的名詞，不建附屬用語卡");
                return new TermDiscoveryResult(judged, 0, [], null, totalTokens, null);
            }

            var cardId = glossaryStore.CreateGlossaryCard(
                new GlossaryCard
                {
                    Kind = GlossaryKind.Common,
                    Name = supplementName,
                    ParentId = game.Id,
                    Status = "Pending",
                    Description = "遊戲用語探勘自動建立",
                },
                accepted.Select(ToTerm).ToList());
            logger.LogInformation("遊戲用語探勘完成：建立附屬用語卡 {CardId}「{Name}」，寫入 {Words}",
                cardId, supplementName, string.Join("、", accepted.Select(a => a.Word)));
            return new TermDiscoveryResult(judged, accepted.Count,
                accepted.Select(a => a.Word).ToList(), cardId, totalTokens, null);
        }

        // 既有卡：已有相同 Source 的詞（任何狀態，含被拒絕的）跳過，不重複提議
        var existingSources = glossaryStore.GetAllGlossaryTerms()
            .Where(term => term.CardId == supplement.Id)
            .Select(term => term.Source)
            .ToHashSet(StringComparer.Ordinal);
        var inserted = accepted
            .Where(a => !existingSources.Contains(a.Word))
            .Select(a =>
            {
                var term = ToTerm(a);
                term.CardId = supplement.Id;
                return term;
            })
            .ToList();
        foreach (var skipped in accepted.Where(a => existingSources.Contains(a.Word)))
        {
            logger.LogInformation("遊戲用語探勘：「{Word}」已在附屬用語卡中，跳過", skipped.Word);
        }

        if (inserted.Count > 0)
        {
            glossaryStore.SaveGlossaryTermEdits([], inserted);
        }
        logger.LogInformation("遊戲用語探勘完成：附屬用語卡 {CardId}「{Name}」新增 {Words}",
            supplement.Id, supplementName, string.Join("、", inserted.Select(t => t.Source)));
        return new TermDiscoveryResult(judged, accepted.Count,
            inserted.Select(t => t.Source).ToList(), supplement.Id, totalTokens, null);

        GlossaryTerm ToTerm((string Word, string Target) accepted) => new()
        {
            Source = accepted.Word,
            Target = accepted.Target,
            Category = "遊戲用語",
            Note = $"遊戲用語探勘：{FirstExample(accepted.Word)}",
            Status = "Pending",
        };

        string FirstExample(string word) =>
            examplesByWord.GetValueOrDefault(word) is { Count: > 0 } examples ? examples[0] : string.Empty;
    }

    /// <summary>isTerm 也接受字串 "true"（模型偶爾把布林回成字串）。</summary>
    private static bool IsTerm(JsonElement? value)
    {
        if (value is not { } element)
        {
            return false;
        }
        if (element.ValueKind == JsonValueKind.True)
        {
            return true;
        }
        return element.ValueKind == JsonValueKind.String &&
            bool.TryParse(element.GetString(), out var result) && result;
    }

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
        public string? Word { get; set; }

        public JsonElement? IsTerm { get; set; }

        public string? Target { get; set; }
    }
}
