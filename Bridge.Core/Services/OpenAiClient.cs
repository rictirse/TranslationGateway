using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

public class OpenAiClient : IOpenAiClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<OpenAiClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public OpenAiClient(
        IHttpClientFactory httpFactory, 
        ISettingsService settings, 
        ILogger<OpenAiClient> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ChatResult> SendAsync(
        string userText,
        string systemText,
        CancellationToken cancellationToken)
    {
        var s = _settings.Current;
        var host = s.UseLocalModel
            ? "http://localhost:11434"
            : s.ApiHost;
        var url = BuildChatCompletionsUrl(host, s.ChatCompletionsPath);

        var payload = new
        {
            model = s.Model,
            temperature = 0,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = systemText },
                new { role = "user", content = userText }
            }
        };

        var json = JsonSerializer.Serialize(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(s.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);

        var http = _httpFactory.CreateClient("openai");

        using var resp = await http.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Chat completion failed: {StatusCode} {Body}", (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var dto = JsonSerializer.Deserialize<OpenAiChatCompletionResponse>(body, _jsonOptions);
        var msg = dto?.Choices?.Length > 0 ? dto.Choices[0].Message?.Content ?? "" : "";
        var usage = dto?.Usage;

        return new ChatResult(
            Content: msg,
            PromptTokens: usage?.PromptTokens,
            CompletionTokens: usage?.CompletionTokens,
            TotalTokens: usage?.TotalTokens,
            Model: dto?.Model);
    }

    internal static string BuildChatCompletionsUrl(string apiHost, string path)
    {
        var host = (apiHost ?? "").Trim().TrimEnd('/');
        var p = (path ?? "").Trim();

        if (string.IsNullOrWhiteSpace(p)) p = "/v1/chat/completions";
        if (!p.StartsWith('/')) p = "/" + p;

        if (host.Contains("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            return host;

        if (host.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) && p.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase))
            p = p.Substring(3);

        return host + p;
    }
}