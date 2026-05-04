using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;

namespace Translation.Gateway.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ILogger<SettingsViewModel> _logger;

    // 直接暴露實體，UI 透過 Translation.ApiHost 進行綁定
    [ObservableProperty]
    private TranslationSettings _translation = null!;

    [ObservableProperty]
    private TranslationSettings _lorebook = null!;

    [ObservableProperty]
    private string _baseStoragePath = string.Empty;

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

        Translation = cur.Translation;
        BaseStoragePath = cur.BaseStoragePath;
    }

    [RelayCommand]
    private async Task SaveSettings()
    {
        await _settingsService.SaveAsync();
        _logger.LogInformation("設定已儲存");
    }
}