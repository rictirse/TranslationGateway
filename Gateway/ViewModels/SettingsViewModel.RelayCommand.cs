using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Translation.Gateway.ViewModels;

public partial class SettingsViewModel
{

    [RelayCommand]
    private async Task SaveSettings()
    {
        var cur = _settingsService.Current;

        cur.ApiHost = ApiHost;
        cur.Model = Model;
        cur.UseLocalModel = IsLocalModel;
        cur.LastSelectedControlName = LastSelectedControlName;

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