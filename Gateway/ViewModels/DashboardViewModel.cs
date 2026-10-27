using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;
using Translation.Gateway.Services;

namespace Translation.Gateway.ViewModels;

/// <summary>情境下方的結果標籤：顯示文字與種類（種類決定顏色，對照 MiningOutcome）。
/// copyText 為點擊標籤時要複製的名詞（ null 表示不可點）。</summary>
public sealed class MiningTag(string text, string kind, string? copyText = null)
{
    public string Text { get; } = text;

    public string Kind { get; } = kind;

    /// <summary>點一下標籤要複製到剪貼簿的名詞（不含「已比對：」等前綴）。</summary>
    public string? CopyText { get; } = copyText;

    /// <summary>是否可點擊複製（候選數量標籤等無名詞的不可點）。</summary>
    public bool CanCopy => !string.IsNullOrEmpty(CopyText);
}

public partial class DashboardViewModel : ObservableObject
{
    private const int MaxJobs = 200;

    /// <summary>統計列的時間窗</summary>
    private static readonly TimeSpan StatsWindow = TimeSpan.FromMinutes(5);

    private readonly ISettingsService _settings;
    private readonly TranslationManager _manager;
    private readonly IGlossaryStore _glossaryStore;
    private readonly GlossaryState _glossaryState;
    private readonly SubtitleLogWriter _logWriter;
    private readonly StreamCardRecorder _streamCardRecorder;
    private readonly KeywordGlossary _keywordGlossary;
    private readonly MishearingFixState _mishearingFixes;
    private readonly MiningService _mining;
    private readonly MishearingMiningService _mishearingMining;
    private readonly GameTermDiscoveryService _termDiscovery;
    private readonly ISubtitleLogStore _subtitleLogStore;
    private readonly ToastService _toasts;
    private readonly StreamSession _streamSession;
    private readonly ReviewCountSignal _reviewSignal;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly List<TranslationJob> _pending = new();
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _mishearingMiningTimer;
    private readonly DispatcherTimer _mishearingStatusTimer;

    /// <summary>最近推送給 PotPlayer 的字幕行（錄影時找「目前播放到的那一句」用）；null 時不啟用高亮。</summary>
    private readonly PlaybackLineTracker? _playbackLines;

    /// <summary>播放位置來源（同 PotPlayerSubtitlePusher／CaptionReference 用的時鐘）；null 時不啟用高亮。</summary>
    private readonly IPlaybackClock? _playbackClock;

    /// <summary>錄影時的「目前播放」高亮計時器；兩個選用參數都有才建立。</summary>
    private readonly DispatcherTimer? _playingTimer;

    /// <summary>目前高亮（IsPlaying = true）的那一列；沒有高亮時為 null。只在 UI 執行緒存取。</summary>
    private TranslationJob? _playingJob;

    /// <summary>播放位置查詢進行中：上一次還沒回來就略過這次 tick。只在 UI 執行緒存取。</summary>
    private bool _playingQueryInFlight;

    /// <summary>章節名稱疊加狀態（錄影開片時載入章節，T-2107 起）：歌曲清單的資料來源；null 時相關功能停用。</summary>
    private readonly ChapterOverlayState? _chapterOverlay;

    /// <summary>讓播放器跳到指定位置（點歌曲清單的一列用）；null 時點歌不做事。</summary>
    private readonly IPlaybackSeeker? _playbackSeeker;

    /// <summary>已經因為第一次載到章節而自動切到歌曲清單分頁的影片 Id；換片時因 Id 比對不同自動重置。</summary>
    private string? _songListAutoSwitchedVideoId;

    /// <summary>時間來源（測試可注入可手動推進的 TimeProvider）。</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>目前影片的生成式字幕狀態；有字幕時代表錄影開片，不自動跑聽錯字探勘（null 一律照舊）。</summary>
    private readonly CaptionTrackState? _captions;

    /// <summary>聽錯字探勘要用的最近字幕（原文與接收時間），只保留時間窗內的。</summary>
    private readonly List<(string Text, DateTimeOffset ReceivedAt)> _mishearingLines = [];

    /// <summary>上一次聽錯字探勘之後收到的新字幕句數。</summary>
    private int _mishearingNewLines;

    /// <summary>上一次聽錯字探勘的時間（起始值＝建構時間，代表程式啟動）。</summary>
    private DateTimeOffset _lastMishearingMiningAt;

    /// <summary>最近一次「真的執行」聽錯字探勘的時間；null 表示還沒探勘過（建構時的基準不算）。</summary>
    private DateTimeOffset? _lastMishearingMinedAt;

    /// <summary>最近一次聽錯字探勘的結果描述（狀態膠囊 ToolTip 用）；null 表示還沒跑過。</summary>
    private string? _lastMishearingResultText;

    /// <summary>聽錯字探勘執行中（與資料探勘的 IsMining 互不阻擋）。</summary>
    private bool _isMishearingMining;

    /// <summary>遊戲用語探勘的計數器：累計本場直播片假名詞出現的句數，換直播時重置。</summary>
    private readonly TermDiscoveryCounter _termCounter = new();

    /// <summary>上一次遊戲用語探勘（或程式啟動）的時間；沒有候選詞時不更新。</summary>
    private DateTimeOffset _lastTermDiscoveryAt;

    /// <summary>遊戲用語探勘執行中（與聽錯字探勘、資料探勘互不阻擋）。</summary>
    private bool _isTermDiscoveryRunning;

    /// <summary>執行中的資料探勘的取消令牌；沒有在執行時為 null。</summary>
    private CancellationTokenSource? _miningCts;

    /// <summary>執行中的資料探勘整個流程（RunMiningCoreAsync）的 Task；CancelMiningAsync 等它結束用。</summary>
    private Task? _miningTask;

    /// <summary>知識庫模型的健康狀態（可為 null，測試不傳）：直播中故障等恢復時，探勘進度顯示暫停提示。</summary>
    private readonly LlmHealth? _lorebookHealth;

    /// <summary>直播／影片標題翻譯（可為 null，測試不傳）：null 時頁首只顯示原文。</summary>
    private readonly StreamTitleTranslator? _titleTranslator;

    /// <summary>頁首的預設文字（還沒開台時）。</summary>
    private const string DefaultHeaderTitle = "字幕監看";

    /// <summary>SetStreamTitle 的遞增版本號：換場後回來的舊翻譯結果靠它丟掉。</summary>
    private int _titleVersion;

    /// <summary>進行中的標題翻譯的取消令牌；沒有在翻譯時為 null。</summary>
    private CancellationTokenSource? _titleCts;

    /// <summary>詞彙卡型錄（記憶體）：搜尋與組 prompt 段落都用它，不直接查資料庫。</summary>
    private GlossaryCatalog _glossaryCatalog = new([], []);

    /// <summary>生效中的詞彙卡 Id（依生效順序）。</summary>
    private readonly List<long> _activeGlossaryIds = [];

    /// <summary>每張生效卡的原因代碼（StreamCardReason），顯示 ReasonText 用。</summary>
    private readonly Dictionary<long, string> _activeReasons = [];

    /// <summary>生效中詞彙卡 Id 的唯讀快照（審核頁判斷儲存的卡是否生效中用）。</summary>
    public IReadOnlyList<long> ActiveGlossaryIds => [.. _activeGlossaryIds];

    /// <summary>套用設定檔時批次改寫各欄位：期間略過個別欄位的存檔與預熱，最後統一處理一次</summary>
    private bool _applyingProfile;

    public ObservableCollection<TranslationJob> Jobs { get; } = new();

    [ObservableProperty] private TranslationJob? _selectedJob;

    [ObservableProperty] private string _replacementRules;

    /// <summary>原文修正規則（翻譯前套用，修正 Whisper 聽錯）</summary>
    [ObservableProperty] private string _sourceReplacementRules;

    [ObservableProperty] private string _streamSituation;

    [ObservableProperty] private string _characterTable;

    /// <summary>編輯儲存格中：鎖定滾動，新字幕先進暫存</summary>
    [ObservableProperty] private bool _isLocked;

    /// <summary>原文修正後重翻中</summary>
    [ObservableProperty] private bool _isRetranslating;

    [ObservableProperty] private int _pendingCount;

    /// <summary>手動暫停捲動：新字幕先進暫存，方便回頭看（與編輯鎖定各自獨立）</summary>
    [ObservableProperty] private bool _isPaused;

    /// <summary>直播資訊展開狀態：只記住展開狀態寫進設定檔，與直播資訊內容無關（不重置對話、不預熱）</summary>
    [ObservableProperty] private bool _streamInfoExpanded;

    /// <summary>修正規則展開狀態：只記住展開狀態寫進設定檔</summary>
    [ObservableProperty] private bool _rulesExpanded;

    public string PauseButtonText => IsPaused ? "繼續捲動" : "暫停捲動";

    /// <summary>最近一次操作的結果（重翻失敗、已複製等），顯示在表格上方狀態列右側</summary>
    [ObservableProperty] private string _actionMessage = string.Empty;

    /// <summary>頁首的大字標題：還沒開台時為「字幕監看」，開台後為直播／影片標題（翻譯完成後換譯文）</summary>
    [ObservableProperty] private string _headerTitle = DefaultHeaderTitle;

    /// <summary>頁首的小字原文（標題翻譯成功後顯示）；null 時隱藏</summary>
    [ObservableProperty] private string? _headerSubtitle;

    /// <summary>設定檔下拉選單（可直接輸入新名稱）</summary>
    public ObservableCollection<string> ProfileNames { get; } = new();

    [ObservableProperty] private string _profileName;

    [ObservableProperty] private string _profileMessage = string.Empty;

    /// <summary>近 5 分鐘統計摘要</summary>
    [ObservableProperty] private string _statsText = string.Empty;

    /// <summary>統計顯示警示色：有逾時或 P95 接近逾時門檻</summary>
    [ObservableProperty] private bool _statsWarning;

    /// <summary>聽錯字探勘狀態膠囊：間隔、上次探勘時間、倒數與新字幕數（每秒更新）</summary>
    [ObservableProperty] private string _mishearingStatusText = string.Empty;

    /// <summary>聽錯字探勘狀態膠囊的完整說明：運作方式與最近一次結果</summary>
    [ObservableProperty] private string _mishearingStatusToolTip = string.Empty;

    // ===== 歌曲清單（錄影章節） =====

    /// <summary>歌曲清單：每章一列（中日文、時間範圍），點一下讓 PotPlayer 跳到該時間。</summary>
    public ObservableCollection<SongListItem> SongList { get; } = [];

    /// <summary>顯示「歌曲清單」分頁：錄影（chapterOverlay.VideoId 有值）而且不是直播時才顯示。</summary>
    [ObservableProperty] private bool _showSongList;

    /// <summary>直播資訊左欄分頁的索引：0 = 情境、1 = 歌曲清單。</summary>
    [ObservableProperty] private int _infoTabIndex;

    /// <summary>歌曲清單的來源說明（例：「來源：留言セトリ」）；沒有章節時為貼上セトリ的提示。</summary>
    [ObservableProperty] private string _songListSourceText = string.Empty;

    /// <summary>歌曲清單的短訊息（貼上結果、跳轉失敗等）；空字串時不顯示。</summary>
    [ObservableProperty] private string _songListMessage = string.Empty;

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

