using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>LrclibClient：PickDurationMs 的評分挑選（手寫 JSON、不連網）
/// 與 GetDurationMsAsync 的 URL 參數、錯誤處理、記憶體快取（stub handler，不連網）。</summary>
public class LrclibClientTests
{
    private static SongQuery Query(string title, string? artist = null) => new(title, artist);

    private static string ResultsJson(params string[] items) =>
        $"[{string.Join(",", items)}]";

    private static JsonElement ParseResults(params string[] items) =>
        JsonDocument.Parse(ResultsJson(items)).RootElement;

    private static string Item(double duration, string trackName, string? artistName = null, string? syncedLyrics = null) =>
        $$"""
        {
            "id": 1,
            "trackName": "{{trackName}}",
            "artistName": "{{artistName ?? ""}}",
            "duration": {{duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}},
            "syncedLyrics": {{(syncedLyrics is null ? "null" : $"\"{syncedLyrics}\"")}}
        }
        """;

    // 回傳的歌手是「TOKOTOKO(西沢さんP)」：包含查詢的歌手就 +2，曲名相等再 +2。
    [Fact]
    public void PickDurationMs_ArtistWithParentheticalSuffix_Matches()
    {
        var results = ParseResults(
            Item(210.5, "別の曲", "其他人"),
            Item(184.0, "Booo!", "TOKOTOKO(西沢さんP)", syncedLyrics: "[00:00.00]Booo!"));

        Assert.Equal(184000, LrclibClient.PickDurationMs(results, Query("Booo!", "TOKOTOKO")));
    }

    // 回傳的歌手是「初星学園; 月村手毬」：包含查詢的歌手（半形分號＋空白隔開的多位歌手）。
    [Fact]
    public void PickDurationMs_MultiArtistListing_Matches()
    {
        var results = ParseResults(Item(240.25, "Luna say maybe", "初星学園; 月村手毬"));

        // 240.25 秒 → 四捨五入成 240250 毫秒
        Assert.Equal(240250, LrclibClient.PickDurationMs(results, Query("Luna say maybe", "月村手毬")));
    }

    // duration 是 0 的項目不考慮：只剩 duration 0 時回 null。
    [Fact]
    public void PickDurationMs_ZeroDuration_Skipped()
    {
        var results = ParseResults(Item(0, "星座になれたら", "結束バンド"));

        Assert.Null(LrclibClient.PickDurationMs(results, Query("星座になれたら", "結束バンド")));
    }

    // 歌手不符、曲名只有包含（＋1）：分數 1 < 2，回 null。
    [Fact]
    public void PickDurationMs_ArtistMismatch_TrackNameOnlyContains_Null()
    {
        var results = ParseResults(Item(200, "星座になれたら (Live)", "別人"));

        Assert.Null(LrclibClient.PickDurationMs(results, Query("星座になれたら", "結束バンド")));
    }

    // 沒有歌手：曲名必須正規化後相等；只有包含不算。
    [Fact]
    public void PickDurationMs_WithoutArtist_TrackNameMustEqual()
    {
        var equal = ParseResults(Item(200, "夜明けと蛍", "n-buna"));
        Assert.Equal(200000, LrclibClient.PickDurationMs(equal, Query("夜明けと蛍")));

        var contains = ParseResults(Item(200, "夜明けと蛍 (Live)", "n-buna"));
        Assert.Null(LrclibClient.PickDurationMs(contains, Query("夜明けと蛍")));
    }

    // 同分取陣列中較前者。
    [Fact]
    public void PickDurationMs_Tie_EarlierItemWins()
    {
        var results = ParseResults(
            Item(180, "曲", "歌手A"),
            Item(200, "曲", "歌手A"));

        Assert.Equal(180000, LrclibClient.PickDurationMs(results, Query("曲", "歌手A")));
    }

    // syncedLyrics 加分：曲名相等（平手）時，有歌詞的勝過沒歌詞但較後面的。
    [Fact]
    public void PickDurationMs_SyncedLyricsBonus_BreaksTie()
    {
        var results = ParseResults(
            Item(180, "曲", "歌手", syncedLyrics: "[00:00.00]曲"),
            Item(200, "曲", "歌手"));

        Assert.Equal(180000, LrclibClient.PickDurationMs(results, Query("曲", "歌手")));
    }

