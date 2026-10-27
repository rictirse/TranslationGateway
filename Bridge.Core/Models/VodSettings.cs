namespace Translation.Bridge.Core.Models;

/// <summary>錄影（有生成式字幕的 YouTube 影片）的處理設定。</summary>
public class VodSettings
{
    /// <summary>預先翻譯每批送幾句字幕（第一批用一半，最少 1）。</summary>
    public int PrefetchBatchSize { get; set; } = 20;

    /// <summary>急件批次（即時翻譯正在等結果）每批送幾句：少量才能在即時翻譯逾時內回來。0 以下視為 1。</summary>
    public int UrgentBatchSize { get; set; } = 3;

    /// <summary>播放位置之後已翻好的字幕剩不到幾秒時，補翻下一批。
    /// PotPlayer 會提前請求翻譯（實測約提前 110 秒）：實際門檻會自動拉高到
    /// 「觀察到的提前距離＋30 秒」（與設定值取較大者，上限 600 秒）。</summary>
    public int PrefetchLowWaterSeconds { get; set; } = 180;

    /// <summary>預先翻譯單次請求的逾時（秒），比即時翻譯寬鬆。</summary>
    public int PrefetchTimeoutSeconds { get; set; } = 30;

    /// <summary>開片時最多等資料探勘幾秒，才開始處理字幕（探勘完成或失敗就不再等）。</summary>
    public int MiningWaitSeconds { get; set; } = 180;

    /// <summary>插隊門檻（%）：正在執行的低優先工作，預估進度達到這個比例就讓它跑完，不到就取消改跑高優先工作。
    /// 只用在知識庫模型；翻譯模型不插隊。</summary>
    public int PreemptThresholdPercent { get; set; } = 80;

    /// <summary>錄影時在 PotPlayer 左上角顯示目前章節名稱（中文在上、日文在下）。</summary>
    public bool ShowChapterTitles { get; set; } = true;

    /// <summary>左上角章節名稱的字級（SRT 的 \fs）；1 以下視為 12。</summary>
    public int ChapterTitleFontSize { get; set; } = 12;

    /// <summary>查不到原曲長度時，左上角章節名稱顯示多久（秒）；仍不超過下一章開始。</summary>
    public int ChapterTitleFallbackSeconds { get; set; } = 300;
}
