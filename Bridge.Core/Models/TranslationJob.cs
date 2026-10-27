using CommunityToolkit.Mvvm.ComponentModel;

namespace Translation.Bridge.Core.Models;

public partial class TranslationJob : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>
    /// 上下文
    /// </summary>
    public string Context { get; set; } = string.Empty;
    /// <summary>
    /// 當前句子
    /// </summary>
    [ObservableProperty] private string _current = string.Empty;
    /// <summary>
    /// 翻譯後的句子
    /// </summary>
    [ObservableProperty] private string _translatedText = string.Empty;
    /// <summary>
    /// 快取索引（PotPlayer 送來的原始原文）；修正原文後 <see cref="Current"/> 會改變，但此值不變
    /// </summary>
    public string CacheKey { get; set; } = string.Empty;

    /// <summary>Whisper 原始文字（原文修正之前）；沒有原文修正時與 Current 相同。</summary>
    public string RawSource { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    /// <summary>
    /// 譯文來源（模型、快取、逾時、直通）
    /// </summary>
    public TranslationSource Source { get; set; } = TranslationSource.Model;

    /// <summary>此筆取代的前一筆 Job 的 Id；不是修正版時為 null。</summary>
    public string? RevisionOf { get; set; }

    /// <summary>錄影時這一列是 PotPlayer 目前播放到的那一句（字幕監看高亮用，不寫資料庫）。</summary>
    [ObservableProperty] private bool _isPlaying;

    /// <summary>命中翻譯快取時，該筆 TranslationCache 的 Id；其他情況為 null。</summary>
    public long? CacheId { get; set; }

    public int UserTokens
    {
        get
        {
            return userTokens ??= EstimateTokens(UserPrompt);
        }
    }

    private int? userTokens = null;

    public int SystemTokens
    {
        get
        {
            return systemTokens ??= EstimateTokens(SystemPrompt);
        }
    }
    private int? systemTokens = null;
    public int TotalTokens => UserTokens + SystemTokens;

    // 耗時統計 (ms)
    [ObservableProperty] private long _totalLatency;  // 總耗時 (I)
    [ObservableProperty] private long _modelLatency;  // 模型耗時 (II)
    [ObservableProperty] private long _processLatency;// 後處理耗時 (III)

    private int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int cjkCount = text.Count(c => c > 127);
        int asciiCount = text.Length - cjkCount;

        return (int)(cjkCount * 1.5) + (int)Math.Ceiling(asciiCount / 4.0);
    }

    public DateTime Timestamp { get; set; } = DateTime.Now;
}