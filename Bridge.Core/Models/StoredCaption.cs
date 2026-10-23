namespace Translation.Bridge.Core.Models;

/// <summary>資料庫快取的 YouTube 生成式字幕（youtube_captions 一列）。</summary>
/// <param name="Id">資料庫 Id。</param>
/// <param name="VideoId">YouTube 影片 ID。</param>
/// <param name="Language">字幕語言代碼（例如 ja）。</param>
/// <param name="IsAuto">是否為 YouTube 自動產生（asr）。</param>
/// <param name="ClientName">下載時使用的用戶端身分（VISIONOS、IOS）。</param>
/// <param name="Cues">字幕段落（依 StartMs 排序）。</param>
/// <param name="CreatedAt">寫入時間。</param>
/// <param name="LastUsedAt">最後一次從快取載入的時間。</param>
public sealed record StoredCaption(long Id, string VideoId, string Language, bool IsAuto, string? ClientName,
    IReadOnlyList<CaptionCue> Cues, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt);
