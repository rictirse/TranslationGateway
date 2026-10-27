using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;
using Translation.Gateway.Services;

namespace Translation.Gateway.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsService _settingsService;
    private readonly TranslationManager _manager;
    private readonly StreamlinkLauncher _launcher;
    private readonly ISubtitleLogStore _logStore;
    private readonly StreamSession _streamSession;
    private readonly StreamCardRecorder _streamCardRecorder;
    private readonly ReviewCountSignal _reviewSignal;
    private readonly IGlossaryStore _glossaryStore;
    private readonly CaptionLoader _captionLoader;
    private readonly CaptionPreprocessor _captionPreprocessor;
    private readonly IPotPlayerProcesses _potPlayerProcesses;
    private readonly CardReadinessGate _cardGate;
    private readonly LlmHealth _lorebookHealth;
    private readonly RouterIpv6Refresher? _routerRefresher;
    private readonly DispatcherTimer _reviewCountTimer;
    private readonly ILogger<MainViewModel> _logger;

    /// <summary>視窗右下角的提示框（MainWindow 疊層綁定 Items）。</summary>
    public ToastService Toasts { get; }

    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private bool useThroughPass;
    [ObservableProperty] private bool autoUpdatePotPlayerSubtitle;

    /// <summary>目前頁面名稱（Dashboard／Glossary／Mishearing／Settings／Log），供側邊欄顯示選取狀態</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboardPage), nameof(IsGlossaryPage), nameof(IsMishearingPage), nameof(IsSettingsPage), nameof(IsLogPage))]
    private string _currentPage = string.Empty;

    /// <summary>側邊欄底部顯示的模型與端點摘要</summary>
    [ObservableProperty] private string _modelSummary = string.Empty;

    /// <summary>側邊欄底部顯示的知識庫模型健康狀態：健康時為空字串；被標記時顯示改用翻譯模型的提示與再試時間。</summary>
    [ObservableProperty] private string lorebookHealthText = string.Empty;

    /// <summary>側邊欄左下角的版本號：「v1.0.0 · 07c64f4」（git commit 由 .NET SDK 建置時寫入 InformationalVersion）</summary>
    public string AppVersion { get; } = FormatVersion(
        typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>側邊欄「知識庫」的待審數量文字（ReviewCountSignal.FormatBadge；0 筆時為空字串）。</summary>
    [ObservableProperty] private string _glossaryBadgeText = string.Empty;

    /// <summary>側邊欄「聽錯字修正」的待審數量文字（ReviewCountSignal.FormatBadge；0 筆時為空字串）。</summary>
    [ObservableProperty] private string _mishearingBadgeText = string.Empty;

    /// <summary>待審數量刷新正在跑；期間再要求只標記跑完再跑一次（volatile：Changed 可能從背景執行緒觸發）。</summary>
    private volatile bool _refreshingReviewCounts;

    /// <summary>刷新期間又有新要求：跑完這輪再重算一次。</summary>
    private volatile bool _refreshReviewCountsAgain;

    /// <summary>「1.0.0+&lt;40 碼 hash&gt;」→「v1.0.0 · 前 7 碼」；沒有 hash 時只顯示版本。</summary>
    public static string FormatVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return "v?";
        var parts = informationalVersion.Split('+', 2);
        var hash = parts.Length > 1 ? parts[1] : string.Empty;
        return hash.Length == 0 ? $"v{parts[0]}" : $"v{parts[0]} · {hash[..Math.Min(7, hash.Length)]}";
    }

    /// <summary>一鍵開台的進度／結果訊息</summary>
    [ObservableProperty] private string _streamStatus = string.Empty;

    /// <summary>開台失敗缺套件（streamlink／PotPlayer）時的下載頁網址；有值時狀態列下方顯示「前往下載」按鈕。</summary>
    [ObservableProperty] private string? streamHelpUrl;

    /// <summary>「前往下載」按鈕的文字（例如「前往下載 streamlink」）；沒有 HelpUrl 時為 null。</summary>
    [ObservableProperty] private string? streamHelpLabel;

    /// <summary>翻譯模型的開台資料探勘等待中（只設定翻譯模型、缺卡需要探勘時）：側邊欄顯示「中斷探勘，直接播放」按鈕。</summary>
    [ObservableProperty] private bool isWaitingForStreamMining;

    /// <summary>這次開台的狀態列附加說明（已套用詞彙卡、開始資料探勘、已套用設定檔）：beforePlayerStart 掛勾裡收集，
    /// LaunchAsync 回傳後才組進 StreamStatus（掛勾先跑、訊息後組，等待探勘期間顯示等待文字）。</summary>
    private readonly List<string> _streamOpenNotes = new();

    /// <summary>這次開台的詞彙卡準備門是否已交給開台資料探勘的收尾打開（避免 OpenStreamAsync 的 finally 再開一次）。</summary>
    private bool _streamGateHandedOff;

    /// <summary>讀剪貼簿的進入點；測試可替換。剪貼簿被占用（COMException）時回傳 null。</summary>
    public Func<string?> ClipboardReader { get; set; } = () =>
    {
        try
        {
            return Clipboard.GetText();
        }
        catch (COMException)
        {
            return null;
        }
    };

    /// <summary>是／否確認對話框的進入點；測試可替換。回傳 true 表示「是」。</summary>
    public Func<string, bool> ConfirmDialog { get; set; } = message =>
        MessageBox.Show(message, "開啟直播", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>情境存檔詢問對話框的進入點（與 <see cref="ConfirmDialog"/> 分開，方便測試各自計數）；測試可替換。回傳 true 表示「是」。</summary>
    public Func<string, bool> SaveSituationDialog { get; set; } = message =>
        MessageBox.Show(message, "情境存檔", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>狀態列「前往下載」按鈕開網址的進入點；測試可替換。</summary>
    public Action<string> OpenUrl { get; set; } = url =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>上一場成功開台的頻道名稱；開新直播時當作情境存檔詢問的預設設定檔名稱。</summary>
    private string? _lastStreamChannel;

    public bool IsDashboardPage => CurrentPage == "Dashboard";
    public bool IsGlossaryPage => CurrentPage == "Glossary";
    public bool IsMishearingPage => CurrentPage == "Mishearing";
    public bool IsSettingsPage => CurrentPage == "Settings";
    public bool IsLogPage => CurrentPage == "Log";

    public MainViewModel(
        IServiceProvider serviceProvider,
        ISettingsService settingsService,
        TranslationManager manager,
        StreamlinkLauncher launcher,
        ISubtitleLogStore logStore,
        StreamSession streamSession,
        StreamCardRecorder streamCardRecorder,
        ReviewCountSignal reviewSignal,
        IGlossaryStore glossaryStore,
        ToastService toasts,
        CaptionLoader captionLoader,
        CaptionPreprocessor captionPreprocessor,
        IPotPlayerProcesses potPlayerProcesses,
        CardReadinessGate cardGate,
        LlmHealth lorebookHealth,
        ILogger<MainViewModel> logger,
        RouterIpv6Refresher? routerRefresher = null)
    {
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;
        _manager = manager;
        _launcher = launcher;
        _logStore = logStore;
        _streamSession = streamSession;
        _streamCardRecorder = streamCardRecorder;
        _reviewSignal = reviewSignal;
        _glossaryStore = glossaryStore;
        _captionLoader = captionLoader;
        _captionPreprocessor = captionPreprocessor;
        _potPlayerProcesses = potPlayerProcesses;
        _cardGate = cardGate;
        _lorebookHealth = lorebookHealth;
        _routerRefresher = routerRefresher;
        _logger = logger;
        Toasts = toasts;
        // 直接設欄位：還原已存的直通狀態，不觸發存檔
        useThroughPass = settingsService.Current.ThroughPass;
        autoUpdatePotPlayerSubtitle = settingsService.Current.AutoUpdatePotPlayerSubtitle;

        if (string.IsNullOrEmpty(_settingsService.Current.LastSelectedControlName))
        {
            _settingsService.Current.LastSelectedControlName = "Dashboard";
        }
        Navigate(_settingsService.Current.LastSelectedControlName);

        // 待審數量：啟動算一次；探勘／審核頁寫入資料庫時的信號與定時器再補
        _reviewSignal.Changed += OnReviewCountChanged;
        // YouTube 字幕下載狀態：背景執行緒觸發，排到 UI 執行緒顯示
        _captionLoader.StatusChanged += OnCaptionStatusChanged;
        // 生成式字幕前處理狀態：與字幕下載狀態同一條狀態列、同一種處理方式
        _captionPreprocessor.StatusChanged += OnCaptionStatusChanged;
        // 知識庫模型健康狀態：被標記／恢復時更新側邊欄提示（先設一次，啟動前就可能已經被標記）
        _lorebookHealth.Changed += OnLorebookHealthChanged;
        RefreshLorebookHealthText();
        _reviewCountTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _reviewCountTimer.Tick += (_, _) => _ = RefreshReviewCountsAsync();
        _reviewCountTimer.Start();
        _ = RefreshReviewCountsAsync();
    }

    /// <summary>待審數量可能變了（可能從背景執行緒呼叫）：排到 UI 執行緒重新計數。</summary>
    private void OnReviewCountChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => _ = RefreshReviewCountsAsync());
    }

    /// <summary>YouTube 字幕下載狀態變了（可能從背景執行緒呼叫）：排到 UI 執行緒加到狀態列
    /// （前處理的逐批進度會換掉上一行，不會一直往下堆）。</summary>
    private void OnCaptionStatusChanged(string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => StreamStatus = StatusText.AppendOrReplace(StreamStatus, message));
    }

    /// <summary>知識庫模型健康狀態變了（可能從背景執行緒呼叫）：排到 UI 執行緒更新側邊欄提示。</summary>
    private void OnLorebookHealthChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(RefreshLorebookHealthText);
    }

    /// <summary>依 <c>LlmHealth</c> 更新側邊欄文字：被標記時顯示截止時間（當地時間）——直播中暫停資料探勘、
    /// 其他情況改用翻譯模型；健康時清空。</summary>
    private void RefreshLorebookHealthText() =>
        LorebookHealthText = _lorebookHealth.DownUntil is { } until
            ? _streamSession.IsLive
                ? $"知識庫模型暫時無法使用，直播中暫停資料探勘（{until.LocalDateTime:HH:mm} 後再試）"
                : $"知識庫模型暫時無法使用，改用翻譯模型（{until.LocalDateTime:HH:mm} 後再試）"
            : string.Empty;

    /// <summary>重新計算側邊欄兩個待審數量：背景執行緒讀 store，回 UI 執行緒設定顯示文字。
    /// 已經在跑時只標記「跑完再跑一次」，不同時發出多個查詢；讀取失敗記 Warning log、保留舊值，不擲例外。</summary>
    public Task RefreshReviewCountsAsync()
    {
        if (_refreshingReviewCounts)
        {
            _refreshReviewCountsAgain = true;
            return Task.CompletedTask;
        }

        _refreshingReviewCounts = true;
        return RefreshReviewCountsCoreAsync();
    }

    private async Task RefreshReviewCountsCoreAsync()
    {
        try
        {
            do
            {
                _refreshReviewCountsAgain = false;
                (int Glossary, int Mishearing)? counts = null;
                try
                {
                    counts = await Task.Run(() =>
                    {
                        // 知識庫：未被拒絕的卡（GetGlossaryCards 已排除 Rejected）底下 Status = Pending 的名詞數
                        // （與審核頁預設篩選「有待審名詞」同一判斷）；聽錯字修正：Pending 的筆數
                        var cardIds = _glossaryStore.GetGlossaryCards().Select(c => c.Id).ToHashSet();
                        var glossary = _glossaryStore.GetGlossaryTerms()
                            .Count(t => t.Status == GlossaryReviewTermItem.PendingStatus && cardIds.Contains(t.CardId));
                        var mishearing = _glossaryStore.GetMishearingFixes()
                            .Count(f => f.Status == MishearingStatus.Pending);
                        return (glossary, mishearing);
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "計算側邊欄待審數量失敗，保留先前的數字");
                }

                if (counts is { } c)
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher is null)
                    {
                        continue;
                    }

                    await dispatcher.InvokeAsync(() =>
                    {
                        GlossaryBadgeText = ReviewCountSignal.FormatBadge(c.Glossary);
                        MishearingBadgeText = ReviewCountSignal.FormatBadge(c.Mishearing);
                    });
                }
            }
            while (_refreshReviewCountsAgain);
        }
        finally
        {
            _refreshingReviewCounts = false;
        }
    }

    partial void OnUseThroughPassChanged(bool value)
    {
        _settingsService.Current.ThroughPass = value;
        _settingsService.SaveAsync();
    }

    partial void OnAutoUpdatePotPlayerSubtitleChanged(bool value)
    {
        _settingsService.Current.AutoUpdatePotPlayerSubtitle = value;
        _settingsService.SaveAsync();
    }

    [RelayCommand]
    private void Navigate(string destination)
    {
        if (CurrentView is SettingsViewModel settingsVm)
        {
            settingsVm.SaveSettingsCommand.Execute(null);
            // system prompt 或對話設定可能改了：預熱讓下一句不用從頭讀 prompt（沒改時也只是延續快取，無副作用）
            _ = _manager.WarmUpAsync();
        }

        CurrentView = destination switch
        {
            "Dashboard" => _serviceProvider.GetRequiredService<DashboardViewModel>(),
            "Glossary" => _serviceProvider.GetRequiredService<GlossaryReviewViewModel>(),
            "Mishearing" => _serviceProvider.GetRequiredService<MishearingReviewViewModel>(),
            "Settings" => _serviceProvider.GetRequiredService<SettingsViewModel>(),
            "Log" => _serviceProvider.GetRequiredService<LogViewModel>(),
            _ => CurrentView
        };

        // 審核頁是 singleton：每次切進來依 Dashboard 目前的生效清單更新 ● 標記；
        // 沒有未儲存的變更時先重載，抽屜存檔的修改才看得到（有變更時不覆蓋使用者的編輯）
        if (CurrentView is GlossaryReviewViewModel glossaryReview)
        {
            if (!glossaryReview.HasUnsavedChanges)
            {
                glossaryReview.Load();
            }

            glossaryReview.RefreshActiveFlags();
        }

        // 聽錯字修正頁：每次進入都重新讀取（背景探勘隨時會新增）
        if (CurrentView is MishearingReviewViewModel mishearingReview)
        {
            mishearingReview.Load();
        }

        if (CurrentView is null)
        {
            CurrentView = _serviceProvider.GetRequiredService<DashboardViewModel>();
            destination = "Dashboard";
        }

        _settingsService.Current.LastSelectedControlName = destination;
        CurrentPage = destination;
        RefreshModelSummary();
        // 每次切頁都重算待審數量（切頁前審核頁可能存了檔）
        _ = RefreshReviewCountsAsync();
    }

    /// <summary>依目前設定組出「模型 @ 端點」摘要（離開設定頁時會重新整理）</summary>
    private void RefreshModelSummary()
    {
        var t = _settingsService.Current.Translation;
        var model = string.IsNullOrWhiteSpace(t.Model) ? "（未設定模型）" : t.Model;
        var host = t.UseLocalModel
            ? "本機模型"
            : string.IsNullOrWhiteSpace(t.ApiHost) ? "（未設定 API Host）" : t.ApiHost;
        ModelSummary = $"{model}\n{host}";
    }

    /// <summary>一鍵開台：讀剪貼簿網址 → streamlink 查中繼資料 → 帶起 PotPlayer；上一場還在播時先詢問，先自動套用詞彙卡，頻道對得上設定檔時再套用設定檔。</summary>
    [RelayCommand]
    private async Task OpenStreamAsync()
    {
        StreamStatus = "開啟中…";
        // 上一次失敗的「前往下載」按鈕先收掉，等這次結果再決定要不要顯示
        StreamHelpUrl = null;
        StreamHelpLabel = null;
        // 關上詞彙卡準備門：等套用詞彙卡與（需要時的）開台資料探勘生效，前處理與預先翻譯才開始
        _cardGate.Begin();
        _streamOpenNotes.Clear();
        _streamGateHandedOff = false;
        try
        {
            // 剪貼簿只讀一次，給確認詢問與 LaunchAsync 共用
            var clipboardText = ClipboardReader();
            var hasUrl = StreamUrl.TryParse(clipboardText, out var url, out var urlPlatform);

            // 全新的直播：網址與上一次成功開台的不同（Ordinal；從未開過台也算）
            // → 開台成功後清空詞彙卡與情境；同一個網址重開（例如播放器當掉）不清空
            var isNewStream = hasUrl &&
                !string.Equals(url, _settingsService.Current.LastStreamUrl, StringComparison.Ordinal);

            // 上一場還在執行時先詢問；使用者已自己關掉 PotPlayer 時 streamlink 也跟著結束，不再詢問。
            // 沒有網址時沿用 LaunchAsync 的失敗訊息，不詢問
            var stoppedPrevious = false;
            if (hasUrl && _launcher.IsRunning)
            {
                var confirmed = ConfirmDialog(
                    $"目前正在播「{_launcher.CurrentDescription}」。\n要關掉它（包含 PotPlayer）並開啟新的直播嗎？");
                if (!confirmed)
                {
                    StreamStatus = "已取消，繼續播放目前的直播";
                    return;
                }

                _launcher.StopCurrent();
                stoppedPrevious = true;
                // 讓之後的字幕不再記到舊直播；成功開新場時會再設定新的 StreamId
                _streamSession.CurrentStreamId = null;
            }

            // PotPlayer 準備：Gateway 寫的翻譯腳本與登錄檔設定只在 PotPlayer 啟動時讀取，
            // 還開著的 PotPlayer 得先關閉（它關閉時又會把設定寫回登錄檔）
            if (hasUrl)
            {
                if (stoppedPrevious)
                {
                    // 剛被結束的 PotPlayer 需要一點時間才會從程序清單消失
                    await _potPlayerProcesses.WaitAllExitedAsync(TimeSpan.FromSeconds(3));
                }

                if (_potPlayerProcesses.AnyRunning())
                {
                    var closePotPlayer = ConfirmDialog(
                        "PotPlayer 正在執行。\n要套用 Gateway 的翻譯腳本和設定，必須先關閉它（PotPlayer 關閉時會把設定寫回登錄檔）。\n要關閉 PotPlayer 並開啟直播嗎？");
                    if (!closePotPlayer)
                    {
                        StreamStatus = "已取消：PotPlayer 還開著";
                        return;
                    }

                    if (!await _potPlayerProcesses.CloseAllAsync(TimeSpan.FromSeconds(10)))
                    {
                        StreamStatus = "PotPlayer 沒有在 10 秒內關閉，請手動關閉後再按一次開啟直播";
                        return;
                    }
                }
            }

            // 全新的直播：上一場的情境還沒存成設定檔時先詢問要不要存（在 LaunchAsync 之前；
            // 使用者取消上一場的確認時不會走到這裡）
            if (isNewStream)
            {
                var dashboardForSave = _serviceProvider.GetRequiredService<DashboardViewModel>();
                var saveName = dashboardForSave.SituationSaveTarget(_lastStreamChannel);
                if (saveName is not null && SaveSituationDialog(
                        $"情境還沒存成設定檔，要存到「{saveName}」嗎？\n\n選「否」會直接清掉情境。"))
                {
                    dashboardForSave.SaveProfileAs(saveName);
                }
            }

            // 開台的準備（狀態設定、套用詞彙卡與設定檔、建立直播紀錄、開台資料探勘）移到 beforePlayerStart 掛勾：
            // PotPlayer 啟動前做完；翻譯模型的開台探勘會在掛勾裡等待（最多 Vod.MiningWaitSeconds 秒），等完才會啟動播放器
            Func<StreamMetadata?, CancellationToken, Task> beforePlayerStart =
                (metadata, cancellationToken) => PrepareStreamAsync(metadata, cancellationToken, url, urlPlatform, hasUrl, isNewStream);
            var result = await _launcher.LaunchAsync(clipboardText, beforePlayerStart);

            // 被 YouTube 擋下（匿名存取次數太多）：不開播。有設定路由器時先換 IPv6 再重試一次，沒有時提示使用者
            if (result.Blocked)
            {
                result = await HandleBlockedLaunchAsync(clipboardText, beforePlayerStart, result);
                // 被擋（含重試仍被擋）的狀態列已在 HandleBlockedLaunchAsync 設好，不重新覆寫
                if (result.Blocked) return;
            }

            StreamStatus = ComposeStreamStatus(result.Message);
            StreamHelpUrl = result.HelpUrl;
            StreamHelpLabel = result.HelpLabel;

            if (!result.Success) return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "開啟直播失敗");
            StreamStatus = $"開啟失敗：{ex.Message}";
        }
        finally
        {
            // 開台資料探勘沒有啟動時自己在這裡開門（所有 return 與例外都會走到）；
            // 有啟動時交給 MineThenOpenGateAsync／翻譯模型等待流程的 finally
            if (!_streamGateHandedOff)
            {
                _cardGate.Complete();
            }
        }
    }

    /// <summary>組狀態列文字：訊息之後接上這次開台的附加說明（<see cref="_streamOpenNotes"/>，一行一條）。</summary>
    private string ComposeStreamStatus(string message) =>
        _streamOpenNotes.Count == 0 ? message : message + "\n" + string.Join("\n", _streamOpenNotes);

    /// <summary>被 YouTube 擋下時的處理：有設定路由器就先請路由器更換 IPv6（只呼叫同網段的那幾台）再重試一次開台；
    /// 沒有設定路由器時提示使用者可能是匿名存取被擋。被擋與重試的狀態都直接寫進 <see cref="StreamStatus"/>，
    /// 回傳值為重試後的結果（不重試時回傳原本的結果，呼叫端不再覆寫狀態列）。</summary>
    private async Task<StreamLaunchResult> HandleBlockedLaunchAsync(string? clipboardText,
        Func<StreamMetadata?, CancellationToken, Task> beforePlayerStart, StreamLaunchResult result)
    {
        if (_routerRefresher?.IsConfigured != true)
        {
            StreamStatus = ComposeStreamStatus(
                $"{result.Message}\n可能是匿名存取 YouTube 的次數太多，這個 IP 暫時被擋。可以換一個 IP（例如重新撥號）或稍後再試。");
            return result;
        }

        StreamStatus = "YouTube 擋下了這次存取，正在請路由器更換 IPv6…";
        var renewed = false;
        try
        {
            var routerResults = await _routerRefresher.RefreshAsync();
            _streamOpenNotes.AddRange(routerResults.Select(r => r.Message));
            renewed = routerResults.Any(r => r.Success);
        }
        catch (Exception ex)
        {
            // RefreshAsync 理論上不擲例外（取消除外）；保險起來再包一層，失敗不能擋住後續處理
            _logger.LogWarning(ex, "更換 IPv6 失敗");
            _streamOpenNotes.Add($"更換 IPv6 失敗：{ex.Message}");
        }

        if (!renewed)
        {
            StreamStatus = ComposeStreamStatus($"{result.Message}\n更換 IPv6 失敗");
            return result;
        }

        // 用同一份剪貼簿文字與同一個掛勾重試一次
        var retry = await _launcher.LaunchAsync(clipboardText, beforePlayerStart);
        if (retry.Blocked)
        {
            StreamStatus = ComposeStreamStatus($"{retry.Message}\n換了 IPv6 仍被擋，可能是 ISP 每次都發同一段前綴，請稍後再試");
            return retry;
        }

        // 重試成功（或其他結果）：照一般開台流程走，狀態列由呼叫端組（含路由器的說明）
        return retry;
    }

    /// <summary>開台的準備工作（<see cref="StreamlinkLauncher.LaunchAsync"/> 的 beforePlayerStart 掛勾，PotPlayer 啟動前執行）：
    /// 設定直播狀態、記住網址、字幕載入、清空與套用詞彙卡／設定檔、建立直播紀錄，最後依設定的模型決定開台資料探勘——
    /// 知識庫模型在背景跑（現行行為）；只設定翻譯模型時在這裡等待完成（還沒有字幕，不會和即時翻譯搶模型）。
    /// 丟例外時由 Launcher 記 log 後照常啟動播放器；說明文字收集在 <see cref="_streamOpenNotes"/>。</summary>
    private async Task PrepareStreamAsync(StreamMetadata? metadata, CancellationToken cancellationToken,
        string? url, StreamPlatform urlPlatform, bool hasUrl, bool isNewStream)
    {
        // 這場是直播還是錄影：直播中知識庫模型故障時不借用翻譯模型（等恢復），錄影維持備援
        _streamSession.IsLive = !(metadata?.IsVod ?? false);
        RefreshLorebookHealthText();

        var dashboard = _serviceProvider.GetRequiredService<DashboardViewModel>();

        // 上一場還有知識庫工作在等模型恢復：先取消並等它結束，這一場的開台探勘才不會被 IsMining 略過
        if (_lorebookHealth.HasWaiters)
        {
            await dashboard.CancelMiningAsync();
            _logger.LogInformation("換場：取消等待知識庫模型恢復的資料探勘");
        }

        // 記住這場的網址與頻道名稱：下一次開台判斷是否全新的直播，情境存檔詢問也用它當預設名稱
        _settingsService.Current.LastStreamUrl = url ?? string.Empty;
        _ = _settingsService.SaveAsync();
        _lastStreamChannel = metadata?.Author;

        // 換直播後舊字幕一律不能再用：清空字幕狀態，並讓進行中的下載作廢（結果被丟掉）
        _captionLoader.Reset();
        string? captionVideoId = null;
        if (metadata is { Platform: StreamPlatform.YouTube, IsVod: true })
        {
            captionVideoId = !string.IsNullOrWhiteSpace(metadata.Id)
                ? metadata.Id
                : YouTubeVideoId.TryParse(url);
            if (captionVideoId is not null)
            {
                // 不 await：開台流程不能等字幕下載
                _ = _captionLoader.LoadAsync(captionVideoId, url);
            }
        }

        // 章節名稱疊加：YouTube 錄影才在背景載入章節（說明欄優先，沒有才用留言セトリ）；
        // 不 await，開台流程不能被章節下載或標題翻譯拖住。直播、Twitch 或解析不到 videoId 時清掉上一支影片的章節
        var chapterOverlay = _serviceProvider.GetRequiredService<ChapterOverlayState>();
        if (metadata is { Platform: StreamPlatform.YouTube, IsVod: true } && captionVideoId is not null)
        {
            _ = chapterOverlay.LoadAsync(captionVideoId, metadata.Description);
        }
        else
        {
            chapterOverlay.Reset();
        }

        // 先清掉目前直播：舊直播的詞彙卡紀錄不會被這次的自動套用或之後的手動變更影響；
        // 建立新的直播紀錄後會再設定新的 StreamId
        _streamSession.CurrentStreamId = null;

        // 全新的直播：清空生效中的詞彙卡與情境（角色表、修正規則不動）；開台失敗時什麼都不清
        if (isNewStream)
        {
            dashboard.ClearForNewStream();
        }

        // 開台自動套用詞彙卡：主播角色卡 → 團體人名 → 標題連動成員 → 標題遊戲卡（在設定檔邏輯之前，
        // 之後找不到設定檔也不會跳過）。比對用的網址優先用 oEmbed 的頻道網址（含 @handle）
        var glossaryUrl = string.IsNullOrWhiteSpace(metadata?.ChannelUrl) ? url : metadata.ChannelUrl;
        IReadOnlyList<GlossaryCardMatch> matches = hasUrl
            ? dashboard.ApplyGlossaryForStream(glossaryUrl, metadata?.Author, metadata?.Title)
            : [];
        if (hasUrl)
        {
            // 開台比對結果一律寫 log：比對不到任何卡時才有跡可循
            _logger.LogInformation("開台比對詞彙卡：頻道「{Channel}」，主播卡 {Host}，團體 {Group}，連動 {Collab}，作品 {Game}",
                string.IsNullOrWhiteSpace(metadata?.Author) ? "無" : metadata.Author,
                MatchNames(matches, StreamCardReason.Host),
                MatchNames(matches, StreamCardReason.HostGroup),
                MatchNames(matches, StreamCardReason.Title),
                MatchNames(matches, StreamCardReason.TitleGame));
        }
        var appliedNames = matches
            .Where(m => m.Reason != StreamCardReason.Parent)
            .Select(m => m.Reason switch
            {
                StreamCardReason.Title => $"{m.Card.Name}（連動）",
                StreamCardReason.TitleGame => $"{m.Card.Name}（作品）",
                _ => m.Card.Name,
            })
            .ToList();
        if (appliedNames.Count > 0)
        {
            _streamOpenNotes.Add($"已套用詞彙卡：{string.Join("、", appliedNames)}");
        }

        // 這場的標題：頁首先顯示原文，背景翻譯（在套用詞彙卡之後，標題翻譯的 prompt 才用得到這場的名詞）
        dashboard.SetStreamTitle(metadata?.Title);

        // 還缺哪種卡（主播角色卡、遊戲直播的作品卡）：缺卡就開台自動資料探勘
        // （探勘會跳過已經有卡的名詞，不會重複建主播卡）
        var miningReason = StreamMiningDecider.MissingCardReason(
            matches, metadata?.Platform ?? urlPlatform, metadata?.Category);
        var miningModel = StreamOpenMiningModel.None;
        if (hasUrl && miningReason is not null)
        {
            miningModel = StreamOpenMiningMode.Decide(_settingsService.Current);
            if (miningModel != StreamOpenMiningModel.None)
            {
                _streamOpenNotes.Add($"{miningReason}，開始資料探勘…");
            }
            else
            {
                _logger.LogInformation("{Reason}；未設定知識庫模型，略過開台資料探勘", miningReason);
            }
        }

        // 全新的直播已清空情境：即使是同一個設定檔也要重新套用，把它存的情境帶回來
        var name = StreamProfiles.FindForChannel(_settingsService.Current, metadata?.Author);
        if (name is not null &&
            (isNewStream || !string.Equals(name, _settingsService.Current.ActiveProfileName, StringComparison.Ordinal)) &&
            dashboard.ApplyProfileByName(name))
        {
            _streamOpenNotes.Add($"已套用設定檔「{name}」");
        }

        // 直播紀錄：新增一筆 Streams，之後的字幕都帶這個 StreamId；Platform 優先用
        // streamlink 查到的，沒有中繼資料時用網址解析的
        var streamPlatform = metadata?.Platform ?? urlPlatform;
        try
        {
            var streamId = await Task.Run(() => _logStore.CreateStream(new StreamRecord(
                streamPlatform.ToString(),
                url ?? string.Empty,
                metadata?.Author,
                metadata?.Title,
                metadata?.Category,
                HostCharacterCardId(matches),
                metadata?.IsVod ?? false,
                metadata?.StartTime,
                metadata?.EndTime)), cancellationToken);
            // 回傳 null（資料庫不可用）也要寫入 null，避免把字幕記到上一場直播
            _streamSession.CurrentStreamId = streamId;
            if (streamId is not null)
            {
                // 記錄這場直播生效過的每一張卡（含沿用的遊戲卡）；背景執行，失敗只記 log
                _ = _streamCardRecorder.RecordStreamStart(streamId.Value, dashboard.ActiveGlossaryIds, matches);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "建立直播紀錄失敗");
            _streamSession.CurrentStreamId = null;
        }

        // 開台自動資料探勘（等直播紀錄建立完、CurrentStreamId 設好才啟動，名詞候選才會歸到這場直播）：
        // 知識庫模型有設定 → 背景跑（不等待完成，開台指令不能被探勘拖住），門交給探勘的收尾打開；
        // 只設定翻譯模型 → 在掛勾裡等待完成（PotPlayer 等探勘做完才啟動，這時還沒有字幕不會搶模型）；
        // 都沒設定 → 只記 log（上面已記）
        if (miningModel == StreamOpenMiningModel.Lorebook)
        {
            _streamGateHandedOff = true;
            _ = MineThenOpenGateAsync(dashboard, metadata?.Author, metadata?.Title, metadata?.Description);
        }
        else if (miningModel == StreamOpenMiningModel.Translation)
        {
            await MineStreamInfoBeforePlayerAsync(dashboard, metadata?.Author, metadata?.Title, metadata?.Description);
        }
    }

    /// <summary>翻譯模型的開台資料探勘（只設定翻譯模型時）：在 beforePlayerStart 掛勾裡等待完成，PotPlayer 等探勘做完才啟動
    /// （這時還沒有字幕進來，不會和即時翻譯搶翻譯模型）；最多等 <c>Vod.MiningWaitSeconds</c> 秒，逾時（或使用者按中斷）取消後直接播放。
    /// 例外已在 Dashboard 內處理；門在 finally 打開（外層 finally 不再開一次）。</summary>
    private async Task MineStreamInfoBeforePlayerAsync(DashboardViewModel dashboard, string? author, string? title, string? description)
    {
        StreamStatus = "正在查資訊卡（用翻譯模型），完成後就開始播放影片…";
        IsWaitingForStreamMining = true;
        var waitSeconds = Math.Max(0, _settingsService.Current.Vod.MiningWaitSeconds);
        try
        {
            var mining = dashboard.MineStreamInfoAsync(author, title, description);
            var completed = await Task.WhenAny(mining, Task.Delay(TimeSpan.FromSeconds(waitSeconds)));
            if (completed != mining)
            {
                await dashboard.CancelMiningAsync();
                _logger.LogInformation("開台資料探勘超過 {Seconds} 秒，取消後直接播放", waitSeconds);
                _streamOpenNotes.Add("開台資料探勘超過時間，已取消");
            }
        }
        finally
        {
            IsWaitingForStreamMining = false;
            // 門在這裡開：探勘結束（完成、失敗、取消或逾時）前處理與預先翻譯不開始
            _cardGate.Complete();
            _streamGateHandedOff = true;
        }
    }

    /// <summary>「中斷探勘，直接播放」按鈕：取消執行中的開台資料探勘——探勘的 Task 結束後掛勾接著完成，PotPlayer 隨即啟動；
    /// 已經建好的卡保留（Dashboard 既有行為會跳「已取消資料探勘」提示框）。</summary>
    [RelayCommand]
    private void SkipStreamMining() =>
        _ = _serviceProvider.GetRequiredService<DashboardViewModel>().CancelMiningAsync();

    /// <summary>狀態列的「前往下載」按鈕：開啟這次開台失敗原因（缺 streamlink／PotPlayer）的下載頁。</summary>
    [RelayCommand]
    private void OpenStreamHelp()
    {
        if (StreamHelpUrl is { } url)
        {
            OpenUrl(url);
        }
    }

    /// <summary>開台資料探勘跑完（成功或失敗）後打開詞彙卡準備門：前處理與預先翻譯的 prompt 這時才開始組。
    /// 例外已在 Dashboard 內處理，這裡只要確保門一定會開。</summary>
    private async Task MineThenOpenGateAsync(DashboardViewModel dashboard, string? author, string? title, string? description)
    {
        try
        {
            await dashboard.MineStreamInfoAsync(author, title, description);
        }
        finally
        {
            _cardGate.Complete();
        }
    }

    /// <summary>比對結果中指定原因的卡名（多張用「、」連接）；沒有時為「無」。</summary>
    private static string MatchNames(IReadOnlyList<GlossaryCardMatch> matches, string reason)
    {
        var names = matches.Where(m => m.Reason == reason).Select(m => m.Card.Name).ToList();
        return names.Count == 0 ? "無" : string.Join("、", names);
    }

    /// <summary>直播紀錄的主播角色卡：比對結果中第一張原因 Host 且種類 Character 的卡；沒有為 null。</summary>
    private static long? HostCharacterCardId(IReadOnlyList<GlossaryCardMatch> matches) =>
        matches.FirstOrDefault(m =>
            m.Reason == StreamCardReason.Host &&
            m.Card.Kind == GlossaryKind.Character)?.Card.Id;
}