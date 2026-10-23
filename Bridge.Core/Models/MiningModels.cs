namespace Translation.Bridge.Core.Models;

/// <summary>名詞候選（TermCandidates 表的一列）：資料探勘拆出來、但沒有建卡的專有名詞。</summary>
public sealed record TermCandidate(string Surface, string Kind, int Importance, string Origin, long? StreamId, string? Context);

/// <summary>AI 從情境文字拆出的一個專有名詞。</summary>
public sealed record MinedNoun(string Surface, string Kind, int Importance);

/// <summary>資料探勘對單一名詞的處理結果。</summary>
public enum MiningOutcome
{
    /// <summary>比對到既有詞彙卡（第 2 關：抹平寫法後名稱相同）。</summary>
    Matched,

    /// <summary>建立了新詞彙卡。</summary>
    Created,

    /// <summary>併入既有詞彙卡（第 1 關 Wikidata 編號相同，或 GLM 判斷 existing，加了別名）。</summary>
    AddedAlias,

    /// <summary>存進名詞候選（重要性 5 名以後、非三種建卡名詞、查不到或失敗）。</summary>
    Candidate,

    /// <summary>三個知識來源都查不到資料。</summary>
    NotFound,

    /// <summary>比對到已拒絕的卡，略過（拒絕的卡當黑名單）。</summary>
    Rejected,

    /// <summary>處理失敗（例如 AI 回覆無法解析）。</summary>
    Failed,
}

/// <summary>資料探勘對單一名詞的結果：Outcome 與相關卡片資訊。</summary>
public sealed record MiningItem(string Surface, MiningOutcome Outcome, long? CardId, string? CardName, string? Detail);

/// <summary>一次資料探勘的整體結果：每個名詞的處理結果、token 用量與耗時；Error 非 null 表示流程沒有跑完（例如 AI 回覆無法解析）。</summary>
public sealed record MiningResult(IReadOnlyList<MiningItem> Items, int TotalTokens, long ElapsedMs, string? Error)
{
    /// <summary>要設為生效的詞彙卡 Id：Matched、Created、AddedAlias 的 CardId，去重。</summary>
    public IReadOnlyList<long> CardIdsToActivate =>
        Items
            .Where(item => item.Outcome is MiningOutcome.Matched or MiningOutcome.Created or MiningOutcome.AddedAlias
                && item.CardId is not null)
            .Select(item => item.CardId!.Value)
            .Distinct()
            .ToList();
}
