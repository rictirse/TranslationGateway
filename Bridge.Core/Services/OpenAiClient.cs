using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

public class OpenAiClient : IOpenAiClient
{
    private readonly ISettingsService _settings;
    private readonly ILogger<OpenAiClient> _logger;
    private readonly PipelineTransport? _transport;

    private readonly object _clientLock = new();
    private OpenAIClient? _openAiClient;
    private Uri? _cachedEndpoint;
    private string _cachedApiKey = string.Empty;

    /// <param name="transport">測試用：替換 HTTP 傳輸層；正式環境為 null。</param>
    public OpenAiClient(ISettingsService settings, ILogger<OpenAiClient> logger, PipelineTransport? transport = null)
    {
        _settings = settings;
        _logger = logger;
        _transport = transport;
    }

    public Task<ChatResult> SendAsync(
        string userText,
        string systemText,
        CancellationToken cancellationToken,
        int? maxOutputTokens = null)
        => SendChatAsync(systemText, [], userText, cancellationToken, maxOutputTokens);

    public async Task<ChatResult> SendChatAsync(
        string systemText,
        IReadOnlyList<ChatTurn> history,
        string userText,
        CancellationToken cancellationToken,
        int? maxOutputTokens = null)
    {
        var s = _settings.Current;

        var endpoint = OpenAiEndpoint.TryBuild(s.Translation)
            ?? throw new InvalidOperationException("尚未設定 API Host，請到設定頁填寫。");

        var apiKey = string.IsNullOrWhiteSpace(s.Translation.ApiKey) ? "no-key-needed" : s.Translation.ApiKey!;

        ChatClient chatClient = GetOrCreateClient(endpoint, apiKey).GetChatClient(s.Translation.Model);

        List<ChatMessage> messages = BuildMessages(systemText, history, userText);
        ChatCompletionOptions options = BuildOptions(s.Translation, maxOutputTokens ?? 1024);

        try
        {
            // 4. 發送請求
            ChatCompletion completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);

            // 5. 解析回應 (SDK 已經幫你轉成強型別物件了)
            // 模型可能回傳空的 content（例如只有思考內容）：Content 是空清單，不能直接取 [0]
            string msg = completion.Content is { Count: > 0 } content ? content[0].Text ?? "" : "";
            var usage = completion.Usage;

            return new ChatResult(
                Content: msg,
                PromptTokens: usage?.InputTokenCount,     // 修正為 InputTokenCount
                CompletionTokens: usage?.OutputTokenCount, // 修正為 OutputTokenCount
                TotalTokens: usage?.TotalTokenCount,       // 修正為 TotalTokenCount
                Model: completion.Model,
                Truncated: completion.FinishReason == ChatFinishReason.Length); // 被截斷的譯文不可寫入快取
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Chat completion failed via OpenAI SDK.");
            throw;
        }
    }

    public async Task WarmUpAsync(string systemText, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken)
    {
        try
        {
            var s = _settings.Current;

            var endpoint = OpenAiEndpoint.TryBuild(s.Translation);
            if (endpoint is null)
            {
                return;
            }

            var apiKey = string.IsNullOrWhiteSpace(s.Translation.ApiKey) ? "no-key-needed" : s.Translation.ApiKey!;

            ChatClient chatClient = GetOrCreateClient(endpoint, apiKey).GetChatClient(s.Translation.Model);

            List<ChatMessage> messages = BuildMessages(systemText, history, userText: null);
            ChatCompletionOptions options = BuildOptions(s.Translation, 1);

            await chatClient.CompleteChatAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 預熱失敗不影響後續翻譯：只記警告、不往外拋
            _logger.LogWarning(ex, "Warm-up request failed.");
        }
        catch
        {
            // 取消或其他例外：靜默吞掉
        }
    }

    /// <summary>組聊天訊息：system →（每輪 user/assistant）→ 新的 user（userText 為 null 時不加，預熱用）。</summary>
    private static List<ChatMessage> BuildMessages(string systemText, IReadOnlyList<ChatTurn> history, string? userText)
    {
        List<ChatMessage> messages = [new SystemChatMessage(systemText)];
        foreach (var turn in history)
        {
            messages.Add(new UserChatMessage(turn.User));
            messages.Add(new AssistantChatMessage(turn.Assistant));
        }
        if (userText is not null)
        {
            messages.Add(new UserChatMessage(userText));
        }
        return messages;
    }

    /// <summary>組請求參數：溫度與輸出上限會以底層 json 強制寫入。</summary>
    private static ChatCompletionOptions BuildOptions(TranslationSettings settings, int effectiveMaxOutputTokens)
    {
        // 3. 設定參數 (例如 Temperature)
        ChatCompletionOptions options = new()
        {
            Temperature = 0f, // 注意：新版 SDK 是 float
            MaxOutputTokenCount = effectiveMaxOutputTokens // 💡 新版 SDK 的 max_tokens 改名為 MaxOutputTokenCount
        };

        // 2. 萬用破玻璃解法：完全關閉思考
        // 透過 IJsonModel 強制將 "none"、max_completion_tokens 與 temperature 寫入發送至 API 的底層 json payload
        //（溫度與 top_p 須以 InvariantCulture 格式化，避免小數點被輸成逗號）
        var topPJson = settings.TopP is { } topP
            ? $", \"top_p\": {topP.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        return ((IJsonModel<ChatCompletionOptions>)options).Create(
            new BinaryData($$"""
            {
                "reasoning_effort": "none",
                "max_completion_tokens": {{effectiveMaxOutputTokens}},
                "temperature": {{settings.Temperature.ToString(CultureInfo.InvariantCulture)}}{{topPJson}}
            }
            """),
            ModelReaderWriterOptions.Json
        )!;
    }

    /// <summary>只有端點或 API key 改變時才重建內部 OpenAIClient（執行緒安全）。</summary>
    private OpenAIClient GetOrCreateClient(Uri endpoint, string apiKey)
    {
        lock (_clientLock)
        {
            if (_openAiClient is not null &&
                _cachedEndpoint == endpoint &&
                string.Equals(_cachedApiKey, apiKey, StringComparison.Ordinal))
            {
                return _openAiClient;
            }

            var options = new OpenAIClientOptions
            {
                Endpoint = endpoint
            };
            if (_transport is not null)
            {
                options.Transport = _transport;
            }

            _openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), options);
            _cachedEndpoint = endpoint;
            _cachedApiKey = apiKey;
            return _openAiClient;
        }
    }
}
