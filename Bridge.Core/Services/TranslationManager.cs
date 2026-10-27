using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

public class TranslationManager
{
    /// <summary>最近字幕清單的最大筆數（供重複字幕判斷比對）。</summary>
    private const int MaxRecentItems = 3;

    /// <summary>單輪模式：Context 只有最後幾行會附上最近翻過的譯文。</summary>
    private const int ContextTranslationLines = 3;

    private readonly IOpenAiClient _llmClient;
    private readonly ITranslationStore _db;
    private readonly ISettingsService _settings;
    private readonly ITextProcessingService _textService;
    private readonly ConversationSession _session;
    private readonly TimeProvider _timeProvider;
    private readonly GlossaryState? _glossary;
    private readonly KeywordGlossary? _keywordGlossary;
    private readonly MishearingFixState? _mishearingFixes;
    private readonly CaptionReference? _captionReference;
    private readonly PrefetchInFlight? _prefetchInFlight;
    private readonly ChapterOverlayState? _chapterOverlay;
    private readonly ILogger<TranslationManager>? _logger;
    private readonly LlmCallRecorder? _recorder;

    /// <summary>最近翻譯過的字幕（舊到新），供重複字幕判斷使用。</summary>
    private readonly object _recentLock = new();
    private readonly List<RecentItem> _recent = [];

    /// <summary>背景完成上限：逾時後不再取消模型請求，讓它在背景繼續跑完（伺服器會留下對話檢查點）。</summary>
    private static readonly TimeSpan BackgroundCompletionLimit = TimeSpan.FromSeconds(30);

    /// <summary>背景完成請求（逾時後不取消的模型請求）與補記快取／歷史所需的資訊。</summary>
    private sealed class BackgroundRequest
    {
        /// <summary>進行中的模型請求（以背景 token 送出，尚未完成）。</summary>
        public Task<ChatResult> CallTask { get; set; } = null!;
        /// <summary>背景 token 的來源；請求結束時處置（處置不會取消 token）。</summary>
        public CancellationTokenSource CallCts { get; set; } = null!;
        /// <summary>送出那次 TranslationJob 的 Id（llm_calls 已以此 Id 記錄，僅供追蹤）。</summary>
        public string JobId { get; set; } = string.Empty;
        /// <summary>原文（原文修正規則套用後的 Current，即快取索引）。</summary>
        public string Current { get; set; } = string.Empty;
        /// <summary>送出時的 system prompt。</summary>
        public string SystemPrompt { get; set; } = string.Empty;
        /// <summary>送出時的重複字幕判斷前一句（成功時 Replace 的對象）。</summary>
        public RecentItem? Previous { get; set; }
        /// <summary>送出時間（Stopwatch timestamp，供背景完成 log 計算實際耗時）。</summary>
        public long StartedAtTimestamp { get; set; }
    }

    /// <summary>背景完成請求（同時最多一筆）；null 表示目前沒有。</summary>
    private readonly object _backgroundLock = new();
    private BackgroundRequest? _background;

    /// <summary>背景請求進行中收到的快取命中輪次，待背景結束後依序補進對話歷史。</summary>
    private readonly List<ChatTurn> _pendingHistoryTurns = [];

    /// <summary>最近一次真正送出即時模型請求的 UTC 時間（ticks；0＝還沒送過）。</summary>
    private long _lastLiveModelCallAtTicks;

    /// <summary>最近一次真正送出即時模型請求的時間（UTC）；還沒送過時為 null。
    /// 預先翻譯用它判斷即時翻譯是否正在運作，決定批次結束後要不要預熱即時對話。</summary>
    public DateTimeOffset? LastLiveModelCallAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastLiveModelCallAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public TranslationManager(
        IOpenAiClient llmClient,
        ITextProcessingService textService,
        ITranslationStore db,
        ISettingsService settings,
        TimeProvider? timeProvider = null,
        GlossaryState? glossary = null,
        KeywordGlossary? keywordGlossary = null,
        MishearingFixState? mishearingFixes = null,
        CaptionReference? captionReference = null,
        ILogger<TranslationManager>? logger = null,
        PrefetchInFlight? prefetchInFlight = null,
        LlmCallRecorder? recorder = null,
        ChapterOverlayState? chapterOverlay = null)
    {
        _llmClient = llmClient;
        _db = db;
        _settings = settings;
        _textService = textService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _glossary = glossary;
        _keywordGlossary = keywordGlossary;
        _mishearingFixes = mishearingFixes;
        _captionReference = captionReference;
        _logger = logger;
        _prefetchInFlight = prefetchInFlight;
        _recorder = recorder;
        _chapterOverlay = chapterOverlay;
        _session = new ConversationSession(_timeProvider);
    }

    /// <summary>常駐生效中的詞彙卡 Id（GlossaryState.ActiveIds）加上其附屬用語卡
    /// （<see cref="GlossaryCatalog.FindSupplementCards"/>：生效遊戲卡的 Common 子卡）；
    /// 聽錯字修正只套用這些卡的 Approved + Pre 修正。</summary>
    private IReadOnlyCollection<long> GetActiveGlossaryCardIds()
    {
        var ids = new HashSet<long>();
        if (_glossary is null)
        {
            return ids;
        }

        var activeIds = _glossary.ActiveIds;
        foreach (var id in activeIds)
        {
            ids.Add(id);
        }
        // 上層遊戲卡生效時，它的附屬用語卡也套用聽錯字修正（卡片本身不算生效）
        foreach (var card in _glossary.Catalog.FindSupplementCards(activeIds))
        {
            ids.Add(card.Id);
        }
        return ids;
    }

    /// <summary>原文正規化（與 TranslateAsync 查快取用的值完全相同）：先套用生效中詞彙卡的聽錯字修正，
    /// 再套用使用者手寫的原文修正規則（使用者的規則永遠是最後決定）。</summary>
    public string NormalizeSource(string text) => NormalizeSource(text, null);

