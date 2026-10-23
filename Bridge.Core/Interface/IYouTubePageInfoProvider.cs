using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

/// <summary>讀 YouTube 影片頁面的 ytInitialPlayerResponse（不需要 API Key）。</summary>
public interface IYouTubePageInfoProvider
{
    /// <summary>查不到、逾時、網路錯誤或格式不符時回傳 null，不擲例外。</summary>
    Task<YouTubePageInfo?> GetPageInfoAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>同 <see cref="GetPageInfoAsync"/>，另外回報是否被 YouTube 擋下（匿名存取次數太多）。預設實作不偵測被擋。</summary>
    async Task<YouTubePageCheck> CheckPageAsync(string url, CancellationToken cancellationToken = default) =>
        new(await GetPageInfoAsync(url, cancellationToken), BlockedReason: null);
}
