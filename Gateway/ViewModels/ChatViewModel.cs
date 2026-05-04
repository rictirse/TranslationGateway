using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;

namespace Translation.Gateway.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly IOpenAiClient _openAiClient;
    private readonly ISettingsService _settings;
    private readonly ITextProcessingService _textProcessingService;
    private readonly ILogger<ChatViewModel> _logger;

    public ChatViewModel(
        IOpenAiClient llmClient,
        ISettingsService settings,
        ITextProcessingService novelTextProcessor,
        ILogger<ChatViewModel> logger)
    {
        _openAiClient = llmClient;
        _settings = settings;
        _textProcessingService = novelTextProcessor;
        _logger = logger;
        _systemInput = settings.Current.Translation.SystemPromptTemplate;
        _currentModel = settings.Current.Translation.Model;
    }

    partial void OnSystemInputChanged(string value)
    {
        _settings.Current.Translation.SystemPromptTemplate = value;
        _ = _settings.SaveAsync();
    }

    partial void OnCurrentModelChanged(string value)
    {
        _settings.Current.Translation.Model = value;
        _ = _settings.SaveAsync();
    }

}