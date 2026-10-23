namespace Translation.Bridge.Core.Models;

/// <summary>聽錯字修正的種類。</summary>
public static class MishearingKind
{
    /// <summary>前處理：修正 Whisper 原文，再送去翻譯。</summary>
    public const string Pre = "Pre";

    /// <summary>後處理：修正譯文（之後才會有）。</summary>
    public const string Post = "Post";
}

/// <summary>聽錯字修正的狀態（駁回直接刪除，沒有 Rejected）。</summary>
public static class MishearingStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
}

/// <summary>聽錯字修正（MishearingFixes 表的一列）：Whisper 把詞彙卡名詞聽錯的寫法 → 正確寫法。</summary>
public sealed class MishearingFix
{
    public long Id { get; set; }
    public long CardId { get; set; }
    public long TermId { get; set; }
    public string Kind { get; set; } = MishearingKind.Pre;
    public string Wrong { get; set; } = string.Empty;
    public string Right { get; set; } = string.Empty;
    public string Status { get; set; } = MishearingStatus.Pending;
    public string? Evidence { get; set; }
    public int Occurrences { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>字幕探勘提出的一筆修正（還沒存進資料庫）。</summary>
public sealed record MishearingProposal(long CardId, long TermId, string Kind, string Wrong, string Right, string Evidence);

/// <summary>聽錯字探勘要給 GLM 的一個名詞。</summary>
public sealed record MishearingTermInput(long TermId, long CardId, string CardName, string Source, string Target, string? Aliases);

/// <summary>一次聽錯字探勘的結果：Error 非 null 表示流程沒有跑完（例如 AI 回覆無法解析）。</summary>
public sealed record MishearingMiningResult(int Proposed, int Inserted, int Accumulated, int Dropped, int TotalTokens, long ElapsedMs, string? Error);