    /// <summary>同 <see cref="NormalizeSource(string)"/>；<paramref name="appliedFixIds"/> 非空時收集實際命中的聽錯字修正 Id（回溯紀錄用）。</summary>
    private string NormalizeSource(string text, ICollection<long>? appliedFixIds)
    {
        var s = _settings.Current;

        // 聽錯字修正（前處理）：把 Whisper 聽錯的寫法換成正確寫法，只套用常駐生效中詞彙卡的 Approved + Pre 修正
        if (_mishearingFixes is { Count: > 0 })
        {
            text = _mishearingFixes.Apply(text, GetActiveGlossaryCardIds(), appliedFixIds);
        }

        // 原文修正規則：使用者的規則永遠是最後決定
        if (!string.IsNullOrWhiteSpace(s.SourceReplacementRules))
        {
            text = ReplacementRules.Apply(text, s.SourceReplacementRules);
        }

        return text;
    }

    /// <summary>最近一筆字幕的紀錄（供重複字幕判斷）。</summary>
    private sealed class RecentItem
    {
        public DateTimeOffset Time { get; set; }
        /// <summary>原文（原文修正規則套用後的 Current）。</summary>
        public string Source { get; set; } = string.Empty;
        /// <summary>譯文（後處理後、譯名修正前；與快取存的值相同）。</summary>
        public string Translation { get; set; } = string.Empty;
        /// <summary>對應 TranslationJob 的 Id。</summary>
        public string JobId { get; set; } = string.Empty;
        /// <summary>該句在多輪歷史中的 user 文字。</summary>
        public string HistoryUser { get; set; } = string.Empty;
    }

    /// <summary>從最近字幕由新到舊找第一筆時間窗內且與 current 相關（Classify 非 None）的項目。</summary>
    private (RecentItem? Item, RevisionKind Kind) FindRecentRevision(int windowSeconds, string current)
    {
        if (windowSeconds <= 0) return (null, RevisionKind.None);

        var now = _timeProvider.GetUtcNow();
        var window = TimeSpan.FromSeconds(windowSeconds);
        lock (_recentLock)
        {
            for (var i = _recent.Count - 1; i >= 0; i--)
            {
                var item = _recent[i];
                if (now - item.Time > window) continue;
                var kind = RevisionDetector.Classify(item.Source, current);
                if (kind != RevisionKind.None) return (item, kind);
            }
        }
        return (null, RevisionKind.None);
    }

    /// <summary>單輪模式用：對 Context 的最後 <see cref="ContextTranslationLines"/> 行，在最近字幕由新到舊找
    /// Source 完全相同的項目取其譯文；更早的行與找不到的行都是 null（譯文空字串視為 null）。</summary>
    private string?[] FindContextTranslations(string[] context)
    {
        var translations = new string?[context.Length];
        var start = Math.Max(0, context.Length - ContextTranslationLines);
        lock (_recentLock)
        {
            for (var i = start; i < context.Length; i++)
            {
                for (var j = _recent.Count - 1; j >= 0; j--)
                {
                    if (!string.Equals(_recent[j].Source, context[i], StringComparison.Ordinal)) continue;
                    if (_recent[j].Translation.Length > 0)
                    {
                        translations[i] = _recent[j].Translation;
                    }
                    break;
                }
            }
        }
        return translations;
    }

    /// <summary>新增一筆最近字幕；超過上限時移除最舊的。</summary>
    private void AddRecent(DateTimeOffset time, string source, string translation, string jobId, string historyUser)
    {
        lock (_recentLock)
        {
            _recent.Add(new RecentItem
            {
                Time = time,
                Source = source,
                Translation = translation,
                JobId = jobId,
                HistoryUser = historyUser
            });
            if (_recent.Count > MaxRecentItems)
            {
                _recent.RemoveAt(0);
            }
        }
    }

    /// <summary>以這次的值完整更新最近字幕的那一筆。</summary>
    private void UpdateRecent(RecentItem item, DateTimeOffset time, string source, string translation, string jobId, string historyUser)
    {
        lock (_recentLock)
        {
            item.Time = time;
            item.Source = source;
            item.Translation = translation;
            item.JobId = jobId;
            item.HistoryUser = historyUser;
        }
    }

    /// <summary>沿用譯文時更新最近字幕的那一筆：時間、原文、Job Id 換成這次的，譯文與歷史 user 不變。</summary>
    private void RefreshRecent(RecentItem item, DateTimeOffset time, string source, string jobId)
    {
        lock (_recentLock)
        {
            item.Time = time;
            item.Source = source;
            item.JobId = jobId;
        }
    }

    /// <summary>移除原文等於指定文字的最近字幕項目。</summary>
    private void RemoveRecent(string text)
    {
        lock (_recentLock)
        {
            _recent.RemoveAll(item =>
                string.Equals(item.Source, text, StringComparison.Ordinal));
        }
    }

