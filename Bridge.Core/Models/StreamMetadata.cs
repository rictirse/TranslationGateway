namespace Translation.Bridge.Core.Models;

public enum StreamPlatform
{
    YouTube,
    Twitch
}

/// <summary>從 streamlink --json 查到的直播中繼資料（查不到的欄位為 null）。</summary>
/// <param name="ChannelUrl">頻道網址（oEmbed 的 author_url，例如 https://www.youtube.com/@lisahanabusa）。</param>
/// <param name="IsVod">錄影（不經 streamlink，由 PotPlayer 直接開網址）。</param>
/// <param name="Description">說明欄文字（YouTube 影片頁面的 shortDescription，開台資料探勘用）。</param>
/// <param name="StartTime">YouTube 頁面上的直播開始時間。</param>
/// <param name="EndTime">YouTube 頁面上的直播結束時間；直播進行中或非直播存檔時為 null。</param>
public sealed record StreamMetadata(StreamPlatform Platform, string? Id, string? Author, string? Category, string? Title,
    string? ChannelUrl = null, bool IsVod = false, string? Description = null,
    DateTimeOffset? StartTime = null, DateTimeOffset? EndTime = null);

/// <summary>開啟直播的結果：Success 附上使用者可讀的訊息，成功時 Metadata 可能仍為 null（查不到中繼資料）。</summary>
/// <param name="Blocked">被 YouTube 擋下（匿名存取次數太多）：沒有跑 streamlink、沒有開播放器。</param>
/// <param name="BlockedReason">被擋的判斷依據（給使用者看的短句）；沒有被擋時為 null。</param>
public sealed record StreamLaunchResult(bool Success, string Message, StreamMetadata? Metadata,
    bool Blocked = false, string? BlockedReason = null);
