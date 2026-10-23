namespace Translation.Bridge.Core.Interface;

/// <summary>把字幕檔交給正在播放的 PotPlayer。</summary>
public interface IPotPlayerSubtitleLoader
{
    /// <summary>找到正在執行的 PotPlayer 就送出，回傳 true；找不到或失敗回傳 false，不擲例外。</summary>
    bool Load(string subtitlePath);
}