    public async Task<string> TranslateAsync(PotplayerRequest? request, CancellationToken cancellationToken = default)
    {
        if (request is null) return string.Empty;

        // 章節名稱疊加的 cue：PotPlayer 把左上角的章節標題送來翻譯時，直接回傳 ChapterOverlayState 已翻好的中文
        // （不送模型、不寫快取、不進對話歷史、不寫字幕紀錄），PotPlayer 會照平常的方式顯示「中文在上、日文在下」
        if (_chapterOverlay is not null && !string.IsNullOrWhiteSpace(request.Current))
        {
            var chapterTranslation = _chapterOverlay.FindTranslation(request.Current);
            if (chapterTranslation is not null)
            {
                _logger?.LogDebug("即時翻譯：{Current} 是章節名稱，直接回傳疊加譯文", request.Current);
                return ReplacementRules.Apply(chapterTranslation, _settings.Current.ReplacementRules);
            }
        }

        // 連線測試請求：直接回傳原文，不送模型、不讀寫快取、不進歷史、不發 Job
        if (request.Probe || string.Equals(request.Current, PromptBuilder.ProbeText, StringComparison.Ordinal))
        {
            return request.Current;
        }

        var s = _settings.Current;

        // Whisper 原始文字（聽錯字修正與原文修正之前）：之後的每一個 Job 都帶著它寫進字幕紀錄
        var rawSource = request.Current;

        // 原文修正：翻譯前把聽錯的原文（Current 與 Context）換成正確文字（NormalizeSource：聽錯字修正 → 原文修正規則），
        // 之後的快取索引、Job、多輪歷史、送給模型的內容與崩壞偵測一律使用修正後的原文
        var appliedFixIds = new List<long>();
        var normalizedCurrent = NormalizeSource(request.Current, appliedFixIds);
        var normalizedContext = request.Context
            .Select(NormalizeSource)
            .ToArray();
        if (normalizedCurrent != request.Current || !normalizedContext.SequenceEqual(request.Context))
        {
            request = new PotplayerRequest
            {
                Current = normalizedCurrent,
                Context = normalizedContext,
                Probe = request.Probe
            };
        }

        // 無意義字幕（純符號、單字重複等）不送模型、不寫快取，直接原文回傳
        if (_textService.ShouldSkipTranslation(request.Current))
        {
            return request.Current;
        }

        // 關鍵字詞彙提示：非直通模式時掃描本句與前文（原文修正之後的文字），
        // 未生效卡的名詞出現就放進 user prompt（不改 system prompt，因此不重置對話、不需重新預熱）
        var matchedTermIds = new List<long>();
        var keywordSection = !s.ThroughPass
            ? _keywordGlossary?.BuildUserPromptSection(request.Current, request.Context, matchedTermIds) ?? string.Empty
            : string.Empty;

        // YouTube 字幕參考：非直通模式時，以播放位置在同時段找最像的 YouTube 字幕，
        // 夠像時放進 user prompt（Whisper 明顯聽錯時模型可採用 YouTube 的寫法）
        var captionSection = !s.ThroughPass
            ? _captionReference?.BuildUserPromptSection(request.Current) ?? string.Empty
            : string.Empty;

        // 每次使用前同步多輪對話的重置設定
        _session.MaxTurns = s.Translation.ConversationMaxTurns;
        _session.KeepTurns = s.Translation.ConversationKeepTurns;
        _session.IdleReset = TimeSpan.FromSeconds(s.Translation.ConversationIdleResetSeconds);

        // 重複字幕判斷：非直通模式時，從最近字幕（時間窗內、由新到舊）找「前一句」修正版
        var (previous, revision) = !s.ThroughPass
            ? FindRecentRevision(s.Translation.RevisionWindowSeconds, request.Current)
            : ((RecentItem?)null, RevisionKind.None);

        var conversation = s.Translation.UseConversationMode;
        var systemPrompt = PromptBuilder.BuildSystemPrompt(s, _glossary?.PromptSection);
        IReadOnlyList<ChatTurn> history = [];

        string userPrompt;
        // 實際送給模型的 user 內容（多輪＝本句原文；可能加關鍵字詞彙段落，但歷史存的仍是本句原文）
        string modelUserText;
        if (conversation)
        {
            // 多輪模式：整段參考（CaptionReference.FullHeader 開頭）時本句前面加標題，
            // 明確標出只翻譯這段（避免模型照參考段落翻出前後句）；短提示只有一行，不會被誤翻成前後句，不加
            var currentText = captionSection.StartsWith(CaptionReference.FullHeader, StringComparison.Ordinal)
                ? $"# 本句（只翻譯這段）\n{request.Current}"
                : request.Current;
            history = _session.GetHistory(systemPrompt);
            // system prompt 改變（對話被重置）：背景期間暫存的歷史輪次一併清空
            ClearPendingHistoryIfSystemChanged(systemPrompt);
            userPrompt = BuildConversationUserPrompt(history, currentText);
            modelUserText = currentText;
        }
        else
        {
            // 單輪模式：prompt 已有 # Current 標題，不再包 # 本句；
            // Context 的最後幾行附上最近翻過的譯文（原文 → 譯文），多輪與直通模式不做
            userPrompt = PromptBuilder.BuildSingleTurnUserPrompt(
                request,
                s.ThroughPass ? null : FindContextTranslations(request.Context));
            modelUserText = userPrompt;
        }
        // 段落順序：關鍵字詞彙 → YouTube 字幕參考 → 本句內容（空段落略過）
        var promptSections = new[] { keywordSection, captionSection }.Where(section => section.Length > 0).ToArray();
        if (promptSections.Length > 0)
        {
            var prefix = string.Join("\n---\n", promptSections);
            userPrompt = $"{prefix}\n---\n{userPrompt}";
            modelUserText = $"{prefix}\n---\n{modelUserText}";
        }

        var job = new TranslationJob
        {
            Current = request.Current,
            RawSource = rawSource,
            CacheKey = request.Current,
            Context = string.Join("\n", request.Context),
            UserPrompt = userPrompt,
            SystemPrompt = systemPrompt
        };

        var swTotal = Stopwatch.StartNew();

        string result = string.Empty;
        long modelTime = 0;
        bool truncated = false;

        if (s.ThroughPass)
        {
            await Task.Delay(10);
            result = request.Current;
            modelTime = 10;
            job.Source = TranslationSource.ThroughPass;
        }
        else
        {
            // 這句正在預先翻譯的批次裡：等那批完成（最多 TimeoutMs）再查快取，不另外送模型；
            // 但快取已有譯文時不等（上次看過的句子，預翻只是在重翻），直接往下走既有的查快取流程
            if (_prefetchInFlight?.WaitTaskFor(request.Current) is { } pending &&
                _db.GetCache(request.Current) is null)
            {
                var swWait = Stopwatch.StartNew();
                var waitLimitMs = s.Translation.TimeoutMs > 0 ? s.Translation.TimeoutMs : 5000;
                await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(waitLimitMs), _timeProvider, cancellationToken));
                swWait.Stop();
                _logger?.LogDebug("即時翻譯：這句正在預先翻譯，等批次完成（{Elapsed} ms）", swWait.ElapsedMilliseconds);
                // 呼叫端取消時，照原本的行為往外拋 OperationCanceledException
                cancellationToken.ThrowIfCancellationRequested();
            }

