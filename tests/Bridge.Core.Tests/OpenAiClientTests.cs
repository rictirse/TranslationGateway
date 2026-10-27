using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class OpenAiClientTests
{
    private const string StopCompletionJson = """
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "test-model",
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "你好" },
              "finish_reason": "stop",
              "logprobs": null
            }
          ],
          "usage": { "prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15 }
        }
        """;

    private const string LengthCompletionJson = """
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "test-model",
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "這是截斷的譯" },
              "finish_reason": "length",
              "logprobs": null
            }
          ],
          "usage": { "prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15 }
        }
        """;

    private static OpenAiClient CreateSut(
        HttpMessageHandler handler,
        double temperature = 0,
        double? topP = null,
        string apiHost = "localhost:8080",
        ILogger<OpenAiClient>? logger = null)
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings { Model = "test-model", Temperature = temperature, TopP = topP, ApiHost = apiHost }
        };
        return new OpenAiClient(
            new FakeSettingsService(settings),
            logger ?? NullLogger<OpenAiClient>.Instance,
            new HttpClientPipelineTransport(new HttpClient(handler)));
    }

    [Fact]
    public void Constructor_WithEmptyHost_DoesNotThrow()
    {
        var settings = new AppSettings();

        var ex = Record.Exception(() => new OpenAiClient(
            new FakeSettingsService(settings),
            NullLogger<OpenAiClient>.Instance));

        Assert.Null(ex);
    }

    [Fact]
    public async Task SendAsync_WithEmptyHost_ThrowsInvalidOperation()
    {
        var sut = CreateSut(new StubHandler(StopCompletionJson), apiHost: "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SendAsync("user", "system", TestContext.Current.CancellationToken));

        Assert.Equal("尚未設定 API Host，請到設定頁填寫。", ex.Message);
    }

    [Fact]
    public async Task SendAsync_AfterHostChanged_UsesNewEndpoint()
    {
        var handler = new StubHandler(StopCompletionJson);
        var settings = new AppSettings
        {
            Translation = new TranslationSettings { Model = "test-model", ApiHost = "localhost:8080" }
        };
        var sut = new OpenAiClient(
            new FakeSettingsService(settings),
            NullLogger<OpenAiClient>.Instance,
            new HttpClientPipelineTransport(new HttpClient(handler)));

        await sut.SendAsync("user", "system", TestContext.Current.CancellationToken);
        settings.Translation.ApiHost = "http://newhost:9999";
        await sut.SendAsync("user", "system", TestContext.Current.CancellationToken);

        Assert.Equal(["localhost", "newhost"], handler.RequestHosts);
    }

    [Fact]
    public async Task SendAsync_RequestJson_ContainsTemperatureAndMaxTokens()
    {
        var handler = new StubHandler(StopCompletionJson);
        var sut = CreateSut(handler, temperature: 0);

        await sut.SendAsync("user text", "system text", TestContext.Current.CancellationToken, maxOutputTokens: 64);

        Assert.NotNull(handler.RequestBody);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;
        Assert.Equal(0d, root.GetProperty("temperature").GetDouble());
        Assert.Equal(64, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("none", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("top_p", out _));
    }

    [Fact]
    public async Task SendAsync_WithTopP_RequestJson_ContainsTopP()
    {
        var handler = new StubHandler(StopCompletionJson);
        var sut = CreateSut(handler, topP: 0.8);

        await sut.SendAsync("user text", "system text", TestContext.Current.CancellationToken, maxOutputTokens: 64);

        Assert.NotNull(handler.RequestBody);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var root = doc.RootElement;
        Assert.Equal(0.8, root.GetProperty("top_p").GetDouble());
        Assert.Equal("none", root.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task SendAsync_FinishReasonLength_SetsTruncated()
    {
        var sut = CreateSut(new StubHandler(LengthCompletionJson));

        var result = await sut.SendAsync("user", "system", TestContext.Current.CancellationToken);

        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("null")]
    public async Task SendAsync_EmptyContent_ReturnsEmptyString(string content)
    {
        var json = StopCompletionJson.Replace("\"content\": \"你好\"", $"\"content\": {content}");
        var sut = CreateSut(new StubHandler(json));

        var result = await sut.SendAsync("user", "system", TestContext.Current.CancellationToken);

        Assert.Equal("", result.Content);
    }

    [Fact]
    public async Task SendAsync_FinishReasonStop_NotTruncated()
    {
        var sut = CreateSut(new StubHandler(StopCompletionJson));

        var result = await sut.SendAsync("user", "system", TestContext.Current.CancellationToken);

        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task SendAsync_ExternalCancellation_DoesNotLogError()
    {
        var logger = new RecordingLogger();
        var sut = CreateSut(new CancellingHandler(), logger: logger);
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<Exception>(() => sut.SendAsync("user", "system", cts.Token, 64));

        Assert.DoesNotContain(logger.Levels, level => level == LogLevel.Error);
    }

    [Fact]
    public async Task SendChatAsync_MessageOrder_SystemHistoryUser()
    {
        var handler = new StubHandler(StopCompletionJson);
        var sut = CreateSut(handler);
        var history = new List<ChatTurn> { new("u1", "a1") };

        await sut.SendChatAsync("system text", history, "user text", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestBody);
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var roles = doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString())
            .ToArray();
        Assert.Equal(["system", "user", "assistant", "user"], roles);
    }

    [Fact]
    public async Task WarmUpAsync_SendsMaxTokensOne_EndsAtLastHistoryUser()
    {
        var handler = new StubHandler(StopCompletionJson);
        var sut = CreateSut(handler);

        await sut.WarmUpAsync("system text", [], TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestBody);
        using (var doc = JsonDocument.Parse(handler.RequestBody!))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("max_completion_tokens").GetInt32());
            var role = Assert.Single(doc.RootElement.GetProperty("messages").EnumerateArray())
                .GetProperty("role").GetString();
            Assert.Equal("system", role);
        }

        // 有歷史：送到最後一輪的 user 為止，不含它的 assistant（下一句的請求會以這段為開頭）
        await sut.WarmUpAsync("system text", [new ChatTurn("u1", "a1"), new ChatTurn("u2", "a2")], TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestBody);
        using (var doc = JsonDocument.Parse(handler.RequestBody!))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("max_completion_tokens").GetInt32());
            var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => m.GetProperty("role").GetString()).ToArray());
            Assert.Equal("u2", messages[^1].GetProperty("content").GetString());
        }
    }

    [Fact]
    public async Task WarmUpAsync_ServerError_DoesNotThrow()
    {
        var sut = CreateSut(new Error500Handler());

        var ex = await Record.ExceptionAsync(() => sut.WarmUpAsync("system text", [], TestContext.Current.CancellationToken));

        Assert.Null(ex);
    }

    [Fact]
    public async Task WarmUpAsync_EmptyHost_DoesNotThrowOrSend()
    {
        var handler = new StubHandler(StopCompletionJson);
        var sut = CreateSut(handler, apiHost: "");

        var ex = await Record.ExceptionAsync(() => sut.WarmUpAsync("system text", [], TestContext.Current.CancellationToken));

        Assert.Null(ex);
        Assert.Empty(handler.RequestHosts);
    }

    /// <summary>攔截請求內容並回傳固定 chat.completion JSON。</summary>
    private sealed class StubHandler(string responseJson) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public List<string> RequestHosts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestHosts.Add(request.RequestUri?.Host ?? "");
            if (request.Content is not null)
            {
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
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

    /// <summary>一律回 HTTP 500，模擬伺服器錯誤。</summary>
    private sealed class Error500Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("server error", Encoding.UTF8, "application/json")
            });
    }

    /// <summary>記錄所有 log level 供測試斷言。</summary>
    private sealed class RecordingLogger : ILogger<OpenAiClient>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}
