using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class LorebookLlmClientTests
{
    private const string CompletionJson = """
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "glm-4.6",
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "{\"ok\": true}" },
              "finish_reason": "stop"
            }
          ],
          "usage": { "prompt_tokens": 12, "completion_tokens": 34, "total_tokens": 46 }
        }
        """;

    private static (LorebookLlmClient Sut, StubHandler Handler) CreateSut(
        string apiHost = "https://api.z.ai/api/coding/paas/v4",
        string model = "glm-4.6",
        string? apiKey = "sk-test",
        Action<TranslationSettings>? configure = null,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        Func<AppSettings, TranslationSettings>? selectSettings = null,
        bool disableThinking = false)
    {
        var lorebook = new TranslationSettings { ApiHost = apiHost, Model = model, ApiKey = apiKey };
        configure?.Invoke(lorebook);
        var settings = new AppSettings
        {
            Lorebook = lorebook
        };
        var handler = new StubHandler(CompletionJson, statusCode);
        var sut = new LorebookLlmClient(
            new HttpClient(handler),
            new FakeSettingsService(settings),
            NullLogger<LorebookLlmClient>.Instance,
            selectSettings,
            disableThinking);
        return (sut, handler);
    }

    [Fact]
    public async Task CompleteJsonAsync_PostsToEndpointChatCompletions()
    {
        var (sut, handler) = CreateSut();

        await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.Equal("https://api.z.ai/api/coding/paas/v4/chat/completions", handler.RequestUri);
    }

    [Fact]
    public async Task CompleteJsonAsync_ApiKeySet_SendsBearerHeader()
    {
        var (sut, handler) = CreateSut(apiKey: "sk-test");

        await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.Equal("Bearer sk-test", handler.AuthorizationHeader);
    }

    [Fact]
    public async Task CompleteJsonAsync_EmptyApiKey_NoAuthorizationHeader()
    {
        var (sut, handler) = CreateSut(apiKey: "");

        await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.Null(handler.AuthorizationHeader);
    }

    [Fact]
    public async Task CompleteJsonAsync_BodyContainsModelMessagesAndJsonResponseFormat()
    {
        var (sut, handler) = CreateSut();

        await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestBody);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;
        Assert.Equal("glm-4.6", root.GetProperty("model").GetString());
        Assert.Equal(0d, root.GetProperty("temperature").GetDouble());
        Assert.False(root.TryGetProperty("top_p", out _));
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("系統提示", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("使用者提示", messages[1].GetProperty("content").GetString());
        // UnsafeRelaxedJsonEscaping：中文不跳脫
        Assert.Contains("系統提示", handler.RequestBody);
        Assert.DoesNotContain("\\u", handler.RequestBody);
    }

    [Fact]
    public async Task CompleteJsonAsync_GlmModelWithDefaultEffort_SendsThinkingEnabledAndReasoningEffort()
    {
        var (sut, handler) = CreateSut(model: "glm-5.3");

        await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestBody);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;
        Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("low", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("reasoning_budget_tokens", out _));
    }

    [Fact]
    public async Task CompleteJsonAsync_GlmModelUppercaseWithEffortNone_SendsThinkingDisabledWithoutEffort()
    {
        var (sut, handler) = CreateSut(model: "GLM-5.3", configure: s => s.ReasoningEffort = "none");

        await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestBody);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.False(root.TryGetProperty("reasoning_budget_tokens", out _));
    }

    [Theory]
    [InlineData("MAX ", "max")]
    [InlineData("abc", "low")]
    public async Task CompleteJsonAsync_GlmModelEffortIsNormalized(string configured, string sent)
    {
        var (sut, handler) = CreateSut(model: "glm-5.3", configure: s => s.ReasoningEffort = configured);

        await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestBody);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(sent, doc.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task CompleteJsonAsync_NonGlmModel_SendsReasoningBudgetWithoutThinking()
    {
        var (sut, handler) = CreateSut(model: "qwen3-next-80b");

        await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestBody);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;
        Assert.Equal(4000, root.GetProperty("reasoning_budget_tokens").GetInt32());
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task CompleteJsonAsync_NonGlmModelWithZeroBudget_SendsNoReasoningBudget()
    {
        var (sut, handler) = CreateSut(model: "qwen3-next-80b", configure: s => s.ReasoningBudgetTokens = 0);

        await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestBody);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("reasoning_budget_tokens", out _));
    }

    [Fact]
    public async Task CompleteJsonAsync_TemperatureComesFromSettings_TopPOptional()
    {
        var (sut, handler) = CreateSut(configure: s => s.Temperature = 0.25);

        await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestBody);
        using (var doc = JsonDocument.Parse(handler.RequestBody!))
        {
            Assert.Equal(0.25, doc.RootElement.GetProperty("temperature").GetDouble());
            Assert.False(doc.RootElement.TryGetProperty("top_p", out _));
        }

        var (sut2, handler2) = CreateSut(configure: s => s.TopP = 0.9);
        await sut2.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.NotNull(handler2.RequestBody);
        using var doc2 = JsonDocument.Parse(handler2.RequestBody!);
        Assert.Equal(0.9, doc2.RootElement.GetProperty("top_p").GetDouble());
    }

    [Theory]
    [InlineData("glm-4.6", true)]
    [InlineData("GLM-5.3", true)]
    [InlineData("qwen3-next-80b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGlm_MatchesModelNameContainingGlmCaseInsensitive(string? model, bool expected)
    {
        Assert.Equal(expected, LorebookModelKind.IsGlm(model));
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("none", "none")]
    [InlineData("MAX ", "max")]
    [InlineData(" High", "high")]
    [InlineData("abc", "low")]
    [InlineData("", "low")]
    [InlineData(null, "low")]
    public void NormalizeReasoningEffort_TrimsLowercasesAndFallsBackToLow(string? value, string expected)
    {
        Assert.Equal(expected, LorebookModelKind.NormalizeReasoningEffort(value));
    }

    [Fact]
    public async Task CompleteJsonAsync_ParsesContentAndUsage()
    {
        var (sut, _) = CreateSut();

        var result = await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.Equal("{\"ok\": true}", result.Content);
        Assert.Equal(12, result.PromptTokens);
        Assert.Equal(34, result.CompletionTokens);
        Assert.Equal("glm-4.6", result.Model);
        Assert.True(result.ElapsedMs >= 0);
    }

    [Fact]
    public async Task CompleteJsonAsync_MissingUsageAndModel_FallsBackToZeroAndConfiguredModel()
    {
        var minimal = """
            {
              "choices": [
                { "index": 0, "message": { "role": "assistant", "content": "{\"ok\": true}" }, "finish_reason": "stop" }
              ]
            }
            """;
        var settings = new AppSettings
        {
            Lorebook = new TranslationSettings { ApiHost = "https://api.z.ai/api/coding/paas/v4", Model = "glm-4.6" }
        };
        var handler = new StubHandler(minimal);
        var sut = new LorebookLlmClient(new HttpClient(handler), new FakeSettingsService(settings), NullLogger<LorebookLlmClient>.Instance);

        var result = await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.PromptTokens);
        Assert.Equal(0, result.CompletionTokens);
        Assert.Equal("glm-4.6", result.Model);
    }

    [Fact]
    public async Task CompleteJsonAsync_ContentWrappedInCodeFence_FenceStripped()
    {
        var fenced = """
            {
              "model": "glm-4.6",
              "choices": [
                { "index": 0, "message": { "role": "assistant", "content": "```json\n{\"ok\": true}\n```" }, "finish_reason": "stop" }
              ],
              "usage": { "prompt_tokens": 1, "completion_tokens": 2, "total_tokens": 3 }
            }
            """;
        var settings = new AppSettings
        {
            Lorebook = new TranslationSettings { ApiHost = "https://api.z.ai/api/coding/paas/v4", Model = "glm-4.6" }
        };
        var handler = new StubHandler(fenced);
        var sut = new LorebookLlmClient(new HttpClient(handler), new FakeSettingsService(settings), NullLogger<LorebookLlmClient>.Instance);

        var result = await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.Equal("{\"ok\": true}", result.Content);
    }

    [Fact]
    public async Task CompleteJsonAsync_Non2xx_ThrowsWithStatusCode()
    {
        var settings = new AppSettings
        {
            Lorebook = new TranslationSettings { ApiHost = "https://api.z.ai/api/coding/paas/v4", Model = "glm-4.6" }
        };
        var handler = new StubHandler("""{"error": {"message": "invalid api key"}}""", HttpStatusCode.Unauthorized);
        var sut = new LorebookLlmClient(new HttpClient(handler), new FakeSettingsService(settings), NullLogger<LorebookLlmClient>.Instance);

        var ex = await Assert.ThrowsAsync<LorebookLlmException>(
            () => sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken));

        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid api key", ex.Message);
    }

    // HTTP 429（限流）與 5xx（伺服器錯誤）是暫時性錯誤；4xx（非 429）不是。
    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    public async Task CompleteJsonAsync_HttpError_TransientDependsOnStatus(int statusCode, bool expected)
    {
        var (sut, _) = CreateSut(statusCode: (HttpStatusCode)statusCode);

        var ex = await Assert.ThrowsAsync<LorebookLlmException>(
            () => sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken));

        Assert.Equal(expected, ex.IsTransient);
    }

    [Fact]
    public async Task CompleteJsonAsync_SelectTranslationSettings_SendsToTranslationHostAndModel()
    {
        var settings = new AppSettings
        {
            Lorebook = new TranslationSettings { ApiHost = "https://api.z.ai/api/coding/paas/v4", Model = "glm-4.6" },
            Translation = new TranslationSettings { ApiHost = "http://192.168.81.50:11434/strata", Model = "qwen3.8-flash" },
        };
        var handler = new StubHandler(CompletionJson);
        var sut = new LorebookLlmClient(
            new HttpClient(handler),
            new FakeSettingsService(settings),
            NullLogger<LorebookLlmClient>.Instance,
            s => s.Translation);

        await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.Equal("http://192.168.81.50:11434/strata/chat/completions", handler.RequestUri);
        Assert.NotNull(handler.RequestBody);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("qwen3.8-flash", doc.RootElement.GetProperty("model").GetString());
    }

    // disableThinking：不送 thinking 與 reasoning_budget_tokens，改送 reasoning_effort = none（GLM 與非 GLM 都一樣）。
    [Theory]
    [InlineData("glm-5.3")]
    [InlineData("qwen3-next-80b")]
    public async Task CompleteJsonAsync_DisableThinking_SendsReasoningEffortNone(string model)
    {
        var (sut, handler) = CreateSut(model: model, disableThinking: true);

        await sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestBody);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;
        Assert.Equal("none", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("reasoning_budget_tokens", out _));
    }

    [Theory]
    [InlineData("", "glm-4.6")]
    [InlineData("https://api.z.ai/api/coding/paas/v4", "")]
    public async Task CompleteJsonAsync_IncompleteSettings_Throws(string apiHost, string model)
    {
        var settings = new AppSettings
        {
            Lorebook = new TranslationSettings { ApiHost = apiHost, Model = model }
        };
        var handler = new StubHandler(CompletionJson);
        var sut = new LorebookLlmClient(new HttpClient(handler), new FakeSettingsService(settings), NullLogger<LorebookLlmClient>.Instance);

        var ex = await Assert.ThrowsAsync<LorebookLlmException>(
            () => sut.CompleteJsonAsync("系統提示", "使用者提示", TestContext.Current.CancellationToken));

        Assert.Equal("尚未設定知識庫模型（設定 → 知識庫模型）", ex.Message);
    }

    [Fact]
    public async Task CompleteJsonAsync_ExternalCancellation_PropagatesOperationCanceled()
    {
        var settings = new AppSettings
        {
            Lorebook = new TranslationSettings { ApiHost = "https://api.z.ai/api/coding/paas/v4", Model = "glm-4.6" }
        };
        var sut = new LorebookLlmClient(
            new HttpClient(new CancellingHandler()),
            new FakeSettingsService(settings),
            NullLogger<LorebookLlmClient>.Instance);
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.CompleteJsonAsync("系統提示", "使用者提示", cts.Token));
    }

    [Fact]
    public async Task TestConnectionAsync_Success_ReturnsTrueWithModelAndElapsed()
    {
        var (sut, _) = CreateSut();

        var (success, message) = await sut.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.True(success);
        Assert.Contains("連線成功", message);
        Assert.Contains("glm-4.6", message);
        Assert.Contains("ms", message);
    }

    [Fact]
    public async Task TestConnectionAsync_ServerError_ReturnsFalseWithoutThrowing()
    {
        var settings = new AppSettings
        {
            Lorebook = new TranslationSettings { ApiHost = "https://api.z.ai/api/coding/paas/v4", Model = "glm-4.6" }
        };
        var handler = new StubHandler("server error", HttpStatusCode.InternalServerError);
        var sut = new LorebookLlmClient(new HttpClient(handler), new FakeSettingsService(settings), NullLogger<LorebookLlmClient>.Instance);

        var (success, message) = await sut.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.False(success);
        Assert.Contains("連線失敗", message);
        Assert.Contains("500", message);
    }

    [Fact]
    public async Task TestConnectionAsync_SettingsMissing_ReturnsFalseWithoutThrowing()
    {
        var settings = new AppSettings();
        var handler = new StubHandler(CompletionJson);
        var sut = new LorebookLlmClient(new HttpClient(handler), new FakeSettingsService(settings), NullLogger<LorebookLlmClient>.Instance);

        var (success, message) = await sut.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.False(success);
        Assert.Contains("連線失敗", message);
        Assert.Empty(handler.RequestUris);
    }

    /// <summary>攔截請求並回傳固定回應的假 handler。</summary>
    private sealed class StubHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string? RequestUri { get; private set; }
        public string? AuthorizationHeader { get; private set; }
        public List<string> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            RequestUris.Add(RequestUri ?? "");
            AuthorizationHeader = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
            {
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>持續等待直到被取消，模擬模型遲遲不回應。</summary>
    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
