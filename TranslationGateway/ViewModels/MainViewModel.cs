using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslationGateway.Interface;

namespace TranslationGateway.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty] private object? _currentView;

    // 透過 DI 注入 ServiceProvider 與 SettingsService
    public MainViewModel(
        IServiceProvider serviceProvider,
        ISettingsService settingsService,
        ILogger<MainViewModel> logger)
    {
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;

        // 根據上次離開時的記錄自動導航，若無記錄則預設 Dashboard
        if (string.IsNullOrEmpty(_settingsService.Current.LastSelectedControlName))
        {
            _settingsService.Current.LastSelectedControlName = "Dashboard";
        }
        Navigate(_settingsService.Current.LastSelectedControlName);

        _logger = logger;
    }

    [RelayCommand]
    private void Navigate(string destination)
    {
        //離開設定畫面先存檔
        if (_settingsService.Current.LastSelectedControlName == "Settings")
        {
            _settingsService.SaveAsync().ConfigureAwait(false).GetAwaiter();
        }

        CurrentView = destination switch
        {
            "Dashboard" => _serviceProvider.GetRequiredService<DashboardViewModel>(),
            "Settings" => _serviceProvider.GetRequiredService<SettingsViewModel>(),
            "Chat" => _serviceProvider.GetRequiredService<ChatViewModel>(),
            "Knowledge" => _serviceProvider.GetRequiredService<KnowledgeViewModel>(),
            "Log" => _serviceProvider.GetRequiredService<LogViewModel>(),
            _ => CurrentView
        };

        _settingsService.Current.LastSelectedControlName = destination;
    }
}