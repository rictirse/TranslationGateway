using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class OpenAiEndpointTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryBuild_EmptyOrWhitespaceHost_ReturnsNull(string apiHost)
    {
        var settings = new TranslationSettings { ApiHost = apiHost };

        var result = OpenAiEndpoint.TryBuild(settings);

        Assert.Null(result);
    }

    [Fact]
    public void TryBuild_UseLocalModel_IgnoresApiHost()
    {
        var settings = new TranslationSettings
        {
            UseLocalModel = true,
            ApiHost = "http://example.com:1234"
        };

        var result = OpenAiEndpoint.TryBuild(settings);

        Assert.Equal(new Uri("http://192.168.81.50:11434/v1"), result);
    }

    [Theory]
    [InlineData("localhost:8080", "http://localhost:8080/v1")]
    [InlineData("http://h:1/v1/", "http://h:1/v1")]
    [InlineData("https://h/v1/chat/completions", "https://h/v1")]
    [InlineData("http://h:1/V1", "http://h:1/V1")]
    [InlineData("  http://h:1  ", "http://h:1/v1")]
    public void TryBuild_ValidHosts_ReturnsExpectedEndpoint(string apiHost, string expected)
    {
        var settings = new TranslationSettings { ApiHost = apiHost };

        var result = OpenAiEndpoint.TryBuild(settings);

        Assert.Equal(new Uri(expected), result);
    }

    /// <summary>任務卡 T-2030 的規則：只有網址沒有路徑才補 /v1，已有路徑（如 Z.AI 的 /v4）照原樣使用。</summary>
    [Theory]
    [InlineData("http://192.168.81.50:11434", "http://192.168.81.50:11434/v1")]
    [InlineData("localhost:8080", "http://localhost:8080/v1")]
    [InlineData("https://api.z.ai/api/coding/paas/v4", "https://api.z.ai/api/coding/paas/v4")]
    [InlineData("https://api.z.ai/api/coding/paas/v4/chat/completions", "https://api.z.ai/api/coding/paas/v4")]
    [InlineData("http://h:1/v1/", "http://h:1/v1")]
    [InlineData("http://litellm:4000/v1", "http://litellm:4000/v1")]
    public void TryBuild_NoPathAppendsV1_ExistingPathKeptAsIs(string apiHost, string expected)
    {
        var settings = new TranslationSettings { ApiHost = apiHost };

        var result = OpenAiEndpoint.TryBuild(settings);

        Assert.Equal(new Uri(expected), result);
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("::::")]
    [InlineData("/")]
    public void TryBuild_InvalidHost_ReturnsNullWithoutThrowing(string apiHost)
    {
        var settings = new TranslationSettings { ApiHost = apiHost };

        var ex = Record.Exception(() => OpenAiEndpoint.TryBuild(settings));

        Assert.Null(ex);
        Assert.Null(OpenAiEndpoint.TryBuild(settings));
    }
}
