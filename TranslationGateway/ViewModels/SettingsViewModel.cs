using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TranslationGateway.Interface;

namespace TranslationGateway.ViewModels;

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
        SystemInput = cur.SystemInput;
        IsThroughPass = cur.ThroughPass;

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

    [RelayCommand]
    private async Task SaveSettings()
    {
        var cur = _settingsService.Current;

        cur.ApiHost = ApiHost;
        cur.Model = Model;
        cur.UseLocalModel = IsLocalModel;
        cur.LastSelectedControlName = LastSelectedControlName;
        cur.SystemInput = SystemInput;
        cur.ThroughPass = IsThroughPass;

        // 更新翻譯子項目
        cur.TranslationSettings.MaxParallelism = MaxParallelism;
        cur.TranslationSettings.BatchSizeHardCap = BatchSize;
        cur.TranslationSettings.Temperature = Temperature;

        cur.TranslationSettings.Context = ContextWindow;
        cur.TranslationSettings.MaxOutput = MaxOutput;
        cur.TranslationSettings.SystemPromptTemplate = SystemPromptTemplate;

        await _settingsService.SaveAsync();
    }

    [RelayCommand]
    private async Task ApplyHardwarePresetAsync(string gpuModel)
    {
        if (gpuModel == "4090")
        {
            MaxParallelism = 4;
            BatchSize = 80;
            ContextWindow = 8192;
            MaxOutput = 2048;
            _logger.LogInformation("已套用 RTX 4090 最佳化設定");
        }
        else if (gpuModel == "5090")
        {
            MaxParallelism = 8;
            BatchSize = 100;
            ContextWindow = 16384;
            MaxOutput = 4096;
            _logger.LogInformation("已套用 RTX 5090 最佳化設定");
        }

        // 立即將數值同步回 Settings 物件並存檔
        var cur = _settingsService.Current.TranslationSettings;
        cur.MaxParallelism = MaxParallelism;
        cur.BatchSizeHardCap = BatchSize;
        cur.Context = ContextWindow;
        cur.MaxOutput = MaxOutput;

        await _settingsService.SaveAsync();
    }

    [RelayCommand]
    private void ApplyModelPreset(string modelName)
    {
        if (modelName.Contains("qwen2.5:32b"))
        {
            ContextWindow = 16384;
            Model = "qwen32b-low-vram";
            _settingsService.Current.Model = "qwen32b-low-vram";
            _logger.LogInformation("切換至 Qwen 2.5，已自動調整 Context Window。");
        }
        else
        {
            // 針對 gpt-oss:20b 或其他 20b 模型
            ContextWindow = 8192;
            Model = modelName;
            _settingsService.Current.Model = modelName;
            _logger.LogInformation("切換至 20B 模型，Context Window 已還原為 8192。");
        }
    }

    [RelayCommand]
    private async Task ResetSettings()
    {
        // 重新載入或重置的邏輯
        await _settingsService.LoadAsync();
        ReloadSetting();
    }
}