using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class YouTubeOEmbedClientTests
{
    private const string Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    private const string SuccessJson =
        """
        {"title":"【歌枠】歌うよ","author_name":"Lisa Ch. 花鏡都","author_url":"https://www.youtube.com/@lisahanabusa","thumbnail_url":"https://i.ytimg.com/vi/x/hqdefault.jpg"}
        """;

    private static YouTubeOEmbedClient Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<YouTubeOEmbedClient>.Instance);

    [Fact]
    public async Task GetYouTubeInfoAsync_Success_ReturnsAuthorTitleAndChannelUrl()
    {
        var handler = new StubHandler(SuccessJson, HttpStatusCode.OK);

        var info = await Create(handler).GetYouTubeInfoAsync(Url, TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal(StreamPlatform.YouTube, info.Platform);
        Assert.Null(info.Id);
        Assert.Equal("Lisa Ch. 花鏡都", info.Author);
        Assert.Null(info.Category);
        Assert.Equal("【歌枠】歌うよ", info.Title);
        Assert.Equal("https://www.youtube.com/@lisahanabusa", info.ChannelUrl);
        Assert.True(info.IsVod);
    }

    [Fact]
    public async Task GetYouTubeInfoAsync_RequestUrlContainsEncodedVideoUrl()
    {
        var handler = new StubHandler(SuccessJson, HttpStatusCode.OK);

        await Create(handler).GetYouTubeInfoAsync(Url, TestContext.Current.CancellationToken);

        var requestUrl = Assert.Single(handler.RequestUrls);
        Assert.StartsWith("https://www.youtube.com/oembed?", requestUrl);
        // 原網址要經過編碼（: ／ ＝ 等字元都會被換掉）
        Assert.Contains(Uri.EscapeDataString(Url), requestUrl);
        Assert.EndsWith("&format=json", requestUrl);
    }

    [Fact]
    public async Task GetYouTubeInfoAsync_NotFound_ReturnsNull()
    {
        var handler = new StubHandler("Not Found", HttpStatusCode.NotFound);

        var info = await Create(handler).GetYouTubeInfoAsync(Url, TestContext.Current.CancellationToken);

        Assert.Null(info);
    }

    [Fact]
    public async Task GetYouTubeInfoAsync_NotJson_ReturnsNull()
    {
        var handler = new StubHandler("<html>Oops</html>", HttpStatusCode.OK);

        var info = await Create(handler).GetYouTubeInfoAsync(Url, TestContext.Current.CancellationToken);

        Assert.Null(info);
    }

    [Fact]
    public async Task GetYouTubeInfoAsync_HttpRequestException_ReturnsNull()
    {
        var handler = new ThrowingHandler();

        var info = await Create(handler).GetYouTubeInfoAsync(Url, TestContext.Current.CancellationToken);

        Assert.Null(info);
    }

    /// <summary>攔截請求網址並回傳固定狀態碼與內容。</summary>
    private sealed class StubHandler(string content, HttpStatusCode statusCode) : HttpMessageHandler
    {
        public List<string> RequestUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUrls.Add(request.RequestUri?.ToString() ?? "");
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>模擬網路錯誤（連不上、DNS 失敗等）。</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("network error");
    }
}
