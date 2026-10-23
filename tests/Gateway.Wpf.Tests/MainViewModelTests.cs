using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;
using Translation.Gateway.Logging;
using Translation.Gateway.Services;
using Translation.Gateway.ViewModels;

namespace Translation.Gateway.Wpf.Tests;

/// <summary>一鍵開台被 YouTube 擋下的流程：用假的影片頁面查詢與假的路由器 REST 回應驗證「換 IPv6 後重試一次」
/// 與提示文字，不連網、不啟動任何外部程式（WPF 的同步內容比照 UiSmokeTests 裝在 STA 執行緒）。
/// 路由器更換用真的 <see cref="RouterIpv6Refresher"/>（假的 HttpMessageHandler 與本機位址替身），一併涵蓋網段過濾。</summary>
public class MainViewModelTests
{
    private const string StreamMetadataJson =
        """
        {"plugin":"youtube","metadata":{"id":"abc","author":"Someone","category":null,"title":"LIVE"},"streams":{"best":{"type":"hls","url":"https://hls/example"}}}
        """;

    private const string Url = "https://www.youtube.com/watch?v=abc";

    /// <summary>被 YouTube 擋下的頁面查詢結果（playabilityStatus 判定為要求登入確認不是機器人）。</summary>
    private static YouTubePageCheck BlockedCheck { get; } = new(null, "YouTube 要求登入確認不是機器人");

    [Fact]
    public void OpenStream_Blocked_WithRouterRefreshSuccess_RetriesOnceAndOpens()
    {
        RunOnSta(() =>
        {
            using var ctx = CreateContext(pageResults: [BlockedCheck, LiveCheck()], routerConfigured: true, refreshSuccess: true);
            ctx.Runner.Results.Enqueue(new ProcessResult(0, StreamMetadataJson, string.Empty, false));

            ctx.MainVm.OpenStreamCommand.Execute(null);
            PumpUntil(() => !ctx.MainVm.OpenStreamCommand.IsRunning);

            // 被擋後換 IPv6 成功：用同一份剪貼簿文字再呼叫一次 LaunchAsync（頁面查詢兩次），重試成功開播
            Assert.Equal(2, ctx.Page.CheckCount);
            Assert.True(ctx.Router.RequestCount > 0);
            Assert.Single(ctx.Runner.Starts);
            Assert.Contains("已開啟", ctx.MainVm.StreamStatus);
            // 狀態列接上路由器更換結果的說明
            Assert.Contains("已更換 IPv6 前綴", ctx.MainVm.StreamStatus);
        });
    }

    [Fact]
    public void OpenStream_Blocked_WithRouter_RetryStillBlocked_RetriesOnlyOnce()
    {
        RunOnSta(() =>
        {
            using var ctx = CreateContext(pageResults: [BlockedCheck, BlockedCheck], routerConfigured: true, refreshSuccess: true);

            ctx.MainVm.OpenStreamCommand.Execute(null);
            PumpUntil(() => !ctx.MainVm.OpenStreamCommand.IsRunning);

            // 只重試一次（頁面查詢兩次就停），重試仍被擋時不開播
            Assert.Equal(2, ctx.Page.CheckCount);
            Assert.True(ctx.Router.RequestCount > 0);
            Assert.Empty(ctx.Runner.Runs);
            Assert.Empty(ctx.Runner.Starts);
            Assert.Contains("無法開啟", ctx.MainVm.StreamStatus);
            Assert.Contains("仍被擋", ctx.MainVm.StreamStatus);
        });
    }

    [Fact]
    public void OpenStream_Blocked_WithRouter_RefreshFails_DoesNotRetry()
    {
        RunOnSta(() =>
        {
            using var ctx = CreateContext(pageResults: [BlockedCheck], routerConfigured: true, refreshSuccess: false);

            ctx.MainVm.OpenStreamCommand.Execute(null);
            PumpUntil(() => !ctx.MainVm.OpenStreamCommand.IsRunning);

            // 更換 IPv6 失敗（沒有任何一台成功）：不重試
            Assert.Equal(1, ctx.Page.CheckCount);
            Assert.Contains("更換 IPv6 失敗", ctx.MainVm.StreamStatus);
            Assert.Contains("連不上", ctx.MainVm.StreamStatus);
            Assert.Empty(ctx.Runner.Starts);
        });
    }

