using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>可以控制 HasExited 並記錄 KillTree 次數的假背景程序。</summary>
internal sealed class FakeStartedProcess : IStartedProcess
{
    public bool HasExited { get; set; }

    public int KillTreeCount { get; private set; }

    public void KillTree() => KillTreeCount++;
}

/// <summary>記錄呼叫並回傳預設結果的假行程執行器；不真的執行任何程式。</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public HashSet<string> ExistingFiles { get; } = new();

    /// <summary>RunAsync 依序 dequeue 的結果；空時回傳 ExitCode 0 的空結果。</summary>
    public Queue<ProcessResult> Results { get; } = new();

    public List<(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout)> Runs { get; } = new();

    public List<(string FileName, IReadOnlyList<string> Arguments)> Starts { get; } = new();

    /// <summary>非 null 時把每次呼叫（"run"／"start"）記進共享清單，用來驗證呼叫順序。</summary>
    public List<string>? CallLog { get; set; }

    /// <summary>Start 依序回傳的假程序；測試用它控制 HasExited 與檢查 KillTree。</summary>
    public List<FakeStartedProcess> StartedProcesses { get; } = new();

    public bool FileExists(string path) => ExistingFiles.Contains(path);

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Runs.Add((fileName, arguments, timeout));
        CallLog?.Add("run");
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new ProcessResult(0, string.Empty, string.Empty, false));
    }

    public IStartedProcess Start(string fileName, IReadOnlyList<string> arguments, Action<string> onOutput)
    {
        Starts.Add((fileName, arguments));
        CallLog?.Add("start");
        var process = new FakeStartedProcess();
        StartedProcesses.Add(process);
        return process;
    }
}

/// <summary>假的影片資訊查詢：記錄查詢網址並依序回傳排好的結果（沒排時回 null）。</summary>
internal sealed class FakeVideoInfoProvider : IVideoInfoProvider
{
    public Queue<StreamMetadata?> Results { get; } = new();

    public List<string> QueriedUrls { get; } = new();

    public Task<StreamMetadata?> GetYouTubeInfoAsync(string url, CancellationToken cancellationToken = default)
    {
        QueriedUrls.Add(url);
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : null);
    }
}

/// <summary>假的影片頁面查詢：記錄查詢網址並依序回傳排好的結果（沒排時回 null）。</summary>
internal sealed class FakePageInfoProvider : IYouTubePageInfoProvider
{
    public Queue<YouTubePageInfo?> Results { get; } = new();

    public List<string> QueriedUrls { get; } = new();

    public Task<YouTubePageInfo?> GetPageInfoAsync(string url, CancellationToken cancellationToken = default)
    {
        QueriedUrls.Add(url);
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : null);
    }
}

/// <summary>假的影片頁面查詢（含被擋偵測）：CheckPageAsync 依序回傳排好的結果（沒排時回 null、不被擋）。</summary>
internal sealed class FakePageCheckProvider : IYouTubePageInfoProvider
{
    public Queue<YouTubePageCheck> Results { get; } = new();

    public List<string> QueriedUrls { get; } = new();

    public Task<YouTubePageInfo?> GetPageInfoAsync(string url, CancellationToken cancellationToken = default) =>
        Task.FromResult<YouTubePageInfo?>(null);

    public Task<YouTubePageCheck> CheckPageAsync(string url, CancellationToken cancellationToken = default)
    {
        QueriedUrls.Add(url);
        return Task.FromResult(Results.Count > 0
            ? Results.Dequeue()
            : new YouTubePageCheck(null, null));
    }
}

public class StreamlinkLauncherTests
{
    private const string SuccessJson =
        """
        {"plugin":"twitch","metadata":{"id":"12345","author":"Usada Pekora","category":null,"title":"Minecraft"},"streams":{"best":{"url":"hls://example"}}}
        """;

    private static (StreamlinkLauncher Launcher, FakeProcessRunner Runner, FakeVideoInfoProvider VideoInfo) Create(StreamlinkSettings? config = null, IYouTubePageInfoProvider? pageInfo = null)
    {
        var settings = new FakeSettingsService();
        if (config is not null)
        {
            settings.Current.Streamlink = config;
        }

        var runner = new FakeProcessRunner();
        runner.ExistingFiles.Add(settings.Current.Streamlink.StreamlinkPath);
        runner.ExistingFiles.Add(settings.Current.Streamlink.PlayerPath);

        var videoInfo = new FakeVideoInfoProvider();
        var launcher = new StreamlinkLauncher(runner, settings, videoInfo, NullLogger<StreamlinkLauncher>.Instance, pageInfo);
        return (launcher, runner, videoInfo);
    }

    // ===== ParseJson =====

    [Fact]
    public void ParseJson_SuccessFormat_ReturnsMetadataWithGivenPlatform()
    {
        var (metadata, error) = StreamlinkLauncher.ParseJson(SuccessJson, StreamPlatform.Twitch);

        Assert.Null(error);
        Assert.NotNull(metadata);
        Assert.Equal(StreamPlatform.Twitch, metadata.Platform);
        Assert.Equal("12345", metadata.Id);
        Assert.Equal("Usada Pekora", metadata.Author);
        Assert.Equal("Minecraft", metadata.Title);
    }

    [Fact]
    public void ParseJson_NullFields_MapToNull()
    {
        var json = """{"plugin":"youtube","metadata":{"id":null,"author":"someone","category":null,"title":"anything"},"streams":{}}""";

        var (metadata, error) = StreamlinkLauncher.ParseJson(json, StreamPlatform.YouTube);

        Assert.Null(error);
        Assert.NotNull(metadata);
        Assert.Null(metadata.Id);
        Assert.Equal("someone", metadata.Author);
        Assert.Null(metadata.Category);
    }

    [Fact]
    public void ParseJson_MissingMetadata_AllFieldsNull()
    {
        var (metadata, error) = StreamlinkLauncher.ParseJson("""{"plugin":"youtube","streams":{}}""", StreamPlatform.YouTube);

        Assert.Null(error);
        Assert.NotNull(metadata);
        Assert.Equal(StreamPlatform.YouTube, metadata.Platform);
        Assert.Null(metadata.Id);
        Assert.Null(metadata.Author);
        Assert.Null(metadata.Title);
    }

