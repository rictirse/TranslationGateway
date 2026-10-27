using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>VodPrefetcher：字幕推送後依播放位置批次預先翻譯寫進快取
/// （假的播放 clock、假的 LLM 回編號譯文、真的 PotPlayerSubtitlePusher 推送流程與 SQLite 暫存檔，不連網）。</summary>
// 與 TranslationManagerTests 綁同一個 collection：這裡用真的 TranslationManager，
// 送出的 TranslationJob 訊息會被平行執行的 TranslationManagerTests 收走（見 TranslationMessengerCollection）
[Collection("translation-messenger")]
public class VodPrefetcherTests
{
    private const string VideoA = "aaaaaaaaaaa";
    private const string VideoB = "bbbbbbbbbbb";

    private static readonly string[] Numerals = ["一", "二", "三", "四", "五", "六", "七", "八", "九", "十"];

    /// <summary>第 index 句的固定文字（內容都不同，成為不同的快取 key）。</summary>
    private static string TextOf(int index) => $"テスト文その{Numerals[index]}";

    /// <summary>一段 cue 一句：開始時間自訂、為時 2 秒，cue 間隔 ≥ 3 秒會各自成行。</summary>
    private static CaptionCue Cue(int index, long startMs) => new(startMs, 2000, TextOf(index));

    private static AppSettings Settings(int batchSize = 4, int lowWaterSeconds = 60, string sourceRules = "", int timeoutSeconds = 5, int urgentBatchSize = 3) => new()
    {
        SourceReplacementRules = sourceRules,
        Translation = new TranslationSettings { SystemPromptTemplate = "你是翻譯專家" },
        Vod = new VodSettings
        {
            PrefetchBatchSize = batchSize,
            PrefetchLowWaterSeconds = lowWaterSeconds,
            PrefetchTimeoutSeconds = timeoutSeconds,
            UrgentBatchSize = urgentBatchSize,
        },
    };

    /// <summary>可設定播放位置的假 clock。</summary>
    private sealed class FakeClock : IPlaybackClock
    {
        public long? Position { get; set; }

        public long? GetPositionMs() => Position;
    }

    /// <summary>假的翻譯模型：解析批次 user prompt 的每一行「編號. 原文」，逐行回「編號. 第N句譯文」；
    /// 原文含 SkipText 的行不回（模擬模型漏句），並記錄呼叫當下的優先度與每批的原文。</summary>
    private sealed class FakePrefetchLlm : IOpenAiClient
    {
        public List<IReadOnlyList<string>> Batches { get; } = [];

        public List<LlmPriority?> Priorities { get; } = [];

        public string? SkipText { get; set; }

        public int Calls { get; private set; }

        public Task<ChatResult> SendAsync(string userText, string systemText, CancellationToken cancellationToken, int? maxOutputTokens = null)
        {
            Priorities.Add(LlmPriorityScope.Current?.Priority);
            var lines = userText.Split('\n')
                .Select(line => line.Split(". ", 2))
                .Select(parts => (Number: parts[0], Text: parts.Length > 1 ? parts[1] : parts[0]))
                .ToList();
            Batches.Add(lines.Select(l => l.Text).ToList());
            // 記好這批之後才加次數：測試以 Calls 等到新的一批後會讀 Batches[^1]
            Calls++;

            var reply = string.Join("\n", lines
                .Where(l => SkipText is null || !l.Text.Contains(SkipText))
                .Select(l => $"{l.Number}. 第{l.Number}句譯文"));
            return Task.FromResult(new ChatResult(reply, 1, 1, 2, "fake"));
        }

        public Task<ChatResult> SendChatAsync(string systemText, IReadOnlyList<ChatTurn> history, string userText, CancellationToken cancellationToken, int? maxOutputTokens = null) =>
            throw new NotSupportedException("預先翻譯不使用多輪對話");

