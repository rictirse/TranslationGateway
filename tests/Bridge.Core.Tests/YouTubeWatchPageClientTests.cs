using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class YouTubeWatchPageClientTests
{
    /// <summary>把 JSON 包成影片頁裡的樣子：後面還有其他 script 內容，不能多吃到。</summary>
    private static string Wrap(string json) => $"<script>var ytInitialPlayerResponse = {json};var meta = 1;</script>";

    [Fact]
    public void Parse_MinimalPage_ReadsAllFieldsAndFixesChannelUrlScheme()
    {
        // 說明欄含 '{'、'}'、跳脫的 \" 與 \\"，括號配對不能被騙到
        var html = Wrap(
            """
            {"videoDetails":{"videoId":"NFJIVFSgFUs","title":"【雑談】{ つな }","author":"猫汰つな / Nekota Tsuna","channelId":"UCabc123","lengthSeconds":"5342","isLiveContent":false,"shortDescription":"說明欄有 } 括號 與 \"引號\" 還有 \\\"跳脫的反斜線引號\"","keywords":["ゲーム","雑談"]},"microformat":{"playerMicroformatRenderer":{"category":"Entertainment","ownerProfileUrl":"http://www.youtube.com/@tsuna_nekota"}},"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[{"languageCode":"ja","kind":"asr"},{"languageCode":"ja","kind":"asr"},{"languageCode":"en"}]}}}
            """);

        var info = YouTubeWatchPageClient.Parse(html);

        Assert.NotNull(info);
        Assert.Equal("NFJIVFSgFUs", info.VideoId);
        Assert.Equal("【雑談】{ つな }", info.Title);
        Assert.Equal("猫汰つな / Nekota Tsuna", info.Author);
        Assert.Equal("UCabc123", info.ChannelId);
        Assert.Equal("https://www.youtube.com/@tsuna_nekota", info.ChannelUrl);
        Assert.Equal("Entertainment", info.Category);
        Assert.Equal("說明欄有 } 括號 與 \"引號\" 還有 \\\"跳脫的反斜線引號\"", info.Description);
        Assert.Equal(["ゲーム", "雑談"], info.Keywords);
        Assert.False(info.IsLiveNow);
        Assert.False(info.IsLiveContent);
        Assert.False(info.IsUpcoming);
        Assert.Null(info.StartTime);
        Assert.Null(info.EndTime);
        Assert.Equal(5342, info.LengthSeconds);
        Assert.Equal(["ja", "en"], info.CaptionLanguages);
    }

    [Fact]
    public void Parse_LiveArchive_IsLiveNowFalseButLiveContentTrueWithTimestamps()
    {
        var html = Wrap(
            """
            {"videoDetails":{"videoId":"vod1","title":"【録画】歌枠","author":"A","channelId":"UC1","lengthSeconds":"5342","isLiveContent":true},"microformat":{"playerMicroformatRenderer":{"category":"Entertainment","ownerProfileUrl":"https://www.youtube.com/@a","liveBroadcastDetails":{"isLiveNow":false,"startTimestamp":"2026-10-17T12:02:04+00:00","endTimestamp":"2026-10-17T13:31:06+00:00"}}}}
            """);

        var info = YouTubeWatchPageClient.Parse(html);

        Assert.NotNull(info);
        Assert.False(info.IsLiveNow);
        Assert.True(info.IsLiveContent);
        Assert.False(info.IsUpcoming);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T12:02:04+00:00"), info.StartTime);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T13:31:06+00:00"), info.EndTime);
    }

    [Fact]
    public void Parse_VideoDetailsIsLive_AloneMeansLiveNow()
    {
        var html = Wrap("""{"videoDetails":{"videoId":"live1","title":"LIVE","author":"B","channelId":"UC2","isLiveContent":true,"isLive":true}}""");

        var info = YouTubeWatchPageClient.Parse(html);

        Assert.NotNull(info);
        Assert.True(info.IsLiveNow);
        Assert.True(info.IsLiveContent);
        Assert.Null(info.StartTime);
    }

    [Fact]
    public void Parse_Upcoming_IsUpcomingTrueAndNotLiveNow()
    {
        var html = Wrap("""{"videoDetails":{"videoId":"up1","title":"待機中","author":"C","channelId":"UC3","isLiveContent":true,"isUpcoming":true}}""");

        var info = YouTubeWatchPageClient.Parse(html);

        Assert.NotNull(info);
        Assert.True(info.IsUpcoming);
        Assert.False(info.IsLiveNow);
    }

    [Fact]
    public void Parse_VisitorDataInHtml_IsRead()
    {
        var html = Wrap("""{"videoDetails":{"videoId":"vod1","title":"T","author":"A","channelId":"UC1"}}""") +
            """<script>ytcfg.set({"VISITOR_DATA":"abc","INNERTUBE_CONTEXT":1});</script>""";

        var info = YouTubeWatchPageClient.Parse(html);

        Assert.NotNull(info);
        Assert.Equal("abc", info.VisitorData);
    }

    [Fact]
    public void Parse_VisitorDataMissing_IsNull()
    {
        var html = Wrap("""{"videoDetails":{"videoId":"vod1","title":"T","author":"A","channelId":"UC1"}}""");

        var info = YouTubeWatchPageClient.Parse(html);

        Assert.NotNull(info);
        Assert.Null(info.VisitorData);
    }

    [Theory]
    [InlineData("<html>no player response here</html>")]
    [InlineData("<script>var ytInitialData = {\"a\":1};</script>")]
    [InlineData("<script>var ytInitialPlayerResponse = {\"videoDetails\": ;};</script>")]
    [InlineData("<script>var ytInitialPlayerResponse = {\"a\":1</script>")]
    [InlineData("<script>var ytInitialPlayerResponse = {\"playabilityStatus\":{\"status\":\"OK\"}};</script>")]
    public void Parse_MissingOrBroken_ReturnsNull(string html)
    {
        Assert.Null(YouTubeWatchPageClient.Parse(html));
    }

    [Fact]
    public void Parse_AsrCaptionTrack_SetsAutoCaptionLanguageToItsLanguage()
    {
        var html = Wrap(
            """
            {"videoDetails":{"videoId":"v1","title":"T","author":"A","channelId":"UC1"},"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[{"languageCode":"ja"},{"languageCode":"en","kind":"asr"}]}}}
            """);

        var info = YouTubeWatchPageClient.Parse(html);

        Assert.NotNull(info);
        Assert.Equal(["ja", "en"], info.CaptionLanguages);
        Assert.Equal("en", info.AutoCaptionLanguage);
    }

    [Fact]
    public void Parse_NoAsrCaptionTrack_AutoCaptionLanguageIsNull()
    {
        var html = Wrap(
            """
            {"videoDetails":{"videoId":"v1","title":"T","author":"A","channelId":"UC1"},"captions":{"playerCaptionsTracklistRenderer":{"captionTracks":[{"languageCode":"ja"},{"languageCode":"en"}]}}}
            """);

        var info = YouTubeWatchPageClient.Parse(html);

        Assert.NotNull(info);
        Assert.Equal(["ja", "en"], info.CaptionLanguages);
        Assert.Null(info.AutoCaptionLanguage);
    }

    [Fact]
    public void Parse_NoCaptionsAtAll_AutoCaptionLanguageIsNull()
    {
        var html = Wrap("""{"videoDetails":{"videoId":"v1","title":"T","author":"A","channelId":"UC1"}}""");

        var info = YouTubeWatchPageClient.Parse(html);

        Assert.NotNull(info);
        Assert.Empty(info.CaptionLanguages);
        Assert.Null(info.AutoCaptionLanguage);
    }

    // ===== CheckPageAsync（被擋偵測）：假的 HttpMessageHandler，不連網 =====

    private const string CheckUrl = "https://www.youtube.com/watch?v=abc";

    /// <summary>正常直播頁的 ytInitialPlayerResponse（videoDetails 完整，playabilityStatus 是 OK）。</summary>
    private static string LivePageHtml => Wrap(
        """{"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"abc","title":"LIVE","author":"A","channelId":"UC1","isLiveContent":true,"isLive":true}}""");

    /// <summary>只有 playabilityStatus 的頁面（被擋頁通常長這樣：沒有 videoDetails）。</summary>
    private static string PlayabilityOnlyHtml(string status, string? reason = null)
    {
        var reasonJson = reason is null ? string.Empty : $",\"reason\":\"{reason}\"";
        return Wrap("{\"playabilityStatus\":{\"status\":\"" + status + "\"" + reasonJson + "}}");
    }

    private static YouTubeWatchPageClient CreateClient(StubHttpHandler handler) =>
        new(new HttpClient(handler), NullLogger<YouTubeWatchPageClient>.Instance);

    [Fact]
    public async Task CheckPageAsync_Http429_IsBlockedWithNullInfo()
    {
        var handler = new StubHttpHandler().On(CheckUrl, HttpStatusCode.TooManyRequests, "<html>429</html>");
        var client = CreateClient(handler);

        var check = await client.CheckPageAsync(CheckUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(check.BlockedReason);
        Assert.Contains("429", check.BlockedReason);
        Assert.Null(check.Info);
    }

    [Fact]
    public async Task CheckPageAsync_RedirectedToGoogleSorry_IsBlocked()
    {
        // 回應最後的網址是 Google 的 /sorry（機器人驗證頁）
        var handler = new StubHttpHandler().On(CheckUrl, HttpStatusCode.OK, "<html>sorry</html>",
            finalUrl: "https://www.google.com/sorry/index?continue=https://www.youtube.com/");
        var client = CreateClient(handler);

        var check = await client.CheckPageAsync(CheckUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(check.BlockedReason);
        Assert.Contains("機器人驗證", check.BlockedReason);
    }

    [Fact]
    public async Task CheckPageAsync_LoginRequiredWithBotReason_IsBlocked()
    {
        var handler = new StubHttpHandler().On(CheckUrl, HttpStatusCode.OK,
            PlayabilityOnlyHtml("LOGIN_REQUIRED", "Sign in to confirm you’re not a bot"));
        var client = CreateClient(handler);

        var check = await client.CheckPageAsync(CheckUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(check.BlockedReason);
        Assert.Contains("要求登入確認不是機器人", check.BlockedReason);
        Assert.Contains("Sign in to confirm", check.BlockedReason);
        Assert.Null(check.Info);
    }

    [Fact]
    public async Task CheckPageAsync_LoginRequiredWithOtherReason_IsNotBlocked()
    {
        // 年齡限制等其他原因的 LOGIN_REQUIRED：不算被擋，照原本行為（Info 是 null，讀不到 videoDetails）
        var handler = new StubHttpHandler().On(CheckUrl, HttpStatusCode.OK,
            PlayabilityOnlyHtml("LOGIN_REQUIRED", "This video may be inappropriate for some users."));
        var client = CreateClient(handler);

        var check = await client.CheckPageAsync(CheckUrl, TestContext.Current.CancellationToken);

        Assert.Null(check.BlockedReason);
        Assert.Null(check.Info);
    }

    [Fact]
    public async Task CheckPageAsync_NormalPage_NotBlockedAndInfoParsed()
    {
        var handler = new StubHttpHandler().On(CheckUrl, HttpStatusCode.OK, LivePageHtml);
        var client = CreateClient(handler);

        var check = await client.CheckPageAsync(CheckUrl, TestContext.Current.CancellationToken);

        Assert.Null(check.BlockedReason);
        Assert.NotNull(check.Info);
        Assert.Equal("abc", check.Info.VideoId);
        Assert.True(check.Info.IsLiveNow);
    }

    [Fact]
    public async Task CheckPageAsync_BotReasonInErrorScreen_IsBlocked()
    {
        // reason 欄位不存在、機器人確認的文字藏在 errorScreen 裡也要找得到
        var html = Wrap(
            """{"playabilityStatus":{"status":"LOGIN_REQUIRED","errorScreen":{"playerErrorCodeRenderer":{"reason":{"simpleText":"ログインしてボットではないことを確認してください"}}}}}""");
        var handler = new StubHttpHandler().On(CheckUrl, HttpStatusCode.OK, html);
        var client = CreateClient(handler);

        var check = await client.CheckPageAsync(CheckUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(check.BlockedReason);
        Assert.Contains("ログインして", check.BlockedReason);
    }

    [Fact]
    public async Task CheckPageAsync_Unplayable_NotBlockedButInfoAsUsual()
    {
        // UNPLAYABLE（例如下架）：不算被擋，記 Warning 後照常回傳（這裡讀不到 videoDetails → Info null）
        var handler = new StubHttpHandler().On(CheckUrl, HttpStatusCode.OK,
            PlayabilityOnlyHtml("UNPLAYABLE", "Video unavailable"));
        var client = CreateClient(handler);

        var check = await client.CheckPageAsync(CheckUrl, TestContext.Current.CancellationToken);

        Assert.Null(check.BlockedReason);
        Assert.Null(check.Info);
    }

    /// <summary>依 URL 回應的假 handler（不連網）；finalUrl 非 null 時把它設成回應最後的網址（模擬轉址）。</summary>
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Content, string? FinalUrl)> _routes = new(StringComparer.Ordinal);

        public StubHttpHandler On(string url, HttpStatusCode status, string content, string? finalUrl = null)
        {
            _routes[url] = (status, content, finalUrl);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, content, finalUrl) =
                _routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var route)
                    ? route
                    : (HttpStatusCode.NotFound, string.Empty, null);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content),
                RequestMessage = finalUrl is null ? request : new HttpRequestMessage(HttpMethod.Get, finalUrl)
            });
        }
    }
}