    // ===== 詞彙卡 =====

    /// <summary>詞彙卡搜尋文字（中文或日文）；變更時重新搜尋（記憶體中，不查資料庫）</summary>
    [ObservableProperty] private string _glossarySearchText = string.Empty;

    /// <summary>是否顯示 User／System Prompt 詳細內容（預設隱藏，空間留給詞彙卡區）</summary>
    [ObservableProperty] private bool _showPromptDetails;

    /// <summary>詞彙卡區狀態：生效張數與 prompt 段落大小，或讀取失敗訊息</summary>
    [ObservableProperty] private string _glossaryStatus = string.Empty;

    /// <summary>直播資訊標題列的詞彙卡摘要（生效卡）；沒有任何卡時為空字串</summary>
    [ObservableProperty] private string _glossarySummaryText = string.Empty;

    /// <summary>詞彙卡搜尋結果下拉是否打開（搜尋文字非空白且有結果時）</summary>
    [ObservableProperty] private bool _isGlossarySearchOpen;

    /// <summary>搜尋結果</summary>
    public ObservableCollection<GlossaryCardItem> GlossaryResults { get; } = new();

    /// <summary>右側名詞編輯抽屜；null 表示關閉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGlossaryDrawerOpen))]
    private GlossaryDrawerViewModel? _glossaryDrawer;

    /// <summary>抽屜是否開著（給 XAML 的 Visibility 綁定）。</summary>
    public bool IsGlossaryDrawerOpen => GlossaryDrawer is not null;

    /// <summary>生效中的卡：依「上層卡在前、子卡緊接在後」排列</summary>
    public ObservableCollection<GlossaryCardItem> ActiveGlossaryCards { get; } = new();

    /// <summary>情境存檔時比對到的卡名與資料探勘結果（顯示各種顏色的標籤）</summary>
    public ObservableCollection<MiningTag> MiningResultTags { get; } = new();

    // ===== 資料探勘 =====

    /// <summary>資料探勘執行中：按鈕停用、顯示進度與取消鈕（其他功能不受影響）</summary>
    [ObservableProperty] private bool _isMining;

    /// <summary>資料探勘的進度文字（例如「查詢中 2/5：ほの暮しの庭」）</summary>
    [ObservableProperty] private string _miningProgressText = string.Empty;

    /// <summary>知識庫模型（Lorebook）的 ApiHost 與 Model 都已填寫；手動探勘、成員探勘與排程聽錯字探勘的啟用條件。</summary>
    public bool MiningConfigured => StreamOpenMiningMode.LorebookConfigured(_settings.Current);

    /// <summary>未設定知識庫模型時的提示（資料探勘按鈕附近）：只有開台探勘會用翻譯模型執行。</summary>
    public bool ShowLorebookMissingHint => !MiningConfigured;

    /// <summary>資料探勘按鈕的啟用條件：模型已設定、情境有內容、沒有探勘正在執行。</summary>
    public bool CanStartMining => !IsMining && MiningConfigured && !string.IsNullOrWhiteSpace(StreamSituation);

    /// <summary>停用時的 ToolTip：沒有設定或沒有情境；啟用時為 null（不顯示）。</summary>
    public string? MiningDisabledToolTip
    {
        get
        {
            if (IsMining)
            {
                return null;
            }
            if (!MiningConfigured)
            {
                return "請先到 設定 → 知識庫模型 填寫並測試連線";
            }
            return string.IsNullOrWhiteSpace(StreamSituation) ? "請先在情境輸入內容" : null;
        }
    }

    /// <summary>臨時角色表的行數（不含空行與 # 開頭的註解行）</summary>
    public int CharacterTableLineCount =>
        CharacterTable.Split('\n').Count(line =>
        {
            var trimmed = line.Trim();
            return trimmed.Length > 0 && !trimmed.StartsWith("#", StringComparison.Ordinal);
        });

    public string LockStatusText => IsRetranslating
        ? "重翻中…"
        : IsLocked
            ? $"編輯中，已暫存 {PendingCount} 筆新字幕"
            : IsPaused
                ? $"已暫停捲動，已暫存 {PendingCount} 筆新字幕"
                : "雙擊原文或翻譯可編輯（Enter 儲存、Esc 取消）・選取後按 Delete 刪除・右鍵有更多操作";

    /// <summary>直播資訊標題列顯示的「目前套用的設定檔」</summary>
    public string ActiveProfileText => string.IsNullOrEmpty(_settings.Current.ActiveProfileName)
        ? string.Empty
        : $"　設定檔：{_settings.Current.ActiveProfileName}";

    public string StreamInfoUpdatedText => _settings.Current.StreamInfoUpdatedAt is null
        ? "尚未設定"
        : $"最後更新 {_settings.Current.StreamInfoUpdatedAt:HH:mm:ss}";