    // 全形半形、大小寫、空白差異都視為相同。
    [Fact]
    public void PickDurationMs_Normalization_FullWidthCaseWhitespace()
    {
        var results = ParseResults(Item(200, "Ｌｕｎａ  Ｓａｙ　Ｍａｙｂｅ", "月村 手毬"));

        Assert.Equal(200000, LrclibClient.PickDurationMs(results, Query("luna say maybe", "月村手毬")));
    }

    // 空陣列、非陣列：回 null。
    [Fact]
    public void PickDurationMs_EmptyOrNotArray_Null()
    {
        Assert.Null(LrclibClient.PickDurationMs(ParseResults(), Query("曲", "歌手")));
        Assert.Null(LrclibClient.PickDurationMs(JsonDocument.Parse("""{"error":"x"}""").RootElement, Query("曲", "歌手")));
    }

    // 有歌手：URL 用 track_name＋artist_name；User-Agent 是 TranslationGateway。
    [Fact]
    public async Task GetDurationMsAsync_WithArtist_UsesTrackAndArtistParams()
    {
        var h = new Harness(ResultsJson(Item(200, "Booo!", "TOKOTOKO(西沢さんP)")));

        var duration = await h.Sut.GetDurationMsAsync(Query("Booo!", "TOKOTOKO"), TestContext.Current.CancellationToken);

        Assert.Equal(200000, duration);
        Assert.Equal(1, h.Handler.CallCount);
        var uri = h.Handler.LastRequestUri;
        Assert.StartsWith("https://lrclib.net/api/search?", uri);
        Assert.Contains("track_name=Booo%21", uri);
        Assert.Contains("artist_name=TOKOTOKO", uri);
        Assert.DoesNotContain("q=", uri);
        Assert.Equal("TranslationGateway (https://github.com/rictirse/TranslationGateway)",
            h.Handler.LastRequest?.Headers.UserAgent.ToString());
    }

    // 沒有歌手：URL 用 q。
    [Fact]
    public async Task GetDurationMsAsync_WithoutArtist_UsesQueryParam()
    {
        var h = new Harness(ResultsJson(Item(200, "夜明けと蛍", "n-buna")));

        var duration = await h.Sut.GetDurationMsAsync(Query("夜明けと蛍"), TestContext.Current.CancellationToken);

        Assert.Equal(200000, duration);
        var uri = h.Handler.LastRequestUri;
        Assert.Contains("q=", uri);
        Assert.DoesNotContain("track_name=", uri);
        Assert.DoesNotContain("artist_name=", uri);
    }

    // 曲名需要 URL 編碼（空白與全形斜線）。
    [Fact]
    public async Task GetDurationMsAsync_TitleNeedsEscaping_Encoded()
    {
        var h = new Harness(ResultsJson(Item(200, "回る空うさぎ", "Orangestar")));

        await h.Sut.GetDurationMsAsync(Query("回る空うさぎ ／ Orangestar"), TestContext.Current.CancellationToken);

        Assert.Contains("q=%E5%9B%9E%E3%82%8B%E7%A9%BA%E3%81%86%E3%81%95%E3%81%8E%20%EF%BC%8F%20Orangestar", h.Handler.LastRequestUri);
    }

    // 5xx 一直失敗：重試完回 null，而且不快取（下次再查會再打）。
    [Fact]
    public async Task GetDurationMsAsync_ServerErrorEveryTime_ReturnsNull_NotCached()
    {
        var h = new Harness("{}", System.Net.HttpStatusCode.InternalServerError);

        var duration = await h.Sut.GetDurationMsAsync(Query("Booo!", "TOKOTOKO"), TestContext.Current.CancellationToken);

        Assert.Null(duration);
        Assert.Equal(3, h.Handler.CallCount);

        // 暫時性失敗不快取：再查一次會重新打
        await h.Sut.GetDurationMsAsync(Query("Booo!", "TOKOTOKO"), TestContext.Current.CancellationToken);
        Assert.Equal(6, h.Handler.CallCount);
    }

