using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TranslationGateway.Interface;

namespace TranslationGateway.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly IOpenAiClient _llmClient;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private string _systemInput = string.Empty;
    [ObservableProperty]
    private string _chatInput = string.Empty;
    [ObservableProperty]
    private string _chatOutput = string.Empty;

    public ChatViewModel(
        IOpenAiClient llmClient,
        ISettingsService settings)
    {
        _llmClient = llmClient;
        _settings = settings;
        _systemInput = settings.Current.TranslationSettings.SystemPromptTemplate;
    }

    partial void OnSystemInputChanged(string value)
    {
        _settings.Current.TranslationSettings.SystemPromptTemplate = value;
        _ = _settings.SaveAsync();
    }

    [RelayCommand]
    private async Task SendChat()
    {
        if (string.IsNullOrWhiteSpace(ChatInput)) return;

        string currentInput = ChatInput;

        try
        {
            var chatResult = await _llmClient.SendAsync(
                currentInput,
                SystemInput,
                CancellationToken.None
            );
            ChatOutput = chatResult.Content;
        }
        catch (Exception ex)
        {
            ChatOutput = $"[Error]: {ex.Message}";
        }
    }
}