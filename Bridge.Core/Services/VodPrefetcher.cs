using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>錄影預先翻譯：PotPlayerSubtitlePusher 把字幕推送給 PotPlayer 後，依播放位置在背景批次翻譯
/// 還沒翻好的字幕行並寫入翻譯快取（不覆蓋手動修正），播放時直接命中快取。只維持庫存
/// （位置之後已翻好的秒數）不翻整支影片；模型請求一律在背景迴圈內以 Prefetch 優先度經排程器送出
/// （翻譯模型不插隊：即時翻譯排在正在跑的批次後面），同一時間最多一批。</summary>
public sealed class VodPrefetcher : IDisposable
{
    private readonly PotPlayerSubtitlePusher _pusher;
    private readonly IOpenAiClient _llm;
    private readonly TranslationManager _translation;
    private readonly ITextProcessingService _text;
    private readonly ITranslationStore _store;
    private readonly ISettingsService _settings;
    private readonly GlossaryState? _glossary;
    private readonly IPlaybackClock? _clock;
    private readonly CardReadinessGate? _cardGate;
    private readonly PrefetchInFlight? _inFlight;
    private readonly ILogger<VodPrefetcher> _logger;
    private readonly LlmCallRecorder? _recorder;

    /// <summary>庫存 0 時的第一批句數上限：先送少量，約 1 秒內拿到第一段庫存。</summary>
    private const int ColdStartBatchSize = 5;

    private readonly object _lock = new();

    /// <summary>已翻好的修正後原文（這個執行階段內不再重翻）。</summary>
    private readonly HashSet<string> _done = [];

    /// <summary>每個修正後原文的連續失敗次數；達 2 次視為翻好不再重試。</summary>
    private readonly Dictionary<string, int> _failures = [];

    private string? _videoId;
    private IReadOnlyList<SubtitleLine>? _lines;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    /// <summary>等詞彙卡準備門的等待 log 已寫過（同一段等待只寫一次）；只在背景迴圈執行緒存取。</summary>
    private bool _loggedGateWait;

    public VodPrefetcher(
        PotPlayerSubtitlePusher pusher,
        IOpenAiClient llm,
        TranslationManager translation,
        ITextProcessingService text,
        ITranslationStore store,
        ISettingsService settings,
        GlossaryState? glossary,
        IPlaybackClock? clock,
        ILogger<VodPrefetcher> logger,
        CardReadinessGate? cardGate = null,
        PrefetchInFlight? inFlight = null,
        LlmCallRecorder? recorder = null)
    {
        _pusher = pusher;
        _llm = llm;
        _translation = translation;
        _text = text;
        _store = store;
        _settings = settings;
        _glossary = glossary;
        _clock = clock;
        _cardGate = cardGate;
        _inFlight = inFlight;
        _logger = logger;
        _recorder = recorder;
        _pusher.Pushed += OnPushed;
    }