            // 記錄推送字幕行被請求的時間（不論快取是否命中）：預翻用它換算 PotPlayer 的提前請求距離
            if (_prefetchInFlight is { } noted &&
                noted.IsActive &&
                noted.IsKnown(request.Current))
            {
                noted.NoteRequested(request.Current);
            }

            // 讓路：預先翻譯運作中、這句是推送字幕行（IsKnown）、快取沒命中而且不在正在執行的批次裡時，
            // 要求預翻「從這句開始馬上翻一批」並等結果；等完只查快取，沒命中也不另外送即時模型
            // （預翻與即時翻譯共用單槽模型伺服器，輪流送會互相擠掉前綴快取，形成連鎖逾時）
            var yieldedToPrefetch = false;
            if (_prefetchInFlight is { } prefetch &&
                prefetch.IsActive &&
                prefetch.IsKnown(request.Current) &&
                _db.GetCache(request.Current) is null &&
                prefetch.WaitTaskFor(request.Current) is null)
            {
                yieldedToPrefetch = true;
                var swYield = Stopwatch.StartNew();
                var urgent = prefetch.RequestUrgent(request.Current);
                var yieldLimitMs = s.Translation.TimeoutMs > 0 ? s.Translation.TimeoutMs : 5000;
                await Task.WhenAny(urgent, Task.Delay(TimeSpan.FromMilliseconds(yieldLimitMs), _timeProvider, cancellationToken));
                swYield.Stop();
                _logger?.LogDebug("即時翻譯：交給預先翻譯（{Elapsed} ms，{Result}）",
                    swYield.ElapsedMilliseconds, _db.GetCache(request.Current) is not null ? "命中" : "未命中");
                // 呼叫端取消時，照原本的行為往外拋 OperationCanceledException
                cancellationToken.ThrowIfCancellationRequested();
            }

            // 1. 先查 SQLite 快取
            var cached = _db.GetCache(request.Current);
            if (cached != null)
            {
                // 崩壞譯文（長句只回一兩個字）會污染多輪歷史並自我強化：
                // 非手動項目刪除後視為未命中、改走模型；手動修正的項目維持命中，但不進歷史
                var collapsedCache = CollapseDetector.IsCollapsed(request.Current, cached);
                if (!collapsedCache || !_db.DeleteCache(request.Current))
                {
                    swTotal.Stop();
                    // 快取存「替換前」譯文，替換只在輸出時做：規則更新後舊快取也會被修正
                    var output = ReplacementRules.Apply(cached, s.ReplacementRules);
                    // 多輪模式：快取命中也記一輪對話（存快取內容，未經譯名替換）；
                    // 是前一句的修正版時以 Replace 取代該輪（位置不變），找不到才 Append；
                    // 崩壞的手動項目不記錄，避免污染多輪對話
                    if (conversation && !collapsedCache)
                    {
                        if (_background is not null)
                        {
                            // 背景請求進行中：先進暫存清單，背景結束後依序補進（歷史順序：背景那句在前）
                            lock (_backgroundLock)
                            {
                                _pendingHistoryTurns.Add(new ChatTurn(request.Current, cached));
                            }
                        }
                        else if (previous is null || !_session.Replace(previous.HistoryUser, request.Current, cached))
                        {
                            _session.Append(request.Current, cached);
                        }
                    }
                    // 發送一個 "Cache Hit" 的 Job 到 UI，讓用戶知道這是快取回傳的
                    var cacheJob = new TranslationJob
                    {
                        Current = request.Current,
                        RawSource = rawSource,
                        CacheKey = request.Current,
                        Context = string.Join("\n", request.Context),
                        UserPrompt = userPrompt,
                        TranslatedText = output,
                        ModelLatency = 0, // 快取命中，模型耗時為 0
                        TotalLatency = swTotal.ElapsedMilliseconds,
                        SystemPrompt = "CACHED",
                        Source = TranslationSource.Cache,
                        RevisionOf = previous?.JobId
                    };
                    // 回溯紀錄：命中快取時記下該筆 TranslationCache 的 Id（只在命中時查一次）
                    cacheJob.CacheId = _db.GetCacheId(request.Current);
                    // 崩壞的手動項目不進歷史，也不更新最近字幕；其他情況更新或新增最近字幕
                    if (!collapsedCache)
                    {
                        var now = _timeProvider.GetUtcNow();
                        if (previous is not null)
                        {
                            UpdateRecent(previous, now, request.Current, cached, cacheJob.Id, request.Current);
                        }
                        else
                        {
                            AddRecent(now, request.Current, cached, cacheJob.Id, request.Current);
                        }
                    }
                    WeakReferenceMessenger.Default.Send(cacheJob);
                    return output;
                }
                // 非手動的崩壞項目已刪除：視為未命中，繼續走模型流程
            }

            // 快取未命中且只差標點或變短（Identical/Shrink）：直接沿用前一句譯文，
            // 不呼叫模型、不寫快取、不動多輪歷史
            if (previous is not null && revision is RevisionKind.Identical or RevisionKind.Shrink)
            {
                swTotal.Stop();
                var output = ReplacementRules.Apply(previous.Translation, s.ReplacementRules);
                var reusedJob = new TranslationJob
                {
                    Current = request.Current,
                    RawSource = rawSource,
                    CacheKey = request.Current,
                    Context = string.Join("\n", request.Context),
                    UserPrompt = userPrompt,
                    TranslatedText = output,
                    ModelLatency = 0,
                    TotalLatency = 0,
                    SystemPrompt = "REUSED",
                    Source = TranslationSource.Reused,
                    RevisionOf = previous.JobId
                };
                // 前一句那筆換成這次的時間、原文與 Job Id；譯文與歷史 user 維持原值
                RefreshRecent(previous, _timeProvider.GetUtcNow(), request.Current, reusedJob.Id);
                WeakReferenceMessenger.Default.Send(reusedJob);
                return output;
            }