    [Fact]
    public void OpenStream_Blocked_WithoutRouter_SuggestsIpChange_WithoutCallingRefresher()
    {
        RunOnSta(() =>
        {
            using var ctx = CreateContext(pageResults: [BlockedCheck], routerConfigured: false);

            ctx.MainVm.OpenStreamCommand.Execute(null);
            PumpUntil(() => !ctx.MainVm.OpenStreamCommand.IsRunning);

            // 沒有設定路由器：不呼叫 refresher（不送任何路由器請求），提示使用者可能是匿名存取被擋
            Assert.Equal(1, ctx.Page.CheckCount);
            Assert.Equal(0, ctx.Router.RequestCount);
            Assert.Contains("無法開啟", ctx.MainVm.StreamStatus);
            Assert.Contains("可能是匿名存取 YouTube", ctx.MainVm.StreamStatus);
            Assert.Empty(ctx.Runner.Starts);
        });
    }

    [Fact]
    public void OpenStream_NotBlocked_DoesNotCallRefresher()
    {
        RunOnSta(() =>
        {
            // 沒被擋：開台前不再主動更換 IPv6（T-2090 拿掉「開 YouTube 前一律更換」）
            using var ctx = CreateContext(pageResults: [LiveCheck()], routerConfigured: true, refreshSuccess: true);
            ctx.Runner.Results.Enqueue(new ProcessResult(0, StreamMetadataJson, string.Empty, false));

            ctx.MainVm.OpenStreamCommand.Execute(null);
            PumpUntil(() => !ctx.MainVm.OpenStreamCommand.IsRunning);

            Assert.Equal(1, ctx.Page.CheckCount);
            Assert.Equal(0, ctx.Router.RequestCount);
            Assert.Single(ctx.Runner.Starts);
            Assert.Contains("已開啟", ctx.MainVm.StreamStatus);
            Assert.DoesNotContain("已更換 IPv6 前綴", ctx.MainVm.StreamStatus);
        });
    }

    /// <summary>正常直播頁的查詢結果（isLiveNow → 走 streamlink）。</summary>
    private static YouTubePageCheck LiveCheck() =>
        new(new YouTubePageInfo("abc", "LIVE", "Someone", "UC1", null, null, null, [],
            IsLiveNow: true, IsLiveContent: true, IsUpcoming: false,
            null, null, null, []), null);

    // ===== 測試內容組裝 =====

    private sealed class OpenStreamContext : IDisposable
    {
        public required string Dir { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required StubPageProvider Page { get; init; }
        public required FakeRouterHandler Router { get; init; }
        public required FakeProcessRunner Runner { get; init; }
        public required MainViewModel MainVm { get; init; }

        public void Dispose()
        {
            Provider.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { /* SQLite 連線池可能還握著檔案 */ }
        }
    }

    private static OpenStreamContext CreateContext(
        IReadOnlyList<YouTubePageCheck> pageResults, bool routerConfigured = false, bool refreshSuccess = false)
    {
        var dir = Directory.CreateTempSubdirectory("gateway-blocked-");
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, Path.Combine(dir.FullName, "settings.json"));
        settings.Current.Streamlink.StreamlinkPath = Path.Combine(dir.FullName, "streamlink.exe");
        settings.Current.Streamlink.PlayerPath = Path.Combine(dir.FullName, "player.exe");

        var db = new SqliteTranslationStore(NullLogger<SqliteTranslationStore>.Instance, Path.Combine(dir.FullName, "cache.db"));

        var page = new StubPageProvider();
        foreach (var result in pageResults)
        {
            page.Results.Enqueue(result);
        }

        var routerHandler = new FakeRouterHandler
        {
            ThrowAll = routerConfigured && !refreshSuccess
        };
        // 執行前的前綴 → 執行後換新（前綴改變 → 判定已重新取得）
        routerHandler.DhcpResponses.Enqueue(DhcpJson("2001:db8:1::/64", "1d"));
        routerHandler.DhcpResponses.Enqueue(DhcpJson("2001:db8:2::/64", "4w"));
        var router = new RouterIpv6Refresher(
            settings,
            NullLogger<RouterIpv6Refresher>.Instance,
            routerHandler,
            localAddresses: () => [IPAddress.Parse("192.168.5.27")]);
        if (routerConfigured)
        {
            // 本機假位址 192.168.5.27 的同網段（/24）路由器
            settings.Current.Router.Routers.Add(new RouterEndpoint { Host = "192.168.5.254:8844", UserName = "renew", Password = "pw" });
        }

        var runner = new FakeProcessRunner();
        runner.ExistingFiles.Add(settings.Current.Streamlink.StreamlinkPath);
        runner.ExistingFiles.Add(settings.Current.Streamlink.PlayerPath);

        var provider = BuildServices(settings, db, runner, page, router);
        var mainVm = provider.GetRequiredService<MainViewModel>();
        // 剪貼簿與對話框都換成替身，不碰系統剪貼簿、不彈出真的 MessageBox
        mainVm.ClipboardReader = () => Url;
        mainVm.ConfirmDialog = _ => false;
        mainVm.SaveSituationDialog = _ => false;
        return new OpenStreamContext
        {
            Dir = dir.FullName,
            Provider = provider,
            Page = page,
            Router = routerHandler,
            Runner = runner,
            MainVm = mainVm
        };
    }

