using CommunityToolkit.Mvvm.ComponentModel;
using Translation.Bridge.Core.Models;
using Translation.Gateway.Logging;

namespace Translation.Gateway.ViewModels;

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