            // 讓路後仍然沒命中：不另外送即時模型，回傳原文並發出與逾時相同的 TIMEOUT Job
            // （Id 沿用本句的 job）；不寫快取、不進歷史，也不記 llm_calls（沒有送模型）
            if (yieldedToPrefetch)
            {
                swTotal.Stop();
                WeakReferenceMessenger.Default.Send(new TranslationJob
                {
                    Id = job.Id,
                    Current = request.Current,
                    RawSource = rawSource,
                    CacheKey = request.Current,
                    Context = string.Join("\n", request.Context),
                    UserPrompt = userPrompt,
                    SystemPrompt = "TIMEOUT",
                    TranslatedText = request.Current,
                    ModelLatency = 0,
                    TotalLatency = swTotal.ElapsedMilliseconds,
                    Source = TranslationSource.Timeout
                });
                return request.Current;
            }

            // 背景請求進行中：伺服器一次只跑一個請求，這句只會排在背景請求後面一起逾時，
            // 而且會打亂對話歷史的順序，因此不另外送模型，回傳原文並發出與逾時相同的 TIMEOUT Job；
            // 不寫快取、不進歷史，也不記 llm_calls（沒有送模型）
            if (_background is not null)
            {
                swTotal.Stop();
                _logger?.LogDebug("即時翻譯：背景請求進行中，略過模型");
                WeakReferenceMessenger.Default.Send(new TranslationJob
                {
                    Id = job.Id,
                    Current = request.Current,
                    RawSource = rawSource,
                    CacheKey = request.Current,
                    Context = string.Join("\n", request.Context),
                    UserPrompt = userPrompt,
                    SystemPrompt = "TIMEOUT",
                    TranslatedText = request.Current,
                    ModelLatency = 0,
                    TotalLatency = swTotal.ElapsedMilliseconds,
                    Source = TranslationSource.Timeout
                });
                return request.Current;
            }

            var swModel = Stopwatch.StartNew();

            // 依輸入長度限制模型輸出 token，避免模型囉嗦或重複拖長回應時間
            int maxOutputTokens = Math.Clamp(request.Current.Length * 3, 64, 256);

            // 多輪模式：user 只放 Current（不放 Context），歷史改由訊息陣列承接；單輪模式維持原本 prompt。
            // 每一次實際送出模型請求都記一筆 llm_calls（崩壞重試會送兩次，就記兩筆）；外部取消不記
            async Task<ChatResult> SendCurrentAsync(CancellationToken token)
            {
                // 真正送出即時模型請求的時間：預翻用它判斷即時翻譯是否正在運作
                Interlocked.Exchange(ref _lastLiveModelCallAtTicks, _timeProvider.GetUtcNow().UtcTicks);
                var swModelCall = Stopwatch.StartNew();
                try
                {
                    var result = conversation
                        ? await _llmClient.SendChatAsync(systemPrompt, history, modelUserText, token, maxOutputTokens)
                        : await _llmClient.SendAsync(modelUserText, systemPrompt, token, maxOutputTokens);
                    swModelCall.Stop();
                    _recorder?.Record(new LlmCallInput
                    {
                        Purpose = LlmCallPurposes.LiveTranslation,
                        JobId = job.Id,
                        SystemPrompt = systemPrompt,
                        UserPrompt = modelUserText,
                        History = conversation ? history : null,
                        ResponseRaw = result.Content,
                        KeywordTermIds = matchedTermIds,
                        FixIds = appliedFixIds,
                        Model = result.Model,
                        PromptTokens = result.PromptTokens,
                        CompletionTokens = result.CompletionTokens,
                        LatencyMs = swModelCall.ElapsedMilliseconds,
                        Outcome = LlmCallOutcomes.Ok,
                    });
                    return result;
                }
                catch (Exception) when (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // 逾時（連動 token 已觸發而外部未取消）：記一筆 Timeout
                    swModelCall.Stop();
                    _recorder?.Record(new LlmCallInput
                    {
                        Purpose = LlmCallPurposes.LiveTranslation,
                        JobId = job.Id,
                        SystemPrompt = systemPrompt,
                        UserPrompt = modelUserText,
                        History = conversation ? history : null,
                        LatencyMs = swModelCall.ElapsedMilliseconds,
                        Outcome = LlmCallOutcomes.Timeout,
                    });
                    throw;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // 其他例外：記一筆 Error 後照原本流程重擲（外部取消不記）
                    swModelCall.Stop();
                    _recorder?.Record(new LlmCallInput
                    {
                        Purpose = LlmCallPurposes.LiveTranslation,
                        JobId = job.Id,
                        SystemPrompt = systemPrompt,
                        UserPrompt = modelUserText,
                        History = conversation ? history : null,
                        LatencyMs = swModelCall.ElapsedMilliseconds,
                        Outcome = LlmCallOutcomes.Error,
                    });
                    throw;
                }
            }

            // 崩壞重試：多輪模式下「有歷史卻長句只回一兩個字」多半是歷史污染，
            // 重置對話後以空歷史重送一次（同一個 token，仍受同一個逾時保護）；最多重試一次
            async Task<ChatResult> CallModelAsync(CancellationToken token)
            {
                var first = await SendCurrentAsync(token);
                if (!first.Truncated && conversation && history.Count > 0 &&
                    CollapseDetector.IsCollapsed(request.Current, first.Content))
                {
                    _session.Reset();
                    history = [];
                    return await SendCurrentAsync(token);
                }
                return first;
            }

