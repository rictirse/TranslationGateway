namespace Translation.Bridge.Core.Interface;

/// <summary>播放器的播放位置來源（翻譯時對照同時段的 YouTube 字幕用）。</summary>
public interface IPlaybackClock
{
    /// <summary>目前播放位置（毫秒）；播放器沒開、沒回應或回傳值 ≤ 0 時回傳 null，不擲例外。</summary>
    long? GetPositionMs();
}