    // 503（LRCLIB 忙碌，2026-10-26 實測常見）後重試成功：回長度並快取。
    [Fact]
    public async Task GetDurationMsAsync_BusyThenOk_RetriesAndCaches()
    {
        var h = new Harness(ResultsJson(Item(174, "Booo!", "TOKOTOKO(西沢さんP)")),
            System.Net.HttpStatusCode.ServiceUnavailable, System.Net.HttpStatusCode.OK);

        var duration = await h.Sut.GetDurationMsAsync(Query("Booo!", "TOKOTOKO"), TestContext.Current.CancellationToken);

        Assert.Equal(174_000, duration);
        Assert.Equal(2, h.Handler.CallCount);
        await h.Sut.GetDurationMsAsync(Query("Booo!", "TOKOTOKO"), TestContext.Current.CancellationToken);
        Assert.Equal(2, h.Handler.CallCount);
    }

    // 429 也算暫時性：重試。
    [Fact]
    public async Task GetDurationMsAsync_TooManyRequests_Retried()
    {
        var h = new Harness(ResultsJson(Item(174, "Booo!", "TOKOTOKO")),
            System.Net.HttpStatusCode.TooManyRequests, System.Net.HttpStatusCode.OK);

        Assert.Equal(174_000, await h.Sut.GetDurationMsAsync(Query("Booo!", "TOKOTOKO"), TestContext.Current.CancellationToken));
        Assert.Equal(2, h.Handler.CallCount);
    }

    // 其他 4xx（例如 404）：視為查不到，不重試、快取。
    [Fact]
    public async Task GetDurationMsAsync_NotFound_NullCachedWithoutRetry()
    {
        var h = new Harness("{}", System.Net.HttpStatusCode.NotFound);

        Assert.Null(await h.Sut.GetDurationMsAsync(Query("原創曲", "某人"), TestContext.Current.CancellationToken));
        Assert.Null(await h.Sut.GetDurationMsAsync(Query("原創曲", "某人"), TestContext.Current.CancellationToken));
        Assert.Equal(1, h.Handler.CallCount);
    }

    // JSON 格式錯誤：回 null。
    [Fact]
    public async Task GetDurationMsAsync_InvalidJson_ReturnsNull()
    {
        var h = new Harness("not json");

        Assert.Null(await h.Sut.GetDurationMsAsync(Query("Booo!", "TOKOTOKO"), TestContext.Current.CancellationToken));
    }

    // 快取：同一個（曲名、歌手）只打一次（含查不到的結果）。
    [Fact]
    public async Task GetDurationMsAsync_SameQuery_Cached()
    {
        var h = new Harness(ResultsJson(Item(200, "Booo!", "TOKOTOKO(西沢さんP)")));

        await h.Sut.GetDurationMsAsync(Query("Booo!", "TOKOTOKO"), TestContext.Current.CancellationToken);
        await h.Sut.GetDurationMsAsync(Query("Booo!", "TOKOTOKO"), TestContext.Current.CancellationToken);
        Assert.Equal(1, h.Handler.CallCount);

        // 查不到的結果（null）也快取：不再重打
        await h.Sut.GetDurationMsAsync(Query("原創曲", "某人"), TestContext.Current.CancellationToken);
        await h.Sut.GetDurationMsAsync(Query("原創曲", "某人"), TestContext.Current.CancellationToken);
        Assert.Equal(2, h.Handler.CallCount);
    }

    private sealed class Harness
    {
        public StubHandler Handler { get; }

        public LrclibClient Sut { get; }

        /// <summary>statusCodes 依序回應，用完後一直用最後一個；沒給時一律 200。重試不等待。</summary>
        public Harness(string responseBody, params System.Net.HttpStatusCode[] statusCodes)
        {
            Handler = new StubHandler(responseBody, statusCodes.Length > 0 ? statusCodes : [System.Net.HttpStatusCode.OK]);
            Sut = new LrclibClient(new HttpClient(Handler), NullLogger<LrclibClient>.Instance)
            {
                RetryDelays = [TimeSpan.Zero, TimeSpan.Zero]
            };
        }
    }

    /// <summary>攔截請求並回傳固定回應的假 handler（不連網）。</summary>
    private sealed class StubHandler(string response, System.Net.HttpStatusCode[] statusCodes) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public int CallCount { get; private set; }

        public HttpRequestMessage? LastRequest => Requests.Count > 0 ? Requests[^1] : null;

        // AbsoluteUri 保留百分號編碼（ToString 會把 %XX 解回 Unicode）
        public string LastRequestUri => LastRequest?.RequestUri?.AbsoluteUri ?? "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var statusCode = statusCodes[Math.Min(CallCount, statusCodes.Length - 1)];
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