    [Fact]
    public void ParseJson_ErrorFormat_ReturnsErrorString()
    {
        var (metadata, error) = StreamlinkLauncher.ParseJson(
            """{"error":"No playable streams found on this URL: https://twitch.tv/x"}""",
            StreamPlatform.Twitch);

        Assert.Null(metadata);
        Assert.Equal("No playable streams found on this URL: https://twitch.tv/x", error);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseJson_NotJson_ReturnsUnparsableMessage(string json)
    {
        var (metadata, error) = StreamlinkLauncher.ParseJson(json, StreamPlatform.YouTube);

        Assert.Null(metadata);
        Assert.Equal("無法解析 streamlink 的輸出", error);
    }

    // ===== LaunchAsync =====

    [Theory]
    [InlineData(null)]
    [InlineData("hello")]
    public async Task LaunchAsync_InvalidClipboard_FailsWithoutCallingRunner(string? clipboardText)
    {
        var (launcher, runner, _) = Create();

        var result = await launcher.LaunchAsync(clipboardText, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("剪貼簿裡沒有 YouTube 或 Twitch 的網址", result.Message);
        Assert.Empty(runner.Runs);
        Assert.Empty(runner.Starts);
    }

    [Fact]
    public async Task LaunchAsync_StreamlinkMissing_FailsWithPathInMessage()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, _) = Create(config);
        runner.ExistingFiles.Remove(config.StreamlinkPath);

        var result = await launcher.LaunchAsync("https://www.twitch.tv/someone", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(config.StreamlinkPath, result.Message);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task LaunchAsync_PlayerMissing_FailsWithPathInMessage()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, _) = Create(config);
        runner.ExistingFiles.Remove(config.PlayerPath);

        var result = await launcher.LaunchAsync("https://www.twitch.tv/someone", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(config.PlayerPath, result.Message);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task LaunchAsync_QueryTimesOut_FailsWithoutStart()
    {
        var (launcher, runner, _) = Create();
        runner.Results.Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var result = await launcher.LaunchAsync("https://www.twitch.tv/someone", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("streamlink 查詢逾時，請稍後再試", result.Message);
        Assert.Empty(runner.Starts);
    }

    [Fact]
    public async Task LaunchAsync_QueryReturnsErrorJson_FailsWithErrorAndNoStart()
    {
        var (launcher, runner, _) = Create();
        runner.Results.Enqueue(new ProcessResult(1, """{"error":"No playable streams found on this URL: x"}""", string.Empty, false));

        var result = await launcher.LaunchAsync("https://www.twitch.tv/someone", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("No playable streams found", result.Message);
        Assert.Empty(runner.Starts);
    }

    [Fact]
    public async Task LaunchAsync_YouTubeErrorJson_StartsPlayerWithUrlOnlyAndOEmbedMetadata()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, videoInfo) = Create(config);
        runner.Results.Enqueue(new ProcessResult(1, """{"error":"No playable streams found on this URL: x"}""", string.Empty, false));
        videoInfo.Results.Enqueue(new StreamMetadata(
            StreamPlatform.YouTube,
            null,
            "Lisa Ch. 花鏡都",
            null,
            "【錄影】歌うよ",
            ChannelUrl: "https://www.youtube.com/@lisahanabusa",
            IsVod: true));
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("已開啟錄影：Lisa Ch. 花鏡都 - 【錄影】歌うよ", result.Message);
        Assert.Contains("改用 PotPlayer 直接播放", result.Message);
        Assert.NotNull(result.Metadata);
        Assert.Equal("Lisa Ch. 花鏡都", result.Metadata.Author);
        Assert.Equal("【錄影】歌うよ", result.Metadata.Title);
        Assert.Equal("https://www.youtube.com/@lisahanabusa", result.Metadata.ChannelUrl);
        Assert.True(result.Metadata.IsVod);

        // 不經 streamlink：直接用 PlayerPath 開網址，參數只有網址
        Assert.Equal([url], Assert.Single(runner.Starts).Arguments);
        Assert.Equal(config.PlayerPath, Assert.Single(runner.Starts).FileName);
        Assert.Equal(url, Assert.Single(videoInfo.QueriedUrls));

        Assert.True(launcher.IsRunning);
        Assert.Contains("Lisa Ch. 花鏡都", launcher.CurrentDescription);
    }

    [Fact]
    public async Task LaunchAsync_YouTubeErrorJson_OEmbedUnavailable_StillStartsWithVodMetadata()
    {
        var (launcher, runner, videoInfo) = Create();
        runner.Results.Enqueue(new ProcessResult(1, """{"error":"No playable streams found on this URL: x"}""", string.Empty, false));
        videoInfo.Results.Enqueue(null);
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.StartsWith("已開啟錄影", result.Message);
        Assert.Contains("改用 PotPlayer 直接播放", result.Message);
        Assert.NotNull(result.Metadata);
        Assert.Equal(StreamPlatform.YouTube, result.Metadata.Platform);
        Assert.True(result.Metadata.IsVod);
        Assert.Null(result.Metadata.Author);

        Assert.Single(runner.Starts);
        Assert.True(launcher.IsRunning);
        // 沒有中繼資料時說明文字退回網址
        Assert.Equal(url, launcher.CurrentDescription);
    }

    [Fact]
    public async Task LaunchAsync_YouTubeNotJsonWithNonZeroExit_StartsPlayerDirectly()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, videoInfo) = Create(config);
        runner.Results.Enqueue(new ProcessResult(1, "Traceback (most recent call last):", "error: No plugin found", false));
        videoInfo.Results.Enqueue(new StreamMetadata(StreamPlatform.YouTube, null, "Someone", null, "VOD", IsVod: true));
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal([url], Assert.Single(runner.Starts).Arguments);
        Assert.Equal(config.PlayerPath, Assert.Single(runner.Starts).FileName);
    }

    [Fact]
    public async Task LaunchAsync_YouTubeQueryTimesOut_FailsWithoutStart()
    {
        var (launcher, runner, videoInfo) = Create();
        runner.Results.Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var result = await launcher.LaunchAsync("https://www.youtube.com/watch?v=abc", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("streamlink 查詢逾時，請稍後再試", result.Message);
        Assert.Empty(runner.Starts);
        Assert.Empty(videoInfo.QueriedUrls);
    }

    [Fact]
    public async Task LaunchAsync_YouTubeHttpOnlyStreams_OpensPlayerDirectlyAsVod()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, videoInfo) = Create(config);
        var json =
            """
            {"plugin":"youtube","metadata":{"id":"abc","author":"Someone","category":null,"title":"VOD"},"streams":{"360p":{"type":"http","url":"http://360p"},"worst":{"type":"muxed-stream","url":"http://144p"},"best":{"type":"http","url":"http://360p"}}}
            """;
        runner.Results.Enqueue(new ProcessResult(0, json, string.Empty, false));
        videoInfo.Results.Enqueue(new StreamMetadata(StreamPlatform.YouTube, null, "Someone", null, "VOD", IsVod: true));
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("偵測為錄影", result.Message);
        Assert.NotNull(result.Metadata);
        Assert.True(result.Metadata.IsVod);

