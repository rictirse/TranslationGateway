using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>VodPrefetcher：字幕推送後依播放位置批次預先翻譯寫進快取
/// （假的播放 clock、假的 LLM 回編號譯文、真的 PotPlayerSubtitlePusher 推送流程與 SQLite 暫存檔，不連網）。</summary>
public class VodPrefetcherTests
{
    private const string VideoA = "aaaaaaaaaaa";
    private const string VideoB = "bbbbbbbbbbb";

    private static readonly string[] Numerals = ["一", "二", "三", "四", "五", "六"];

    /// <summary>第 index 句的固定文字（內容都不同，成為不同的快取 key）。</summary>
    private static string TextOf(int index) => $"テスト文その{Numerals[index]}";

    /// <summary>一段 cue 一句：開始時間自訂、為時 2 秒，cue 間隔 ≥ 3 秒會各自成行。</summary>
    private static CaptionCue Cue(int index, long startMs) => new(startMs, 2000, TextOf(index));

    private static AppSettings Settings(int batchSize = 4, int lowWaterSeconds = 60, string sourceRules = "", int timeoutSeconds = 5) => new()
    {
        SourceReplacementRules = sourceRules,
        Translation = new TranslationSettings { SystemPromptTemplate = "你是翻譯專家" },
        Vod = new VodSettings
        {
            PrefetchBatchSize = batchSize,
            PrefetchLowWaterSeconds = lowWaterSeconds,
            PrefetchTimeoutSeconds = timeoutSeconds,
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
            Calls++;
            Priorities.Add(LlmPriorityScope.Current?.Priority);
            var lines = userText.Split('\n')
                .Select(line => line.Split(". ", 2))
                .Select(parts => (Number: parts[0], Text: parts.Length > 1 ? parts[1] : parts[0]))
                .ToList();
            Batches.Add(lines.Select(l => l.Text).ToList());

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

    /// <summary>記下所有 log 訊息的假 logger（驗證等資料探勘的 log 只寫一次，以及失敗 log 不帶例外）。</summary>
    private sealed class SpyLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    /// <summary>不連網的假字幕下載：回傳目前設定的 cue（每次載入前換上新影片的 cue）。</summary>
    private sealed class StubProvider : IYouTubeCaptionProvider
    {
        public IReadOnlyList<CaptionCue> Cues { get; set; } = [];

        public Task<YouTubeCaptionResult?> GetJapaneseCaptionsAsync(
            string videoId, string? visitorData, CancellationToken cancellationToken = default) =>
            Task.FromResult<YouTubeCaptionResult?>(new YouTubeCaptionResult("ja", true, "IOS", Cues));
    }

    /// <summary>組出待測物件：真的 PotPlayerSubtitlePusher（debounce 20ms）推送、VodPrefetcher（PollInterval 20ms）、
    /// 假 clock、假 LLM、真的 TranslationManager 與 SQLite 暫存檔。</summary>
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

        public Harness(AppSettings settings, CardReadinessGate? gate = null, PrefetchInFlight? inFlight = null, IOpenAiClient? llm = null,
            LlmCallRecorder? recorder = null)
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
            var translation = new TranslationManager(
                client, new TextProcessingService(), Db.Service, new FakeSettingsService(settings),
                prefetchInFlight: inFlight, recorder: recorder);
            Prefetcher = new VodPrefetcher(
                Pusher, client, translation, new TextProcessingService(), Db.Service,
                new FakeSettingsService(settings), glossary: null, Clock, Logger, gate, inFlight, recorder)
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

    // 4. 換影片時清空狀態：同樣的文字也會重新從新影片的位置翻一遍。
    [Fact]
    public async Task Pushed_AnotherVideo_ClearsStateAndStartsOver()
    {
        using var h = new Harness(Settings(batchSize: 4));
        h.Clock.Position = 0;
        var cues = new[] { Cue(0, 0), Cue(1, 5000) };

        await h.LoadAsync(VideoA, cues);
        await UntilAsync(() => h.Llm.Calls >= 1 && h.Db.Service.GetCache(TextOf(1)) != null);

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
}
