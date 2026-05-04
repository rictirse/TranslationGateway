using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Translation.Gateway.ViewModels;

public partial class ChatViewModel
{
    [ObservableProperty]
    private string _systemInput = string.Empty;
    [ObservableProperty]
    private string _chatInput = string.Empty;
    [ObservableProperty]
    private string _chatOutput = string.Empty;
    [ObservableProperty]
    private string _currentModel = string.Empty;
    [ObservableProperty]
    private string _selectedPromptFile = string.Empty;
    public ObservableCollection<string> PromptFiles { get; } = new();
}