        // 沒有啟動 streamlink 播放：直接用 PlayerPath 開網址，參數就是網址
        var start = Assert.Single(runner.Starts);
        Assert.Equal(config.PlayerPath, start.FileName);
        Assert.Equal([url], start.Arguments);
        Assert.Equal(url, Assert.Single(videoInfo.QueriedUrls));
    }

    [Fact]
    public async Task LaunchAsync_YouTubeWithHlsStream_StartsStreamlinkWithPlayer()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, videoInfo) = Create(config);
        var json =
            """
            {"plugin":"youtube","metadata":{"id":"abc","author":"Someone","category":null,"title":"LIVE"},"streams":{"1080p60 (worst)":{"type":"hls","url":"https://hls/example"},"best":{"type":"hls","url":"https://hls/example"}}}
            """;
        runner.Results.Enqueue(new ProcessResult(0, json, string.Empty, false));
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.Metadata?.IsVod);

        var start = Assert.Single(runner.Starts);
        Assert.Equal(config.StreamlinkPath, start.FileName);
        Assert.Contains("--player", start.Arguments);
        Assert.Empty(videoInfo.QueriedUrls);
    }

    [Fact]
    public async Task LaunchAsync_YouTubeStreamsWithoutType_StartsStreamlink()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, videoInfo) = Create(config);
        var json =
            """
            {"plugin":"youtube","metadata":{"id":"abc","author":"Someone","category":null,"title":"LIVE"},"streams":{"best":{"url":"https://example"}}}
            """;
        runner.Results.Enqueue(new ProcessResult(0, json, string.Empty, false));
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);

        var start = Assert.Single(runner.Starts);
        Assert.Equal(config.StreamlinkPath, start.FileName);
        Assert.Contains("--player", start.Arguments);
        Assert.Empty(videoInfo.QueriedUrls);
    }

    [Fact]
    public async Task LaunchAsync_TwitchHttpOnlyStreams_StartsStreamlink()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, videoInfo) = Create(config);
        var json =
            """
            {"plugin":"twitch","metadata":{"id":"12345","author":"Usada Pekora","category":null,"title":"Minecraft"},"streams":{"360p":{"type":"http","url":"http://360p"},"best":{"type":"http","url":"http://360p"}}}
            """;
        runner.Results.Enqueue(new ProcessResult(0, json, string.Empty, false));
        var url = "https://www.twitch.tv/someone";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);

        var start = Assert.Single(runner.Starts);
        Assert.Equal(config.StreamlinkPath, start.FileName);
        Assert.Contains("--player", start.Arguments);
        Assert.Empty(videoInfo.QueriedUrls);
    }

    [Fact]
    public async Task LaunchAsync_QueryOk_StartsStreamlinkWithJsonQueryAndPlayer()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, _) = Create(config);
        runner.Results.Enqueue(new ProcessResult(0, SuccessJson, string.Empty, false));
        var url = "https://www.twitch.tv/someone";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("已開啟：Usada Pekora - Minecraft", result.Message);
        Assert.NotNull(result.Metadata);
        Assert.Equal("Usada Pekora", result.Metadata.Author);

        var query = Assert.Single(runner.Runs);
        Assert.Equal("/fake/streamlink.exe", query.FileName);
        Assert.Equal(["--json", url], query.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(30), query.Timeout);

        var start = Assert.Single(runner.Starts);
        Assert.Equal("/fake/streamlink.exe", start.FileName);
        Assert.Equal([url, "best", "--player", "/fake/player.exe"], start.Arguments);
    }

    [Fact]
    public async Task LaunchAsync_BlankQuality_UsesBest()
    {
        var config = new StreamlinkSettings { Quality = "   " };
        var (launcher, runner, _) = Create(config);
        runner.Results.Enqueue(new ProcessResult(0, SuccessJson, string.Empty, false));
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal([url, "best", "--player", config.PlayerPath], Assert.Single(runner.Starts).Arguments);
    }

    [Fact]
    public async Task LaunchAsync_NotJsonButExitZero_StillStartsWithoutMetadata()
    {
        var (launcher, runner, _) = Create();
        runner.Results.Enqueue(new ProcessResult(0, "streamlink 5.0.0", string.Empty, false));
        var url = "https://www.twitch.tv/someone";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("已開啟直播", result.Message);
        Assert.Null(result.Metadata);
        Assert.Single(runner.Starts);
        // 沒有中繼資料時說明文字退回網址
        Assert.True(launcher.IsRunning);
        Assert.Equal(url, launcher.CurrentDescription);
    }

    // ===== IsRunning／CurrentDescription／StopCurrent =====

    [Fact]
    public async Task LaunchAsync_Success_IsRunningWithAuthorAndTitleDescription()
    {
        var (launcher, runner, _) = Create();
        runner.Results.Enqueue(new ProcessResult(0, SuccessJson, string.Empty, false));

        await launcher.LaunchAsync("https://www.twitch.tv/someone", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(launcher.IsRunning);
        Assert.NotNull(launcher.CurrentDescription);
        Assert.Contains("Usada Pekora", launcher.CurrentDescription);
        Assert.Contains("Minecraft", launcher.CurrentDescription);
    }

    [Fact]
    public async Task IsRunning_AfterProcessExits_IsFalseAndDescriptionNull()
    {
        var (launcher, runner, _) = Create();
        runner.Results.Enqueue(new ProcessResult(0, SuccessJson, string.Empty, false));
        await launcher.LaunchAsync("https://www.twitch.tv/someone", cancellationToken: TestContext.Current.CancellationToken);

        runner.StartedProcesses.Single().HasExited = true;

        Assert.False(launcher.IsRunning);
        Assert.Null(launcher.CurrentDescription);
    }

    [Fact]
    public async Task StopCurrent_KillsTreeOnceAndClearsRecord()
    {
        var (launcher, runner, _) = Create();
        runner.Results.Enqueue(new ProcessResult(0, SuccessJson, string.Empty, false));
        await launcher.LaunchAsync("https://www.twitch.tv/someone", cancellationToken: TestContext.Current.CancellationToken);

        launcher.StopCurrent();

        Assert.False(launcher.IsRunning);
        Assert.Null(launcher.CurrentDescription);
        Assert.Equal(1, runner.StartedProcesses.Single().KillTreeCount);

        // 沒有記錄時再呼叫不擲例外、也不再砍
        launcher.StopCurrent();
        Assert.Equal(1, runner.StartedProcesses.Single().KillTreeCount);
    }

    [Fact]
    public async Task StopCurrent_WithoutAnyRecord_DoesNotThrow()
    {
        var (launcher, runner, _) = Create();

        launcher.StopCurrent();

        Assert.False(launcher.IsRunning);
        Assert.Empty(runner.Starts);
    }

    [Fact]
    public async Task LaunchAsync_Failure_DoesNotOverwritePreviousRecord()
    {
        var (launcher, runner, _) = Create();
        runner.Results.Enqueue(new ProcessResult(0, SuccessJson, string.Empty, false));
        await launcher.LaunchAsync("https://www.twitch.tv/someone", cancellationToken: TestContext.Current.CancellationToken);

        // 上一場還在播時開台失敗（查詢逾時）：記錄不得被覆蓋
        runner.Results.Enqueue(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));
        var result = await launcher.LaunchAsync("https://www.twitch.tv/other", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(launcher.IsRunning);
        Assert.NotNull(launcher.CurrentDescription);
        Assert.Contains("Usada Pekora", launcher.CurrentDescription);
        // 只有一次成功的 Start
        Assert.Single(runner.Starts);
    }

    // ===== YouTube 影片頁面（IYouTubePageInfoProvider）=====

    /// <summary>組一個可調整直播狀態的影片頁面資訊。</summary>
    private static YouTubePageInfo Page(
        bool isLiveNow = false,
        bool isUpcoming = false,
        bool isLiveContent = false,
        string videoId = "pg1",
        string title = "ページのタイトル",
        string author = "Page Author",
        string? channelUrl = "https://www.youtube.com/@page",
        string category = "Gaming",
        string description = "說明欄文字",
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        IReadOnlyList<string>? captionLanguages = null,
        string? autoCaptionLanguage = null) =>
        new(videoId, title, author, "UCpg", channelUrl, category, description, [],
            isLiveNow, isLiveContent, isUpcoming, startTime, endTime, 600,
            captionLanguages ?? [], AutoCaptionLanguage: autoCaptionLanguage);

    [Fact]
    public async Task LaunchAsync_PageSaysVod_OpensPlayerDirectlyWithoutStreamlinkOrOEmbed()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var pageInfo = new FakePageInfoProvider();
        pageInfo.Results.Enqueue(Page());
        var (launcher, runner, videoInfo) = Create(config, pageInfo);
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("已開啟錄影：Page Author - ページのタイトル", result.Message);
        Assert.Contains("不是直播中", result.Message);
        Assert.NotNull(result.Metadata);
        Assert.True(result.Metadata.IsVod);
        Assert.Equal("說明欄文字", result.Metadata.Description);
        Assert.Equal("https://www.youtube.com/@page", result.Metadata.ChannelUrl);

        // 不查 streamlink（沒有 --json）、不查 oEmbed，直接用 PlayerPath 開網址
        Assert.Empty(runner.Runs);
        Assert.Empty(videoInfo.QueriedUrls);
        var start = Assert.Single(runner.Starts);
        Assert.Equal(config.PlayerPath, start.FileName);
        Assert.Equal([url], start.Arguments);
    }

    [Fact]
    public async Task LaunchAsync_PageSaysLive_StartsStreamlinkAndEnrichesMetadataFromPage()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var pageInfo = new FakePageInfoProvider();
        pageInfo.Results.Enqueue(Page(isLiveNow: true, isLiveContent: true));
        var (launcher, runner, _) = Create(config, pageInfo);
        var json =
            """
            {"plugin":"youtube","metadata":{"id":"abc","author":"Someone","category":null,"title":"LIVE"},"streams":{"best":{"type":"hls","url":"https://hls/example"}}}
            """;
        runner.Results.Enqueue(new ProcessResult(0, json, string.Empty, false));
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.Metadata?.IsVod);
        Assert.Equal("說明欄文字", result.Metadata?.Description);
        Assert.Equal("https://www.youtube.com/@page", result.Metadata?.ChannelUrl);
        Assert.Equal("Someone", result.Metadata?.Author);
        Assert.Equal("LIVE", result.Metadata?.Title);

        var start = Assert.Single(runner.Starts);
        Assert.Equal(config.StreamlinkPath, start.FileName);
        Assert.Contains("--player", start.Arguments);
    }

    [Fact]
    public async Task LaunchAsync_PageSaysUpcoming_FailsWithoutStartingAnything()
    {
        var pageInfo = new FakePageInfoProvider();
        pageInfo.Results.Enqueue(Page(isUpcoming: true));
        var (launcher, runner, _) = Create(pageInfo: pageInfo);
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("待機室", result.Message);
        Assert.Null(result.Metadata);
        Assert.Empty(runner.Runs);
        Assert.Empty(runner.Starts);
    }

    [Fact]
    public async Task LaunchAsync_PageInfoNull_FallsBackToStreamlinkWithHlsDetection()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var pageInfo = new FakePageInfoProvider();
        pageInfo.Results.Enqueue(null);
        var (launcher, runner, videoInfo) = Create(config, pageInfo);
        var json =
            """
            {"plugin":"youtube","metadata":{"id":"abc","author":"Someone","category":null,"title":"VOD"},"streams":{"best":{"type":"http","url":"http://360p"}}}
            """;
        runner.Results.Enqueue(new ProcessResult(0, json, string.Empty, false));
        videoInfo.Results.Enqueue(new StreamMetadata(StreamPlatform.YouTube, null, "Someone", null, "VOD", IsVod: true));
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("偵測為錄影", result.Message);
        Assert.True(result.Metadata?.IsVod);
        Assert.Equal(url, Assert.Single(videoInfo.QueriedUrls));

        // 退回 streamlink 判斷：有跑 --json 查詢，之後直接用 PlayerPath 開
        Assert.Equal(["--json", url], Assert.Single(runner.Runs).Arguments);
        var start = Assert.Single(runner.Starts);
        Assert.Equal(config.PlayerPath, start.FileName);
        Assert.Equal([url], start.Arguments);
    }

    [Fact]
    public async Task LaunchAsync_PageSaysVod_CarriesVideoStartAndEndTimesInMetadata()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var pageInfo = new FakePageInfoProvider();
        var started = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(9));
        pageInfo.Results.Enqueue(Page(startTime: started, endTime: started.AddHours(3)));
        var (launcher, runner, videoInfo) = Create(config, pageInfo);
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Metadata);
        Assert.True(result.Metadata.IsVod);
        Assert.Equal(started, result.Metadata.StartTime);
        Assert.Equal(started.AddHours(3), result.Metadata.EndTime);
        Assert.Empty(videoInfo.QueriedUrls);
    }

    [Fact]
    public async Task LaunchAsync_PageSaysLive_EnrichesMetadataWithVideoTimes()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var pageInfo = new FakePageInfoProvider();
        var started = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(9));
        pageInfo.Results.Enqueue(Page(isLiveNow: true, isLiveContent: true, startTime: started, endTime: started.AddHours(5)));
        var (launcher, runner, _) = Create(config, pageInfo);
        var json =
            """
            {"plugin":"youtube","metadata":{"id":"abc","author":"Someone","category":null,"title":"LIVE"},"streams":{"best":{"type":"hls","url":"https://hls/example"}}}
            """;
        runner.Results.Enqueue(new ProcessResult(0, json, string.Empty, false));

        var result = await launcher.LaunchAsync("https://www.youtube.com/watch?v=abc", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Metadata);
        Assert.False(result.Metadata.IsVod);
        // streamlink 查不到起訖時間：補上頁面的
        Assert.Equal(started, result.Metadata.StartTime);
        Assert.Equal(started.AddHours(5), result.Metadata.EndTime);
    }

    [Fact]
    public async Task LaunchAsync_Twitch_DoesNotCallPageInfo()
    {
        var pageInfo = new FakePageInfoProvider();
        var (launcher, runner, _) = Create(pageInfo: pageInfo);
        runner.Results.Enqueue(new ProcessResult(0, SuccessJson, string.Empty, false));
        var url = "https://www.twitch.tv/someone";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(pageInfo.QueriedUrls);
        Assert.Single(runner.Starts);
    }

    // ===== 被 YouTube 擋下（匿名存取次數太多）=====

    [Fact]
    public async Task LaunchAsync_PageBlocked_ReturnsBlockedWithoutRunnerOrHook()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var pageInfo = new FakePageCheckProvider();
        pageInfo.Results.Enqueue(new YouTubePageCheck(null, "YouTube 要求登入確認不是機器人"));
        var (launcher, runner, _) = Create(config, pageInfo);
        var calls = new List<string>();
        runner.CallLog = calls;
        var hook = new RecordingHook(calls);
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, hook.RunAsync, cancellationToken: TestContext.Current.CancellationToken);

        // 被擋：不跑 streamlink、不啟動播放器、不呼叫掛勾
        Assert.True(result.Blocked);
        Assert.False(result.Success);
        Assert.Equal("YouTube 要求登入確認不是機器人", result.BlockedReason);
        Assert.Contains("無法開啟", result.Message);
        Assert.Contains("YouTube 要求登入確認不是機器人", result.Message);
        Assert.Equal(url, Assert.Single(pageInfo.QueriedUrls));
        Assert.Empty(runner.Runs);
        Assert.Empty(runner.Starts);
        Assert.DoesNotContain("hook", calls);
    }

    [Fact]
    public async Task LaunchAsync_StreamlinkErrorSaysSignInToConfirm_ReturnsBlockedWithoutVodFallback()
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, videoInfo) = Create(config);
        runner.Results.Enqueue(new ProcessResult(1,
            """{"error":"Sign in to confirm you're not a bot"}""", string.Empty, false));
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        // 被擋：不算錄影，不改用 PotPlayer 直接播放，也不啟動任何程式
        Assert.True(result.Blocked);
        Assert.False(result.Success);
        Assert.StartsWith("streamlink：", result.BlockedReason);
        Assert.Contains("Sign in to confirm", result.BlockedReason);
        Assert.Single(runner.Runs); // 只有 streamlink 的 --json 查詢
        Assert.Empty(runner.Starts);
        Assert.Empty(videoInfo.QueriedUrls);
    }

    [Theory]
    [InlineData("""{"error":"got error: HTTP 429"}""")]
    [InlineData("not json")]
    public async Task LaunchAsync_StreamlinkErrorContains429OrNotABot_ReturnsBlocked(string stdout)
    {
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, _) = Create(config);
        // error JSON 的 429 在 stdout；非 JSON 時 429 在標準錯誤的最後一行
        var stderr = stdout == "not json" ? "error: this is not a bot check, HTTP 429" : string.Empty;
        runner.Results.Enqueue(new ProcessResult(1, stdout, stderr, false));
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Blocked);
        Assert.Contains("429", result.BlockedReason);
        Assert.Empty(runner.Starts);
    }

    [Fact]
    public async Task LaunchAsync_StreamlinkErrorOtherThanBlocked_StillFallsBackToVod()
    {
        // 既有行為：不是被擋的錯誤（找不到串流）仍然改用 PotPlayer 直接播放
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, videoInfo) = Create(config);
        runner.Results.Enqueue(new ProcessResult(1, """{"error":"No playable streams found on this URL: x"}""", string.Empty, false));
        videoInfo.Results.Enqueue(new StreamMetadata(StreamPlatform.YouTube, null, "Someone", null, "VOD", IsVod: true));
        var url = "https://www.youtube.com/watch?v=abc";

        var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Blocked);
        Assert.True(result.Success);
        Assert.Equal([url], Assert.Single(runner.Starts).Arguments);
    }

    // ===== 開台前準備 PotPlayer（PotPlayerPreparer）=====

    /// <summary>組一個真實的 preparer 與暫存資料夾；unwritable 為 true 時把安裝目錄的腳本路徑建成資料夾，強制走複本。</summary>
    private static (PotPlayerPreparer Preparer, FakePotPlayerRegistry Registry, string CopyRoot, string CopyPlayerPath) CreatePreparer(
        FakeSettingsService settings, string dir, bool unwritable, int port = 5003)
    {
        var installDir = Path.Combine(dir, "install");
        Directory.CreateDirectory(installDir);
        var playerPath = Path.Combine(installDir, "PotPlayerMini64.exe");
        File.WriteAllText(playerPath, "fake exe");
        settings.Current.Streamlink.PlayerPath = playerPath;

        var copyRoot = Path.Combine(dir, "copy");
        if (unwritable)
        {
            Directory.CreateDirectory(Path.Combine(installDir, "Extension", "Subtitle", "Translate", TranslateScriptBuilder.ScriptFileName));
        }

        var registry = new FakePotPlayerRegistry();
        var preparer = new PotPlayerPreparer(
            settings,
            new GatewayEndpoint { Port = port },
            registry,
            TranslateScriptBuilder.LoadTemplate,
            copyRoot,
            NullLogger<PotPlayerPreparer>.Instance);
        return (preparer, registry, copyRoot, Path.Combine(copyRoot, "PotPlayerMini64.exe"));
    }

    [Fact]
    public async Task LaunchAsync_WithPreparer_UsesCopyForVodAndLive()
    {
        var dir = Directory.CreateTempSubdirectory("launcher-preparer-");
        try
        {
            var settings = new FakeSettingsService();
            settings.Current.Streamlink.StreamlinkPath = "/fake/streamlink.exe";
            var (preparer, _, copyRoot, copyPlayerPath) = CreatePreparer(settings, dir.FullName, unwritable: true);

            var runner = new FakeProcessRunner();
            runner.ExistingFiles.Add(settings.Current.Streamlink.StreamlinkPath);
            runner.ExistingFiles.Add(settings.Current.Streamlink.PlayerPath);

            var pageInfo = new FakePageInfoProvider();
            pageInfo.Results.Enqueue(Page()); // 不是直播中 → 錄影流程：直接開網址
            var launcher = new StreamlinkLauncher(
                runner, settings, new FakeVideoInfoProvider(), NullLogger<StreamlinkLauncher>.Instance, pageInfo, preparer);

            var result = await launcher.LaunchAsync("https://www.youtube.com/watch?v=abc", cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            // 錄影路徑用複本的 exe 啟動，成功訊息附加複本說明
            var start = Assert.Single(runner.Starts);
            Assert.Equal(copyPlayerPath, start.FileName);
            Assert.EndsWith("複本開啟）", result.Message);
            // 腳本寫在複本裡（port 寫死）
            Assert.Contains("http://127.0.0.1:5003/translate",
                File.ReadAllText(Path.Combine(copyRoot, "Extension", "Subtitle", "Translate", TranslateScriptBuilder.ScriptFileName)));

            // 直播路徑（Twitch）：streamlink 的 --player 也是複本的 exe
            runner.Results.Enqueue(new ProcessResult(0, SuccessJson, string.Empty, false));
            var liveResult = await launcher.LaunchAsync("https://www.twitch.tv/someone", cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(liveResult.Success);
            var liveStart = runner.Starts[^1];
            Assert.Equal(settings.Current.Streamlink.StreamlinkPath, liveStart.FileName);
            var playerIndex = liveStart.Arguments.ToList().IndexOf("--player");
            Assert.Equal(copyPlayerPath, liveStart.Arguments[playerIndex + 1]);
            Assert.DoesNotContain("直播暫時用安裝目錄", liveResult.Message, StringComparison.Ordinal);
            Assert.EndsWith("複本開啟）", liveResult.Message);
        }
        finally
        {
            try { Directory.Delete(dir.FullName, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task LaunchAsync_PreparerCopy_YouTubeLive_UsesCopyPlayer()
    {
        var dir = Directory.CreateTempSubdirectory("launcher-preparer-");
        try
        {
            var settings = new FakeSettingsService();
            settings.Current.Streamlink.StreamlinkPath = "/fake/streamlink.exe";
            var (preparer, _, _, copyPlayerPath) = CreatePreparer(settings, dir.FullName, unwritable: true);

            var runner = new FakeProcessRunner();
            runner.ExistingFiles.Add(settings.Current.Streamlink.StreamlinkPath);
            runner.ExistingFiles.Add(settings.Current.Streamlink.PlayerPath);

            var pageInfo = new FakePageInfoProvider();
            pageInfo.Results.Enqueue(Page(isLiveNow: true, isLiveContent: true)); // 直播中 → 走 streamlink
            var launcher = new StreamlinkLauncher(
                runner, settings, new FakeVideoInfoProvider(), NullLogger<StreamlinkLauncher>.Instance, pageInfo, preparer);
            var json =
                """
                {"plugin":"youtube","metadata":{"id":"abc","author":"Someone","category":null,"title":"LIVE"},"streams":{"best":{"type":"hls","url":"https://hls/example"}}}
                """;
            runner.Results.Enqueue(new ProcessResult(0, json, string.Empty, false));

            var result = await launcher.LaunchAsync("https://www.youtube.com/watch?v=abc", cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            // 直播：streamlink 的 --player 也是準備好的複本路徑，成功訊息附加複本說明、不再有臨時方案的說明
            var start = Assert.Single(runner.Starts);
            Assert.Equal(settings.Current.Streamlink.StreamlinkPath, start.FileName);
            var playerIndex = start.Arguments.ToList().IndexOf("--player");
            Assert.Equal(copyPlayerPath, start.Arguments[playerIndex + 1]);
            Assert.DoesNotContain("直播暫時用安裝目錄", result.Message, StringComparison.Ordinal);
            Assert.EndsWith("複本開啟）", result.Message);
        }
        finally
        {
            try { Directory.Delete(dir.FullName, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task LaunchAsync_PreparerCopy_StreamlinkDetectsVod_UsesCopyPlayer()
    {
        var dir = Directory.CreateTempSubdirectory("launcher-preparer-");
        try
        {
            var settings = new FakeSettingsService();
            settings.Current.Streamlink.StreamlinkPath = "/fake/streamlink.exe";
            var (preparer, _, _, copyPlayerPath) = CreatePreparer(settings, dir.FullName, unwritable: true);

            var runner = new FakeProcessRunner();
            runner.ExistingFiles.Add(settings.Current.Streamlink.StreamlinkPath);
            runner.ExistingFiles.Add(settings.Current.Streamlink.PlayerPath);

            var videoInfo = new FakeVideoInfoProvider();
            videoInfo.Results.Enqueue(new StreamMetadata(StreamPlatform.YouTube, null, "Someone", null, "VOD", IsVod: true));
            var launcher = new StreamlinkLauncher(
                runner, settings, videoInfo, NullLogger<StreamlinkLauncher>.Instance, preparer: preparer);
            var json =
                """
                {"plugin":"youtube","metadata":{"id":"abc","author":"Someone","category":null,"title":"VOD"},"streams":{"360p":{"type":"http","url":"http://360p"},"best":{"type":"http","url":"http://360p"}}}
                """;
            runner.Results.Enqueue(new ProcessResult(0, json, string.Empty, false));
            var url = "https://www.youtube.com/watch?v=abc";

            var result = await launcher.LaunchAsync(url, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Contains("偵測為錄影", result.Message);
            // streamlink 判斷為錄影：照用準備好的複本路徑，成功訊息照舊附加複本說明
            var start = Assert.Single(runner.Starts);
            Assert.Equal(copyPlayerPath, start.FileName);
            Assert.Equal([url], start.Arguments);
            Assert.EndsWith("複本開啟）", result.Message);
        }
        finally
        {
            try { Directory.Delete(dir.FullName, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task LaunchAsync_PreparerFails_ReturnsFailureWithoutStartingAnything()
    {
        var dir = Directory.CreateTempSubdirectory("launcher-preparer-");
        try
        {
            var settings = new FakeSettingsService();
            settings.Current.Streamlink.StreamlinkPath = "/fake/streamlink.exe";
            // port 0：preparer 直接失敗
            var (preparer, _, _, _) = CreatePreparer(settings, dir.FullName, unwritable: false, port: 0);

            var runner = new FakeProcessRunner();
            runner.ExistingFiles.Add(settings.Current.Streamlink.StreamlinkPath);
            runner.ExistingFiles.Add(settings.Current.Streamlink.PlayerPath);
            var launcher = new StreamlinkLauncher(
                runner, settings, new FakeVideoInfoProvider(), NullLogger<StreamlinkLauncher>.Instance, preparer: preparer);

            var result = await launcher.LaunchAsync("https://www.twitch.tv/someone", cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal("Gateway 沒有可用的 port，PotPlayer 連不到 Gateway，請看系統記錄", result.Message);
            Assert.Empty(runner.Runs);
            Assert.Empty(runner.Starts);
        }
        finally
        {
            try { Directory.Delete(dir.FullName, recursive: true); } catch (IOException) { }
        }
    }

    // ===== Whisper 開關與語言（WhisperDecider）=====

    /// <summary>組一個帶 preparer 的 launcher 與共享的呼叫順序清單（runner 與 registry 的呼叫都記進 calls）；測試要自己刪 Dir。</summary>
    private static (StreamlinkLauncher Launcher, FakeProcessRunner Runner, FakeVideoInfoProvider VideoInfo, FakePotPlayerRegistry Registry, List<string> Calls, DirectoryInfo Dir)
        CreateWithPreparer(IYouTubePageInfoProvider? pageInfo = null)
    {
        var dir = Directory.CreateTempSubdirectory("launcher-whisper-");
        var settings = new FakeSettingsService();
        settings.Current.Streamlink.StreamlinkPath = "/fake/streamlink.exe";
        var (preparer, registry, _, _) = CreatePreparer(settings, dir.FullName, unwritable: false);

        var calls = new List<string>();
        registry.CallLog = calls;
        var runner = new FakeProcessRunner { CallLog = calls };
        runner.ExistingFiles.Add(settings.Current.Streamlink.StreamlinkPath);
        runner.ExistingFiles.Add(settings.Current.Streamlink.PlayerPath);

        var videoInfo = new FakeVideoInfoProvider();
        var launcher = new StreamlinkLauncher(runner, settings, videoInfo, NullLogger<StreamlinkLauncher>.Instance, pageInfo, preparer);
        return (launcher, runner, videoInfo, registry, calls, dir);
    }

    private static void DeleteDir(DirectoryInfo dir)
    {
        try { Directory.Delete(dir.FullName, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task LaunchAsync_YouTubeLive_AppliesWhisperBeforeStart()
    {
        var pageInfo = new FakePageInfoProvider();
        pageInfo.Results.Enqueue(Page(isLiveNow: true, isLiveContent: true));
        var (launcher, runner, _, registry, calls, dir) = CreateWithPreparer(pageInfo);
        try
        {
            var json =
                """
                {"plugin":"youtube","metadata":{"id":"abc","author":"Someone","category":null,"title":"LIVE"},"streams":{"best":{"type":"hls","url":"https://hls/example"}}}
                """;
            runner.Results.Enqueue(new ProcessResult(0, json, string.Empty, false));

            var result = await launcher.LaunchAsync("https://www.youtube.com/watch?v=abc", cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            // 標題有假名 → 日文
            Assert.Equal([(true, 3)], registry.WhisperApplied);
            Assert.Contains("Whisper：開啟（直播中）", result.Message);
            // Whisper 先寫進登錄檔，之後才啟動 streamlink（它會帶起 PotPlayer）；translation 是 Prepare 的自動翻譯設定
            Assert.Equal(["translation", "run", "whisper", "start"], calls);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task LaunchAsync_YouTubeVodWithJapaneseCaptions_DisablesWhisper()
    {
        var pageInfo = new FakePageInfoProvider();
        pageInfo.Results.Enqueue(Page(captionLanguages: ["ja"], autoCaptionLanguage: "ja"));
        var (launcher, runner, _, registry, calls, dir) = CreateWithPreparer(pageInfo);
        try
        {
            var result = await launcher.LaunchAsync("https://www.youtube.com/watch?v=abc", cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Equal([(false, 3)], registry.WhisperApplied);
            Assert.Contains("Whisper：關閉（有日文字幕，改用 YouTube 字幕）", result.Message);
            // 錄影路徑不查 streamlink：寫完 Whisper 才啟動播放器（translation 是 Prepare 的自動翻譯設定）
            Assert.Equal(["translation", "whisper", "start"], calls);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task LaunchAsync_YouTubeVodWithoutJapaneseCaptions_EnablesWhisper()
    {
        var pageInfo = new FakePageInfoProvider();
        pageInfo.Results.Enqueue(Page(captionLanguages: ["en"], autoCaptionLanguage: "en"));
        var (launcher, runner, _, registry, calls, dir) = CreateWithPreparer(pageInfo);
        try
        {
            var result = await launcher.LaunchAsync("https://www.youtube.com/watch?v=abc", cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            // 沒有日文字幕軌：開 Whisper；語言用生成式字幕軌的 en
            Assert.Equal([(true, 2)], registry.WhisperApplied);
            Assert.Contains("Whisper：開啟（錄影沒有日文字幕）", result.Message);
            Assert.Equal(["translation", "whisper", "start"], calls);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task LaunchAsync_VodViaOEmbed_DoesNotApplyWhisper()
    {
        var pageInfo = new FakePageInfoProvider();
        pageInfo.Results.Enqueue(null); // 影片頁讀不到 → 改走 streamlink → 錄影 → oEmbed
        var (launcher, runner, videoInfo, registry, calls, dir) = CreateWithPreparer(pageInfo);
        try
        {
            runner.Results.Enqueue(new ProcessResult(1, """{"error":"No playable streams found on this URL: x"}""", string.Empty, false));
            videoInfo.Results.Enqueue(new StreamMetadata(StreamPlatform.YouTube, null, "Someone", null, "VOD", IsVod: true));

            var result = await launcher.LaunchAsync("https://www.youtube.com/watch?v=abc", cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Empty(registry.WhisperApplied);
            Assert.DoesNotContain("Whisper：", result.Message, StringComparison.Ordinal);
            Assert.Equal(["translation", "run", "start"], calls);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    // ===== 啟動播放器前的掛勾（beforePlayerStart）=====

    /// <summary>記錄呼叫（進共享的順序清單）與收到的中繼資料的假掛勾；throwCanceled／throwGeneral 為 true 時擲對應例外。</summary>
    private sealed class RecordingHook(List<string> calls)
    {
        public List<StreamMetadata?> Metadata { get; } = [];

        public bool ThrowCanceled { get; set; }

        public bool ThrowGeneral { get; set; }

        public Task RunAsync(StreamMetadata? metadata, CancellationToken cancellationToken)
        {
            Metadata.Add(metadata);
            calls.Add("hook");
            if (ThrowCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (ThrowGeneral)
            {
                throw new InvalidOperationException("掛勾失敗");
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task LaunchAsync_BeforePlayerStart_LivePath_CalledOnceBeforeStartWithReturnedMetadata()
    {
        var (launcher, runner, _) = Create();
        runner.Results.Enqueue(new ProcessResult(0, SuccessJson, string.Empty, false));
        var calls = new List<string>();
        runner.CallLog = calls;
        var hook = new RecordingHook(calls);

        var result = await launcher.LaunchAsync("https://www.twitch.tv/someone",
            hook.RunAsync, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        // 掛勾在 runner.Start（帶起播放器）之前被呼叫一次
        Assert.Equal(["run", "hook", "start"], calls);
        Assert.Same(result.Metadata, Assert.Single(hook.Metadata));
    }

    [Fact]
    public async Task LaunchAsync_BeforePlayerStart_VodPath_CalledOnceBeforeStartWithReturnedMetadata()
    {
        var pageInfo = new FakePageInfoProvider();
        pageInfo.Results.Enqueue(Page());
        var (launcher, runner, _) = Create(pageInfo: pageInfo);
        var calls = new List<string>();
        runner.CallLog = calls;
        var hook = new RecordingHook(calls);

        var result = await launcher.LaunchAsync("https://www.youtube.com/watch?v=abc",
            hook.RunAsync, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(["hook", "start"], calls);
        Assert.Same(result.Metadata, Assert.Single(hook.Metadata));
        var vodMetadata = Assert.Single(hook.Metadata);
        Assert.NotNull(vodMetadata);
        Assert.True(vodMetadata.IsVod);
    }

    [Fact]
    public async Task LaunchAsync_BeforePlayerStart_YouTubeLive_ReceivesEnrichedMetadata()
    {
        var pageInfo = new FakePageInfoProvider();
        pageInfo.Results.Enqueue(Page(isLiveNow: true, isLiveContent: true));
        var (launcher, runner, _) = Create(pageInfo: pageInfo);
        var json =
            """
            {"plugin":"youtube","metadata":{"id":"abc","author":"Someone","category":null,"title":"LIVE"},"streams":{"best":{"type":"hls","url":"https://hls/example"}}}
            """;
        runner.Results.Enqueue(new ProcessResult(0, json, string.Empty, false));
        var hook = new RecordingHook(new List<string>());

        var result = await launcher.LaunchAsync("https://www.youtube.com/watch?v=abc",
            hook.RunAsync, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        // 掛勾收到的是 Enrich 過的中繼資料（說明欄來自頁面），與回傳結果的 Metadata 是同一份
        var hookMetadata = Assert.Single(hook.Metadata);
        Assert.Same(result.Metadata, hookMetadata);
        Assert.Equal("說明欄文字", hookMetadata?.Description);
        Assert.Equal("https://www.youtube.com/@page", hookMetadata?.ChannelUrl);
        Assert.Equal("Someone", hookMetadata?.Author);
    }

    [Fact]
    public async Task LaunchAsync_BeforePlayerStart_ThrowsGeneralException_StillStartsPlayer()
    {
        var (launcher, runner, _) = Create();
        runner.Results.Enqueue(new ProcessResult(0, SuccessJson, string.Empty, false));
        var hook = new RecordingHook(new List<string>()) { ThrowGeneral = true };

        var result = await launcher.LaunchAsync("https://www.twitch.tv/someone",
            hook.RunAsync, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Single(runner.Starts);
    }

    [Fact]
    public async Task LaunchAsync_BeforePlayerStart_ThrowsOperationCanceled_DoesNotStartPlayer()
    {
        var (launcher, runner, _) = Create();
        runner.Results.Enqueue(new ProcessResult(0, SuccessJson, string.Empty, false));
        var hook = new RecordingHook(new List<string>()) { ThrowCanceled = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.LaunchAsync("https://www.twitch.tv/someone",
            hook.RunAsync, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(runner.Starts);
    }

    [Fact]
    public async Task LaunchAsync_BeforePlayerStart_FailurePaths_DoNotCallHook()
    {
        // 找不到 streamlink
        var config = new StreamlinkSettings { StreamlinkPath = "/fake/streamlink.exe", PlayerPath = "/fake/player.exe" };
        var (launcher, runner, _) = Create(config);
        runner.ExistingFiles.Remove(config.StreamlinkPath);
        var calls = new List<string>();
        runner.CallLog = calls;
        var hook = new RecordingHook(calls);

        var result = await launcher.LaunchAsync("https://www.twitch.tv/someone",
            hook.RunAsync, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Empty(hook.Metadata);
        Assert.Empty(runner.Starts);

        // 待機室
        var pageInfo = new FakePageInfoProvider();
        pageInfo.Results.Enqueue(Page(isUpcoming: true));
        var (launcher2, runner2, _) = Create(pageInfo: pageInfo);
        var calls2 = new List<string>();
        runner2.CallLog = calls2;
        var hook2 = new RecordingHook(calls2);

        var result2 = await launcher2.LaunchAsync("https://www.youtube.com/watch?v=abc",
            hook2.RunAsync, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result2.Success);
        Assert.Empty(hook2.Metadata);
        Assert.Empty(runner2.Starts);
        Assert.DoesNotContain("hook", calls2);
    }
}
