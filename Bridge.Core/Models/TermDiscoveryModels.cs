namespace Translation.Bridge.Core.Models;

/// <summary>跨主題普遍度計算用的一句字幕：所屬直播、那場直播生效過的遊戲卡（Id 最小的一張，沒有為 null）、原文。</summary>
public sealed record TopicSubtitleSource(long StreamId, long? GameCardId, string Source);

/// <summary>待判斷的候選詞：詞、本場出現的句數、例句（最多 2 句）。</summary>
public sealed record TermDiscoveryCandidate(string Word, int Count, IReadOnlyList<string> Examples);

/// <summary>一次遊戲用語探勘的結果。Error 非 null 表示流程沒有跑完（例如 AI 回覆無法解析）。</summary>
public sealed record TermDiscoveryResult(int Judged, int Accepted, IReadOnlyList<string> InsertedWords,
    long? SupplementCardId, int TotalTokens, string? Error);
