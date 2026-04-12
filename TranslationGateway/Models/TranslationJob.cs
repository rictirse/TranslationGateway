using CommunityToolkit.Mvvm.ComponentModel;

namespace TranslationGateway.Models;

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
    public string Current { get; set; } = string.Empty;
    /// <summary>
    /// 翻譯後的句子
    /// </summary>
    public string TranslatedText { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;

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