        public Task WarmUpAsync(string systemText, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>送出後卡住不回的假 LLM：由測試控制何時回（TCS）；批次 user prompt 的「編號. 原文」放行時回「編號. 譯文」。</summary>
    private sealed class ControlledLlm : IOpenAiClient
    {
        private readonly TaskCompletionSource<ChatResult> _pending =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? LastUserText { get; private set; }

        public Task<ChatResult> SendAsync(string userText, string systemText, CancellationToken cancellationToken, int? maxOutputTokens = null)
        {
            LastUserText = userText;
            return _pending.Task;
        }

        public Task<ChatResult> SendChatAsync(string systemText, IReadOnlyList<ChatTurn> history, string userText, CancellationToken cancellationToken, int? maxOutputTokens = null) =>
            throw new NotSupportedException("預先翻譯不使用多輪對話");

        public Task WarmUpAsync(string systemText, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        /// <summary>放行：依送出的批次行回譯文。</summary>
        public void CompleteReply()
        {
            var reply = string.Join("\n", (LastUserText ?? string.Empty).Split('\n')
                .Select(line => line.Split(". ", 2))
                .Where(parts => parts.Length == 2)
                .Select(parts => $"{parts[0]}. 第{parts[0]}句譯文"));
            _pending.TrySetResult(new ChatResult(reply, 1, 1, 2, "fake"));
        }
    }

    /// <summary>一直不回的假 LLM（只在取消時丟例外）：模擬預先翻譯批次逾時。</summary>
    private sealed class HangingLlm : IOpenAiClient
    {
        public int Calls { get; private set; }

        public async Task<ChatResult> SendAsync(string userText, string systemText, CancellationToken cancellationToken, int? maxOutputTokens = null)
        {
            Calls++;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new System.Diagnostics.UnreachableException();
        }

        public Task<ChatResult> SendChatAsync(string systemText, IReadOnlyList<ChatTurn> history, string userText, CancellationToken cancellationToken, int? maxOutputTokens = null) =>
            throw new NotSupportedException("預先翻譯不使用多輪對話");

        public Task WarmUpAsync(string systemText, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>批次與即時都會到、並數預熱次數的假 LLM：批次解析「編號. 原文」後逐行回「編號. 譯文」，
    /// SendAsyncHangs = true 時批次一直不回（模擬逾時），即時（SendChatAsync）直接回固定譯文。</summary>
    private sealed class WarmUpCountingLlm : IOpenAiClient
    {
        public int WarmUps { get; private set; }

        public bool SendAsyncHangs { get; set; }

        public async Task<ChatResult> SendAsync(string userText, string systemText, CancellationToken cancellationToken, int? maxOutputTokens = null)
        {
            if (SendAsyncHangs)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new System.Diagnostics.UnreachableException();
            }

            var reply = string.Join("\n", userText.Split('\n')
                .Select(line => line.Split(". ", 2))
                .Where(parts => parts.Length == 2)
                .Select(parts => $"{parts[0]}. 第{parts[0]}句譯文"));
            return new ChatResult(reply, 1, 1, 2, "fake");
        }

        public Task<ChatResult> SendChatAsync(string systemText, IReadOnlyList<ChatTurn> history, string userText, CancellationToken cancellationToken, int? maxOutputTokens = null) =>
            Task.FromResult(new ChatResult("ライブ譯文", 1, 1, 2, "fake"));

        public Task WarmUpAsync(string systemText, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken)
        {
            WarmUps++;
            return Task.CompletedTask;
        }
    }

    /// <summary>記錄呼叫次數的假 PotPlayer 字幕載入器（一律成功）。</summary>
    private sealed class FakePotPlayerLoader : IPotPlayerSubtitleLoader
    {
        public int Loads { get; private set; }

        public bool Load(string subtitlePath)
        {
            Loads++;
            return true;
        }
    }

    /// <summary>記下所有 log 訊息的假 logger（驗證等資料探勘的 log 只寫一次，以及失敗 log 不帶例外）。
    /// 預翻迴圈在背景執行緒寫入、測試端同時列舉，所以用鎖保護，讀取時回傳快照。</summary>
    private sealed class SpyLogger<T> : ILogger<T>
    {
        private readonly object _lock = new();
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get
            {
                lock (_lock)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                _entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    /// <summary>不連網的假字幕下載：回傳目前設定的 cue（每次載入前換上新影片的 cue）。</summary>
    private sealed class StubProvider : IYouTubeCaptionProvider
    {
        public IReadOnlyList<CaptionCue> Cues { get; set; } = [];

        public Task<YouTubeCaptionResult?> GetJapaneseCaptionsAsync(
            string videoId, string? visitorData, CancellationToken cancellationToken = default) =>
            Task.FromResult<YouTubeCaptionResult?>(new YouTubeCaptionResult("ja", true, "IOS", Cues));
    }

    /// <summary>包住真實 store、數每個 key 的 GetCache 呼叫次數的裝飾器（驗證同一支影片每個 key 最多查一次）。</summary>
    private sealed class CountingStore(ITranslationStore inner) : ITranslationStore
    {
        private readonly object _lock = new();

        public Dictionary<string, int> GetCacheCounts { get; } = [];

        public string Description => inner.Description;

        public string? GetCache(string source)
        {
            lock (_lock)
            {
                GetCacheCounts[source] = GetCacheCounts.GetValueOrDefault(source) + 1;
            }

            return inner.GetCache(source);
        }

        public void SaveCache(string source, string target, bool isManual = false) => inner.SaveCache(source, target, isManual);

        public void SaveCacheUnlessManual(string source, string target) => inner.SaveCacheUnlessManual(source, target);

        public bool DeleteCache(string source, bool includeManual = false) => inner.DeleteCache(source, includeManual);

        public IEnumerable<TranslationCacheItem> GetAllCache() => inner.GetAllCache();

        public long? GetCacheId(string source) => inner.GetCacheId(source);

        /// <summary>目前每個 key 的查詢次數快照（背景迴圈同時在寫）。</summary>
        public IReadOnlyDictionary<string, int> CountsSnapshot()
        {
            lock (_lock)
            {
                return new Dictionary<string, int>(GetCacheCounts);
            }
        }
    }

    /// <summary>GetCache 一律丟例外、其他方法照常委派的 store 裝飾器（驗證查詢失敗視為沒有譯文、迴圈不中斷）。</summary>
    private sealed class ThrowingGetCacheStore(ITranslationStore inner) : ITranslationStore
    {
        public string Description => inner.Description;

        public string? GetCache(string source) => throw new InvalidOperationException("資料庫不可用");

        public void SaveCache(string source, string target, bool isManual = false) => inner.SaveCache(source, target, isManual);

        public void SaveCacheUnlessManual(string source, string target) => inner.SaveCacheUnlessManual(source, target);

        public bool DeleteCache(string source, bool includeManual = false) => inner.DeleteCache(source, includeManual);

        public IEnumerable<TranslationCacheItem> GetAllCache() => inner.GetAllCache();

        public long? GetCacheId(string source) => inner.GetCacheId(source);
    }

    /// <summary>組出待測物件：真的 PotPlayerSubtitlePusher（debounce 20ms）推送、VodPrefetcher（PollInterval 20ms）、
    /// 假 clock、假 LLM、真的 TranslationManager 與 SQLite 暫存檔。wrapStore 可以把真實 store 包一層裝飾器
    /// （TranslationManager 與 VodPrefetcher 用同一個），Store 是實際使用中的 store。</summary>
    private sealed class Harness : IDisposable
    {
        public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory("vod-prefetcher-");

        public TempDatabase Db { get; } = new();

        public FakeClock Clock { get; } = new() { Position = 0 };

        public FakePrefetchLlm Llm { get; } = new();

        public SpyLogger<VodPrefetcher> Logger { get; } = new();

        public CaptionTrackState Captions { get; } = new();

        public StubProvider Provider { get; } = new();

        public CaptionLoader Loader { get; }

        public PotPlayerSubtitlePusher Pusher { get; }

        public VodPrefetcher Prefetcher { get; }

        public TranslationManager Translation { get; }

        /// <summary>實際交給 TranslationManager／VodPrefetcher 使用的 store（wrapStore 有設定時是裝飾器）。</summary>
        public ITranslationStore Store { get; }

        public Harness(AppSettings settings, CardReadinessGate? gate = null, PrefetchInFlight? inFlight = null, IOpenAiClient? llm = null,
            LlmCallRecorder? recorder = null, TimeProvider? timeProvider = null, Func<ITranslationStore, ITranslationStore>? wrapStore = null)
        {
            Loader = new CaptionLoader(Captions, Provider);
            Pusher = new PotPlayerSubtitlePusher(
                new CaptionSentenceState(), Captions, Loader, new FakePotPlayerLoader(),
                isEnabled: () => true, Dir.FullName,
                NullLogger<PotPlayerSubtitlePusher>.Instance)
            {
                Debounce = TimeSpan.FromMilliseconds(20),
            };
            var client = llm ?? (IOpenAiClient)Llm;
            ITranslationStore store = wrapStore is null ? Db.Service : wrapStore(Db.Service);
            Store = store;
            Translation = new TranslationManager(
                client, new TextProcessingService(), store, new FakeSettingsService(settings),
                timeProvider, prefetchInFlight: inFlight, recorder: recorder);
            Prefetcher = new VodPrefetcher(
                Pusher, client, Translation, new TextProcessingService(), store,
                new FakeSettingsService(settings), glossary: null, Clock, Logger, gate, inFlight, recorder, timeProvider)
            {
                PollInterval = TimeSpan.FromMilliseconds(20),
            };
        }

        /// <summary>載入影片（PotPlayer 正在播放這支影片）：字幕下載完成後觸發推送，推送成功再觸發 Pushed。</summary>
        public Task LoadAsync(string videoId, IReadOnlyList<CaptionCue> cues)
        {
            Provider.Cues = cues;
            return Loader.LoadAsync(videoId, null);
        }

        public void Dispose()
        {
            Prefetcher.Dispose();
            Pusher.Dispose();
            Db.Dispose();
            try { Dir.Delete(recursive: true); } catch (IOException) { /* 寫入中的檔案留給系統清 */ }
        }
    }

    /// <summary>輪詢直到條件成立（逾時 5 秒視為失敗）。</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等候條件逾時");
            await Task.Delay(10);
        }
    }

    /// <summary>輪詢度過指定的時間：供「沒有再送」這類反面條件等到超過數個輪詢週期再斷言。</summary>
    private static async Task PassAsync(TimeSpan time)
    {
        var deadline = DateTime.UtcNow + time;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    // 1. 位置 0、沒有庫存：第一批句數是 min(每批句數, 5)（庫存 0 時先送少量，約 1 秒內拿到第一段庫存），譯文寫進 store。
    [Fact]
    public async Task Pushed_AtPositionZero_FirstBatchIsCappedAtFiveAndSaved()
    {
        using var h = new Harness(Settings(batchSize: 4));
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA, [Cue(0, 0), Cue(1, 5000), Cue(2, 10000), Cue(3, 15000), Cue(4, 20000), Cue(5, 25000)]);
        await UntilAsync(() => h.Llm.Calls >= 1);

        Assert.Equal([TextOf(0), TextOf(1), TextOf(2), TextOf(3)], h.Llm.Batches[0]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(0)) != null && h.Db.Service.GetCache(TextOf(1)) != null);
        Assert.Equal("第1句譯文", h.Db.Service.GetCache(TextOf(0)));
        Assert.Equal("第2句譯文", h.Db.Service.GetCache(TextOf(1)));
    }

    // 1b. 庫存 0、PrefetchBatchSize = 20：第一批只送 5 句。
    [Fact]
    public async Task Pushed_AtPositionZero_BatchSizeTwenty_FirstBatchSendsFive_Temp()
    {
        using var h = new Harness(Settings(batchSize: 20));
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA, [Cue(0, 0), Cue(1, 5000), Cue(2, 10000), Cue(3, 15000), Cue(4, 20000), Cue(5, 25000)]);
        await UntilAsync(() => h.Llm.Calls >= 1);

        Assert.Equal([TextOf(0), TextOf(1), TextOf(2), TextOf(3), TextOf(4)], h.Llm.Batches[0]);
    }

    // 2. 庫存低於門檻時補下一批（完整句數）；庫存足夠時不送；全部翻好後不再送。
    [Fact]
    public async Task Poll_BelowLowWater_RefillsFullBatch_SufficientStock_DoesNotSend()
    {
        using var h = new Harness(Settings(batchSize: 4, lowWaterSeconds: 60));
        h.Clock.Position = 0;

        // 前兩句在 0s/5s，後四句在 70s 之後：第一批（min(4,5) = 4 句，含 70s 之後的行）完成後
        // 下一個未翻好行在 80s，庫存 80 秒 ≥ 60，不送
        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 70000), Cue(3, 75000), Cue(4, 80000), Cue(5, 85000)]);
        await UntilAsync(() => h.Llm.Calls >= 1 && h.Db.Service.GetCache(TextOf(3)) != null);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(1, h.Llm.Calls);