    private static ServiceProvider BuildServices(
        SettingsService settings, SqliteTranslationStore db, FakeProcessRunner runner,
        StubPageProvider page, RouterIpv6Refresher router)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton<ITranslationStore>(db);
        services.AddSingleton<IGlossaryStore>(sp => (IGlossaryStore)sp.GetRequiredService<ITranslationStore>());
        services.AddSingleton<ISubtitleLogStore>(sp => (ISubtitleLogStore)sp.GetRequiredService<ITranslationStore>());
        services.AddSingleton<ICaptionStore>(sp => (ICaptionStore)sp.GetRequiredService<ITranslationStore>());
        services.AddSingleton<IAuditStore>(sp => (IAuditStore)sp.GetRequiredService<ITranslationStore>());
        services.AddSingleton<PromptTemplateRegistry>();
        services.AddSingleton<StreamSession>();
        services.AddSingleton<SubtitleLogWriter>();
        services.AddSingleton<StreamCardRecorder>();
        services.AddSingleton<GlossaryState>();
        services.AddSingleton<KeywordGlossary>();
        services.AddSingleton<MishearingFixState>();
        services.AddSingleton<IOpenAiClient, StubOpenAiClient>();
        services.AddSingleton<ITextProcessingService, TextProcessingService>();
        services.AddSingleton<IProcessRunner>(runner);
        services.AddSingleton<IVideoInfoProvider>(new StubVideoInfoProvider());
        services.AddSingleton<IYouTubePageInfoProvider>(page);
        services.AddSingleton<CaptionTrackState>();
        services.AddSingleton<IYouTubeCaptionProvider>(new StubCaptionProvider());
        services.AddSingleton<CaptionLoader>();
        services.AddSingleton<CaptionSentenceState>();
        services.AddSingleton<CardReadinessGate>();
        services.AddSingleton<CaptionPreprocessor>();
        services.AddSingleton<IPotPlayerSubtitleLoader>(new StubPotPlayerSubtitleLoader());
        services.AddSingleton<IPlaybackClock>(new NullPlaybackClock());
        services.AddSingleton<CaptionReference>();
        services.AddSingleton<IPotPlayerProcesses>(new StubPotPlayerProcesses());
        services.AddSingleton<StreamlinkLauncher>();
        services.AddSingleton(sp => new TranslationManager(
            sp.GetRequiredService<IOpenAiClient>(),
            sp.GetRequiredService<ITextProcessingService>(),
            sp.GetRequiredService<ITranslationStore>(),
            sp.GetRequiredService<ISettingsService>(),
            glossary: sp.GetRequiredService<GlossaryState>(),
            keywordGlossary: sp.GetRequiredService<KeywordGlossary>(),
            mishearingFixes: sp.GetRequiredService<MishearingFixState>(),
            captionReference: sp.GetRequiredService<CaptionReference>()));
        services.AddSingleton(new UiLogStore());
        services.AddSingleton(new ApiTraceStore());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILorebookLlm>(new StubLorebookLlm());
        services.AddSingleton<IKnowledgeSources>(new StubKnowledgeSources());
        services.AddSingleton<MiningService>();
        services.AddSingleton<MishearingMiningService>();
        services.AddSingleton<GameTermDiscoveryService>();
        services.AddSingleton<TermImportService>();
        services.AddSingleton<ReviewCountSignal>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<LlmHealth>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<GlossaryReviewViewModel>();
        services.AddSingleton<MishearingReviewViewModel>();
        services.AddSingleton(router);
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider();
    }

    // ===== 替身（不連網、不啟動外部程式）=====

    /// <summary>假的影片頁面查詢：CheckPageAsync 依序回傳排好的結果並計數（每次 LaunchAsync 會查一次）。</summary>
    private sealed class StubPageProvider : IYouTubePageInfoProvider
    {
        public Queue<YouTubePageCheck> Results { get; } = new();

        public int CheckCount { get; private set; }

        public Task<YouTubePageInfo?> GetPageInfoAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult<YouTubePageInfo?>(Results.Count > 0 ? Results.Peek().Info : null);

        public Task<YouTubePageCheck> CheckPageAsync(string url, CancellationToken cancellationToken = default)
        {
            CheckCount++;
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new YouTubePageCheck(null, null));
        }
    }

    /// <summary>RouterOS 的 DHCPv6 client 狀態 JSON（一筆 bound、prefix 是「前綴, 租期」）。</summary>
    private static string DhcpJson(string prefix, string lease) =>
        $$"""[{ "interface": "pppoe-out1", "status": "bound", "prefix": "{{prefix}}, {{lease}}" }]""";

    /// <summary>假的路由器 REST API（MikroTik）：依路徑回應固定 JSON，dhcp-client 用佇列依序回（模擬換前綴）；
    /// ThrowAll 時全部丟 HttpRequestException（模擬連不上）。不連網。</summary>
    private sealed class FakeRouterHandler : HttpMessageHandler
    {
        public bool ThrowAll { get; set; }

        public Queue<string> DhcpResponses { get; } = new();

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (ThrowAll) throw new HttpRequestException("連線被拒");
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var (status, content) = path switch
            {
                "/rest/system/identity" => (HttpStatusCode.OK, """{"name":"AESOP"}"""),
                "/rest/ipv6/dhcp-client" => (HttpStatusCode.OK,
                    DhcpResponses.Count > 0 ? DhcpResponses.Dequeue() : """[{ "status": "bound" }]"""),
                "/rest/system/script/run" => (HttpStatusCode.OK, "[]"),
                _ => (HttpStatusCode.NotFound, string.Empty)
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
        }
    }

    /// <summary>不真的執行程式的假行程執行器：RunAsync 回傳排入的結果，Start 回傳假的程序。</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public HashSet<string> ExistingFiles { get; } = new();

        public Queue<ProcessResult> Results { get; } = new();

        public List<(string FileName, IReadOnlyList<string> Arguments)> Runs { get; } = [];

        public List<(string FileName, IReadOnlyList<string> Arguments)> Starts { get; } = [];

        public bool FileExists(string path) => ExistingFiles.Contains(path);

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Runs.Add((fileName, arguments));
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new ProcessResult(0, string.Empty, string.Empty, false));
        }

        public IStartedProcess Start(string fileName, IReadOnlyList<string> arguments, Action<string> onOutput)
        {
            Starts.Add((fileName, arguments));
            return new FakeStartedProcess();
        }
    }

    private sealed class FakeStartedProcess : IStartedProcess
    {
        public bool HasExited { get; set; }

        public void KillTree() { }
    }

    private sealed class StubVideoInfoProvider : IVideoInfoProvider
    {
        public Task<StreamMetadata?> GetYouTubeInfoAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult<StreamMetadata?>(null);
    }

    private sealed class NullPlaybackClock : IPlaybackClock
    {
        public long? GetPositionMs() => null;
    }

    private sealed class StubPotPlayerSubtitleLoader : IPotPlayerSubtitleLoader
    {
        public bool Load(string subtitlePath) => false;
    }

    private sealed class StubPotPlayerProcesses : IPotPlayerProcesses
    {
        public bool AnyRunning() => false;

        public Task<bool> WaitAllExitedAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> CloseAllAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class StubCaptionProvider : IYouTubeCaptionProvider
    {
        public Task<YouTubeCaptionResult?> GetJapaneseCaptionsAsync(string videoId, string? visitorData, CancellationToken cancellationToken = default) =>
            Task.FromResult<YouTubeCaptionResult?>(null);
    }

    private sealed class StubLorebookLlm : ILorebookLlm
    {
        public Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmJsonResult("{\"nouns\":[]}", 1, 1, 1, "stub-glm"));

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }

    private sealed class StubKnowledgeSources : IKnowledgeSources
    {
        public Task<IReadOnlyList<WikidataHit>> SearchWikidataAsync(string text, string language = "ja", int limit = 3, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WikidataHit>>([]);

        public Task<WikidataEntity?> GetWikidataEntityAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<WikidataEntity?>(null);

        public Task<IReadOnlyList<string>> SearchWikipediaAsync(string lang, string text, int limit = 3, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<WikiPage?> GetWikipediaPageAsync(string lang, string title, CancellationToken ct = default) =>
            Task.FromResult<WikiPage?>(null);

        public Task<bool> FandomExistsAsync(string subdomain, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> SearchFandomAsync(string subdomain, string text, int limit = 3, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<WikiPage?> GetFandomPageAsync(string subdomain, string title, CancellationToken ct = default) =>
            Task.FromResult<WikiPage?>(null);
    }

    // ===== STA 執行緒與 Dispatcher 幫手（比照 UiSmokeTests）=====

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                // 非同步指令（開台）的 await 之後要回到這條 UI 執行緒
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    /// <summary>讓 Dispatcher 跑完排隊中的工作（繫結更新、非同步接續）。</summary>
    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>反覆打氣 Dispatcher 直到條件成立（或逾時），給非同步命令收尾用。</summary>
    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                DoEvents();
                Assert.True(condition(), "等候條件逾時");
                return;
            }
            DoEvents();
            Thread.Sleep(10);
        }
    }
}
