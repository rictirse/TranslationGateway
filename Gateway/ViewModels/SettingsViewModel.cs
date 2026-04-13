using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;

namespace Translation.Gateway.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        ISettingsService settingsService,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _logger = logger;

        ReloadSetting();
    }

    public void ReloadSetting()
    {
        var cur = _settingsService.Current;

        ApiHost = cur.ApiHost;
        Model = cur.Model;
        IsLocalModel = cur.UseLocalModel;
        LastSelectedControlName = cur.LastSelectedControlName;

        // 從 TranslationSettings 載入
        MaxParallelism = cur.TranslationSettings.MaxParallelism;
        BatchSize = cur.TranslationSettings.BatchSizeHardCap;
        Temperature = cur.TranslationSettings.Temperature;

        ContextWindow = cur.TranslationSettings.Context;
        MaxOutput = cur.TranslationSettings.MaxOutput;

        SystemPromptTemplate = cur.TranslationSettings.SystemPromptTemplate;
        if (string.IsNullOrWhiteSpace(SystemPromptTemplate))
        {
            var defaultTemplate = "你是一個專業的日文直播翻譯官，負責將日文語音轉寫的文字翻譯成繁體中文。\r\n\r\n語境理解：我會提供「完整語境(Context)」與「當前句子(Current)」。請參考語境來判斷主詞、性別與語氣，但僅需翻譯當前句子。\r\n\r\n口語優化：直播內容包含大量口語、口頭禪（如：草、w、あの、ええと），請將其轉化為自然流暢的社群語言或直接略過無意義贅詞。\r\n\r\n格式限制：直接輸出翻譯結果，不要包含任何解釋、拼音或原文。\r\n\r\n專有名詞：若遇到遊戲術語或 VTuber 相關梗，請保持原意或使用習慣譯名。";

            cur.TranslationSettings.SystemPromptTemplate = defaultTemplate;
            SystemPromptTemplate = defaultTemplate;
        }
    }
}