        // 快轉到 65 秒：庫存只剩 15 秒 < 60 → 補一批完整的（前面 4 句已翻好，只剩 2 句）
        h.Clock.Position = 65000;
        await UntilAsync(() => h.Llm.Calls >= 2);
        Assert.Equal(
            [TextOf(4), TextOf(5)],
            h.Llm.Batches[1]);

        // 全部翻好：不再送
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(5)) != null);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(2, h.Llm.Calls);
    }

    // 3. 假 LLM 少回一個編號：該句不算翻好；連續失敗 2 次後不再送。
    [Fact]
    public async Task Poll_MissingNumber_CountsAsFailure_StopsRetryingAfterTwo()
    {
        using var h = new Harness(Settings(batchSize: 4));
        h.Clock.Position = 0;
        h.Llm.SkipText = TextOf(1);

        await h.LoadAsync(VideoA, [Cue(0, 0), Cue(1, 5000)]);

        // 兩批都漏第二句：第一批含全部行數、第二批只剩第二句（同 key 重試一次）
        await UntilAsync(() => h.Llm.Calls >= 2);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(0)) != null);

        // 連續失敗 2 次後視為翻好，不再重試；第二句始終沒寫進 store
        await PassAsync(TimeSpan.FromMilliseconds(200));
        Assert.Equal(2, h.Llm.Calls);
        Assert.Null(h.Db.Service.GetCache(TextOf(1)));
    }

    // 4. 換影片時清空狀態（記憶體已翻好集合與資料庫查詢記錄）：新規則下資料庫快取也算已翻好（重看不重翻），
    //    所以先刪掉這兩句的快取，同樣的文字才會從新影片的位置重新翻一遍。
    [Fact]
    public async Task Pushed_AnotherVideo_ClearsStateAndStartsOver()
    {
        using var h = new Harness(Settings(batchSize: 4));
        h.Clock.Position = 0;
        var cues = new[] { Cue(0, 0), Cue(1, 5000) };

        await h.LoadAsync(VideoA, cues);
        await UntilAsync(() => h.Llm.Calls >= 1 && h.Db.Service.GetCache(TextOf(1)) != null);

        // 資料庫快取已有譯文時重看不重翻：刪掉快取才能觀察到換影片真的清空了記憶體狀態
        h.Db.Service.DeleteCache(TextOf(0), includeManual: true);
        h.Db.Service.DeleteCache(TextOf(1), includeManual: true);

        // 換成另一支影片（文字相同）：已翻好集合被清空 → 重新翻譯
        await h.LoadAsync(VideoB, cues);
        await UntilAsync(() => h.Llm.Calls >= 2);
        Assert.Equal([TextOf(0), TextOf(1)], h.Llm.Batches[1]);
    }

    // 5. 原文修正規則：store 收到的 key 是 NormalizeSource 之後的文字。
    [Fact]
    public async Task Poll_WithSourceRules_SavesNormalizedKey()
    {
        using var h = new Harness(Settings(sourceRules: "きく=聞く"));
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA, [new CaptionCue(0, 2000, "これはきくのテスト"), Cue(1, 5000)]);
        await UntilAsync(() => h.Db.Service.GetCache("これは聞くのテスト") != null);

        Assert.Null(h.Db.Service.GetCache("これはきくのテスト"));
    }

    // 6. 送出時的 LlmPriorityScope.Current.Priority 是 Prefetch。
    [Fact]
    public async Task Poll_SendsWithPrefetchPriority()
    {
        using var h = new Harness(Settings(batchSize: 4));
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA, [Cue(0, 0), Cue(1, 5000)]);
        await UntilAsync(() => h.Llm.Calls >= 1);

        Assert.All(h.Llm.Priorities, priority => Assert.Equal(LlmPriority.Prefetch, priority));
    }

    // 7. 詞彙卡準備門等待中（門沒開）：不送模型；「等資料探勘完成才開始」的 log 同一段等待只寫一次；
    //    門打開之後照常開始送。
    [Fact]
    public async Task Poll_GatePending_DoesNotSend_LogsOnce_CompleteThenSends()
    {
        var gate = new CardReadinessGate();
        gate.Begin();
        using var h = new Harness(Settings(batchSize: 4), gate);
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA, [Cue(0, 0), Cue(1, 5000)]);
        await PassAsync(TimeSpan.FromMilliseconds(200));

        Assert.Equal(0, h.Llm.Calls);
        Assert.Null(h.Db.Service.GetCache(TextOf(0)));
        Assert.Equal(1, h.Logger.Entries.Count(e => e.Message == "預先翻譯：等資料探勘完成才開始"));

        gate.Complete();
        await UntilAsync(() => h.Llm.Calls >= 1);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(1)) != null);
        // 開始送之後不再寫等待 log
        Assert.Equal(1, h.Logger.Entries.Count(e => e.Message == "預先翻譯：等資料探勘完成才開始"));
    }

    // 8. 批次送出期間，PrefetchInFlight.WaitTaskFor(批次內的 key) 不是 null（批次外的 key 是 null）；
    //    批次完成後 Task 完成，這時快取已經有譯文。
    [Fact]
    public async Task Poll_BatchInFlight_WaitsForKey_CompletesAfterCacheWritten_Temp()
    {
        var inFlight = new PrefetchInFlight();
        var llm = new ControlledLlm();
        using var h = new Harness(Settings(batchSize: 4), inFlight: inFlight, llm: llm);
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA, [Cue(0, 0), Cue(1, 5000)]);
        await UntilAsync(() => llm.LastUserText is not null);

        var pending = inFlight.WaitTaskFor(TextOf(0));
        Assert.NotNull(pending);
        Assert.Null(inFlight.WaitTaskFor("不在批次裡的句子"));

        llm.CompleteReply();
        await pending!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await UntilAsync(() => h.Db.Service.GetCache(TextOf(0)) != null);
        Assert.Equal("第1句譯文", h.Db.Service.GetCache(TextOf(0)));
    }

    // 9. 逾時（假 LLM 一直不回）時 log 是「預先翻譯批次逾時」，Warning 沒有帶 exception；
    //    失敗也會結束批次，等待中的即時翻譯被放行。
    [Fact]
    public async Task Poll_LlmNeverReplies_TimesOutWithWarningWithoutException_Temp()
    {
        var inFlight = new PrefetchInFlight();
        var llm = new HangingLlm();
        using var h = new Harness(Settings(batchSize: 4, timeoutSeconds: 1), inFlight: inFlight, llm: llm);
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA, [Cue(0, 0), Cue(1, 5000)]);
        await UntilAsync(() => llm.Calls >= 1);
        var pending = inFlight.WaitTaskFor(TextOf(0));
        Assert.NotNull(pending);

        await UntilAsync(() => h.Logger.Entries.Any(e => e.Message.StartsWith("預先翻譯批次逾時", StringComparison.Ordinal)));

        var warning = h.Logger.Entries.Single(e =>
            e.Level == LogLevel.Warning && e.Message.StartsWith("預先翻譯批次逾時", StringComparison.Ordinal));
        Assert.Null(warning.Exception);
        Assert.Null(h.Db.Service.GetCache(TextOf(0)));

        // 失敗的 catch 結束後批次也被 Dispose，等待者被放行
        await pending!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // 10. 批次完成後記一筆 VodBatch 的 llm_calls（完整紀錄開啟：UserPrompt 是整批的編號原文）。
    [Fact]
    public async Task Poll_BatchCompleted_RecordsVodBatchLlmCall()
    {
        var auditStore = new SpyAuditStore();
        var settings = Settings(batchSize: 4);
        settings.Audit.FullRecordEnabled = true;
        var recorder = new LlmCallRecorder(
            auditStore, new FakeSettingsService(settings), registry: null, new StreamSession(),
            NullLogger<LlmCallRecorder>.Instance);
        using var h = new Harness(settings, recorder: recorder);
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA, [Cue(0, 0), Cue(1, 5000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(1)) != null);
        await recorder.FlushAsync();

        var call = Assert.Single(auditStore.Calls);
        Assert.Equal(LlmCallPurposes.VodBatch, call.Purpose);
        Assert.Null(call.JobId);
        Assert.Equal(LlmCallOutcomes.Ok, call.Outcome);
        Assert.Equal("fake", call.Model);
        Assert.Contains("1. " + TextOf(0), call.UserPrompt);
        Assert.Contains("2. " + TextOf(1), call.UserPrompt);
    }

    // 11. 急件（RequestUrgent）：即使庫存還夠（一般流程不動作），也馬上從那一行開始送一批；
    //     批次結束（快取寫好）時要求方的 Task 完成。
    [Fact]
    public async Task Poll_UrgentKey_SendsBatchDespiteSufficientStock_StartingFromThatLine()
    {
        var inFlight = new PrefetchInFlight();
        using var h = new Harness(Settings(batchSize: 4, lowWaterSeconds: 60), inFlight: inFlight);
        h.Clock.Position = 0;

        // 第一批把 0～3 句翻好後，下一個未翻好行在 80 秒：庫存 80 ≥ 60，一般流程不再送
        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 70000), Cue(3, 75000), Cue(4, 80000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(3)) != null);
        await UntilAsync(() => inFlight.IsActive);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        var calls = h.Llm.Calls;

        // 急件：第五句的庫存仍有 80 秒，但這句是現在要顯示的字幕，馬上從那一行翻一批
        var urgent = inFlight.RequestUrgent(TextOf(4));
        await UntilAsync(() => h.Llm.Calls >= calls + 1);
        Assert.Equal([TextOf(4)], h.Llm.Batches[^1]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(4)) != null);

        await urgent.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // 11a. 播放位置拿不到時仍會取走急件、從那一行送一批（不能留著急件讓 WaitForWorkAsync 立刻返回而空轉）。
    [Fact]
    public async Task Poll_UrgentKeyWithoutPosition_StillSendsBatchFromThatLine()
    {
        var inFlight = new PrefetchInFlight();
        using var h = new Harness(Settings(batchSize: 4, lowWaterSeconds: 60), inFlight: inFlight);
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 70000), Cue(3, 75000), Cue(4, 80000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(3)) != null);
        await UntilAsync(() => inFlight.IsActive);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        var calls = h.Llm.Calls;

        h.Clock.Position = null;
        var urgent = inFlight.RequestUrgent(TextOf(4));
        await UntilAsync(() => h.Llm.Calls >= calls + 1);
        Assert.Equal([TextOf(4)], h.Llm.Batches[^1]);

        await urgent.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // 11b. 急件的 key 已翻好時：忽略這個要求，不另外再送一批。
    [Fact]
    public async Task Poll_UrgentKeyAlreadyDone_IgnoresRequest_NoExtraBatch()
    {
        var inFlight = new PrefetchInFlight();
        using var h = new Harness(Settings(batchSize: 4), inFlight: inFlight);
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA, [Cue(0, 0), Cue(1, 5000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(1)) != null);
        await UntilAsync(() => inFlight.IsActive);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        var calls = h.Llm.Calls;

        _ = inFlight.RequestUrgent(TextOf(0));
        await PassAsync(TimeSpan.FromMilliseconds(150));

        Assert.Equal(calls, h.Llm.Calls);
    }

    // 11c. 急件批次只送 UrgentBatchSize 句：庫存 > 0（一般流程不會補翻）的急件，
    //      不照一般規則湊滿 PrefetchBatchSize（有人在等結果，20 句約 3.5 秒會超過即時翻譯逾時 1500 ms）。
    [Fact]
    public async Task Urgent_WithStock_SendsUrgentBatchSize()
    {
        var inFlight = new PrefetchInFlight();
        using var h = new Harness(Settings(batchSize: 20, lowWaterSeconds: 60, urgentBatchSize: 3), inFlight: inFlight);
        h.Clock.Position = 0;

        // 第一批（冷啟動 5 句）翻好 0～4 句後，下一個未翻好行在 100 秒：庫存 100 ≥ 60，一般流程不再送
        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 10000), Cue(3, 15000), Cue(4, 20000),
             Cue(5, 100000), Cue(6, 105000), Cue(7, 110000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(4)) != null);
        await UntilAsync(() => inFlight.IsActive);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        var calls = h.Llm.Calls;

        // 急件：第六句的庫存仍有 100 秒，這批只送 3 句（UrgentBatchSize），不湊滿 20 句
        var urgent = inFlight.RequestUrgent(TextOf(5));
        await UntilAsync(() => h.Llm.Calls >= calls + 1);
        Assert.Equal([TextOf(5), TextOf(6), TextOf(7)], h.Llm.Batches[^1]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(5)) != null);

        await urgent.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // 11d. UrgentBatchSize > PrefetchBatchSize 時：急件批次受每批句數上限（batchSize）限制。
    [Fact]
    public async Task Urgent_UrgentBatchSizeLargerThanBatchSize_CappedByBatchSize()
    {
        var inFlight = new PrefetchInFlight();
        using var h = new Harness(Settings(batchSize: 2, lowWaterSeconds: 60, urgentBatchSize: 20), inFlight: inFlight);
        h.Clock.Position = 0;

        // 第一批（冷啟動）翻好 0～1 句後，下一個未翻好行在 100 秒：庫存 100 ≥ 60，一般流程不再送
        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 100000), Cue(3, 105000), Cue(4, 110000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(1)) != null);
        await UntilAsync(() => inFlight.IsActive);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        var calls = h.Llm.Calls;

        // 急件：這批只送 2 句（受 PrefetchBatchSize = 2 上限），不送到 20 句
        var urgent = inFlight.RequestUrgent(TextOf(2));
        await UntilAsync(() => h.Llm.Calls >= calls + 1);
        Assert.Equal([TextOf(2), TextOf(3)], h.Llm.Batches[^1]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(2)) != null);

        await urgent.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // 11e. UrgentBatchSize = 0（0 以下視為 1）：急件批次只送 1 句。
    [Fact]
    public async Task Urgent_UrgentBatchSizeZero_TreatedAsOne()
    {
        var inFlight = new PrefetchInFlight();
        using var h = new Harness(Settings(batchSize: 4, lowWaterSeconds: 60, urgentBatchSize: 0), inFlight: inFlight);
        h.Clock.Position = 0;

        // 第一批（冷啟動 4 句）翻好 0～3 句後，下一個未翻好行在 100 秒：庫存 100 ≥ 60，一般流程不再送
        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 10000), Cue(3, 15000), Cue(4, 100000), Cue(5, 105000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(3)) != null);
        await UntilAsync(() => inFlight.IsActive);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        var calls = h.Llm.Calls;

        // 急件：0 視為 1，這批只送第五句
        var urgent = inFlight.RequestUrgent(TextOf(4));
        await UntilAsync(() => h.Llm.Calls >= calls + 1);
        Assert.Equal([TextOf(4)], h.Llm.Batches[^1]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(4)) != null);

        await urgent.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // 11f. 一般補庫存批次（非急件）不受 UrgentBatchSize 影響：庫存不足時照樣送整批（PrefetchBatchSize 句）。
    [Fact]
    public async Task NonUrgent_WithStock_StillSendsFullBatch()
    {
        var inFlight = new PrefetchInFlight();
        using var h = new Harness(Settings(batchSize: 2, lowWaterSeconds: 60, urgentBatchSize: 1), inFlight: inFlight);
        h.Clock.Position = 0;

        // 第一批（冷啟動 2 句）翻好 0～1 句後，下一個未翻好行在 70 秒：庫存 70 ≥ 60，不補翻
        await h.LoadAsync(VideoA, [Cue(0, 0), Cue(1, 5000), Cue(2, 70000), Cue(3, 75000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(1)) != null);
        await UntilAsync(() => inFlight.IsActive);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(1, h.Llm.Calls);

        // 快轉到 65 秒：庫存 5 秒 < 60 → 補一整批 2 句（沒有被 UrgentBatchSize = 1 壓縮）
        h.Clock.Position = 65000;
        await UntilAsync(() => h.Llm.Calls >= 2);
        Assert.Equal([TextOf(2), TextOf(3)], h.Llm.Batches[1]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(3)) != null);
    }

    // 12. 批次成功而且即時翻譯最近（60 秒內）有送模型：背景預熱即時對話（WarmUpAsync 次數 +1）。
    [Fact]
    public async Task Poll_SuccessfulBatchWithRecentLiveCall_WarmsUpLiveConversation()
    {
        var time = new ManualTimeProvider();
        var llm = new WarmUpCountingLlm();
        using var h = new Harness(Settings(batchSize: 4), llm: llm, timeProvider: time);
        h.Clock.Position = 0;

        await h.Translation.TranslateAsync(new PotplayerRequest { Current = "ライブ" }, TestContext.Current.CancellationToken);
        Assert.NotNull(h.Translation.LastLiveModelCallAt);

        await h.LoadAsync(VideoA, [Cue(0, 0)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(0)) != null);

        await UntilAsync(() => llm.WarmUps >= 1);
    }

    // 12b. 即時翻譯上次送模型已超過 60 秒：批次成功也不預熱。
    [Fact]
    public async Task Poll_SuccessfulBatchWithStaleLiveCall_DoesNotWarmUp()
    {
        var time = new ManualTimeProvider();
        var llm = new WarmUpCountingLlm();
        using var h = new Harness(Settings(batchSize: 4), llm: llm, timeProvider: time);
        h.Clock.Position = 0;

        await h.Translation.TranslateAsync(new PotplayerRequest { Current = "ライブ" }, TestContext.Current.CancellationToken);
        time.UtcNow = time.UtcNow.AddSeconds(61);

        await h.LoadAsync(VideoA, [Cue(0, 0)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(0)) != null);
        await PassAsync(TimeSpan.FromMilliseconds(150));

        Assert.Equal(0, llm.WarmUps);
    }

    // 12c. 即時翻譯從來沒送過模型（LastLiveModelCallAt 為 null）：批次成功也不預熱。
    [Fact]
    public async Task Poll_SuccessfulBatchWithoutLiveCall_DoesNotWarmUp()
    {
        var llm = new WarmUpCountingLlm();
        using var h = new Harness(Settings(batchSize: 4), llm: llm);
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA, [Cue(0, 0)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(0)) != null);
        await PassAsync(TimeSpan.FromMilliseconds(150));

        Assert.Null(h.Translation.LastLiveModelCallAt);
        Assert.Equal(0, llm.WarmUps);
    }

    // 12d. 批次全部失敗（模型一直不回、逾時）：即使即時翻譯最近有送模型也不預熱。
    [Fact]
    public async Task Poll_FailedBatch_DoesNotWarmUp()
    {
        var time = new ManualTimeProvider();
        var llm = new WarmUpCountingLlm { SendAsyncHangs = true };
        using var h = new Harness(Settings(batchSize: 4, timeoutSeconds: 1), llm: llm, timeProvider: time);
        h.Clock.Position = 0;

        await h.Translation.TranslateAsync(new PotplayerRequest { Current = "ライブ" }, TestContext.Current.CancellationToken);
        await h.LoadAsync(VideoA, [Cue(0, 0)]);

        // 第一批逾時（不會有快取寫入）之後仍不預熱
        await UntilAsync(() => h.Logger.Entries.Any(e => e.Message.StartsWith("預先翻譯批次逾時", StringComparison.Ordinal)));
        await PassAsync(TimeSpan.FromMilliseconds(150));

        Assert.Equal(0, llm.WarmUps);
    }

    // 13. PotPlayer 提前請求：位置 0 時請求 110 秒的行 → 實際門檻 = max(60, 110+30) = 140；
    //     庫存 100 秒雖然 ≥ 設定 60，仍補翻一批；調整 log 只寫一次（不每輪都寫）。
    [Fact]
    public async Task Poll_EarlyRequest_RaisesLowWater_RefillsDespiteStockAboveSetting()
    {
        var time = new ManualTimeProvider();
        var inFlight = new PrefetchInFlight(time);
        using var h = new Harness(Settings(batchSize: 4, lowWaterSeconds: 60), inFlight: inFlight, timeProvider: time);
        h.Clock.Position = 0;

        // 第一批翻 0～75 秒的四句後，下一個未翻好行在 100 秒：庫存 100 ≥ 60，不補翻
        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 70000), Cue(3, 75000), Cue(4, 100000), Cue(5, 110000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(3)) != null);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(1, h.Llm.Calls);

        // PotPlayer 在位置 0 提前請求 110 秒的那行：門檻調為 140，庫存 100 < 140 → 補翻一批
        inFlight.NoteRequested(TextOf(5));

        await UntilAsync(() => h.Llm.Calls >= 2);
        Assert.Equal([TextOf(4), TextOf(5)], h.Llm.Batches[1]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(5)) != null);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(1, h.Logger.Entries.Count(e =>
            e.Message == "預先翻譯：PotPlayer 提前 110 秒請求，庫存門檻調為 140 秒"));
    }

    // 14. 觀察值超過 10 分鐘失效：門檻回到設定值 60，庫存 120 秒 ≥ 60 不再補翻（未失效時 120 < 140 會補翻）。
    [Fact]
    public async Task Poll_ObservedLeadExpiredAfterTenMinutes_ThresholdFallsBackToSetting()
    {
        var time = new ManualTimeProvider();
        var inFlight = new PrefetchInFlight(time);
        using var h = new Harness(Settings(batchSize: 2, lowWaterSeconds: 60), inFlight: inFlight, timeProvider: time);
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 70000), Cue(3, 75000), Cue(4, 100000), Cue(5, 110000),
             Cue(6, 250000), Cue(7, 255000)]);
        await UntilAsync(() => inFlight.IsActive);

        // 第一批（兩句）後庫存 70 ≥ 60 不補；提前請求 110 秒的行 → 門檻 140 → 補到庫存 250 ≥ 140 為止（共 3 批）
        inFlight.NoteRequested(TextOf(5));
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(5)) != null);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(3, h.Llm.Calls);

        // 觀察值過期（虛擬時間 +11 分鐘）：位置 130 秒 → 庫存 250−130 = 120 ≥ 60 → 不補翻
        time.UtcNow = time.UtcNow.AddMinutes(11);
        h.Clock.Position = 130000;
        await PassAsync(TimeSpan.FromMilliseconds(150));

        Assert.Equal(3, h.Llm.Calls);
    }

    // 15. 觀察到 lead 900 秒：門檻上限 600（log 顯示 600），庫存 590 < 600 → 補翻一批。
    [Fact]
    public async Task Poll_ObservedLeadNineHundred_CapsThresholdAtSixHundred()
    {
        var time = new ManualTimeProvider();
        var inFlight = new PrefetchInFlight(time);
        using var h = new Harness(Settings(batchSize: 4, lowWaterSeconds: 60), inFlight: inFlight, timeProvider: time);
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 10000), Cue(3, 15000), Cue(4, 590000), Cue(5, 900000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(3)) != null);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(1, h.Llm.Calls);

        inFlight.NoteRequested(TextOf(5));

        await UntilAsync(() => h.Llm.Calls >= 2);
        Assert.Equal([TextOf(4), TextOf(5)], h.Llm.Batches[1]);
        Assert.Contains(h.Logger.Entries,
            e => e.Message == "預先翻譯：PotPlayer 提前 900 秒請求，庫存門檻調為 600 秒");
    }

    // 16. lead ≤ 0（已在播放位置或播過）或找不到行：不影響門檻（不寫調整 log、不補翻）。
    [Fact]
    public async Task Poll_RequestedLineAlreadyPlayedOrNotFound_DoesNotChangeThreshold()
    {
        var time = new ManualTimeProvider();
        var inFlight = new PrefetchInFlight(time);
        using var h = new Harness(Settings(batchSize: 2, lowWaterSeconds: 60), inFlight: inFlight, timeProvider: time);
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 70000), Cue(3, 75000), Cue(4, 100000), Cue(5, 110000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(1)) != null);
        await UntilAsync(() => inFlight.IsActive);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(1, h.Llm.Calls);

        // 位置 0：請求 0 秒的行（lead = 0）與不存在的行都忽略，門檻維持 60（庫存 70 ≥ 60 不補翻）
        inFlight.NoteRequested(TextOf(0));
        inFlight.NoteRequested("沒有這一句字幕");
        await PassAsync(TimeSpan.FromMilliseconds(150));

        Assert.Equal(1, h.Llm.Calls);
        Assert.DoesNotContain(h.Logger.Entries, e => e.Message.Contains("庫存門檻調為", StringComparison.Ordinal));
    }

    // 17. 資料庫快取已有全部譯文（重看整支翻過的影片）：所有行都算已翻好，多輪輪詢完全不送模型。
    [Fact]
    public async Task AllLinesCachedInStore_SendsNoBatch()
    {
        using var h = new Harness(Settings(batchSize: 4));
        h.Clock.Position = 0;
        for (var i = 0; i <= 5; i++)
        {
            h.Db.Service.SaveCache(TextOf(i), $"既存譯文{i}");
        }

        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 10000), Cue(3, 15000), Cue(4, 20000), Cue(5, 25000)]);
        // 跑多輪（每 20ms 一輪）：查到快取的行直接進已翻好集合，全部翻好 → 不送任何批次
        await PassAsync(TimeSpan.FromMilliseconds(300));

        Assert.Equal(0, h.Llm.Calls);
    }

    // 18. 部分行在資料庫已有譯文：批次從第一個沒有快取的行開始，而且取「連續」的行
    //     （夾在中間已有快取的行照樣一起送，模型才看得到完整前後文）。
    [Fact]
    public async Task PartiallyCached_BatchStartsAtFirstUncachedAndIsContiguous()
    {
        using var h = new Harness(Settings(batchSize: 4));
        h.Clock.Position = 0;

        // 第 0～4 行已有快取，第 7、8 行也有：第一個未翻好是第 5 行
        for (var i = 0; i <= 4; i++)
        {
            h.Db.Service.SaveCache(TextOf(i), $"既存譯文{i}");
        }

        h.Db.Service.SaveCache(TextOf(7), "既存譯文7");
        h.Db.Service.SaveCache(TextOf(8), "既存譯文8");

        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 10000), Cue(3, 15000), Cue(4, 20000),
             Cue(5, 25000), Cue(6, 30000), Cue(7, 35000), Cue(8, 40000)]);
        await UntilAsync(() => h.Llm.Calls >= 1);

        // 批次從第 5 行開始、連續取到第 8 行（含已有快取的第 7、8 行），順序照字幕順序
        Assert.Equal([TextOf(5), TextOf(6), TextOf(7), TextOf(8)], h.Llm.Batches[0]);

        // 批內已翻過的行照樣重翻：非手動修正的快取被批次譯文覆蓋（第 7 行是批次裡的第 3 句）
        // 批次譯文是逐句寫入快取（5→6→7→8）：等最後一句寫好再檢查，只等第 5 行會在第 7 行寫入前就檢查（Windows 上實際發生）
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(8)) == "第4句譯文");
        Assert.Equal("第1句譯文", h.Db.Service.GetCache(TextOf(5)));
        Assert.Equal("第3句譯文", h.Db.Service.GetCache(TextOf(7)));
    }

    // 19. 本次播放已翻好的行夾在批次中間：下一批仍包含它們（取連續句子保留上下文，不因已翻好而跳過；
    //     舊行為會跳過已翻好的行，送出去的句子前後不連貫）。
    [Fact]
    public async Task DoneInSession_StillIncludedInBatchForContext()
    {
        var inFlight = new PrefetchInFlight();
        using var h = new Harness(Settings(batchSize: 4, lowWaterSeconds: 60, urgentBatchSize: 1), inFlight: inFlight);
        h.Clock.Position = 0;

        // 第一批（冷啟動 4 句）翻好 0～3 行；急件（UrgentBatchSize = 1）只翻第 6 行 → 它是本次播放已翻好的行
        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 10000), Cue(3, 15000),
             Cue(4, 70000), Cue(5, 75000), Cue(6, 80000), Cue(7, 85000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(3)) != null);
        await UntilAsync(() => inFlight.IsActive);
        await PassAsync(TimeSpan.FromMilliseconds(150));

        var urgent = inFlight.RequestUrgent(TextOf(6));
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(6)) != null);
        await urgent.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        var calls = h.Llm.Calls;

        // 快轉到 65 秒：庫存 5 秒 < 60 → 從第一個未翻好的第 4 行取連續 4 句；已翻好的第 6 行仍在批次裡
        h.Clock.Position = 65000;
        await UntilAsync(() => h.Llm.Calls >= calls + 1);
        Assert.Equal([TextOf(4), TextOf(5), TextOf(6), TextOf(7)], h.Llm.Batches[^1]);
    }

    // 20. 資料庫查詢節流：同一個 key 在同一支影片最多查一次 GetCache（查過沒有就記住，不隨每秒輪詢重查）；
    //     換影片後記錄清掉、重新計算（查到的行直接進已翻好集合，不送模型）。
    [Fact]
    public async Task CacheLookup_AtMostOncePerKeyPerVideo()
    {
        using var h = new Harness(Settings(batchSize: 4, lowWaterSeconds: 60),
            wrapStore: inner => new CountingStore(inner));
        var store = Assert.IsType<CountingStore>(h.Store);
        h.Clock.Position = 0;
        var cues = new[] { Cue(0, 0), Cue(1, 5000), Cue(2, 10000), Cue(3, 15000), Cue(4, 100000) };

        await h.LoadAsync(VideoA, cues);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(3)) != null);
        // 多跑幾輪：第 4 行（100 秒）沒有快取，每輪掃描都會遇到它，但查過一次就記住、不再查
        await PassAsync(TimeSpan.FromMilliseconds(300));

        var afterA = store.CountsSnapshot();
        Assert.Equal(1, afterA.GetValueOrDefault(TextOf(0)));
        Assert.Equal(1, afterA.GetValueOrDefault(TextOf(4)));
        Assert.All(afterA, pair => Assert.True(pair.Value <= 1, $"{pair.Key} 查了 {pair.Value} 次"));
        var callsA = h.Llm.Calls;

        // 換成同一批字幕的影片 B：查詢記錄清掉重新計算；第 0～3 行資料庫已有譯文 → 查到就進已翻好、不再送模型
        await h.LoadAsync(VideoB, cues);
        await PassAsync(TimeSpan.FromMilliseconds(300));

        var afterB = store.CountsSnapshot();
        Assert.All(afterB, pair =>
            Assert.InRange(pair.Value - afterA.GetValueOrDefault(pair.Key), 0, 1));
        Assert.Equal(1, afterB.GetValueOrDefault(TextOf(0)) - afterA.GetValueOrDefault(TextOf(0)));
        Assert.Equal(1, afterB.GetValueOrDefault(TextOf(4)) - afterA.GetValueOrDefault(TextOf(4)));
        Assert.Equal(callsA, h.Llm.Calls);
    }

    // 21. 急件的 key 在資料庫已有譯文（本次播放還沒翻過）：視同已翻好，不送急件批次。
    [Fact]
    public async Task Urgent_CachedKey_Ignored()
    {
        var inFlight = new PrefetchInFlight();
        using var h = new Harness(Settings(batchSize: 4, lowWaterSeconds: 60), inFlight: inFlight);
        h.Clock.Position = 0;

        // 第 4 行（80 秒，庫存足夠、一般流程不會翻到它）在資料庫已有譯文
        h.Db.Service.SaveCache(TextOf(4), "既存譯文4");
        await h.LoadAsync(VideoA,
            [Cue(0, 0), Cue(1, 5000), Cue(2, 10000), Cue(3, 15000), Cue(4, 80000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(3)) != null);
        await UntilAsync(() => inFlight.IsActive);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        var calls = h.Llm.Calls;

        _ = inFlight.RequestUrgent(TextOf(4));
        await PassAsync(TimeSpan.FromMilliseconds(150));

        Assert.Equal(calls, h.Llm.Calls);
    }

    // 22. 查資料庫快取丟例外：記 Debug log、視為沒有譯文，輪詢迴圈照常繼續（照樣組批次翻譯、寫快取）。
    [Fact]
    public async Task StoreThrows_TreatedAsNotCached_LoopContinues()
    {
        using var h = new Harness(Settings(batchSize: 4),
            wrapStore: inner => new ThrowingGetCacheStore(inner));
        h.Clock.Position = 0;

        await h.LoadAsync(VideoA, [Cue(0, 0), Cue(1, 5000)]);
        await UntilAsync(() => h.Db.Service.GetCache(TextOf(1)) != null);
        await PassAsync(TimeSpan.FromMilliseconds(150));

        // 例外沒有中斷迴圈：批次照常送出、快取照常寫入；查詢失敗只記 Debug log
        Assert.Equal(1, h.Llm.Calls);
        Assert.Contains(h.Logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("查翻譯快取失敗", StringComparison.Ordinal));
    }
}