    public DashboardViewModel(ISettingsService settings, TranslationManager manager,
        IGlossaryStore glossaryStore, GlossaryState glossaryState, SubtitleLogWriter logWriter,
        StreamCardRecorder streamCardRecorder, KeywordGlossary keywordGlossary,
        MishearingFixState mishearingFixes,
        MiningService mining, MishearingMiningService mishearingMining,
        GameTermDiscoveryService termDiscovery, ISubtitleLogStore subtitleLogStore,
        ToastService toasts, StreamSession streamSession,
        ReviewCountSignal reviewSignal,
        ILogger<DashboardViewModel> logger, TimeProvider? timeProvider = null, CaptionTrackState? captions = null,
        LlmHealth? lorebookHealth = null, StreamTitleTranslator? titleTranslator = null,
        PlaybackLineTracker? playbackLines = null, IPlaybackClock? playbackClock = null,
        ChapterOverlayState? chapterOverlay = null, IPlaybackSeeker? playbackSeeker = null)
    {
        _settings = settings;
        _manager = manager;
        _glossaryStore = glossaryStore;
        _glossaryState = glossaryState;
        _logWriter = logWriter;
        _streamCardRecorder = streamCardRecorder;
        _keywordGlossary = keywordGlossary;
        _mishearingFixes = mishearingFixes;
        _mining = mining;
        _mishearingMining = mishearingMining;
        _termDiscovery = termDiscovery;
        _subtitleLogStore = subtitleLogStore;
        _toasts = toasts;
        _streamSession = streamSession;
        _reviewSignal = reviewSignal;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _captions = captions;
        _lorebookHealth = lorebookHealth;
        _titleTranslator = titleTranslator;
        _lastMishearingMiningAt = _timeProvider.GetUtcNow();
        _lastTermDiscoveryAt = _timeProvider.GetUtcNow();
        _replacementRules = settings.Current.ReplacementRules;
        _sourceReplacementRules = settings.Current.SourceReplacementRules;
        _streamSituation = settings.Current.StreamSituation;
        _characterTable = settings.Current.CharacterTable;
        _profileName = settings.Current.ActiveProfileName;
        _streamInfoExpanded = settings.Current.StreamInfoExpanded;
        _rulesExpanded = settings.Current.RulesExpanded;
        RefreshProfileNames();

        // 詞彙卡：讀取型錄 → 還原生效清單 → 設定 prompt 段落。
        // 啟動時的第一次預熱還沒有詞彙表：有生效的卡時補一次預熱（不重置對話、不存檔）
        var loaded = TryLoadGlossaryCatalog();
        if (loaded)
        {
            foreach (var id in settings.Current.ActiveGlossaryCardIds)
            {
                if (_glossaryCatalog.FindCard(id) is not null)
                {
                    _activeGlossaryIds.Add(id);
                    _activeReasons[id] = StreamCardReason.Carried;
                }
            }
            _glossaryState.SetPromptSection(_glossaryCatalog.BuildPromptSection(_activeGlossaryIds));
        }
        _glossaryState.SetCatalog(_glossaryCatalog);
        _glossaryState.SetActiveIds(ActiveGlossaryIds);
        RunGlossarySearch();
        RefreshActiveGlossaryCards();
        if (loaded)
        {
            UpdateGlossaryStatus();
            if (_activeGlossaryIds.Count > 0)
            {
                _ = _manager.WarmUpAsync();
            }
        }

        WeakReferenceMessenger.Default.Register<TranslationJob>(this, (r, job) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 聽錯字探勘的素材：Whisper 原文（含修正版），去除重複交給 Service 處理
                RecordMishearingLine(job);

                if (IsLocked || IsPaused)
                {
                    // 修正版的 Job 會取代前一版：暫存中已有被取代的舊列就先移除
                    if (job.RevisionOf is { } oldId)
                    {
                        _pending.RemoveAll(pending => pending.Id == oldId);
                    }

                    _pending.Add(job);

                    if (_pending.Count > MaxJobs)
                    {
                        _pending.RemoveAt(0);
                    }

                    PendingCount = _pending.Count;
                }
                else
                {
                    InsertJob(job);
                }

                RefreshStats();
            });
        });

        // 沒有新字幕時統計也要隨時間窗滑動
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();
        RefreshStats();

        // 聽錯字探勘：每 30 秒檢查一次是否到了探勘時間（間隔本身由設定決定）
        _mishearingMiningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _mishearingMiningTimer.Tick += (_, _) => _ = RunScheduledMiningAsync();
        _mishearingMiningTimer.Start();

        // 聽錯字探勘狀態膠囊：每秒更新倒數文字（只刷新顯示，不做探勘）
        _mishearingStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _mishearingStatusTimer.Tick += (_, _) => RefreshMishearingStatus();
        _mishearingStatusTimer.Start();
        RefreshMishearingStatus();

        // 知識庫模型健康狀態：直播中故障等恢復時，探勘進度顯示暫停提示（可能從背景執行緒觸發，排到 UI 執行緒）
        if (_lorebookHealth is not null)
        {
            _lorebookHealth.Changed += OnLorebookHealthChanged;
        }

        // 錄影時的「目前播放」高亮：兩個選用參數都有才每 0.5 秒查一次播放位置
        _playbackLines = playbackLines;
        _playbackClock = playbackClock;
        if (_playbackLines is not null && _playbackClock is not null)
        {
            _playingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _playingTimer.Tick += (_, _) => OnPlayingTimerTick();
            _playingTimer.Start();
        }

        // 歌曲清單：章節疊加狀態變動（背景執行緒）時排到 UI 執行緒重建
        _chapterOverlay = chapterOverlay;
        _playbackSeeker = playbackSeeker;
        if (_chapterOverlay is not null)
        {
            _chapterOverlay.Changed += OnChapterOverlayChanged;
            RebuildSongList();
        }
    }

    /// <summary>高亮計時器：直播時不查並清掉高亮；上一次查詢還沒回來就略過這次 tick。</summary>
    private void OnPlayingTimerTick()
    {
        if (_playingQueryInFlight)
        {
            return;
        }

        // 直播沒有「推送字幕檔給 PotPlayer」這回事：不查位置，順手清掉舊高亮
        if (_streamSession.IsLive)
        {
            ClearPlayingHighlight();
            return;
        }

        _playingQueryInFlight = true;
        _ = QueryPlayingHighlightAsync();
    }

    /// <summary>背景查播放位置（GetPositionMs 用 SendMessageTimeout 最多 200ms，不能在 UI 執行緒等），
    /// 找出位置所在的字幕行與歌曲清單的章節後回 UI 執行緒更新高亮；
    /// 查詢結束（成功或失敗）都在 UI 執行緒解鎖下一次 tick。</summary>
    private async Task QueryPlayingHighlightAsync()
    {
        string? text = null;
        long? positionMs = null;
        var succeeded = false;
        try
        {
            var clock = _playbackClock!;
            var tracker = _playbackLines!;
            var position = await Task.Run(clock.GetPositionMs);
            if (position is { } ms)
            {
                positionMs = ms;
                text = tracker.FindTextAt(ms);
            }
            succeeded = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "查詢 PotPlayer 播放位置失敗，略過這次高亮更新");
        }

        RunOnUi(() =>
        {
            if (succeeded)
            {
                ApplyPlayingHighlight(text);
                ApplySongPlayingHighlight(positionMs);
            }

            _playingQueryInFlight = false;
        });
    }

    /// <summary>依播放位置所在的字幕行更新高亮：text 是 null（沒推送過、位置不在任何一行內）時清掉；
    /// 否則從 <see cref="Jobs"/> 最新往回找第一個原文（CacheKey 或 Current）正規化後相同的列。
    /// 與目前高亮同一列就不動，否則舊的 IsPlaying = false、新的 IsPlaying = true。</summary>
    private void ApplyPlayingHighlight(string? text)
    {
        TranslationJob? job = null;
        if (text is not null)
        {
            var normalized = PlaybackLineTracker.Normalize(text);
            job = Jobs.FirstOrDefault(existing =>
                string.Equals(PlaybackLineTracker.Normalize(existing.CacheKey), normalized, StringComparison.Ordinal) ||
                string.Equals(PlaybackLineTracker.Normalize(existing.Current), normalized, StringComparison.Ordinal));
        }

        if (ReferenceEquals(job, _playingJob))
        {
            return;
        }

        if (_playingJob is not null)
        {
            _playingJob.IsPlaying = false;
        }

        _playingJob = job;
        if (job is not null)
        {
            job.IsPlaying = true;
        }
    }

    /// <summary>清掉目前的高亮（只動記住的那一列，不掃全部）。</summary>
    private void ClearPlayingHighlight()
    {
        if (_playingJob is null)
        {
            return;
        }

        _playingJob.IsPlaying = false;
        _playingJob = null;
    }

    // ===== 歌曲清單（錄影章節） =====

    /// <summary>章節疊加狀態變了（可能在背景執行緒）：排到 UI 執行緒重建歌曲清單。</summary>
    private void OnChapterOverlayChanged() => RunOnUi(RebuildSongList);

    /// <summary>依目前的章節狀態重建歌曲清單：每章一列，EndMs 同左上角章節名的顯示規則
    /// （<see cref="CaptionSubtitleBuilder.BuildChapterOverlays"/>：原曲長度＋5 秒收尾、查不到用設定值、不超過下一章開始）。
    /// 分頁只在錄影（VideoId 有值）而且不是直播時顯示；第一次載到 ≥1 章自動切到歌曲清單分頁（同一支影片只做一次）。</summary>
    private void RebuildSongList()
    {
        ClearManualChaptersCommand.NotifyCanExecuteChanged();

        if (_chapterOverlay is not { } overlay)
        {
            return;
        }

        var items = overlay.Items;
        var videoId = overlay.VideoId;
        ShowSongList = videoId is not null && !_streamSession.IsLive;
        if (!ShowSongList)
        {
            InfoTabIndex = 0;
            SongListSourceText = string.Empty;
            SongList.Clear();
            return;
        }

        SongListSourceText = overlay.Source switch
        {
            VideoChapterSources.Manual => "來源：人工貼上",
            VideoChapterSources.Description => "來源：說明欄",
            VideoChapterSources.Comment => "來源：留言セトリ",
            _ => "沒有找到章節，請複製セトリ後按「從剪貼簿貼上」",
        };

        SongList.Clear();
        if (items.Count == 0)
        {
            return;
        }

        var overlays = CaptionSubtitleBuilder.BuildChapterOverlays(
            items, videoEndMs: 0, fontSize: 12,
            fallbackDurationMs: _settings.Current.Vod.ChapterTitleFallbackSeconds * 1000L);
        for (var i = 0; i < overlays.Count; i++)
        {
            SongList.Add(new SongListItem
            {
                StartMs = overlays[i].StartMs,
                EndMs = overlays[i].EndMs,
                Title = items[i].Title,
                Translation = items[i].Translation,
            });
        }

        // 第一次載到 ≥1 章時自動切到歌曲清單分頁；同一支影片只做一次（換片時 Id 比對不同自然重置）
        if (videoId is not null && !string.Equals(videoId, _songListAutoSwitchedVideoId, StringComparison.Ordinal))
        {
            _songListAutoSwitchedVideoId = videoId;
            InfoTabIndex = 1;
        }
    }

    /// <summary>依播放位置標記歌曲清單目前播放的那一章（StartMs ≤ position &lt; EndMs）；
    /// 位置查不到（null）或不在任何一章內時全部取消；只改有變的列。</summary>
    private void ApplySongPlayingHighlight(long? positionMs)
    {
        if (SongList.Count == 0)
        {
            return;
        }

        SongListItem? playing = null;
        if (positionMs is { } position)
        {
            playing = SongList.FirstOrDefault(item => item.StartMs <= position && position < item.EndMs);
        }

        foreach (var item in SongList)
        {
            if (item.IsPlaying && !ReferenceEquals(item, playing))
            {
                item.IsPlaying = false;
            }
        }

        if (playing is { IsPlaying: false })
        {
            playing.IsPlaying = true;
        }
    }

    /// <summary>把剪貼簿的セトリ貼上成人工章節：剪貼簿空白 → 提示；解析成功（≥3 章）→ 套用並提示章節數
    /// （PotPlayer 進度條的章節要重開影片才會更新）；解析失敗 → 提示找不到時間軸。解析在背景執行緒，不阻塞 UI。</summary>
    [RelayCommand]
    private async Task PasteChaptersAsync()
    {
        if (_chapterOverlay is not { } overlay)
        {
            return;
        }

        var text = ClipboardReader();
        if (string.IsNullOrWhiteSpace(text))
        {
            SongListMessage = "剪貼簿是空的";
            return;
        }

        int count;
        try
        {
            count = await Task.Run(async () => await overlay.ApplyManualAsync(text));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "貼上人工章節失敗");
            SongListMessage = $"貼上失敗：{ex.Message}";
            return;
        }

        SongListMessage = count == 0
            ? "剪貼簿裡找不到 3 行以上、時間遞增的時間軸"
            : $"已套用 {count} 個章節（人工貼上），PotPlayer 進度條的章節要重開影片才會更新";
    }

    /// <summary>清除人工章節的條件：章節來源是人工貼上。</summary>
    private bool CanClearManualChapters() => _chapterOverlay?.Source == VideoChapterSources.Manual;

    /// <summary>刪掉這支影片人工貼上的章節，重新自動抓（說明欄→留言）。清除與重抓在背景執行緒。</summary>
    [RelayCommand(CanExecute = nameof(CanClearManualChapters))]
    private async Task ClearManualChaptersAsync()
    {
        if (_chapterOverlay is not { } overlay)
        {
            return;
        }

        try
        {
            await Task.Run(async () => await overlay.ClearManualAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清除人工章節失敗");
            SongListMessage = $"清除失敗：{ex.Message}";
            return;
        }

        SongListMessage = "已改回自動抓到的章節";
    }

    /// <summary>點歌曲清單的一列：讓 PotPlayer 跳到該章開始時間（SendMessageTimeout 在背景執行緒送，不卡 UI）。
    /// 沒有跳轉器（_playbackSeeker 為 null）或送訊息失敗時不跳轉；失敗顯示提示。</summary>
    [RelayCommand]
    private void SeekToSong(SongListItem? item)
    {
        if (item is null || _playbackSeeker is not { } seeker)
        {
            return;
        }

        var startMs = item.StartMs;
        _ = Task.Run(() =>
        {
            bool succeeded;
            try
            {
                succeeded = seeker.SeekTo(startMs);
            }
            catch
            {
                succeeded = false;
            }

            if (!succeeded)
            {
                RunOnUi(() => SongListMessage = "PotPlayer 沒有回應，跳轉失敗");
            }
        });
    }

    /// <summary>知識庫模型健康狀態變了（可能從背景執行緒呼叫）：排到 UI 執行緒更新探勘進度文字。</summary>
    private void OnLorebookHealthChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(ShowMiningWaitText);
    }

    /// <summary>探勘在等知識庫模型恢復時顯示暫停提示；等待結束不用特別處理，探勘的下一個進度回報會覆蓋這段文字。</summary>
    private void ShowMiningWaitText()
    {
        if (IsMining && _lorebookHealth is { HasWaiters: true })
        {
            MiningProgressText = _lorebookHealth.DownUntil is { } until
                ? $"知識庫模型暫停中（{until.LocalDateTime:HH:mm} 後再試），恢復後自動繼續"
                : "知識庫模型暫停中，恢復後自動繼續";
        }
    }

    partial void OnIsLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(LockStatusText));
    }

    partial void OnIsRetranslatingChanged(bool value)
    {
        OnPropertyChanged(nameof(LockStatusText));
    }

    partial void OnPendingCountChanged(int value)
    {
        OnPropertyChanged(nameof(LockStatusText));
    }

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(LockStatusText));
        OnPropertyChanged(nameof(PauseButtonText));
    }

    /// <summary>暫停／繼續捲動；繼續時（且不在編輯中）把暫存的字幕補上</summary>
    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused && !IsLocked)
        {
            FlushPending();
        }
    }

    partial void OnStreamInfoExpandedChanged(bool value)
    {
        _settings.Current.StreamInfoExpanded = value;
        _ = _settings.SaveAsync();
    }

    partial void OnRulesExpandedChanged(bool value)
    {
        _settings.Current.RulesExpanded = value;
        _ = _settings.SaveAsync();
    }

    partial void OnReplacementRulesChanged(string value)
    {
        _settings.Current.ReplacementRules = value;
        if (_applyingProfile) return;
        _ = _settings.SaveAsync();
    }

    partial void OnSourceReplacementRulesChanged(string value)
    {
        _settings.Current.SourceReplacementRules = value;
        if (_applyingProfile) return;
        _ = _settings.SaveAsync();
    }

    partial void OnStreamSituationChanged(string value)
    {
        _settings.Current.StreamSituation = value;
        OnPropertyChanged(nameof(CanStartMining));
        OnPropertyChanged(nameof(MiningDisabledToolTip));
        OnStreamInfoChanged();
    }

    partial void OnIsMiningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartMining));
        OnPropertyChanged(nameof(MiningDisabledToolTip));
        if (GlossaryDrawer is { } drawer)
        {
            drawer.IsMiningBusy = IsMining || !MiningConfigured;
        }
    }

    partial void OnCharacterTableChanged(string value)
    {
        _settings.Current.CharacterTable = value;
        OnPropertyChanged(nameof(CharacterTableLineCount));
        OnStreamInfoChanged();
    }

    private void OnStreamInfoChanged()
    {
        if (_applyingProfile) return;
        _settings.Current.StreamInfoUpdatedAt = DateTime.Now;
        _ = _settings.SaveAsync();
        _ = _manager.WarmUpAsync();
        OnPropertyChanged(nameof(StreamInfoUpdatedText));
    }

    // ===== 直播設定檔 =====

    /// <summary>套用設定檔：覆寫情境、角色表與兩組修正規則，並重置對話（換台通常代表上下文已無關）</summary>
    [RelayCommand]
    private void ApplyProfile() => ApplyProfileByName(ProfileName);

    /// <summary>以名稱套用設定檔；名稱為空或找不到回傳 false（訊息寫入 ProfileMessage）。</summary>
    public bool ApplyProfileByName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            ProfileMessage = "請先選擇或輸入設定檔名稱";
            return false;
        }

        if (!StreamProfiles.Apply(_settings.Current, trimmed))
        {
            ProfileMessage = $"找不到設定檔「{trimmed}」";
            return false;
        }

        _applyingProfile = true;
        try
        {
            StreamSituation = _settings.Current.StreamSituation;
            CharacterTable = _settings.Current.CharacterTable;
            ReplacementRules = _settings.Current.ReplacementRules;
            SourceReplacementRules = _settings.Current.SourceReplacementRules;
        }
        finally
        {
            _applyingProfile = false;
        }

        _manager.ResetConversation();
        _ = _settings.SaveAsync();
        _ = _manager.WarmUpAsync();
        OnPropertyChanged(nameof(StreamInfoUpdatedText));
        OnPropertyChanged(nameof(ActiveProfileText));
        MiningResultTags.Clear();
        ProfileMessage = $"已套用「{trimmed}」，對話已重置";
        return true;
    }

    /// <summary>把目前的情境、角色表與修正規則存成設定檔（同名覆寫），再依情境比對詞彙卡</summary>
    [RelayCommand]
    private void SaveProfile()
    {
        var name = (ProfileName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            ProfileMessage = "請先輸入設定檔名稱（例如主播名）";
            return;
        }

        SaveProfileAs(name);

        // 情境比對詞彙卡：比對到的卡名顯示為「已比對」標籤；還沒生效的卡（連同上層卡）加入生效清單
        var matches = _glossaryCatalog.FindCardsInText(StreamSituation);
        MiningResultTags.Clear();
        foreach (var card in matches)
        {
            MiningResultTags.Add(new MiningTag($"已比對：{card.Name}", nameof(MiningOutcome.Matched), card.Name));
        }

        if (matches.Count == 0)
        {
            return;
        }

        var before = ActiveGlossaryIds;
        var matchIds = matches.Select(c => c.Id).ToHashSet();
        var added = false;
        foreach (var id in _glossaryCatalog.ExpandWithParents(matchIds))
        {
            if (_activeGlossaryIds.Contains(id)) continue;
            _activeGlossaryIds.Add(id);
            _activeReasons[id] = matchIds.Contains(id) ? StreamCardReason.Situation : StreamCardReason.Parent;
            added = true;
        }
        if (added)
        {
            ApplyGlossaryChange();
        }
        _ = _streamCardRecorder.RecordChange(before, ActiveGlossaryIds, StreamCardReason.Situation);
    }

    /// <summary>把目前的情境、角色表與修正規則存成指定名稱的設定檔（同名覆寫，不改生效詞彙卡）。
    /// 「存檔」命令與開新直播的情境存檔詢問共用。</summary>
    public void SaveProfileAs(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            ProfileMessage = "請先輸入設定檔名稱（例如主播名）";
            return;
        }

        var existed = ProfileNames.Contains(trimmed);
        StreamProfiles.SaveCurrent(_settings.Current, trimmed);
        _ = _settings.SaveAsync();
        RefreshProfileNames();
        ProfileName = trimmed;
        OnPropertyChanged(nameof(ActiveProfileText));
        ProfileMessage = existed ? $"已覆寫「{trimmed}」" : $"已新增「{trimmed}」";
    }

    /// <summary>判斷情境是否需要存成設定檔：需要時回傳要存入的設定檔名稱
    /// （依序：ActiveProfileName（找得到設定檔時）→ fallbackName → 畫面上的 ProfileName）；
    /// 情境空白、套用中設定檔的內容與目前情境相同（Ordinal，兩邊都修剪）、或沒有可用名稱時回傳 null。</summary>
    public string? SituationSaveTarget(string? fallbackName)
    {
        var situation = (StreamSituation ?? string.Empty).Trim();
        if (situation.Length == 0)
        {
            return null;
        }

        var activeName = _settings.Current.ActiveProfileName;
        var activeProfile = activeName.Length == 0
            ? null
            : _settings.Current.StreamProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, activeName, StringComparison.Ordinal));
        if (activeProfile is not null &&
            string.Equals(activeProfile.StreamSituation.Trim(), situation, StringComparison.Ordinal))
        {
            return null;
        }

        if (activeProfile is not null)
        {
            return activeName;
        }

        var fallback = (fallbackName ?? string.Empty).Trim();
        if (fallback.Length > 0)
        {
            return fallback;
        }

        var profileName = (ProfileName ?? string.Empty).Trim();
        if (profileName.Length > 0)
        {
            return profileName;
        }

        _logger.LogInformation("情境未存檔，但沒有可用的設定檔名稱，略過詢問");
        return null;
    }

    /// <summary>開新的直播時清空生效中的詞彙卡與情境（角色表、譯名修正與原文修正不動），並重置對話。</summary>
    public void ClearForNewStream()
    {
        var count = _activeGlossaryIds.Count;
        _activeGlossaryIds.Clear();
        _activeReasons.Clear();
        StreamSituation = string.Empty;
        // 上一場的探勘結果標籤與「目前播放」高亮也一併清掉
        MiningResultTags.Clear();
        ClearPlayingHighlight();
        ApplyGlossaryChange();
        _logger.LogInformation("開新直播：清空詞彙卡 {Count} 張與情境", count);
    }

    /// <summary>設定這場直播／影片的標題（一鍵開台時呼叫）：頁首先顯示原文，背景翻譯完成後譯文大字在上、原文小字在下。
    /// title 空白時回到預設「字幕監看」並取消進行中的翻譯；沒有翻譯器或翻譯失敗時只顯示原文。不寫資料庫。</summary>
    public void SetStreamTitle(string? title)
    {
        _titleVersion++;
        _titleCts?.Cancel();
        _titleCts?.Dispose();
        _titleCts = null;

        if (string.IsNullOrWhiteSpace(title))
        {
            HeaderTitle = DefaultHeaderTitle;
            HeaderSubtitle = null;
            return;
        }

        var original = title.Trim();
        HeaderTitle = original;
        HeaderSubtitle = null;
        if (_titleTranslator is null)
        {
            return;
        }

        var version = _titleVersion;
        var cts = new CancellationTokenSource();
        _titleCts = cts;
        _ = TranslateStreamTitleAsync(original, version, cts.Token);
    }

    /// <summary>背景翻譯直播標題（不拖慢開台）：成功且仍是同一次 SetStreamTitle 時才更新頁首（排到 UI 執行緒）。</summary>
    private async Task TranslateStreamTitleAsync(string original, int version, CancellationToken cancellationToken)
    {
        string? translated;
        try
        {
            translated = await _titleTranslator!.TranslateAsync(original, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "直播標題翻譯失敗：{Title}", original);
            return;
        }

        RunOnUi(() =>
        {
            // 換場後回來的舊結果丟掉
            if (version != _titleVersion)
            {
                return;
            }
            if (!string.IsNullOrEmpty(translated) && !string.Equals(translated, original, StringComparison.Ordinal))
            {
                HeaderTitle = translated;
                HeaderSubtitle = original;
            }
        });
    }

    /// <summary>把更新排到這個 ViewModel 的 UI 執行緒執行（背景翻譯的結果回來用）；已在 UI 執行緒時直接執行。
    /// 用建立 ViewModel 時的 Dispatcher（同 DispatcherTimer），不用 Application.Current：測試行程裡 Application 可能建在別的執行緒。</summary>
    private void RunOnUi(Action action)
    {
        var dispatcher = _statsTimer.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }
        dispatcher.BeginInvoke(action);
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        var name = (ProfileName ?? string.Empty).Trim();
        if (!StreamProfiles.Delete(_settings.Current, name))
        {
            ProfileMessage = name.Length == 0 ? "請先選擇要刪除的設定檔" : $"找不到設定檔「{name}」";
            return;
        }

        _ = _settings.SaveAsync();
        RefreshProfileNames();
        ProfileName = string.Empty;
        OnPropertyChanged(nameof(ActiveProfileText));
        ProfileMessage = $"已刪除「{name}」（目前內容不受影響）";
    }

    private void RefreshProfileNames()
    {
        ProfileNames.Clear();
        foreach (var name in StreamProfiles.Names(_settings.Current))
        {
            ProfileNames.Add(name);
        }
    }

    // ===== 統計 =====

    private void RefreshStats()
    {
        var stats = TranslationStats.Compute(Jobs.Concat(_pending), DateTime.Now, StatsWindow);
        if (stats.Count == 0)
        {
            StatsText = "近 5 分鐘沒有字幕";
            StatsWarning = false;
            return;
        }

        var parts = new List<string> { $"近 5 分鐘 {stats.Count} 句" };
        if (stats.ModelAvgMs is { } avg && stats.ModelP95Ms is { } p95)
        {
            parts.Add($"模型 平均 {avg}ms・P95 {p95}ms");
        }
        parts.Add($"快取 {stats.CacheHitRate:P0}");
        parts.Add($"逾時 {stats.TimeoutCount}");
        StatsText = string.Join("　|　", parts);

        // P95 超過逾時門檻的八成，代表已經有不少句子快來不及
        var timeoutMs = _settings.Current.Translation.TimeoutMs;
        StatsWarning = stats.TimeoutCount > 0 || (timeoutMs > 0 && stats.ModelP95Ms > timeoutMs * 0.8);
    }

    // ===== 資料探勘 =====

    /// <summary>執行資料探勘：拆名詞 → 查知識來源 → 比對／建立詞彙卡。在背景執行緒跑，期間可繼續操作其他功能；
    /// 完成後重載型錄、把新卡（連同上層卡）加入生效清單（原因 Mining），結果以標籤與提示顯示。</summary>
    [RelayCommand]
    private async Task MineSituation()
    {
        if (IsMining)
        {
            return;
        }

        var situation = (StreamSituation ?? string.Empty).Trim();
        if (situation.Length == 0 || !MiningConfigured)
        {
            return;
        }

        var activeNames = GetActiveCardNames();
        await RunMiningAsync("資料探勘", (progress, ct) => _mining.MineSituationAsync(
            situation, activeNames, _streamSession.CurrentStreamId, progress, ct), situation);
    }

    /// <summary>開台自動資料探勘：主播沒有角色卡時，用頻道名稱、直播標題與說明欄採礦（不讀寫情境欄）。
    /// 與手動探勘共用 <see cref="IsMining"/>、進度與取消；已有探勘在執行或知識庫與翻譯模型都沒設定時只記 log 略過
    /// （只設定翻譯模型時照常執行：ScheduledLorebookLlm 會直接把請求改送翻譯模型）。</summary>
    public async Task MineStreamInfoAsync(string? channelName, string? title, string? description = null)
    {
        if (IsMining)
        {
            _logger.LogInformation("開台資料探勘略過：已有資料探勘在執行");
            return;
        }

        if (StreamOpenMiningMode.Decide(_settings.Current) == StreamOpenMiningModel.None)
        {
            _logger.LogInformation("開台資料探勘略過：未設定知識庫模型");
            return;
        }

        var activeNames = GetActiveCardNames();
        await RunMiningAsync("開台資料探勘", (progress, ct) => _mining.MineStreamAsync(
            channelName, title, description, activeNames, _streamSession.CurrentStreamId, progress, ct),
            (StreamSituation ?? string.Empty).Trim());
    }

    /// <summary>手動／開台探勘的共用執行流程：設 IsMining、顯示進度、背景執行、可取消，
    /// 例外與取消時顯示提示框；完成時交給 <see cref="FinishMining"/>（提示框標題用 baseTitle＋「完成」）。
    /// 整個流程存成 <see cref="_miningTask"/>（CancelMiningAsync 等它結束用）。</summary>
    private Task RunMiningAsync(string baseTitle, Func<IProgress<string>, CancellationToken, Task<MiningResult>> mine,
        string situationForTags)
    {
        _miningTask = RunMiningCoreAsync(baseTitle, mine, situationForTags);
        return _miningTask;
    }

    private async Task RunMiningCoreAsync(string baseTitle, Func<IProgress<string>, CancellationToken, Task<MiningResult>> mine,
        string situationForTags)
    {
        IsMining = true;
        MiningProgressText = "拆解名詞中…";
        _miningCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(text => MiningProgressText = text);
            var result = await Task.Run(() => mine(progress, _miningCts.Token));
            FinishMining(result, situationForTags, baseTitle);
        }
        catch (OperationCanceledException)
        {
            _toasts.Show(baseTitle, "已取消資料探勘");
        }
        catch (Exception ex)
        {
            _toasts.Show($"{baseTitle}失敗", ex.Message, isError: true);
        }
        finally
        {
            MiningProgressText = string.Empty;
            IsMining = false;
            _miningCts?.Dispose();
            _miningCts = null;
        }
    }

    /// <summary>生效中詞彙卡的卡名清單（探勘時告訴 GLM 這些已經處理過）。</summary>
    private IReadOnlyList<string> GetActiveCardNames() =>
        _activeGlossaryIds
            .Select(id => _glossaryCatalog.FindCard(id)?.Name)
            .Where(name => name is not null)
            .ToList()!;

    /// <summary>取消執行中的資料探勘。</summary>
    [RelayCommand]
    private void CancelMining() => _miningCts?.Cancel();

    /// <summary>取消執行中的資料探勘，並等它結束（IsMining 變 false）再完成，最多等 5 秒；
    /// 沒有探勘在跑時直接完成。換場時取消等待知識庫模型恢復的探勘用。</summary>
    public async Task CancelMiningAsync()
    {
        _miningCts?.Cancel();
        if (_miningTask is not null)
        {
            await Task.WhenAny(_miningTask, Task.Delay(5000));
        }
    }

    /// <summary>資料探勘完成：重載型錄 → 生效新卡 → 顯示結果標籤與提示框（標題用 baseTitle＋「完成」；
    /// 「依情境重算的已比對」標籤用傳入的情境文字，開台探勘傳目前的情境，可以是空的）。</summary>
    private void FinishMining(MiningResult result, string situation, string baseTitle)
    {
        // 0. 有新卡或候選名詞：側邊欄的待審數量可能變了（型錄讀取失敗也要通知，卡已寫進資料庫）
        if (result.Items.Any(i => i.Outcome == MiningOutcome.Created || i.Outcome == MiningOutcome.Candidate))
        {
            _reviewSignal.Notify();
        }

        // 1. 重載型錄（新卡與新名詞進來）；抽屜的成員區塊也要重讀（新卡可能讓「沒有角色卡」的成員變成有卡）
        if (!TryLoadGlossaryCatalog())
        {
            _toasts.Show($"{baseTitle}完成", "詞彙卡讀取失敗，新卡可能沒有生效", isError: true);
            return;
        }
        GlossaryDrawer?.RefreshMembers(_glossaryCatalog, ActiveGlossaryIds);

        // 2. 把還沒生效的卡（連同上層卡）加入生效清單：原因是 Mining、上層卡為 Parent
        var before = ActiveGlossaryIds;
        var added = false;
        foreach (var id in _glossaryCatalog.ExpandWithParents(result.CardIdsToActivate))
        {
            if (_activeGlossaryIds.Contains(id)) continue;
            _activeGlossaryIds.Add(id);
            _activeReasons[id] = result.CardIdsToActivate.Contains(id) ? StreamCardReason.Mining : StreamCardReason.Parent;
            added = true;
        }
        if (added)
        {
            ApplyGlossaryChange();
        }
        _ = _streamCardRecorder.RecordChange(before, ActiveGlossaryIds, StreamCardReason.Mining);

        // 3. 標籤：併入情境存檔的比對結果（重新算一次），再依結果種類顯示
        MiningResultTags.Clear();
        foreach (var card in _glossaryCatalog.FindCardsInText(situation))
        {
            MiningResultTags.Add(new MiningTag($"已比對：{card.Name}", nameof(MiningOutcome.Matched), card.Name));
        }
        foreach (var item in result.Items.Where(i => i.Outcome == MiningOutcome.Matched))
        {
            MiningResultTags.Add(new MiningTag($"已比對：{item.Surface}", nameof(MiningOutcome.Matched), item.Surface));
        }
        foreach (var item in result.Items.Where(i => i.Outcome == MiningOutcome.Created))
        {
            MiningResultTags.Add(new MiningTag($"新增：{item.CardName ?? item.Surface}（待審核）", nameof(MiningOutcome.Created), item.CardName ?? item.Surface));
        }
        foreach (var item in result.Items.Where(i => i.Outcome == MiningOutcome.AddedAlias))
        {
            MiningResultTags.Add(new MiningTag($"併入：{item.Surface} → {item.CardName}", nameof(MiningOutcome.AddedAlias), item.Surface));
        }
        var candidates = result.Items.Count(i => i.Outcome == MiningOutcome.Candidate);
        if (candidates > 0)
        {
            MiningResultTags.Add(new MiningTag($"候選：{candidates} 個", nameof(MiningOutcome.Candidate)));
        }
        foreach (var item in result.Items.Where(i => i.Outcome == MiningOutcome.NotFound))
        {
            MiningResultTags.Add(new MiningTag($"查不到：{item.Surface}", nameof(MiningOutcome.NotFound), item.Surface));
        }
        foreach (var item in result.Items.Where(i => i.Outcome == MiningOutcome.Rejected))
        {
            MiningResultTags.Add(new MiningTag($"已拒絕：{item.Surface}", nameof(MiningOutcome.Rejected), item.Surface));
        }
        foreach (var item in result.Items.Where(i => i.Outcome == MiningOutcome.Failed))
        {
            MiningResultTags.Add(new MiningTag($"失敗：{item.Surface}", nameof(MiningOutcome.Failed), item.Surface));
        }

        // 4. 右下角提示框
        var seconds = (result.ElapsedMs / 1000.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        var message =
            $"新增 {result.Items.Count(i => i.Outcome == MiningOutcome.Created)} 張・併入 {result.Items.Count(i => i.Outcome == MiningOutcome.AddedAlias)}・" +
            $"已比對 {result.Items.Count(i => i.Outcome == MiningOutcome.Matched)}・候選 {candidates}・查不到 {result.Items.Count(i => i.Outcome == MiningOutcome.NotFound)}・" +
            $"已拒絕 {result.Items.Count(i => i.Outcome == MiningOutcome.Rejected)}\n" +
            $"GLM {result.TotalTokens} tokens・{seconds} 秒";
        if (!string.IsNullOrEmpty(result.Error))
        {
            message += $"\n{result.Error}";
        }
        _toasts.Show($"{baseTitle}完成", message, isError: !string.IsNullOrEmpty(result.Error));
    }

    // ===== 聽錯字探勘 =====

    /// <summary>記錄一句字幕到聽錯字探勘的佇列（Whisper 原文，空的時候用 Current；修正版也記），
    /// 並累加「上一次探勘之後的新字幕數」；順手修剪超過時間窗的舊字幕。
    /// 同一句的原文修正後內容（job.Current）也交給遊戲用語探勘的計數器累計（空白不加）。</summary>
    private void RecordMishearingLine(TranslationJob job)
    {
        var text = string.IsNullOrEmpty(job.RawSource) ? job.Current : job.RawSource;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        _mishearingLines.Add((text, _timeProvider.GetUtcNow()));
        _mishearingNewLines++;
        PruneMishearingLines(_timeProvider.GetUtcNow());
        if (!string.IsNullOrWhiteSpace(job.Current))
        {
            _termCounter.Add(job.Current);
        }
    }

    /// <summary>修剪聽錯字探勘的佇列：只保留 MishearingMiningIntervalSeconds 以內的字幕。</summary>
    private void PruneMishearingLines(DateTimeOffset now)
    {
        var window = TimeSpan.FromSeconds(Math.Max(1, _settings.Current.Translation.MishearingMiningIntervalSeconds));
        _mishearingLines.RemoveAll(entry => now - entry.ReceivedAt > window);
    }

    /// <summary>生效中的卡的全部名詞（現有順序），再接附屬用語卡
    /// （<see cref="GlossaryCatalog.FindSupplementCards"/>：生效遊戲卡的 Common 子卡）的名詞，
    /// 轉成聽錯字探勘的輸入名詞（卡名用各卡自己的 Name）；上限從後面截掉，生效卡的名詞優先。</summary>
    private List<MishearingTermInput> BuildMishearingTerms() =>
        _activeGlossaryIds
            .SelectMany(id => _glossaryCatalog.FindCard(id) is { } card ? TermInputsOf(card) : [])
            .Concat(_glossaryCatalog.FindSupplementCards(_activeGlossaryIds).SelectMany(TermInputsOf))
            .ToList();

    /// <summary>一張卡的全部名詞轉成聽錯字探勘的輸入名詞（卡名用卡片自己的 Name）。</summary>
    private IEnumerable<MishearingTermInput> TermInputsOf(GlossaryCard card) =>
        _glossaryCatalog.TermsOf(card.Id)
            .Select(term => new MishearingTermInput(term.Id, card.Id, card.Name, term.Source, term.Target, term.Aliases));

    /// <summary>30 秒計時器呼叫：只檢查是否到期（已啟用、知識庫模型已設定、距離上一次探勘（或程式啟動）
    /// 超過間隔、新字幕達到下限、沒有正在執行的聽錯字探勘），到期才交給 <see cref="ExecuteMishearingMiningAsync"/>。</summary>
    public async Task RunMishearingMiningIfDueAsync()
    {
        // 錄影有生成式字幕時重心放在字幕處理：不自動跑聽錯字探勘（它是為直播的 Whisper 字幕設計的）。
        // 直播開台時字幕狀態會被清空，直播不受影響；手動「立即探勘」不受影響
        if (_captions is { VideoId: not null } track && track.Cues.Count > 0)
        {
            _logger.LogDebug("錄影有生成式字幕，略過自動聽錯字探勘");
            return;
        }

        var translation = _settings.Current.Translation;
        var now = _timeProvider.GetUtcNow();
        if (!translation.MishearingMiningEnabled || !MiningConfigured || _isMishearingMining ||
            now - _lastMishearingMiningAt < TimeSpan.FromSeconds(translation.MishearingMiningIntervalSeconds) ||
            _mishearingNewLines < translation.MishearingMiningMinLines)
        {
            return;
        }

        await ExecuteMishearingMiningAsync(manual: false);
    }

    /// <summary>「立即探勘」按鈕：與自動探勘共用 <see cref="ExecuteMishearingMiningAsync"/>，但略過間隔與新字幕下限；
    /// 沒有新的聽錯字時也會跳「聽錯字探勘完成」提示框，讓使用者知道有跑到。</summary>
    [RelayCommand(CanExecute = nameof(CanMineMishearingNow))]
    private async Task MineMishearingNowAsync() => await ExecuteMishearingMiningAsync(manual: true);

    /// <summary>手動探勘的條件：已啟用、知識庫模型已設定、沒有正在執行、時間窗內至少 1 句字幕、生效中的卡至少 1 個名詞。</summary>
    private bool CanMineMishearingNow()
    {
        if (!_settings.Current.Translation.MishearingMiningEnabled || !MiningConfigured || _isMishearingMining)
        {
            return false;
        }
        PruneMishearingLines(_timeProvider.GetUtcNow());
        return _mishearingLines.Count > 0 && BuildMishearingTerms().Count > 0;
    }

    /// <summary>自動與手動共用的探勘執行流程：生效中的卡沒有名詞（手動還要求時間窗內至少 1 句字幕）就不執行；
    /// 開始前就把「上一次探勘時間」設成現在、新字幕數歸 0，因此失敗也要等下一個間隔才會重試。
    /// 結果提示框：有新增 →「聽錯字修正」、失敗 →「聽錯字探勘失敗」、手動沒有新增 →「聽錯字探勘完成」、自動沒有新增 → 只記 log。</summary>
    private async Task ExecuteMishearingMiningAsync(bool manual)
    {
        var now = _timeProvider.GetUtcNow();
        PruneMishearingLines(now);
        var lines = _mishearingLines.Select(entry => entry.Text).ToList();
        var terms = BuildMishearingTerms();
        if (terms.Count == 0 || (manual && lines.Count == 0))
        {
            return;
        }

        var situation = (StreamSituation ?? string.Empty).Trim();
        _lastMishearingMiningAt = now;
        _lastMishearingMinedAt = now;
        _mishearingNewLines = 0;
        _isMishearingMining = true;
        RefreshMishearingStatus();
        try
        {
            var result = await Task.Run(() => _mishearingMining.MineAsync(situation, terms, lines, CancellationToken.None));
            _lastMishearingResultText = result.Error is not null
                ? $"失敗（{result.Error}）"
                : $"新增 {result.Inserted} 筆、累加 {result.Accumulated} 筆、丟棄 {result.Dropped} 筆";
            if (result.Inserted > 0)
            {
                _reviewSignal.Notify();
                _toasts.Show("聽錯字修正",
                    $"新增 {result.Inserted} 筆待審核，請到「聽錯字修正」頁審核\nGLM {result.TotalTokens} tokens");
            }
            else if (result.Error is not null)
            {
                _toasts.Show("聽錯字探勘失敗", result.Error, isError: true);
            }
            else if (manual)
            {
                _toasts.Show("聽錯字探勘完成",
                    $"沒有新的聽錯字（提出 {result.Proposed}、累加 {result.Accumulated}、丟棄 {result.Dropped}）");
            }
            else
            {
                _logger.LogInformation(
                    "聽錯字探勘沒有新提案：提出 {Proposed}，累加 {Accumulated}，丟棄 {Dropped}", result.Proposed, result.Accumulated, result.Dropped);
            }
        }
        catch (Exception ex)
        {
            _lastMishearingResultText = $"失敗（{ex.Message}）";
            _toasts.Show("聽錯字探勘失敗", ex.Message, isError: true);
        }
        finally
        {
            _isMishearingMining = false;
            RefreshMishearingStatus();
        }
    }

    /// <summary>更新聽錯字探勘的狀態膠囊（依序：已關閉 → 未設定知識庫模型 → 執行中 → 間隔・上次・倒數・新字幕數）
    /// 與 ToolTip，並同步「立即探勘」按鈕的啟用狀態。每秒由計時器呼叫；開始／結束探勘與開台清空字幕時也會呼叫。</summary>
    public void RefreshMishearingStatus()
    {
        var translation = _settings.Current.Translation;
        if (!translation.MishearingMiningEnabled)
        {
            MishearingStatusText = "聽錯字探勘：已關閉";
        }
        else if (!MiningConfigured)
        {
            MishearingStatusText = "聽錯字探勘：未設定知識庫模型";
        }
        else if (_isMishearingMining)
        {
            MishearingStatusText = "聽錯字探勘：執行中…";
        }
        else
        {
            var seconds = translation.MishearingMiningIntervalSeconds;
            var intervalText = seconds % 60 == 0 ? $"每 {seconds / 60} 分" : $"每 {seconds} 秒";
            var lastText = _lastMishearingMinedAt is { } mined ? $"上次 {mined.ToLocalTime():HH:mm}" : "上次 尚未";
            var remaining = _lastMishearingMiningAt + TimeSpan.FromSeconds(Math.Max(1, seconds)) - _timeProvider.GetUtcNow();
            // 生效中的卡沒有名詞時，倒數改成提示沒有名詞（自動與手動都不會執行）
            var countdown = BuildMishearingTerms().Count == 0
                ? "沒有生效名詞"
                : remaining > TimeSpan.Zero ? $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2} 後" : "等待字幕";
            MishearingStatusText =
                $"聽錯字探勘・{intervalText}・{lastText}・{countdown}・新字幕 {_mishearingNewLines}/{translation.MishearingMiningMinLines}";
        }

        MishearingStatusToolTip = BuildMishearingToolTip();
        MineMishearingNowCommand.NotifyCanExecuteChanged();
    }

    /// <summary>組聽錯字探勘的 ToolTip：運作說明（間隔與下限用目前的設定值）＋最近一次結果（有跑過才加）。</summary>
    private string BuildMishearingToolTip()
    {
        var translation = _settings.Current.Translation;
        var seconds = translation.MishearingMiningIntervalSeconds;
        var intervalText = seconds % 60 == 0 ? $"每 {seconds / 60} 分鐘" : $"每 {seconds} 秒";
        var text =
            $"{intervalText}把情境、生效名詞與最近字幕交給知識庫模型，找出 Whisper 聽錯的寫法（存成待審核）。\n" +
            $"新字幕至少 {translation.MishearingMiningMinLines} 句才會自動執行；「立即探勘」略過間隔與下限。";
        return _lastMishearingResultText is { } result ? $"{text}\n上次結果：{result}" : text;
    }

    // ===== 遊戲用語探勘 =====

    /// <summary>30 秒計時器的共用流程：先跑聽錯字探勘，再檢查遊戲用語探勘是否到期。</summary>
    private async Task RunScheduledMiningAsync()
    {
        await RunMishearingMiningIfDueAsync();
        await RunTermDiscoveryIfDueAsync();
    }

    /// <summary>30 秒計時器呼叫：只檢查是否到期（已啟用、知識庫模型已設定、距離上一次探勘（或程式啟動）
    /// 超過間隔、沒有正在執行、生效中的卡有 Kind 是 Game 的卡），到期而且有候選詞才執行。
    /// 沒有候選詞時不更新上一次探勘時間，下一個 30 秒再檢查。</summary>
    public async Task RunTermDiscoveryIfDueAsync()
    {
        var translation = _settings.Current.Translation;
        var now = _timeProvider.GetUtcNow();
        if (!translation.MishearingMiningEnabled || !MiningConfigured || _isTermDiscoveryRunning ||
            now - _lastTermDiscoveryAt < TimeSpan.FromSeconds(translation.MishearingMiningIntervalSeconds))
        {
            return;
        }

        // 生效中的卡（依生效順序）取第一張遊戲卡
        var game = _activeGlossaryIds
            .Select(id => _glossaryCatalog.FindCard(id))
            .FirstOrDefault(card => card is { Kind: GlossaryKind.Game });
        if (game is null)
        {
            return;
        }

        var candidates = _termCounter.TakeDue(BuildKnownNames());
        if (candidates.Count == 0)
        {
            _logger.LogDebug("遊戲用語探勘：沒有候選詞");
            return;
        }

        _lastTermDiscoveryAt = now;
        _isTermDiscoveryRunning = true;
        try
        {
            var result = await Task.Run(() => _termDiscovery.DiscoverAsync(game, candidates, CancellationToken.None));
            if (result.InsertedWords.Count > 0)
            {
                // 附屬用語卡不生效：只重讀型錄讓關鍵字詞彙馬上讀得到新詞，不改 system prompt
                ReloadGlossaryCatalogOnly();
                _reviewSignal.Notify();
                var shown = string.Join("、", result.InsertedWords.Take(5).ToList());
                if (result.InsertedWords.Count > 5)
                {
                    shown += "…";
                }
                _toasts.Show("遊戲用語",
                    $"新增 {result.InsertedWords.Count} 個「{game.Name} 用語」待審核：{shown}\nGLM {result.TotalTokens} tokens");
            }
            else if (!string.IsNullOrEmpty(result.Error))
            {
                _toasts.Show("遊戲用語探勘失敗", result.Error, isError: true);
            }
            else
            {
                _logger.LogInformation(
                    "遊戲用語探勘沒有新詞：判斷 {Judged} 個，接受 {Accepted} 個（{Tokens} tokens）",
                    result.Judged, result.Accepted, result.TotalTokens);
            }
        }
        catch (Exception ex)
        {
            _toasts.Show("遊戲用語探勘失敗", ex.Message, isError: true);
        }
        finally
        {
            _isTermDiscoveryRunning = false;
        }
    }

    /// <summary>遊戲用語探勘的已知名稱：型錄中所有卡的 Name 與別名每一行，加上所有名詞的 Source 與 Aliases 每一行。</summary>
    private List<string> BuildKnownNames()
    {
        var names = new List<string>();
        foreach (var card in _glossaryCatalog.Cards)
        {
            names.Add(card.Name);
            names.AddRange(AliasLines(card.Aliases));
            foreach (var term in _glossaryCatalog.TermsOf(card.Id))
            {
                names.Add(term.Source);
                names.AddRange(AliasLines(term.Aliases));
            }
        }
        return names;
    }

    /// <summary>別名欄位每行一個，去頭尾空白後略過空行。</summary>
    private static List<string> AliasLines(string? aliases)
    {
        if (string.IsNullOrEmpty(aliases))
        {
            return [];
        }
        return aliases
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    // ===== 詞彙卡 =====

    partial void OnGlossarySearchTextChanged(string value)
    {
        RunGlossarySearch();
    }

    /// <summary>切換生效狀態：未生效就生效（連同上層卡，原因 Manual），已生效就移除；直播中會記錄到 StreamCards。
    /// 從搜尋結果下拉執行後清空搜尋文字並關閉下拉。</summary>
    [RelayCommand]
    private void ToggleGlossaryCard(GlossaryCardItem item)
    {
        ToggleGlossaryCardById(item.Id);
        GlossarySearchText = string.Empty;
    }

    /// <summary>以卡片 Id 切換生效狀態（不清搜尋文字）：未生效就生效（連同上層卡，原因 Manual），
    /// 已生效就移除；更新 prompt 與畫面並記錄到 StreamCards。搜尋下拉與抽屜成員勾選共用。</summary>
    private void ToggleGlossaryCardById(long id)
    {
        var before = ActiveGlossaryIds;
        if (_activeGlossaryIds.Contains(id))
        {
            _activeGlossaryIds.Remove(id);
            _activeReasons.Remove(id);
        }
        else
        {
            foreach (var expanded in _glossaryCatalog.ExpandWithParents([id]))
            {
                if (!_activeGlossaryIds.Contains(expanded))
                {
                    _activeGlossaryIds.Add(expanded);
                    _activeReasons[expanded] = StreamCardReason.Manual;
                }
            }
        }
        ApplyGlossaryChange();
        _ = _streamCardRecorder.RecordChange(before, ActiveGlossaryIds);
    }

    /// <summary>移除生效中的一張卡（只移除該張，上層卡不受影響）；直播中會記錄到 StreamCards</summary>
    [RelayCommand]
    private void RemoveGlossaryCard(GlossaryCardItem item)
    {
        var before = ActiveGlossaryIds;
        _activeGlossaryIds.Remove(item.Id);
        _activeReasons.Remove(item.Id);
        ApplyGlossaryChange();
        _ = _streamCardRecorder.RecordChange(before, ActiveGlossaryIds);
    }

    // ===== 詞彙卡抽屜（編輯名詞） =====

    /// <summary>打開右側抽屜編輯這張卡（與其上層卡）的名詞。已有抽屜而且有未存變更時，先詢問是否放棄。</summary>
    [RelayCommand]
    private void OpenGlossaryDrawer(GlossaryCardItem item)
    {
        // 雙擊生效清單空白處時 SelectedItem 為 null（MouseBinding 傳 null 參數）
        if (item is null)
        {
            return;
        }

        if (GlossaryDrawer is { HasChanges: true } existing &&
            !existing.ConfirmDiscard($"抽屜還有 {existing.DirtyCount} 筆變更尚未存檔，要放棄嗎？"))
        {
            return;
        }

        var drawer = new GlossaryDrawerViewModel(
            _glossaryStore, _glossaryCatalog, item.Id, ActiveGlossaryIds, OnGlossaryDrawerSaved, _reviewSignal,
            ToggleGlossaryCardById, MineMemberAsync)
        {
            IsMiningBusy = IsMining || !MiningConfigured,
        };
        drawer.CloseRequested += () => GlossaryDrawer = null;
        GlossaryDrawer = drawer;
    }

    /// <summary>成員探勘（抽屜成員區塊的一鍵探勘）：用單一名字跑資料探勘建角色卡，完成後新卡自動生效
    /// （沿用探勘完成的既有行為）；已有探勘在執行或未設定知識庫模型時只記 log 略過。</summary>
    public async Task MineMemberAsync(string name)
    {
        if (IsMining)
        {
            _logger.LogInformation("成員探勘略過：已有資料探勘在執行");
            return;
        }

        if (!MiningConfigured)
        {
            _logger.LogInformation("成員探勘略過：未設定知識庫模型");
            return;
        }

        await RunMiningAsync("成員探勘", (progress, ct) => _mining.MineNameAsync(
            name, _streamSession.CurrentStreamId, progress, ct), (StreamSituation ?? string.Empty).Trim());
    }

    /// <summary>抽屜存檔完成：重讀型錄；寫入的卡有生效中時重建 prompt（重置對話並預熱），否則只更新清單畫面與狀態；
    /// 最後用新型錄重載抽屜。</summary>
    private void OnGlossaryDrawerSaved(IReadOnlyCollection<long> cardIds)
    {
        if (!TryLoadGlossaryCatalog())
        {
            return;
        }

        if (cardIds.Any(id => _activeGlossaryIds.Contains(id)))
        {
            ApplyGlossaryChange();
        }
        else
        {
            RunGlossarySearch();
            RefreshActiveGlossaryCards();
            UpdateGlossaryStatus();
        }

        GlossaryDrawer?.Reload(_glossaryCatalog, ActiveGlossaryIds);
    }

    /// <summary>重新從資料庫讀取詞彙卡；生效清單保留仍存在的卡；直播中被修剪的卡會記錄到 StreamCards</summary>
    [RelayCommand]
    private void ReloadGlossary()
    {
        if (!TryLoadGlossaryCatalog()) return;

        var before = ActiveGlossaryIds;
        var removed = _activeGlossaryIds.Where(id => _glossaryCatalog.FindCard(id) is null).ToList();
        _activeGlossaryIds.RemoveAll(id => _glossaryCatalog.FindCard(id) is null);
        foreach (var id in removed)
        {
            _activeReasons.Remove(id);
        }
        ApplyGlossaryChange();
        _ = _streamCardRecorder.RecordChange(before, ActiveGlossaryIds);
    }

    /// <summary>只重讀型錄與畫面（搜尋結果、生效清單顯示）：不改 prompt 段落、不重置也不預熱。
    /// 生效清單中的 Id 不修剪，已不存在的卡等使用者按「套用到翻譯」時由 ReloadGlossary 處理。</summary>
    public void ReloadGlossaryCatalogOnly()
    {
        if (!TryLoadGlossaryCatalog()) return;

        RunGlossarySearch();
        RefreshActiveGlossaryCards();
        UpdateGlossaryStatus();
    }

    /// <summary>一鍵開台後自動套用：依網址 handle、頻道名稱與直播標題比對詞彙卡（GlossaryCatalog.FindCardsForStream）。
    /// 沒有比對結果時不改任何東西並回傳空清單；有結果時移除生效中的角色／團體卡（遊戲卡保留），
    /// 依序加入每張比對到的卡與其上層卡（只執行一次 ApplyGlossaryChange）；
    /// 回傳清單 = 比對結果，加上因為展開才加入的上層卡（原因 Parent，已在比對結果中的不重複）。</summary>
    public IReadOnlyList<GlossaryCardMatch> ApplyGlossaryForStream(string? url, string? author, string? title)
    {
        // 開台一定會呼叫：先清空關鍵字詞彙提示記住的前文（換直播＝新的字幕上下文）
        _keywordGlossary.Reset();

        // 聽錯字探勘的素材也換新：換了直播，上一場的字幕不再相關
        _mishearingLines.Clear();
        _mishearingNewLines = 0;
        RefreshMishearingStatus();

        // 遊戲用語探勘的計數也換新，並在背景重算跨主題普遍度（失敗只記 log，不影響開台）
        _termCounter.Reset();
        var excludeStreamId = _streamSession.CurrentStreamId;
        _ = Task.Run(() =>
        {
            try
            {
                var rows = _subtitleLogStore.GetTopicSubtitleSources(excludeStreamId, 100);
                _termCounter.SetTopicFrequency(TermDiscoveryCounter.BuildTopicFrequency(rows));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "計算遊戲用語探勘的跨主題普遍度失敗");
            }
        });

        var matches = _glossaryCatalog.FindCardsForStream(url, author, title);
        if (matches.Count == 0)
        {
            RefreshActiveGlossaryCards();
            UpdateGlossaryStatus();
            return [];
        }

        var matchIds = matches.Select(m => m.Card.Id).ToList();
        var expanded = _glossaryCatalog.ExpandWithParents(matchIds);
        var matchReasons = matches
            .GroupBy(m => m.Card.Id)
            .ToDictionary(g => g.Key, g => g.First().Reason);

        _activeGlossaryIds.RemoveAll(id => _glossaryCatalog.FindCard(id) is { Kind: var kind }
            && (kind == GlossaryKind.Character || kind == GlossaryKind.Group));
        foreach (var id in _activeReasons.Keys.ToList())
        {
            if (!_activeGlossaryIds.Contains(id))
            {
                _activeReasons.Remove(id);
            }
        }
        // 沒被移除、沿用下來的卡：開台前就生效 → Carried（有比對到原因的卡照比對結果）
        foreach (var id in _activeGlossaryIds)
        {
            _activeReasons[id] = matchReasons.GetValueOrDefault(id, StreamCardReason.Carried);
        }
        foreach (var id in expanded)
        {
            if (!_activeGlossaryIds.Contains(id))
            {
                _activeGlossaryIds.Add(id);
                _activeReasons[id] = matchReasons.GetValueOrDefault(id, StreamCardReason.Parent);
            }
        }
        ApplyGlossaryChange();

        var result = new List<GlossaryCardMatch>(matches);
        var seen = matchIds.ToHashSet();
        foreach (var id in expanded)
        {
            if (seen.Contains(id)) continue;
            seen.Add(id);
            if (_glossaryCatalog.FindCard(id) is { } parent)
            {
                result.Add(new GlossaryCardMatch(parent, StreamCardReason.Parent));
            }
        }
        return result;
    }

    /// <summary>生效清單有變動時的共用流程：存檔 → 更新 prompt 段落 → 重置對話並預熱 → 更新畫面
    /// → 重載抽屜的成員區塊（勾選要跟著生效清單更新）</summary>
    private void ApplyGlossaryChange()
    {
        _settings.Current.ActiveGlossaryCardIds = [.. _activeGlossaryIds];
        _ = _settings.SaveAsync();
        _glossaryState.SetPromptSection(_glossaryCatalog.BuildPromptSection(_activeGlossaryIds));
        _glossaryState.SetActiveIds(ActiveGlossaryIds);
        _manager.ResetConversation();
        _ = _manager.WarmUpAsync();
        RunGlossarySearch();
        RefreshActiveGlossaryCards();
        UpdateGlossaryStatus();
        GlossaryDrawer?.RefreshMembers(_glossaryCatalog, ActiveGlossaryIds);
    }

    /// <summary>從資料庫重新讀取詞彙卡並重建型錄；失敗時保留原本的型錄並顯示錯誤訊息。</summary>
    private bool TryLoadGlossaryCatalog()
    {
        try
        {
            _glossaryCatalog = new GlossaryCatalog(_glossaryStore.GetGlossaryCards(), _glossaryStore.GetGlossaryTerms());
            _glossaryState.SetCatalog(_glossaryCatalog);
            // 聽錯字修正與詞彙卡同一個資料來源：型錄重載成功時一併重新載入（失敗時自留舊清單，不影響回傳值）
            _mishearingFixes.Reload(_glossaryStore);
            return true;
        }
        catch (Exception ex)
        {
            GlossaryStatus = $"詞彙卡讀取失敗：{ex.Message}";
            return false;
        }
    }

    /// <summary>依搜尋文字重新搜尋（記憶體中進行，不查資料庫）；
    /// 文字非空白而且有結果時打開搜尋下拉，清空後關閉</summary>
    private void RunGlossarySearch()
    {
        GlossaryResults.Clear();
        foreach (var card in _glossaryCatalog.Search(GlossarySearchText))
        {
            GlossaryResults.Add(CreateGlossaryItem(card, _activeGlossaryIds.Contains(card.Id)));
        }
        IsGlossarySearchOpen = !string.IsNullOrWhiteSpace(GlossarySearchText) && GlossaryResults.Count > 0;
    }

    /// <summary>依生效清單重建右欄清單：一般生效卡（上層卡在前、子卡緊接在後，沒有上下層關係的照生效順序）</summary>
    private void RefreshActiveGlossaryCards()
    {
        ActiveGlossaryCards.Clear();
        foreach (var id in OrderActiveIds())
        {
            if (_glossaryCatalog.FindCard(id) is { } card)
            {
                ActiveGlossaryCards.Add(CreateGlossaryItem(card, isActive: true));
            }
        }
    }

    /// <summary>一般生效卡的顯示順序：上層卡在前、子卡緊接在後；同層照生效順序；沒有上下層關係的照生效順序。</summary>
    private IEnumerable<long> OrderActiveIds()
    {
        var order = new List<long>();
        var emitted = new HashSet<long>();
        foreach (var id in _activeGlossaryIds)
        {
            EmitWithRelatives(id, order, emitted, []);
        }
        return order;
    }

    /// <summary>輸出一張卡（需要時先把上層卡排在前面），接著依生效順序輸出它的子卡；已輸出或循環上層時略過。</summary>
    private void EmitWithRelatives(long id, List<long> order, HashSet<long> emitted, HashSet<long> visiting)
    {
        if (emitted.Contains(id) || !visiting.Add(id))
        {
            return;
        }

        try
        {
            if (_glossaryCatalog.FindCard(id) is { ParentId: { } parentId }
                && _activeGlossaryIds.Contains(parentId)
                && !emitted.Contains(parentId))
            {
                EmitWithRelatives(parentId, order, emitted, visiting);
            }

            // 上層卡輸出時已連帶輸出這張子卡
            if (emitted.Contains(id))
            {
                return;
            }

            emitted.Add(id);
            order.Add(id);
            foreach (var childId in _activeGlossaryIds.Where(other =>
                         _glossaryCatalog.FindCard(other)?.ParentId == id))
            {
                EmitWithRelatives(childId, order, emitted, visiting);
            }
        }
        finally
        {
            visiting.Remove(id);
        }
    }

    private GlossaryCardItem CreateGlossaryItem(GlossaryCard card, bool isActive)
    {
        var termCount = _glossaryCatalog.TermsOf(card.Id).Count;
        var parentName = card.ParentId is { } parentId ? _glossaryCatalog.FindCard(parentId)?.Name : null;
        return new GlossaryCardItem(card, termCount, parentName)
        {
            IsActive = isActive,
            ReasonText = ReasonDisplay(_activeReasons.GetValueOrDefault(card.Id), KindTextFallback(card)),
            Indent = card.ParentId is { } id && _activeGlossaryIds.Contains(id),
        };
    }

    /// <summary>生效原因代碼 → 顯示文字；沒有原因時回傳種類文字（角色／團體／遊戲）。</summary>
    private static string ReasonDisplay(string? reason, string kindText) => reason switch
    {
        StreamCardReason.Host => "主播",
        StreamCardReason.HostGroup => "所屬團體",
        StreamCardReason.Title => "連動",
        StreamCardReason.TitleGame => "作品",
        StreamCardReason.Parent => "上層",
        StreamCardReason.Carried => "沿用",
        StreamCardReason.Manual => "手動",
        StreamCardReason.Situation => "情境",
        StreamCardReason.Mining => "探勘",
        _ => kindText,
    };

    /// <summary>沒有原因時的後備文字：與 GlossaryCardItem.KindText 相同的種類對照。</summary>
    private static string KindTextFallback(GlossaryCard card) => card.Kind switch
    {
        GlossaryKind.Character => "角色",
        GlossaryKind.Group => "團體",
        GlossaryKind.Game => "遊戲",
        GlossaryKind.Common => "共通用語",
        _ => card.Kind,
    };

    private void UpdateGlossaryStatus()
    {
        UpdateGlossarySummary();
        if (_activeGlossaryIds.Count == 0)
        {
            GlossaryStatus = "尚未選擇詞彙卡";
            return;
        }

        var section = _glossaryState.PromptSection;
        var termLines = section.Split('\n').Count(line => !line.StartsWith("#", StringComparison.Ordinal));
        GlossaryStatus = $"生效 {_activeGlossaryIds.Count} 張・名詞 {termLines} 個・約 {section.Length} 字";
    }

    /// <summary>組直播資訊標題列的詞彙卡摘要：生效卡列出前 3 張卡名，超過時加「等 N 張」。</summary>
    private void UpdateGlossarySummary()
    {
        var activeSummary = SummarizeCards(_activeGlossaryIds);
        GlossarySummaryText = activeSummary.Length == 0 ? string.Empty : $"詞彙卡：{activeSummary}";
    }

    private string SummarizeCards(List<long> ids)
    {
        var names = ids
            .Select(id => _glossaryCatalog.FindCard(id)?.Name)
            .Where(name => name is not null)
            .ToList()!;
        if (names.Count == 0)
        {
            return string.Empty;
        }
        var shown = string.Join("、", names.Take(3));
        return names.Count > 3 ? $"{shown} 等 {names.Count} 張" : shown;
    }

    // ===== 編輯與鎖定 =====

    /// <summary>進入編輯時呼叫：鎖定列表，避免滾動跳動</summary>
    public void Lock()
    {
        IsLocked = true;
    }

    /// <summary>結束編輯時呼叫：解除鎖定；沒有手動暫停時把暫存的字幕補上</summary>
    public void Unlock()
    {
        IsLocked = false;
        if (!IsPaused)
        {
            FlushPending();
        }
    }

    /// <summary>把暫存的字幕依時間順序插回，最新的在最上面</summary>
    private void FlushPending()
    {
        foreach (var job in _pending)
        {
            InsertJob(job);
        }

        _pending.Clear();
        PendingCount = 0;
    }

    /// <summary>提交翻譯欄的編輯：寫入手動快取並更新該筆譯文</summary>
    public void CommitTranslationEdit(TranslationJob job, string newText)
    {
        var trimmed = newText.Trim();

        if (string.IsNullOrWhiteSpace(trimmed) || trimmed == job.TranslatedText)
        {
            Unlock();
            return;
        }

        _manager.SaveCorrection(job.CacheKey, job.Current, trimmed);
        job.TranslatedText = trimmed;
        _logWriter.EnqueueEdited(job.Id, null, trimmed);
        ActionMessage = "已儲存修正的翻譯";
        Unlock();
    }

    /// <summary>提交原文欄的編輯：以修正後的原文重翻，成功才更新該筆</summary>
    public async Task CommitSourceEditAsync(TranslationJob job, string newText)
    {
        var trimmed = newText.Trim();

        if (string.IsNullOrWhiteSpace(trimmed) || trimmed == job.Current)
        {
            Unlock();
            return;
        }

        var result = await RetranslateCoreAsync(job.CacheKey, trimmed);
        if (result is not null)
        {
            job.Current = trimmed;
            job.TranslatedText = result;
            _logWriter.EnqueueEdited(job.Id, trimmed, result);
            ActionMessage = "已用修正後的原文重新翻譯";
        }

        Unlock();
    }

    // ===== 右鍵選單 =====

    /// <summary>以目前原文重新翻譯（不鎖定列表）</summary>
    [RelayCommand]
    private async Task RetranslateSelected()
    {
        if (SelectedJob is not { } job || IsLocked || IsRetranslating)
        {
            return;
        }

        var result = await RetranslateCoreAsync(job.CacheKey, job.Current);
        if (result is not null)
        {
            job.TranslatedText = result;
            _logWriter.EnqueueEdited(job.Id, null, result);
            ActionMessage = "已重新翻譯";
        }
    }

    /// <summary>
    /// 把這句原文的修改（雙擊原文修正過的 Whisper 聽錯）轉成一條原文修正規則，之後同樣的聽錯會在翻譯前自動修正。
    /// 只取被改掉的那個詞（例：カーレスター=カレスタ），不是整句。
    /// </summary>
    [RelayCommand]
    private void AddSourceRuleFromEdit()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        var change = TextDiff.ChangedSegment(job.CacheKey, job.Current);
        if (change is null)
        {
            ActionMessage = "這句原文沒有修改過：先雙擊原文修正聽錯的地方，再使用這個功能";
            return;
        }

        var (from, to) = change.Value;

        var rules = Translation.Bridge.Core.Services.ReplacementRules.Parse(SourceReplacementRules);
        if (rules.Any(r => r.From == from))
        {
            ActionMessage = $"原文修正已經有「{from}」的規則";
            return;
        }

        var line = $"{from}={to}";
        SourceReplacementRules = string.IsNullOrWhiteSpace(SourceReplacementRules)
            ? line
            : SourceReplacementRules.TrimEnd('\r', '\n') + Environment.NewLine + line;
        ActionMessage = $"已加入原文修正：{line}";
    }

    [RelayCommand]
    private void CopySource()
    {
        if (SelectedJob is { } job) CopyToClipboard(job.Current, "已複製原文");
    }

    [RelayCommand]
    private void CopyTranslation()
    {
        if (SelectedJob is { } job) CopyToClipboard(job.TranslatedText, "已複製翻譯");
    }

    [RelayCommand]
    private void CopyBoth()
    {
        if (SelectedJob is { } job) CopyToClipboard($"{job.Current}\n{job.TranslatedText}", "已複製原文與翻譯");
    }

    /// <summary>點一下探勘結果標籤：把名詞本身複製到剪貼簿（候選數量等沒有名詞的標籤不做事）。</summary>
    [RelayCommand]
    private void CopyMiningTag(MiningTag? tag)
    {
        if (tag?.CanCopy == true)
        {
            CopyToClipboard(tag.CopyText!, $"已複製「{tag.CopyText}」");
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedJob is not { } job || IsLocked)
        {
            return;
        }

        _manager.DeleteTranslation(job.CacheKey, job.Current);
        _logWriter.EnqueueDeleted(job.Id);
        Jobs.Remove(job);
        SelectedJob = null;
        RefreshStats();
    }

    [RelayCommand]
    private void ResetConversation()
    {
        _manager.ResetConversation();
        _ = _manager.WarmUpAsync();
        ProfileMessage = "對話已重置";
    }

    /// <summary>重翻共用流程：顯示「重翻中」、失敗時留下提示（原本內容不變）</summary>
    private async Task<string?> RetranslateCoreAsync(string cacheKey, string source)
    {
        IsRetranslating = true;
        ActionMessage = string.Empty;
        string? result;
        try
        {
            result = await _manager.RetranslateAsync(cacheKey, source);
        }
        catch
        {
            result = null;
        }
        finally
        {
            IsRetranslating = false;
        }

        if (result is null)
        {
            ActionMessage = "重翻失敗（逾時或結果異常），內容未變更";
        }

        return result;
    }

    private void CopyToClipboard(string text, string message)
    {
        try
        {
            Clipboard.SetText(text);
            ActionMessage = message;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 剪貼簿被其他程式占用時會丟例外：提示即可，不影響其他功能
            ActionMessage = "剪貼簿忙碌中，請再試一次";
        }
    }

    private void InsertJob(TranslationJob job)
    {
        // 修正版的 Job 取代前一版：先移除被取代的舊列（找不到就略過）
        if (job.RevisionOf is { } oldId)
        {
            var stale = Jobs.FirstOrDefault(existing => existing.Id == oldId);
            if (stale is not null)
            {
                Jobs.Remove(stale);
            }
        }

        Jobs.Insert(0, job);

        if (Jobs.Count > MaxJobs)
        {
            Jobs.RemoveAt(Jobs.Count - 1);
        }
    }
}
