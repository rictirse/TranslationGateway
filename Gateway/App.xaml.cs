using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCCNET;
using Serilog;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;
using Translation.Gateway.Logging;
using Translation.Gateway.Services;
using Translation.Gateway.ViewModels;
using Translation.Gateway.Views;

namespace Translation.Gateway;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static IHost AppHost { get; private set; } = null!;

    /// <summary>測試用：true 時 OnStartup 不做任何啟動（不建主機、不開視窗、不聽 PotPlayer）。
    /// WPF 的 Application 建構時就會排入 Startup，測試只要處理視窗訊息就會觸發真正的啟動流程。</summary>
    public bool SkipStartup { get; init; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (SkipStartup) return;

        ZhConverter.Initialize(segmentMode: SegmentMode.Jieba);
        try
        {
            AppHost = BuildHost();
            await AppHost.StartAsync();

            // 資料庫後端由設定決定，必須先載入設定，之後解析 TranslationManager 時才會用正確的後端
            var settingsSvc = AppHost.Services.GetRequiredService<ISettingsService>();
            await settingsSvc.LoadAsync();

            // 字幕紀錄：開始接收 TranslationJob 訊息（背景寫入資料庫）
            AppHost.Services.GetRequiredService<SubtitleLogWriter>().StartListening();

            // prompt 版本化：資料庫初始化完成後、開始接受翻譯請求之前，同步載入預設與選用的版本；
            // 失敗只寫 log（退回程式預設），不能讓程式啟動失敗
            try
            {
                AppHost.Services.GetRequiredService<PromptTemplateRegistry>().Initialize();
            }
            catch (Exception ex)
            {
                AppHost.Services.GetRequiredService<ILogger<App>>()
                    .LogWarning(ex, "prompt 版本載入失敗");
            }

            await StartPotPlayerListenerAsync();

            _ = AppHost.Services.GetRequiredService<TranslationManager>().WarmUpAsync();

            var mainWindow = AppHost.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            // 回溯紀錄過期清除：資料庫初始化完成、主視窗顯示之後，背景執行一次，不得拖住啟動；例外只寫 log
            _ = Task.Run(() =>
            {
                try
                {
                    AppHost.Services.GetRequiredService<AuditRetentionService>().RunOnce();
                }
                catch (Exception ex)
                {
                    AppHost.Services.GetRequiredService<ILogger<App>>()
                        .LogWarning(ex, "回溯紀錄過期清除失敗");
                }
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (AppHost != null)
        {
            try
            {
                // 字幕紀錄先停止接收並把剩下的寫完，再關閉主機
                AppHost.Services.GetRequiredService<SubtitleLogWriter>().Dispose();
                await AppHost.StopAsync();
            }
            catch { /* ignore */ }
            finally
            {
                AppHost.Dispose();
            }
        }

        base.OnExit(e);
    }

    private static IHost BuildHost()
    {
        // log 放在 %LOCALAPPDATA%\Translation.Gateway\logs（與預設 SQLite 檔同一個資料夾），換環境也一定存在
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Translation.Gateway");
        Directory.CreateDirectory(appDataDir);

        var uiLogStore = new UiLogStore();
        var apiTraceStore = new ApiTraceStore();

        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(uiLogStore);
                services.AddSingleton(apiTraceStore);
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<GlossaryReviewViewModel>();
                services.AddSingleton<MishearingReviewViewModel>();
                services.AddSingleton<ITranslationStore>(sp => TranslationStoreFactory.Create(
                    sp.GetRequiredService<ISettingsService>().Current.Database,
                    sp.GetRequiredService<ILoggerFactory>()));
                services.AddSingleton<IGlossaryStore>(sp => (IGlossaryStore)sp.GetRequiredService<ITranslationStore>());
                services.AddSingleton<ISubtitleLogStore>(sp => (ISubtitleLogStore)sp.GetRequiredService<ITranslationStore>());
                services.AddSingleton<ICaptionStore>(sp => (ICaptionStore)sp.GetRequiredService<ITranslationStore>());
                services.AddSingleton<IAuditStore>(sp => (IAuditStore)sp.GetRequiredService<ITranslationStore>());
                // 影片章節（video_chapters）：重開同一支影片直接用存的章節與譯名，不重抓留言、不重翻
                services.AddSingleton<IVideoChapterStore>(sp => (IVideoChapterStore)sp.GetRequiredService<ITranslationStore>());
                // prompt 版本化：啟動時寫入各種預設 prompt，直播 system prompt 改由 DB 版本提供
                // （appVersion 照 MainViewModel.AppVersion 的方式取組件的 InformationalVersion）
                services.AddSingleton(sp => new PromptTemplateRegistry(
                    sp.GetRequiredService<IAuditStore>(),
                    sp.GetRequiredService<ISettingsService>(),
                    sp.GetRequiredService<ILogger<PromptTemplateRegistry>>(),
                    typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion));
                // 回溯紀錄過期清除：啟動時背景執行一次（使用者的決定：不定時）
                services.AddSingleton<AuditRetentionService>();
                // 回溯紀錄：每次呼叫模型都寫入 llm_calls（背景佇列，不拖慢翻譯）；
                // TranslationManager 與 VodPrefetcher 的建構子 optional 參數由 DI 自動帶入
                services.AddSingleton<LlmCallRecorder>();
                services.AddSingleton<StreamSession>();
                services.AddSingleton<SubtitleLogWriter>();
                services.AddSingleton<StreamCardRecorder>();
                services.AddSingleton<GlossaryState>();
                services.AddSingleton<KeywordGlossary>();
                services.AddSingleton<MishearingFixState>();
                services.AddSingleton<TranslationManager>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<LogViewModel>();

                System.Windows.Data.BindingOperations.EnableCollectionSynchronization(
                    apiTraceStore.Items,
                    apiTraceStore.LockObject
                );

                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<ITextProcessingService, TextProcessingService>();

                services.AddTransient<ApiLoggingHandler>();
                services.AddTransient<SettingsViewModel>();

                // 翻譯模型：本體註冊為 Singleton，再用排程包裝註冊 IOpenAiClient
                // （全部請求經 LlmLane 排隊：即時翻譯 Realtime、預先翻譯 Prefetch；
                //   翻譯模型不插隊：即時翻譯每句很短又很頻繁，插隊會讓預先翻譯永遠做不完）
                services.AddSingleton<OpenAiClient>();
                services.AddSingleton<IOpenAiClient>(sp => new ScheduledOpenAiClient(
                    sp.GetRequiredService<OpenAiClient>(),
                    sp.GetRequiredService<LlmLanes>().Translation!));
                services.AddSingleton<IProcessRunner, ProcessRunner>();
                // 不用 AddHttpClient（需要 Microsoft.Extensions.Http 套件），直接注入新的 HttpClient
                services.AddSingleton<IVideoInfoProvider>(sp => new YouTubeOEmbedClient(
                    new HttpClient(),
                    sp.GetRequiredService<ILogger<YouTubeOEmbedClient>>()));
                services.AddSingleton<IYouTubePageInfoProvider>(sp => new YouTubeWatchPageClient(
                    new HttpClient(),
                    sp.GetRequiredService<ILogger<YouTubeWatchPageClient>>()));
                // YouTube 錄影字幕：以 App 用戶端身分（visionOS／iOS）背景下載日文字幕，存在記憶體供翻譯參考
                services.AddSingleton<IYouTubeCaptionProvider>(sp => new YouTubeCaptionClient(
                    new HttpClient(),
                    sp.GetRequiredService<ILogger<YouTubeCaptionClient>>()));
                // YouTube 熱門留言（章節來源）：匿名呼叫 InnerTube next API，照 YouTubeCaptionClient 直接 new HttpClient
                services.AddSingleton<IYouTubeCommentProvider>(sp => new YouTubeCommentClient(
                    new HttpClient(),
                    sp.GetRequiredService<ILogger<YouTubeCommentClient>>()));
                // 原曲長度（章節名稱顯示多久）：查 LRCLIB，照其他 HTTP 用戶端直接 new HttpClient
                services.AddSingleton<ISongInfoProvider>(sp => new LrclibClient(
                    new HttpClient(),
                    sp.GetRequiredService<ILogger<LrclibClient>>()));
                // 影片章節：留言 → ChapterExtractor 整理成章節，記憶體快取 30 分鐘（GET /chapters 用）
                services.AddSingleton<VideoChapterService>();
                // 錄影章節名稱疊加：章節標題翻譯與其狀態（singleton 一處共用），
                // PotPlayerSubtitlePusher 推字幕時把目前章節名加進 SRT 左上角
                services.AddSingleton<ChapterTitleTranslator>();
                services.AddSingleton<ChapterOverlayState>();
                services.AddSingleton<CaptionTrackState>();
                // YouTube 字幕下載（一鍵開台與 PotPlayer 腳本通知共用）
                services.AddSingleton<CaptionLoader>();
                // Gateway 字幕軌（GET /subtitle）：前處理句子＋程式合併的整句組成 WebVTT 給 PotPlayer 直接播放
                services.AddSingleton<CaptionSubtitleService>();
                // 生成式字幕前處理：知識庫模型重新斷句、修正聽錯的詞，結果存 caption_sentences 與記憶體狀態
                services.AddSingleton<CaptionSentenceState>();
                // 錄影開片的詞彙卡準備門：一鍵開台時關上，開台資料探勘結束（或逾時）才打開，
                // 前處理與預先翻譯等門開了才開始（建構子 optional 參數，這裡註冊成 singleton 一處共用）
                services.AddSingleton<CardReadinessGate>();
                services.AddSingleton<CaptionPreprocessor>();
                // 前處理有進度時把整理過的字幕交給正在播放的 PotPlayer（找不到 PotPlayer 時不啟動）
                services.AddSingleton<IPotPlayerSubtitleLoader, PotPlayerSubtitleLoader>();
                services.AddSingleton(sp =>
                {
                    var settingsService = sp.GetRequiredService<ISettingsService>();
                    return new PotPlayerSubtitlePusher(
                        sp.GetRequiredService<CaptionSentenceState>(),
                        sp.GetRequiredService<CaptionTrackState>(),
                        sp.GetRequiredService<CaptionLoader>(),
                        sp.GetRequiredService<IPotPlayerSubtitleLoader>(),
                        isEnabled: () => settingsService.Current.AutoUpdatePotPlayerSubtitle,
                        outputDirectory: Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Translation.Gateway", "subtitles"),
                        logger: sp.GetRequiredService<ILogger<PotPlayerSubtitlePusher>>(),
                        chapterOverlay: sp.GetRequiredService<ChapterOverlayState>(),
                        vodSettings: () => settingsService.Current.Vod,
                        playbackClock: sp.GetRequiredService<IPlaybackClock>());
                });
                // 錄影時的字幕監看高亮：記住最近推送給 PotPlayer 的字幕行（DashboardViewModel 依播放位置找目前那一句）；
                // 建構子訂閱 Pushed，這裡註冊成 singleton 一處共用（播放位置用上面已註冊的 IPlaybackClock）
                services.AddSingleton(sp => new PlaybackLineTracker(
                    sp.GetRequiredService<PotPlayerSubtitlePusher>()));
                // 正在執行的預先翻譯批次：即時翻譯要翻的句子在批次裡時，等批次完成後直接用快取
                // （TranslationManager 與 VodPrefetcher 共用；VodPrefetcher 已依賴 TranslationManager，
                //   獨立一個類別避免 DI 循環）
                services.AddSingleton<PrefetchInFlight>();
                // 錄影預先翻譯：訂閱 PotPlayerSubtitlePusher.Pushed，把播放位置之後的字幕行批次翻好寫進翻譯快取
                services.AddSingleton<VodPrefetcher>();
                // 翻譯時參考 YouTube 字幕：問 PotPlayer 播放位置，在同時段找最像的字幕放進 user prompt；
                // 跳轉（歌曲清單點歌）與查詢位置共用同一個 PotPlayerClock（找視窗的程式共用）
                services.AddSingleton<PotPlayerClock>();
                services.AddSingleton<IPlaybackClock>(sp => sp.GetRequiredService<PotPlayerClock>());
                services.AddSingleton<IPlaybackSeeker>(sp => sp.GetRequiredService<PotPlayerClock>());
                services.AddSingleton<CaptionReference>();
                // 開台前準備 PotPlayer：寫翻譯腳本（port 寫死）與登錄檔設定；安裝目錄不能寫時改用 %LOCALAPPDATA% 的複本，
                // 複本的 Model／Engine 用目錄連結共用 %APPDATA%\<exe 名稱>（Whisper 模型只放一份）
                services.AddSingleton<GatewayEndpoint>();
                services.AddSingleton<IPotPlayerRegistry, PotPlayerRegistry>();
                services.AddSingleton<IPotPlayerProcesses, PotPlayerProcesses>();
                services.AddSingleton(sp => new PotPlayerPreparer(
                    sp.GetRequiredService<ISettingsService>(),
                    sp.GetRequiredService<GatewayEndpoint>(),
                    sp.GetRequiredService<IPotPlayerRegistry>(),
                    TranslateScriptBuilder.LoadTemplate,
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Translation.Gateway", "PotPlayer"),
                    sp.GetRequiredService<ILogger<PotPlayerPreparer>>(),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    new DirectoryJunction(),
                    loadChaptersHook: ChaptersHookBuilder.LoadTemplate));
                // 一鍵開台先經 preparer 準備 PotPlayer，再啟動（可能改用複本的路徑）
                services.AddSingleton(sp => new StreamlinkLauncher(
                    sp.GetRequiredService<IProcessRunner>(),
                    sp.GetRequiredService<ISettingsService>(),
                    sp.GetRequiredService<IVideoInfoProvider>(),
                    sp.GetRequiredService<ILogger<StreamlinkLauncher>>(),
                    sp.GetRequiredService<IYouTubePageInfoProvider>(),
                    sp.GetRequiredService<PotPlayerPreparer>()));
                // 開台前更換 IPv6（MikroTik RouterOS REST API，選用）：開 YouTube 前執行路由器腳本重新取得前綴
                // （自簽憑證的略過驗證只在該類別內部的 HttpClient，不影響其他 HttpClient）
                services.AddSingleton(sp => new RouterIpv6Refresher(
                    sp.GetRequiredService<ISettingsService>(),
                    sp.GetRequiredService<ILogger<RouterIpv6Refresher>>()));
                // 資料探勘（手動觸發：情境 → GLM 拆名詞 → 知識來源 → 建卡）與視窗右下角提示框
                services.AddSingleton<MiningService>();
                // 聽錯字探勘（定期：最近字幕 → GLM 找出 Whisper 聽錯的寫法 → 存成待審核修正）
                services.AddSingleton<MishearingMiningService>();
                // 遊戲用語探勘（定期：直播中重複出現的未知片假名 → GLM 判斷 → 寫進生效遊戲卡的附屬用語卡）
                services.AddSingleton<GameTermDiscoveryService>();
                // 待審數量變更通知：側邊欄（知識庫／聽錯字修正）收到後重新計數
                services.AddSingleton<ReviewCountSignal>();
                // 貼上資料整理（審核頁手動貼上網路資料 → GLM 整理成名詞提案，存檔前不動資料庫）
                services.AddSingleton<TermImportService>();
                // 直播／影片標題翻譯（Dashboard 頁首）：用知識庫模型（沒設定時它自己會改用翻譯模型）；
                // DashboardViewModel 的建構子選用參數由 DI 自動注入
                services.AddSingleton<StreamTitleTranslator>();
                services.AddSingleton<ToastService>();
                // 知識庫模型與知識來源查詢：各自的 HttpClient 都走 ApiLoggingHandler，
                // 請求與回應會出現在「系統記錄」頁的 API 追蹤；逾時由 client 自行以 CTS 控制
                // 知識庫模型全部請求經 LlmLane 排隊（一次只送一個、依優先度，插隊門檻由錄影設定決定）；
                // 翻譯模型同一個 lane（Strata 一次只能跑一個請求：即時翻譯最優先；
                // 翻譯模型不插隊：即時翻譯每句很短又很頻繁，插隊會讓預先翻譯永遠做不完）
                services.AddSingleton(sp => new LlmLanes(new LlmLane("知識庫模型",
                    () => sp.GetRequiredService<ISettingsService>().Current.Vod.PreemptThresholdPercent,
                    TimeProvider.System,
                    sp.GetRequiredService<ILogger<LlmLane>>()),
                    new LlmLane("翻譯模型",
                    // 門檻 0：progress * 100 >= 0 永遠成立，即時翻譯不會插隊預先翻譯
                    () => 0,
                    TimeProvider.System,
                    sp.GetRequiredService<ILogger<LlmLane>>())));
                // 知識庫模型（Z.AI）的健康狀態：連續失敗 3 次標記 5 分鐘，期間知識庫工作改用翻譯模型（直播中改成等恢復）
                services.AddSingleton(sp => new LlmHealth(
                    TimeProvider.System,
                    sp.GetRequiredService<ILogger<LlmHealth>>()));
                // 知識庫模型最外層包 RecordingLorebookLlm：每次呼叫都寫一筆 llm_calls
                // （臨時改用翻譯模型時也會被記到，Model 欄位顯示實際使用的模型）
                services.AddSingleton<ILorebookLlm>(sp => new RecordingLorebookLlm(
                    new ScheduledLorebookLlm(
                        new LorebookLlmClient(
                            new HttpClient(new ApiLoggingHandler(sp.GetRequiredService<ApiTraceStore>(), sp.GetRequiredService<ILogger<ApiLoggingHandler>>()) { InnerHandler = new HttpClientHandler() })
                            { Timeout = Timeout.InfiniteTimeSpan },
                            sp.GetRequiredService<ISettingsService>(),
                            sp.GetRequiredService<ILogger<LorebookLlmClient>>()),
                        sp.GetRequiredService<LlmLanes>().Lorebook,
                        // 備援：Z.AI 不能用時知識庫工作改送翻譯模型（本地 Strata），排翻譯模型的 lane、關閉思考
                        fallback: new LorebookLlmClient(
                            new HttpClient(new ApiLoggingHandler(sp.GetRequiredService<ApiTraceStore>(), sp.GetRequiredService<ILogger<ApiLoggingHandler>>()) { InnerHandler = new HttpClientHandler() })
                            { Timeout = Timeout.InfiniteTimeSpan },
                            sp.GetRequiredService<ISettingsService>(),
                            sp.GetRequiredService<ILogger<LorebookLlmClient>>(),
                            s => s.Translation,
                            disableThinking: true),
                        fallbackLane: sp.GetRequiredService<LlmLanes>().Translation,
                        health: sp.GetRequiredService<LlmHealth>(),
                        // 直播中不借用翻譯模型：知識庫工作等原模型恢復再送（錄影維持備援）
                        allowFallback: () => !sp.GetRequiredService<StreamSession>().IsLive,
                        // 知識庫模型沒有設定時一律直接走備援（翻譯模型）：只設定翻譯模型時的開台資料探勘用
                        primaryConfigured: () => StreamOpenMiningMode.LorebookConfigured(sp.GetRequiredService<ISettingsService>().Current),
                        timeProvider: TimeProvider.System,
                        logger: sp.GetRequiredService<ILogger<ScheduledLorebookLlm>>()),
                    sp.GetRequiredService<LlmCallRecorder>()));
                services.AddSingleton<IKnowledgeSources>(sp => new KnowledgeSourcesClient(
                    new HttpClient(new ApiLoggingHandler(sp.GetRequiredService<ApiTraceStore>(), sp.GetRequiredService<ILogger<ApiLoggingHandler>>()) { InnerHandler = new HttpClientHandler() })
                    { Timeout = Timeout.InfiniteTimeSpan },
                    sp.GetRequiredService<ILogger<KnowledgeSourcesClient>>()));
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();

                var logDir = Path.Combine(appDataDir, "logs");
                Directory.CreateDirectory(logDir);

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.File(
                        path: Path.Combine(logDir, "app-.log"),
                        rollingInterval: RollingInterval.Day,
                        shared: true)
                    .CreateLogger();

                logging.AddSerilog(Log.Logger, dispose: true);
                logging.AddProvider(new UiLoggerProvider(uiLogStore));
            })
            .Build();
    }

    /// <summary>PotPlayer 監聽的 WebApplication；啟動後一直活到程式結束（欄位避免被 GC 回收而停止聆聽）。</summary>
    private static WebApplication? _potPlayerApp;

    /// <summary>從 5000 開始找第一個可用的 port 監聽 /translate 與 /subtitle；全部被佔用時只記 log，不擲例外（程式照常開啟）。</summary>
    private async Task StartPotPlayerListenerAsync()
    {
        // 建立 PotPlayerSubtitlePusher（DI singleton），讓它開始訂閱前處理句子的變更事件；
        // 一併建立 VodPrefetcher（訂閱推送事件，開始依播放位置預先翻譯）與 PlaybackLineTracker
        //（訂閱推送事件，Dashboard 還沒開過也能記住字幕行）
        _ = AppHost.Services.GetRequiredService<PotPlayerSubtitlePusher>();
        _ = AppHost.Services.GetRequiredService<VodPrefetcher>();
        _ = AppHost.Services.GetRequiredService<PlaybackLineTracker>();

        var logger = AppHost.Services.GetRequiredService<ILogger<App>>();
        var endpoint = AppHost.Services.GetRequiredService<GatewayEndpoint>();

        // /translate 與 /subtitle 的對應：每次嘗試新 port 都要重新掛一次
        void MapEndpoints(WebApplication app)
        {
            app.MapPost("/translate", async (HttpContext context) =>
            {
//#if DEBUG
//                context.Request.EnableBuffering();

//                string rawBody;
//                using (var reader = new StreamReader(
//                    context.Request.Body,
//                    Encoding.UTF8,
//                    detectEncodingFromByteOrderMarks: false,
//                    leaveOpen: true))
//                {
//                    rawBody = await reader.ReadToEndAsync();
//                    context.Request.Body.Position = 0;
//                }

//                Debug.WriteLine(rawBody);
//#endif
                var manager = AppHost.Services.GetRequiredService<TranslationManager>();
                var potplayerRequest = await context.Request.ReadFromJsonAsync<PotplayerRequest>();
                // PotPlayer 腳本回報正在播放的檔名或網址：換了 YouTube 影片時背景下載字幕（不阻塞翻譯）
                AppHost.Services.GetRequiredService<CaptionLoader>().NotifyPlaying(potplayerRequest?.Playing);
                var translated = await manager.TranslateAsync(potplayerRequest, context.RequestAborted);

                return Results.Ok(new
                {
                    choices = new[] {
                    new { message = new { role = "assistant", content = translated } }
                }
                });
            });

            // PotPlayer 的 YouTube 字幕軌：架構師的解析腳本會在字幕清單第一位加
            // http://127.0.0.1:5000/subtitle?v=<videoId>&fmt=vtt（fmt 參數忽略）
            app.MapGet("/subtitle", async (HttpContext context) =>
            {
                var videoId = context.Request.Query["v"].ToString();
                if (videoId.Length != 11 ||
                    !videoId.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                {
                    return Results.BadRequest();
                }

                var vtt = await AppHost.Services.GetRequiredService<CaptionSubtitleService>()
                    .GetWebVttAsync(videoId, context.RequestAborted);
                return Results.Text(vtt, "text/vtt; charset=utf-8");
            });

            // PotPlayer 的章節掛勾（ChaptersHook.as）：說明欄沒有章節時向這裡要從留言整理出的章節
            //（架構師的掛勾接在 MediaPlayParse - YouTube.as，開片時同步等待，服務端整體逾時 8 秒）
            app.MapGet("/chapters", async (HttpContext context) =>
            {
                var videoId = context.Request.Query["v"].ToString();
                if (videoId.Length != 11 ||
                    !videoId.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                {
                    return Results.BadRequest();
                }

                var chapters = await AppHost.Services.GetRequiredService<VideoChapterService>()
                    .GetChaptersAsync(videoId, context.RequestAborted);
                return Results.Json(chapters
                    .Select(static chapter => new { time = chapter.StartMs, title = chapter.Title })
                    .OrderBy(static item => item.time)
                    .ToList());
            });
        }

        for (var port = 5000; port <= 5019; port++)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));

            var app = builder.Build();
            MapEndpoints(app);

            try
            {
                await app.StartAsync();
            }
            catch (IOException)
            {
                // 含 Kestrel 的 AddressInUseException：port 被其他程式佔用（實際發生過 VS Code 佔用 5000），換下一個
                logger.LogWarning("port {Port} 已被佔用，改試下一個", port);
                await app.DisposeAsync();
                continue;
            }

            _potPlayerApp = app;
            endpoint.Port = port;
            logger.LogInformation("PotPlayer 監聽：http://127.0.0.1:{Port}", port);
            return;
        }

        logger.LogError("找不到可用的 port（5000～5019），PotPlayer 無法連到 Gateway");
    }
}