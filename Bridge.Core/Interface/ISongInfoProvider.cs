using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

/// <summary>歌曲資訊來源（LRCLIB 等）。</summary>
public interface ISongInfoProvider
{
    /// <summary>查原曲長度（毫秒）；查不到、網路錯誤、逾時回 null，不擲例外（呼叫端取消除外）。</summary>
    Task<long?> GetDurationMsAsync(SongQuery query, CancellationToken cancellationToken = default);
}