    /// <summary>背景迴圈的輪詢間隔；測試可以縮短。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>字幕推送成功：換影片時清空狀態，更新字幕行，背景迴圈沒在跑時啟動它（不阻塞呼叫端）。</summary>
    private void OnPushed(string videoId, IReadOnlyList<SubtitleLine> lines)
    {
        lock (_lock)
        {
            if (!string.Equals(videoId, _videoId, StringComparison.Ordinal))
            {
                _videoId = videoId;
                _done.Clear();
                _failures.Clear();
            }

            _lines = lines;

            if (_loopTask is not null)
            {
                return;
            }

            var cts = _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(cts.Token));
        }
    }

    /// <summary>每個 PollInterval 執行一次：依播放位置補一批預先翻譯；例外寫 Warning log 後繼續。</summary>
    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, token);

                // 詞彙卡還沒就緒（開台資料探勘進行中或還沒逾時）：這一輪略過，等門打開才開始送；
                // system prompt 的詞彙表段落是送出當下重新組的，自然會帶到新建的詞彙卡
                if (_cardGate is { } gate &&
                    !gate.IsOpen(TimeSpan.FromSeconds(Math.Max(0, _settings.Current.Vod.MiningWaitSeconds))))
                {
                    if (!_loggedGateWait)
                    {
                        _loggedGateWait = true;
                        _logger.LogInformation("預先翻譯：等資料探勘完成才開始");
                    }

                    continue;
                }

                _loggedGateWait = false;
                await PollOnceAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "預先翻譯背景迴圈發生例外");
            }
        }
    }

    /// <summary>單輪：位置之後庫存不足時，組一批還沒翻好的行送模型翻譯並寫入快取。</summary>
    private async Task PollOnceAsync(CancellationToken loopToken)
    {
        // 1. 播放位置：沒有來源（播放器沒開、沒回應）時這一輪略過
        if (_clock?.GetPositionMs() is not { } positionMs)
        {
            return;
        }

        string videoId;
        IReadOnlyList<SubtitleLine> lines;
        lock (_lock)
        {
            if (_videoId is null || _lines is not { Count: > 0 })
            {
                return;
            }

            videoId = _videoId;
            lines = _lines;
        }

        var settings = _settings.Current;

        // 2. 從第一個 EndMs > 位置 的行開始，算每行的 key（修正後的原文）與是否已翻好
        var startIndex = 0;
        while (startIndex < lines.Count && lines[startIndex].EndMs <= positionMs)
        {
            startIndex++;
        }

        if (startIndex >= lines.Count)
        {
            return;
        }

        // 3. 逐行往後算 key（修正後的原文），找第一個還沒翻好的行 u；只算到這一批湊滿為止，
        //    不對整支影片的每一行都做修正（每秒都會跑一次）。全部翻好時這一輪不動作
        string KeyAt(int index) => _translation.NormalizeSource(CaptionSubtitleBuilder.CueText(lines[index].Text));

        var u = -1;
        string? uKey = null;
        for (var i = startIndex; i < lines.Count; i++)
        {
            var key = KeyAt(i);
            if (!IsDone(key))
            {
                u = i;
                uKey = key;
                break;
            }
        }

        if (u < 0)
        {
            return;
        }

        var stockSeconds = Math.Max(0, lines[u].StartMs - positionMs) / 1000.0;

        // 4. 庫存還夠：這一輪不動作
        if (stockSeconds >= settings.Vod.PrefetchLowWaterSeconds)
        {
            return;
        }

        // 5. 組這一批：從 u 開始往後取還沒翻好的行；庫存是 0 時先送少量（約 1 秒內拿到第一段庫存），同一個 key 只放一次
        var batchSize = Math.Max(1, settings.Vod.PrefetchBatchSize);
        var count = stockSeconds <= 0 ? Math.Min(batchSize, ColdStartBatchSize) : batchSize;
        var batchKeys = new List<string>(count) { uKey! };
        var batchLines = new List<SubtitleLine>(count) { lines[u] };
        var seenKeys = new HashSet<string> { uKey! };
        for (var i = u + 1; i < lines.Count && batchKeys.Count < count; i++)
        {
            var key = KeyAt(i);
            if (IsDone(key) || !seenKeys.Add(key))
            {
                continue;
            }

            batchKeys.Add(key);
            batchLines.Add(lines[i]);
        }

        if (batchKeys.Count == 0)
        {
            return;
        }

        // 6. 送出：以 Prefetch 優先度經排程器送批次 prompt，逾時比即時翻譯寬鬆
        var user = PromptBuilder.BuildBatchUserPrompt(batchKeys);
        var system = PromptBuilder.BuildBatchSystemPrompt(settings, _glossary?.PromptSection);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(settings.Vod.PrefetchTimeoutSeconds));

        // 登記正在跑的批次，範圍涵蓋等回覆、解析與所有 SaveCacheUnlessManual：
        // 批次結束時快取已經寫好，等待中的即時翻譯查快取就會命中（失敗的 catch 也在 Dispose 之前結束）
        using var inFlightHandle = _inFlight?.Begin(batchKeys);

        var sw = Stopwatch.StartNew();
        try
        {
            Task<ChatResult> task;
            using (LlmPriorityScope.Enter(LlmPriority.Prefetch))
            {
                task = _llm.SendAsync(user, system, cts.Token);
            }

            var reply = await task;
            sw.Stop();
            // 錄影批次的模型呼叫也記一筆 llm_calls（purpose＝VodBatch）；外部取消不記
            _recorder?.Record(new LlmCallInput
            {
                Purpose = LlmCallPurposes.VodBatch,
                SystemPrompt = system,
                UserPrompt = user,
                ResponseRaw = reply.Content,
                Model = reply.Model,
                PromptTokens = reply.PromptTokens,
                CompletionTokens = reply.CompletionTokens,
                LatencyMs = sw.ElapsedMilliseconds,
                Outcome = LlmCallOutcomes.Ok,
            });

            // 7. 解析：每個有回來的編號後處理後寫入快取；沒回來、空白或崩壞的算失敗
            var replies = PromptBuilder.ParseBatchReply(reply.Content);
            var saved = 0;
            for (var i = 0; i < batchKeys.Count; i++)
            {
                var key = batchKeys[i];
                if (!replies.TryGetValue(i + 1, out var translated))
                {
                    AddFailure(videoId, key);
                    continue;
                }

                var processed = _text.PostProcess(translated, key);
                if (string.IsNullOrWhiteSpace(processed) || CollapseDetector.IsCollapsed(key, processed))
                {
                    AddFailure(videoId, key);
                    continue;
                }

                _store.SaveCacheUnlessManual(key, processed);
                MarkDone(videoId, key);
                saved++;
            }

            _logger.LogInformation("預先翻譯：{Count} 句（{From}～{To}），{Elapsed} ms，庫存 {Stock} 秒",
                saved, FormatClock(batchLines[0].StartMs), FormatClock(batchLines[^1].EndMs),
                sw.ElapsedMilliseconds, stockSeconds);
        }
        catch (OperationCanceledException) when (!loopToken.IsCancellationRequested)
        {
            // 逾時（背景迴圈被停止的取消不算，直接往上拋收掉迴圈）：不印例外堆疊，log 視窗才不會被洗爆；
            // 這批每個 key 的失敗次數 +1
            sw.Stop();
            _recorder?.Record(new LlmCallInput
            {
                Purpose = LlmCallPurposes.VodBatch,
                SystemPrompt = system,
                UserPrompt = user,
                LatencyMs = sw.ElapsedMilliseconds,
                Outcome = LlmCallOutcomes.Timeout,
            });
            _logger.LogWarning("預先翻譯批次逾時：{Count} 句（{From}～{To}），{Elapsed} ms，庫存 {Stock} 秒",
                batchKeys.Count, FormatClock(batchLines[0].StartMs), FormatClock(batchLines[^1].EndMs),
                sw.ElapsedMilliseconds, stockSeconds);
            AddFailures(videoId, batchKeys);
        }
        catch (Exception ex) when (!loopToken.IsCancellationRequested)
        {
            // 其他錯誤：Warning 只帶訊息不帶例外（堆疊另寫一筆 Debug，需要時再查）；
            // 這批每個 key 的失敗次數 +1
            sw.Stop();
            _recorder?.Record(new LlmCallInput
            {
                Purpose = LlmCallPurposes.VodBatch,
                SystemPrompt = system,
                UserPrompt = user,
                LatencyMs = sw.ElapsedMilliseconds,
                Outcome = LlmCallOutcomes.Error,
            });
            _logger.LogWarning("預先翻譯批次失敗：{Count} 句（{From}～{To}），{Elapsed} ms，庫存 {Stock} 秒：{Message}",
                batchKeys.Count, FormatClock(batchLines[0].StartMs), FormatClock(batchLines[^1].EndMs),
                sw.ElapsedMilliseconds, stockSeconds, ex.Message);
            _logger.LogDebug(ex, "預先翻譯批次失敗：{Count} 句（{From}～{To}），{Elapsed} ms，庫存 {Stock} 秒",
                batchKeys.Count, FormatClock(batchLines[0].StartMs), FormatClock(batchLines[^1].EndMs),
                sw.ElapsedMilliseconds, stockSeconds);
            AddFailures(videoId, batchKeys);
        }
    }

    /// <summary>這個 key 是否視為已翻好：空白、不需翻譯、已成功、或失敗次數已達 2 不再重試。</summary>
    private bool IsDone(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || _text.ShouldSkipTranslation(key))
        {
            return true;
        }

        lock (_lock)
        {
            return _done.Contains(key) || _failures.GetValueOrDefault(key) >= 2;
        }
    }

    /// <summary>把 key 加進已翻好集合；影片已換掉時不動作。必須持有 _lock 呼叫。</summary>
    private void MarkDone(string videoId, string key)
    {
        lock (_lock)
        {
            if (!IsCurrentVideoNoLock(videoId))
            {
                return;
            }

            _done.Add(key);
            _failures.Remove(key);
        }
    }

    /// <summary>key 的失敗次數 +1；影片已換掉時不動作。</summary>
    private void AddFailure(string videoId, string key)
    {
        lock (_lock)
        {
            if (!IsCurrentVideoNoLock(videoId))
            {
                return;
            }

            _failures[key] = _failures.GetValueOrDefault(key) + 1;
        }
    }

    private void AddFailures(string videoId, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            AddFailure(videoId, key);
        }
    }

    /// <summary>必須持有 _lock 呼叫：批次執行期間影片是否已換掉。</summary>
    private bool IsCurrentVideoNoLock(string videoId) =>
        string.Equals(videoId, _videoId, StringComparison.Ordinal);

    /// <summary>字幕時間（毫秒）格式化成 HH:mm:ss。</summary>
    private static string FormatClock(long ms) => TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss");

    /// <summary>清空所有狀態（目前影片、已翻好集合、失敗次數）並停止背景迴圈。</summary>
    public void Reset() => StopLoop();

    /// <summary>清空狀態並取消背景迴圈；回傳迴圈的 Task（沒有在跑時為 null），呼叫端可以等它收尾。</summary>
    private Task? StopLoop()
    {
        CancellationTokenSource? cts = null;
        Task? task = null;
        lock (_lock)
        {
            _videoId = null;
            _lines = null;
            _done.Clear();
            _failures.Clear();
            cts = _loopCts;
            task = _loopTask;
            _loopCts = null;
            _loopTask = null;
        }

        // 只取消不 Dispose：迴圈收尾期間還會讀 token，避免與註冊競爭
        cts?.Cancel();
        return task;
    }

    /// <summary>取消訂閱 Pushed 事件並停止背景迴圈。</summary>
    public void Dispose()
    {
        _pusher.Pushed -= OnPushed;

        // 等背景迴圈收尾（最多 2 秒）：正在寫的快取寫完再結束，資料庫檔案才不會還被佔用
        try
        {
            StopLoop()?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }
}
