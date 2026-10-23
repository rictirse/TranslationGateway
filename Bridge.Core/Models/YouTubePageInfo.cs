namespace Translation.Bridge.Core.Models;

/// <summary>從 YouTube 影片頁面的 ytInitialPlayerResponse 讀到的資訊（查不到的欄位為 null／空清單）。
/// AutoCaptionLanguage 是第一個生成式字幕軌（kind == "asr"）的語言（生成式字幕的語言就是語音的語言）。</summary>
public sealed record YouTubePageInfo(
    string? VideoId, string? Title, string? Author, string? ChannelId, string? ChannelUrl,
    string? Category, string? Description, IReadOnlyList<string> Keywords,
    bool IsLiveNow, bool IsLiveContent, bool IsUpcoming,
    DateTimeOffset? StartTime, DateTimeOffset? EndTime, int? LengthSeconds,
    IReadOnlyList<string> CaptionLanguages, string? VisitorData = null, string? AutoCaptionLanguage = null);

/// <summary>讀影片頁的結果：<see cref="BlockedReason"/> 不是 null 就是被 YouTube 擋下（匿名存取次數太多）。</summary>
public sealed record YouTubePageCheck(YouTubePageInfo? Info, string? BlockedReason);
