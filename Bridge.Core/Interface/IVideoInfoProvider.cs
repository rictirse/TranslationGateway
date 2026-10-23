using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

/// <summary>查影片的標題與頻道（目前只有 YouTube oEmbed）。</summary>
public interface IVideoInfoProvider
{
    /// <summary>查不到、逾時或網路錯誤時回傳 null，不擲例外。</summary>
    Task<StreamMetadata?> GetYouTubeInfoAsync(string url, CancellationToken cancellationToken = default);
}
