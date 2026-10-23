namespace Translation.Bridge.Core.Models;

/// <summary>錄影（有生成式字幕的 YouTube 影片）的處理設定。</summary>
public class VodSettings
{
    /// <summary>預先翻譯每批送幾句字幕（第一批用一半，最少 1）。</summary>
    public int PrefetchBatchSize { get; set; } = 20;

    /// <summary>播放位置之後已翻好的字幕剩不到幾秒時，補翻下一批。</summary>
    public int PrefetchLowWaterSeconds { get; set; } = 60;

    /// <summary>預先翻譯單次請求的逾時（秒），比即時翻譯寬鬆。</summary>
    public int PrefetchTimeoutSeconds { get; set; } = 30;

    /// <summary>開片時最多等資料探勘幾秒，才開始處理字幕（探勘完成或失敗就不再等）。</summary>
    public int MiningWaitSeconds { get; set; } = 180;

    /// <summary>插隊門檻（%）：正在執行的低優先工作，預估進度達到這個比例就讓它跑完，不到就取消改跑高優先工作。
    /// 只用在知識庫模型；翻譯模型不插隊。</summary>
    public int PreemptThresholdPercent { get; set; } = 80;
}
