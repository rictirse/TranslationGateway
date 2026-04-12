using CommunityToolkit.Mvvm.ComponentModel;

namespace TranslationGateway.Models;

public partial class TranslationJob : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    // 內容相關
    public string UserPrompt { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;

    // Token 統計
    public int UserTokens { get; set; }
    public int SystemTokens { get; set; }
    public int TotalTokens => UserTokens + SystemTokens;

    // 耗時統計 (ms)
    [ObservableProperty] private long _totalLatency;  // 總耗時 (I)
    [ObservableProperty] private long _modelLatency;  // 模型耗時 (II)
    [ObservableProperty] private long _processLatency;// 後處理耗時 (III)

    public DateTime Timestamp { get; set; } = DateTime.Now;

    // 在 TranslationService 中加入計算方法
    private int CalculateTokens(string text)
    {
        try
        {
            var tokenizer = Array.Empty<int>(); // 這裡之後根據模型選擇 Tokenizer
            return text.Length; // 暫時用字數代替，稍後串接 TokenizerLib
        }
        catch { return 0; }
    }
}