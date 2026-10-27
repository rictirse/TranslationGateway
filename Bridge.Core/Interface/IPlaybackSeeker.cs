namespace Translation.Bridge.Core.Interface;

/// <summary>讓播放器跳到指定位置。</summary>
public interface IPlaybackSeeker
{
    /// <summary>跳到 positionMs；找不到播放器或送訊息失敗回 false，不擲例外。</summary>
    bool SeekTo(long positionMs);
}