            ChatResult chatResult;
            if (s.Translation.TimeoutMs > 0)
            {
                // 等待逾時（不取消請求）：模型請求改用背景 token（與呼叫端取消無關、上限 30 秒），
                // 呼叫端只等 TimeoutMs——沒等到就回原文、發 TIMEOUT Job，請求交給背景完成處理繼續跑完。
                // 伺服器只在回答開始時留下對話檢查點、被取消的請求不會留下：取消會打斷前綴快取、形成連鎖逾時
                var backgroundCts = new CancellationTokenSource();
                backgroundCts.CancelAfter(BackgroundCompletionLimit);
                var startedAt = Stopwatch.GetTimestamp();
                var callTask = CallModelAsync(backgroundCts.Token);
                var completed = await Task.WhenAny(
                    callTask,
                    Task.Delay(TimeSpan.FromMilliseconds(s.Translation.TimeoutMs), _timeProvider, cancellationToken));
                if (completed == callTask)
                {
                    // TimeoutMs 內完成：與成功路徑完全相同（callTask 的例外照現在的錯誤處理往外拋）
                    try
                    {
                        chatResult = await callTask;
                    }
                    finally
                    {
                        backgroundCts.Dispose();
                    }
                }
                else
                {
                    swModel.Stop();
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // 呼叫端取消（PotPlayer 斷線等）：照原本行為往外拋 OperationCanceledException；
                        // 背景請求繼續，由背景完成處理接手
                        StartBackgroundCompletion(callTask, backgroundCts, job.Id, request.Current, systemPrompt, previous, startedAt);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    // 等待逾時（而非外部取消）：回傳原文、不寫入快取，並發送 TIMEOUT Job 通知 UI
                    // （沿用原本的句子編號：llm_calls 的紀錄以 job.Id 對回 subtitle_log，在請求真正結束時記錄）
                    WeakReferenceMessenger.Default.Send(new TranslationJob
                    {
                        Id = job.Id,
                        Current = request.Current,
                        RawSource = rawSource,
                        CacheKey = request.Current,
                        Context = string.Join("\n", request.Context),
                        UserPrompt = userPrompt,
                        SystemPrompt = "TIMEOUT",
                        TranslatedText = request.Current,
                        ModelLatency = swModel.ElapsedMilliseconds,
                        Source = TranslationSource.Timeout
                    });
                    StartBackgroundCompletion(callTask, backgroundCts, job.Id, request.Current, systemPrompt, previous, startedAt);
                    return request.Current;
                }
            }
            else
            {
                chatResult = await CallModelAsync(cancellationToken);
            }
            result = chatResult.Content;
            truncated = chatResult.Truncated;
            swModel.Stop();
            modelTime = swModel.ElapsedMilliseconds;
        }

        job.ModelLatency = modelTime;

        // 後處理 (Fake Mode 也可以跑，測試過濾邏輯)
        var swProcess = Stopwatch.StartNew();
        var processedResult = _textService.PostProcess(result, request.Current);
        swProcess.Stop();

        job.ProcessLatency = swProcess.ElapsedMilliseconds;
        job.TotalLatency = swTotal.ElapsedMilliseconds;

        // 崩壞譯文（長句只回一兩個字）不寫入快取、不進多輪歷史，避免壞結果自我強化
        var collapsed = CollapseDetector.IsCollapsed(request.Current, processedResult);

        // 成功結果（可寫入快取／進歷史）：直通、截斷、空白、崩壞皆不算
        var success = !s.ThroughPass && !truncated && !collapsed && !string.IsNullOrWhiteSpace(processedResult);

        // 截斷或空白的譯文不寫入快取，避免之後同一句永遠拿到壞結果（快取存「替換前」的譯文）
        if (success)
        {
            _db.SaveCache(request.Current, processedResult);
        }

        // 多輪模式：成功 → 記一輪對話（存模型原始輸出，非後處理或替換後的值）；
        // 是前一句的修正版（Revise）時以 Replace 取代該輪，讓歷史維持一句一輪
        if (conversation && success)
        {
            if (previous is null || !_session.Replace(previous.HistoryUser, request.Current, result))
            {
                _session.Append(request.Current, result);
            }
        }

        // 成功的修正版：Job 標記被取代的上一筆；逾時、截斷、空白、崩壞維持 null
        if (success && previous is not null)
        {
            job.RevisionOf = previous.JobId;
        }

        // 最近字幕：成功才更新／新增，失敗結果不污染重複字幕判斷
        if (success)
        {
            var now = _timeProvider.GetUtcNow();
            if (previous is not null)
            {
                UpdateRecent(previous, now, request.Current, processedResult, job.Id, request.Current);
            }
            else
            {
                AddRecent(now, request.Current, processedResult, job.Id, request.Current);
            }
        }

        // 後處理後為空白：回傳原文，避免 PotPlayer 顯示空字幕（不套用替換）
        if (string.IsNullOrWhiteSpace(processedResult))
        {
            processedResult = request.Current;
        }
        else if (!s.ThroughPass)
        {
            // 譯名修正只在輸出時做一次：快取與直通模式不動，回傳值與 Job 均為替換後
            processedResult = ReplacementRules.Apply(processedResult, s.ReplacementRules);
        }

        job.TranslatedText = processedResult;

        // 更新 UI
        WeakReferenceMessenger.Default.Send(job);

        return processedResult;
    }

    /// <summary>登記背景完成請求（同時最多一筆）並在背景處理其完成；已有一筆進行中時只觀察結果、不補記。</summary>
    private void StartBackgroundCompletion(
        Task<ChatResult> callTask,
        CancellationTokenSource callCts,
        string jobId,
        string current,
        string systemPrompt,
        RecentItem? previous,
        long startedAtTimestamp)
    {
        BackgroundRequest? request = null;
        lock (_backgroundLock)
        {
            if (_background is null)
            {
                _background = request = new BackgroundRequest
                {
                    CallTask = callTask,
                    CallCts = callCts,
                    JobId = jobId,
                    Current = current,
                    SystemPrompt = systemPrompt,
                    Previous = previous,
                    StartedAtTimestamp = startedAtTimestamp
                };
            }
        }

        if (request is null)
        {
            _ = ObserveCallAsync(callTask, callCts);
        }
        else
        {
            _ = ProcessBackgroundCompletionAsync(request);
        }
    }

    /// <summary>已有背景請求進行中時：只觀察結果與處置資源（例外已在 SendCurrentAsync 記進 llm_calls），不寫快取、不進歷史。</summary>
    private static async Task ObserveCallAsync(Task<ChatResult> callTask, CancellationTokenSource callCts)
    {
        try
        {
            await callTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 這裡不能再對背景請求做任何事：只避免未觀察例外
        }
        callCts.Dispose();
    }

    /// <summary>背景完成處理：請求在背景跑完後，成功結果寫入快取；多輪模式下 system prompt 與送出時相同時
    /// 補進對話歷史，並依序補記背景期間暫存的快取命中輪次。失敗、被上限取消或不成功的結果只記 Debug log，
    /// 不寫快取、不進歷史。不再發新的 Job、也不改已送出的 TIMEOUT Job：之後同一句再出現就會命中快取。</summary>
    private async Task ProcessBackgroundCompletionAsync(BackgroundRequest request)
    {
        ChatResult? result = null;
        try
        {
            result = await request.CallTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 失敗或被 30 秒上限取消：例外已由 SendCurrentAsync 記進 llm_calls，不寫快取、不進歷史
        }
        finally
        {
            request.CallCts.Dispose();
        }

        try
        {
            var elapsedMs = Stopwatch.GetElapsedTime(request.StartedAtTimestamp).TotalMilliseconds;

            // 背景結束（不論成功或失敗）：清出進行中槽位與暫存的快取命中輪次，稍後依序補記
            List<ChatTurn> pending;
            lock (_backgroundLock)
            {
                _background = null;
                pending = [.. _pendingHistoryTurns];
                _pendingHistoryTurns.Clear();
            }

            // 結果判斷與後處理照成功路徑：截斷、空白、崩壞皆不算成功
            var success = false;
            var processed = string.Empty;
            if (result is not null)
            {
                processed = _textService.PostProcess(result.Content, request.Current);
                success = !result.Truncated
                    && !CollapseDetector.IsCollapsed(request.Current, processed)
                    && !string.IsNullOrWhiteSpace(processed);
            }
            if (success)
            {
                // 快取存「替換前」譯文（與成功路徑相同）
                _db.SaveCache(request.Current, processed);
            }

            // 多輪模式：目前的 system prompt 和送出時相同才補進對話歷史（規則同成功路徑：
            // 有 previous 用 Replace，否則 Append；歷史存模型原始輸出，才能接續伺服器快取）。
            // 不同就只寫快取。暫存輪次也只在 system 未變時補記（背景那句必須在前，
            // 下一句請求的開頭才會和伺服器留下的檢查點一致）
            var systemUnchanged = string.Equals(
                PromptBuilder.BuildSystemPrompt(_settings.Current, _glossary?.PromptSection),
                request.SystemPrompt,
                StringComparison.Ordinal);
            if (systemUnchanged)
            {
                if (success && result is not null)
                {
                    if (request.Previous is null || !_session.Replace(request.Previous.HistoryUser, request.Current, result.Content))
                    {
                        _session.Append(request.Current, result.Content);
                    }
                }
                foreach (var turn in pending)
                {
                    _session.Append(turn.User, turn.Assistant);
                }
            }

            if (success)
            {
                if (systemUnchanged)
                {
                    _logger?.LogInformation("背景完成：{Elapsed} ms，已寫入快取並補進對話歷史", elapsedMs);
                }
                else
                {
                    _logger?.LogInformation("背景完成：{Elapsed} ms，已寫入快取", elapsedMs);
                }
            }
            else
            {
                _logger?.LogDebug("背景完成：結果失敗或不成功（{Elapsed} ms），不寫快取、不進歷史", elapsedMs);
            }
        }
        catch (Exception ex)
        {
            // 背景補記絕不能拋出例外（會成為未觀察例外）
            _logger?.LogDebug(ex, "背景完成：處理補記時發生錯誤");
        }
    }

    /// <summary>背景請求進行中而 system prompt 已改變（對話被重置）時，清空暫存的歷史輪次。</summary>
    private void ClearPendingHistoryIfSystemChanged(string systemPrompt)
    {
        lock (_backgroundLock)
        {
            if (_background is not null &&
                !string.Equals(_background.SystemPrompt, systemPrompt, StringComparison.Ordinal))
            {
                _pendingHistoryTurns.Clear();
            }
        }
    }

    /// <summary>清空多輪對話歷史與最近字幕清單。</summary>
    public void ResetConversation()
    {
        _session.Reset();
        lock (_backgroundLock)
        {
            _pendingHistoryTurns.Clear();
        }
        lock (_recentLock)
        {
            _recent.Clear();
        }
    }

    /// <summary>重翻的逾時保護（字幕校正允許比即時翻譯久，但仍在限定時間內完成）。</summary>
    private static readonly TimeSpan RetranslateTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 刪除一筆翻譯紀錄：連同手動項目刪除快取，並把對話歷史中該輪移除，
    /// 避免錯誤譯文繼續被模型模仿。<paramref name="current"/> 為修正原文後的句子，與快取索引不同時也一併移除。
    /// </summary>
    public void DeleteTranslation(string cacheKey, string current)
    {
        _db.DeleteCache(cacheKey, includeManual: true);
        _session.Remove(cacheKey);
        if (current != cacheKey)
        {
            _session.Remove(current);
        }
        RemoveRecent(cacheKey);
        if (current != cacheKey)
        {
            RemoveRecent(current);
        }
    }

    /// <summary>
    /// 儲存手動修正的譯文：快取以 <paramref name="cacheKey"/> 為索引存為手動項目，
    /// 並把對話歷史中該輪的 user/assistant 換成修正後的原文與譯文。
    /// </summary>
    public void SaveCorrection(string cacheKey, string current, string translation)
    {
        _db.SaveCache(cacheKey, translation, isManual: true);
        _session.Replace(cacheKey, current, translation);
        if (current != cacheKey)
        {
            _session.Replace(current, current, translation);
        }
        RemoveRecent(cacheKey);
        if (current != cacheKey)
        {
            RemoveRecent(current);
        }
    }

    /// <summary>
    /// 以修正後的原文（<paramref name="correctedSource"/>）重翻：送模型、通過後以原快取索引
    /// <paramref name="cacheKey"/> 寫入手動快取，並同步更新對話歷史。
    /// 逾時、例外、截斷、空白或崩壞的結果回傳 null，且不寫快取、不動歷史（外部取消則照常拋出）。
    /// </summary>
    public async Task<string?> RetranslateAsync(string cacheKey, string correctedSource, CancellationToken cancellationToken = default)
    {
        var s = _settings.Current;
        var systemPrompt = PromptBuilder.BuildSystemPrompt(s, _glossary?.PromptSection);
        var maxOutputTokens = Math.Clamp(correctedSource.Length * 3, 64, 256);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(RetranslateTimeout);

        // 重翻的模型請求也記一筆 llm_calls（purpose＝Retranslate、JobId＝null）：逾時記 Timeout、
        // 其他錯誤記 Error、外部取消不記；送到模型的內容與原本完全相同
        async Task<ChatResult> SendRetranslateAsync()
        {
            var conversation = s.Translation.UseConversationMode;
            var userText = conversation
                ? correctedSource
                : PromptBuilder.BuildSingleTurnUserPrompt(new PotplayerRequest { Current = correctedSource, Context = [correctedSource] });
            // 歷史過濾掉聽錯原文那輪，改以修正後的原文送出
            IReadOnlyList<ChatTurn> historySent = conversation
                ? _session.GetHistory(systemPrompt)
                    .Where(turn => !string.Equals(turn.User, cacheKey, StringComparison.Ordinal))
                    .ToList()
                : [];

            var swCall = Stopwatch.StartNew();
            try
            {
                var result = conversation
                    ? await _llmClient.SendChatAsync(systemPrompt, historySent, userText, linkedCts.Token, maxOutputTokens)
                    : await _llmClient.SendAsync(userText, systemPrompt, linkedCts.Token, maxOutputTokens);
                swCall.Stop();
                _recorder?.Record(new LlmCallInput
                {
                    Purpose = LlmCallPurposes.Retranslate,
                    SystemPrompt = systemPrompt,
                    UserPrompt = userText,
                    History = conversation ? historySent : null,
                    ResponseRaw = result.Content,
                    Model = result.Model,
                    PromptTokens = result.PromptTokens,
                    CompletionTokens = result.CompletionTokens,
                    LatencyMs = swCall.ElapsedMilliseconds,
                    Outcome = LlmCallOutcomes.Ok,
                });
                return result;
            }
            catch (Exception) when (linkedCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                swCall.Stop();
                _recorder?.Record(new LlmCallInput
                {
                    Purpose = LlmCallPurposes.Retranslate,
                    SystemPrompt = systemPrompt,
                    UserPrompt = userText,
                    History = conversation ? historySent : null,
                    LatencyMs = swCall.ElapsedMilliseconds,
                    Outcome = LlmCallOutcomes.Timeout,
                });
                throw;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                swCall.Stop();
                _recorder?.Record(new LlmCallInput
                {
                    Purpose = LlmCallPurposes.Retranslate,
                    SystemPrompt = systemPrompt,
                    UserPrompt = userText,
                    History = conversation ? historySent : null,
                    LatencyMs = swCall.ElapsedMilliseconds,
                    Outcome = LlmCallOutcomes.Error,
                });
                throw;
            }
        }

        ChatResult chatResult;
        try
        {
            chatResult = await SendRetranslateAsync();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // 逾時或其他錯誤：回 null，不寫快取、不動歷史（外部取消則照常往外拋）
            return null;
        }

        if (chatResult.Truncated) return null;

        var processed = _textService.PostProcess(chatResult.Content, correctedSource);
        if (string.IsNullOrWhiteSpace(processed)) return null;
        if (CollapseDetector.IsCollapsed(correctedSource, processed)) return null;

        // 索引仍是聽錯的原文：下次同樣聽錯可直接命中手動快取
        _db.SaveCache(cacheKey, processed, isManual: true);
        // 歷史存模型原始輸出（未後處理）；歷史中沒有該輪則不新增
        _session.Replace(cacheKey, correctedSource, chatResult.Content);
        // 重翻成功：該句不再參與重複字幕判斷（譯文已由手動快取接手）
        RemoveRecent(cacheKey);
        if (correctedSource != cacheKey)
        {
            RemoveRecent(correctedSource);
        }

        return ReplacementRules.Apply(processed, s.ReplacementRules);
    }

    /// <summary>預熱：多輪模式下送出 system 與目前歷史，讓模型提前準備；單輪模式不做事。</summary>
    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        var s = _settings.Current;
        if (!s.Translation.UseConversationMode) return Task.CompletedTask;

        // 背景請求進行中：它本身就會讓伺服器留下檢查點，再預熱只會排隊並打亂順序
        if (_background is not null) return Task.CompletedTask;

        _session.MaxTurns = s.Translation.ConversationMaxTurns;
        _session.KeepTurns = s.Translation.ConversationKeepTurns;
        _session.IdleReset = TimeSpan.FromSeconds(s.Translation.ConversationIdleResetSeconds);

        var systemPrompt = PromptBuilder.BuildSystemPrompt(s, _glossary?.PromptSection);
        return _llmClient.WarmUpAsync(systemPrompt, _session.GetHistory(systemPrompt), cancellationToken);
    }

    /// <summary>多輪模式 user prompt：歷史每輪一行「原文 → 譯文」，以分隔線接上本次待翻句子。</summary>
    private static string BuildConversationUserPrompt(IReadOnlyList<ChatTurn> history, string current)
    {
        if (history.Count == 0)
        {
            return current;
        }

        var turns = string.Join("\n", history.Select(turn => $"{turn.User} → {turn.Assistant}"));
        return $"{turns}\n---\n{current}";
    }
}

// 供內部解析用的 DTO
public record OpenAiResponse(Choice[] choices);
public record Choice(Message message);
public record Message(string content);