using CommunityToolkit.Mvvm.ComponentModel;
using TranslationGateway.Logging;
using TranslationGateway.Models;

namespace TranslationGateway.ViewModels;

public partial class LogViewModel : ObservableObject
{
    [ObservableProperty] private UiLogStore _uiLogs;
    [ObservableProperty] private ApiTraceStore _apiTraces;

    public LogViewModel(UiLogStore uiLogs, ApiTraceStore apiTraces)
    {
        _uiLogs = uiLogs;
        _apiTraces = apiTraces;
    }
}