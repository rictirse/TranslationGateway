using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel.Primitives;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

public class OpenAiClient : IOpenAiClient
{
    private readonly ISettingsService _settings;
    private readonly ILogger<OpenAiClient> _logger;
    private readonly OpenAIClient _openAiClient;

    public OpenAiClient(
        OpenAIClient openAiClient,
        ISettingsService settings,
        ILogger<OpenAiClient> logger)
    {
        _openAiClient = openAiClient;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ChatResult> SendAsync(
        string userText,
        string systemText,
        CancellationToken cancellationToken)
    {
        var s = _settings.Current;

        ChatClient chatClient = _openAiClient.GetChatClient(s.Translation.Model);

        List<ChatMessage> messages = new()
        {
            new SystemChatMessage(systemText),
            new UserChatMessage(userText)
        };

        // 3. 設定參數 (例如 Temperature)
        ChatCompletionOptions options = new()
        {
            Temperature = 0f, // 注意：新版 SDK 是 float
            MaxOutputTokenCount = 1024 // 💡 新版 SDK 的 max_tokens 改名為 MaxOutputTokenCount
        };

        // 2. 萬用破玻璃解法：完全關閉思考
        // 透過 IJsonModel 強制將 "none" 寫入發送至 API 的底層 json payload
        options = ((IJsonModel<ChatCompletionOptions>)options).Create(
            new BinaryData("""
            {
                "reasoning_effort": "none",
                "max_completion_tokens": 1024
            }
            """),
            ModelReaderWriterOptions.Json
        );

        try
        {
            // 4. 發送請求
            ChatCompletion completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);

            // 5. 解析回應 (SDK 已經幫你轉成強型別物件了)
            string msg = completion.Content?[0]?.Text ?? "";
            var usage = completion.Usage;

            return new ChatResult(
                Content: msg,
                PromptTokens: usage?.InputTokenCount,     // 修正為 InputTokenCount
                CompletionTokens: usage?.OutputTokenCount, // 修正為 OutputTokenCount
                TotalTokens: usage?.TotalTokenCount,       // 修正為 TotalTokenCount
                Model: completion.Model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat completion failed via OpenAI SDK.");
            throw;
        }
    }
}