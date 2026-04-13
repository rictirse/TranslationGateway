using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;

namespace Translation.Gateway.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private bool useThroughPass;

    public MainViewModel(
        IServiceProvider serviceProvider,
        ISettingsService settingsService,
        ILogger<MainViewModel> logger)
    {
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;

        if (string.IsNullOrEmpty(_settingsService.Current.LastSelectedControlName))
        {
            _settingsService.Current.LastSelectedControlName = "Dashboard";
        }
        Navigate(_settingsService.Current.LastSelectedControlName);

        _logger = logger;
    }

    partial void OnUseThroughPassChanged(bool value)
    {
        _settingsService.Current.ThroughPass = value;
        _settingsService.SaveAsync(); 
    }

    [RelayCommand]
    private void Navigate(string destination)
    {
        if (CurrentView is SettingsViewModel)
        {
            ((SettingsViewModel)CurrentView).SaveSettingsCommand.Execute(null);
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