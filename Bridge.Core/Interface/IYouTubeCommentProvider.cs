using System.Runtime.CompilerServices;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

public interface IYouTubeCommentProvider
{
    /// <summary>取得熱門留言第一頁（依 YouTube 的「人気順」，順序照 API 回傳）。
    /// 影片關閉留言或沒有留言時回傳空清單；網路錯誤、逾時、格式不符時回傳 null，不擲例外。</summary>
    Task<IReadOnlyList<YouTubeComment>?> GetTopCommentsAsync(string videoId, CancellationToken cancellationToken = default);

    /// <summary>依序取得熱門留言的每一頁（第一頁同 GetTopCommentsAsync），最多 maxPages 頁；某頁失敗就停止（已取得的頁照常回傳）。</summary>
    async IAsyncEnumerable<IReadOnlyList<YouTubeComment>> GetCommentPagesAsync(string videoId, int maxPages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var first = await GetTopCommentsAsync(videoId, cancellationToken);
        if (first is not null)
        {
            yield return first;
        }
    }
}
