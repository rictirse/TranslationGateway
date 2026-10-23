using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class YouTubeCaptionClientTests
{
    // ===== ParseJson3 =====

    [Fact]
    public void ParseJson3_ConcatenatesSegs_AndSortsByStartMs()
    {
        // 順序故意顛倒：結果要照 tStartMs 排序；segs 內多段要串接
        var json = """
            {"events":[
                {"tStartMs":30000,"dDurationMs":2210,"segs":[{"utf8":"なつめ"},{"utf8":"ちゃん"}]},
                {"tStartMs":267040,"dDurationMs":5520,"segs":[{"utf8":"うん。なんか"},{"utf8":"なちちゃん"}]}
            ]}
            """;

        var cues = YouTubeCaptionClient.ParseJson3(json);

        Assert.Equal(2, cues.Count);
        // 照 tStartMs 排序後 30000 在前（JSON 裡放後面）
        Assert.Equal(30000, cues[0].StartMs);
        Assert.Equal("なつめちゃん", cues[0].Text);
        Assert.Equal(267040, cues[1].StartMs);
        Assert.Equal(5520, cues[1].DurationMs);
        Assert.Equal("うん。なんかなちちゃん", cues[1].Text);
    }

    [Fact]
    public void ParseJson3_DropsNewlineOnlyAndMissingSegsEvents()
    {
        // 只有 "\n" 的 event（自動字幕的捲動效果）與沒有 segs 的 event 都要丟掉；換行換成空白、修剪
        var json = """
            {"events":[
                {"tStartMs":267030,"dDurationMs":2210,"aAppend":1,"segs":[{"utf8":"\n"}]},
                {"tStartMs":267040,"dDurationMs":5520,"segs":[{"utf8":"うん。なんか\n"},{"utf8":"なちちゃん"}]},
                {"tStartMs":268000,"dDurationMs":100,"wWinId":1},
                {"tStartMs":269000,"dDurationMs":100,"segs":[{"utf8":"  "}]}
            ]}
            """;

        var cues = YouTubeCaptionClient.ParseJson3(json);

        var cue = Assert.Single(cues);
        Assert.Equal("うん。なんか なちちゃん", cue.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"events\":{}}")]
    public void ParseJson3_BrokenInput_ReturnsEmpty(string json)
    {
        Assert.Empty(YouTubeCaptionClient.ParseJson3(json));
    }

    // ===== 選字幕軌 =====

    [Fact]
    public void SelectTrackUrl_PrefersManualJaTrackOverAsr()
    {
        var response = ParsePlayerResponse(
            """
            {"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[
                {"baseUrl":"https://c/asr","languageCode":"ja","kind":"asr"},
                {"baseUrl":"https://c/manual","languageCode":"ja"},
                {"baseUrl":"https://c/en","languageCode":"en"}
            ]}}}
            """);

        var url = YouTubeCaptionClient.SelectTrackUrl(response, out var languageCode, out var isAuto);

        Assert.Equal("https://c/manual", url);
        Assert.Equal("ja", languageCode);
        Assert.False(isAuto);
    }

    [Fact]
    public void SelectTrackUrl_OnlyAsrJaTrack_SelectsAsr()
    {
        var response = ParsePlayerResponse(
            """
            {"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[
                {"baseUrl":"https://c/asr","languageCode":"ja","kind":"asr"},
                {"baseUrl":"https://c/en","languageCode":"en","kind":"asr"}
            ]}}}
            """);

        var url = YouTubeCaptionClient.SelectTrackUrl(response, out var languageCode, out var isAuto);

        Assert.Equal("https://c/asr", url);
        Assert.Equal("ja", languageCode);
        Assert.True(isAuto);
    }

    [Fact]
    public void SelectTrackUrl_NoJapaneseTrack_ReturnsNull()
    {
        var response = ParsePlayerResponse(
            """
            {"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[
                {"baseUrl":"https://c/en","languageCode":"en"},
                {"baseUrl":"https://c/en-asr","languageCode":"en-US","kind":"asr"}
            ]}}}
            """);

        Assert.Null(YouTubeCaptionClient.SelectTrackUrl(response, out var languageCode, out var isAuto));
        Assert.Equal(string.Empty, languageCode);
        Assert.False(isAuto);
    }

    [Fact]
    public void SelectTrackUrl_JaDialectMatches_AndManualTakesPrecedencePerTrack()
    {
        var response = ParsePlayerResponse(
            """
            {"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[
                {"baseUrl":"https://c/ja-JP","languageCode":"ja-JP"}
            ]}}}
            """);

        Assert.Equal("https://c/ja-JP", YouTubeCaptionClient.SelectTrackUrl(response, out var languageCode, out _));
        Assert.Equal("ja-JP", languageCode);
    }

    // ===== 用假的 HttpMessageHandler 走完整流程（不連網）=====

    /// <summary>依序回傳排入回應、記下每個請求的 URI 與 User-Agent 的假 handler。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Queue<(string Body, HttpStatusCode Status)> Responses { get; } = new();

        public List<(string Uri, string? UserAgent)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // User-Agent 可能被拆成多個值（Typed header），串回完整字串再比對
            Requests.Add((request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.TryGetValues("User-Agent", out var values) ? string.Join(" ", values) : null));
            var (body, status) = Responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static YouTubeCaptionClient Create(StubHandler handler) =>
        new(new HttpClient(handler), NullLogger<YouTubeCaptionClient>.Instance);

    private const string OkWithAsrJa = """
        {"playabilityStatus":{"status":"OK"},"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[
            {"baseUrl":"https://captions.example/api?lang=ja","languageCode":"ja","kind":"asr"}
        ]}}}
        """;

    private const string LoginRequired = """{"playabilityStatus":{"status":"LOGIN_REQUIRED","reason":"Please sign in"}}""";

    private const string OneCueJson3 = """{"events":[{"tStartMs":267040,"dDurationMs":5520,"segs":[{"utf8":"うん。なんか"}]}]}""";

    [Fact]
    public async Task GetJapaneseCaptions_VisionOsLoginRequired_FallsBackToIos()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((LoginRequired, HttpStatusCode.OK));
        handler.Responses.Enqueue((OkWithAsrJa, HttpStatusCode.OK));
        handler.Responses.Enqueue((OneCueJson3, HttpStatusCode.OK));

        var result = await Create(handler).GetJapaneseCaptionsAsync("vid1", "visitor-data-1", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("IOS", result.ClientName);
        Assert.Equal("ja", result.LanguageCode);
        Assert.True(result.IsAutoGenerated);
        var cue = Assert.Single(result.Cues);
        Assert.Equal("うん。なんか", cue.Text);
        Assert.Equal(3, handler.Requests.Count);
        // 第二個請求是 iOS 的 player API（User-Agent 是 iOS 身分，不帶 visionOS 的 UA）
        Assert.Equal("https://www.youtube.com/youtubei/v1/player", handler.Requests[1].Uri);
        Assert.StartsWith("com.google.ios.youtube/", handler.Requests[1].UserAgent);
        // 字幕下載要在 baseUrl 後面接 &fmt=json3，並用同一個身分的 User-Agent
        Assert.Equal("https://captions.example/api?lang=ja&fmt=json3", handler.Requests[2].Uri);
        Assert.StartsWith("com.google.ios.youtube/", handler.Requests[2].UserAgent);
    }

    [Fact]
    public async Task GetJapaneseCaptions_VisionOsSucceeds_DoesNotTryIos()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((OkWithAsrJa, HttpStatusCode.OK));
        handler.Responses.Enqueue((OneCueJson3, HttpStatusCode.OK));

        var result = await Create(handler).GetJapaneseCaptionsAsync("vid1", "visitor-data-1", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("VISIONOS", result.ClientName);
        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("Mozilla/5.0 (Macintosh; Intel Mac OS X", handler.Requests[0].UserAgent);
    }

    [Fact]
    public async Task GetJapaneseCaptions_NullVisitorData_SkipsVisionOs()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((OkWithAsrJa, HttpStatusCode.OK));
        handler.Responses.Enqueue((OneCueJson3, HttpStatusCode.OK));

        var result = await Create(handler).GetJapaneseCaptionsAsync("vid1", null, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("IOS", result.ClientName);
        // 第一個請求就是 iOS（沒有先打 visionOS 的 player API）
        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("com.google.ios.youtube/", handler.Requests[0].UserAgent);
    }

    [Fact]
    public async Task GetJapaneseCaptions_BothClientsFail_ReturnsNull()
    {
        var handler = new StubHandler();
        // visionOS：HTTP 403；iOS：playabilityStatus 不是 OK
        handler.Responses.Enqueue((string.Empty, HttpStatusCode.Forbidden));
        handler.Responses.Enqueue((LoginRequired, HttpStatusCode.OK));

        var result = await Create(handler).GetJapaneseCaptionsAsync("vid1", "visitor-data-1", TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetJapaneseCaptions_NoJapaneseTrack_ReturnsNull()
    {
        var noJapanese = """
            {"playabilityStatus":{"status":"OK"},"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[
                {"baseUrl":"https://captions.example/api?lang=en","languageCode":"en"}
            ]}}}
            """;
        var handler = new StubHandler();
        handler.Responses.Enqueue((noJapanese, HttpStatusCode.OK));

        var result = await Create(handler).GetJapaneseCaptionsAsync("vid1", null, TestContext.Current.CancellationToken);

        // 有 captionTracks 但沒有日文軌：直接回 null（不再試下一個身分）
        Assert.Null(result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetJapaneseCaptions_NoCaptionTracks_TriesNextClient()
    {
        var noTracks = """{"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"vid1"}}""";
        var handler = new StubHandler();
        handler.Responses.Enqueue((noTracks, HttpStatusCode.OK));
        handler.Responses.Enqueue((OkWithAsrJa, HttpStatusCode.OK));
        handler.Responses.Enqueue((OneCueJson3, HttpStatusCode.OK));

        var result = await Create(handler).GetJapaneseCaptionsAsync("vid1", "visitor-data-1", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("IOS", result.ClientName);
        Assert.Equal(3, handler.Requests.Count);
    }

    private static JsonElement ParsePlayerResponse(string json)
    {
        var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
