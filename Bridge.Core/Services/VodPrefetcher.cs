using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>錄影預先翻譯：PotPlayerSubtitlePusher 把字幕推送給 PotPlayer 後，依播放位置在背景批次翻譯
/// 還沒翻好的字幕行並寫入翻譯快取（不覆蓋手動修正），播放時直接命中快取。「已翻好」不只看本次播放的
/// 記憶體集合，資料庫快取已有譯文的句子也算（重看不重翻：整支影片翻過一次後，重播完全不送模型）。
/// 批次從第一個未翻好的行往後取「連續」的句子組成（已翻好的行照樣一起送、一起重翻），模型才看得到
/// 完整前後文。只維持庫存（位置之後已翻好的秒數）不翻整支影片；模型請求一律在背景迴圈內以 Prefetch
/// 優先度經排程器送出（翻譯模型不插隊：即時翻譯排在正在跑的批次後面），同一時間最多一批。</summary>
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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VodPrefetcher> _logger;
    private readonly LlmCallRecorder? _recorder;

    /// <summary>庫存 0 時的第一批句數上限：先送少量，約 1 秒內拿到第一段庫存。</summary>
    private const int ColdStartBatchSize = 5;

    /// <summary>批次成功後，最近一次即時模型呼叫在這個時間內才值得預熱即時對話（把被批次擠掉的前綴補回來）。</summary>
    private static readonly TimeSpan LiveWarmUpWindow = TimeSpan.FromSeconds(60);

    /// <summary>觀察到的提前請求距離時效：超過這麼久沒有新觀察就失效，門檻回到設定值。</summary>
    private static readonly TimeSpan LeadObservationWindow = TimeSpan.FromMinutes(10);

    /// <summary>觀察到的提前距離換算門檻時加上的餘裕（門檻 = 提前距離 + 30 秒）。</summary>
    private const int LeadMarginSeconds = 30;

    /// <summary>實際門檻上限（秒）：避免異常的提前距離讓整部影片都被翻。</summary>
    private const int MaxEffectiveLowWaterSeconds = 600;

    private readonly object _lock = new();

    /// <summary>已翻好的修正後原文（這個執行階段內不再重翻）。</summary>
    private readonly HashSet<string> _done = [];

    /// <summary>每個修正後原文的連續失敗次數；達 2 次視為翻好不再重試。</summary>
    private readonly Dictionary<string, int> _failures = [];

    /// <summary>這支影片已查過資料庫快取而沒有譯文（或查詢失敗）的 key：預翻迴圈每秒跑一次，
    /// 同一個 key 在同一支影片最多查資料庫一次。換影片時與 _done 一起清空；
    /// 之後這句被批次翻好會 MarkDone，不受這個記錄影響。以 _lock 保護。</summary>
    private readonly HashSet<string> _cacheChecked = [];

    /// <summary>最近觀察到的 PotPlayer 提前請求距離（毫秒）；0 表示沒有有效觀察。以 _lock 保護。</summary>
    private long _observedLeadMs;

    /// <summary>提前距離的觀察時間（用 TimeProvider 判斷 10 分鐘時效）。以 _lock 保護。</summary>
    private DateTimeOffset _observedLeadAt;

    /// <summary>上次寫門檻調整 log 時的實際門檻（秒）：增加 ≥ 10 秒才再寫。以 _lock 保護。</summary>
    private int _lastLoggedThresholdSeconds;

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
        LlmCallRecorder? recorder = null,
        TimeProvider? timeProvider = null)
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
        _timeProvider = timeProvider ?? TimeProvider.System;
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
                _cacheChecked.Clear();
                _observedLeadMs = 0;
                _observedLeadAt = default;
                _lastLoggedThresholdSeconds = 0;
            }

            _lines = lines;

            if (_loopTask is null)
            {
                var cts = _loopCts = new CancellationTokenSource();
                _loopTask = Task.Run(() => RunLoopAsync(cts, cts.Token));
            }
        }

        // 目前影片全部推送字幕行的 key（修正後原文）：讓路判斷用。每次推送只算一次，
        // 背景迴圈每秒輪詢時不重算全部
        if (_inFlight is not null)
        {
            var keys = new List<string>(lines.Count);
            foreach (var line in lines)
            {
                keys.Add(_translation.NormalizeSource(CaptionSubtitleBuilder.CueText(line.Text)));
            }

            _inFlight.SetKnownKeys(keys);
        }
    }

    /// <summary>每個 PollInterval 執行一次：依播放位置補一批預先翻譯；例外寫 Warning log 後繼續。</summary>
    private async Task RunLoopAsync(CancellationTokenSource cts, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 等 PollInterval，有新的急件要求（RequestUrgent）時立刻醒來處理
                    if (_inFlight is { } inFlight)
                    {
                        await inFlight.WaitForWorkAsync(PollInterval, token);
                    }
                    else
                    {
                        await Task.Delay(PollInterval, token);
                    }

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
                    _inFlight?.SetActive(true);
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
        finally
        {
            // 迴圈結束：預翻不再運作、清掉已知推送字幕行，還在等的急件一併完成。
            // 只有自己還是目前的迴圈才清理（StopLoop 已清過、或已有新迴圈接手時不能清掉新迴圈的狀態）
            var clean = false;
            lock (_lock)
            {
                clean = ReferenceEquals(_loopCts, cts);
            }

            if (clean)
            {
                _inFlight?.SetActive(false);
                _inFlight?.SetKnownKeys(null);
            }
        }
    }

    /// <summary>單輪：位置之後庫存不足時，組一批還沒翻好的行送模型翻譯並寫入快取。</summary>
    private async Task PollOnceAsync(CancellationToken loopToken)
    {
        // 急件一律在最前面取走：之後任何提早 return 都不能把它留著，否則 WaitForWorkAsync 會立刻返回、迴圈空轉
        var urgentKey = _inFlight?.TakeUrgent();

        // 1. 播放位置：沒有來源（播放器沒開、沒回應）時這一輪略過；有急件時從頭找它的行
        if (_clock?.GetPositionMs() is not { } positionMs)
        {
            if (urgentKey is null)
            {
                return;
            }

            positionMs = 0;
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
        string KeyAt(int index) => _translation.NormalizeSource(CaptionSubtitleBuilder.CueText(lines[index].Text));

        var startIndex = 0;
        while (startIndex < lines.Count && lines[startIndex].EndMs <= positionMs)
        {
            startIndex++;
        }

        // 3. 取出即時翻譯的請求紀錄，換算 PotPlayer 的提前請求距離（用請求當下的播放位置推算，假設 1 倍速）；
        //    實際庫存門檻 = max(設定值, 觀察最大值 + 30 秒)，上限 600 秒
        UpdateObservedLead(lines, KeyAt, startIndex, positionMs);
        var lowWaterSeconds = EffectiveLowWaterSeconds(settings.Vod.PrefetchLowWaterSeconds);

        // 4. 急件優先：即時翻譯要求「從這句開始馬上翻一批」的 key。找到行而且還沒翻好（資料庫快取已有
        //    譯文也算已翻好）時，這批從那一行開始（略過庫存判斷）；找不到或已翻好時忽略，照一般流程處理
        var urgent = false;
        var u = -1;
        string? uKey = null;
        if (urgentKey is not null)
        {
            var i = IndexOfKey(lines, KeyAt, urgentKey, startIndex);
            if (i >= 0 && !IsDoneOrCached(videoId, urgentKey))
            {
                urgent = true;
                u = i;
                uKey = urgentKey;
            }
        }

        // 5. 一般流程：逐行往後算 key（修正後的原文），找第一個還沒翻好的行 u（資料庫快取已有譯文的句子
        //    也算已翻好，重看不重翻；查過資料庫沒有的 key 這支影片內不再重查）；只算到找到為止，
        //    不對整支影片的每一行都做修正（每秒都會跑一次）。全部翻好時這一輪不動作
        if (!urgent)
        {
            for (var i = startIndex; i < lines.Count; i++)
            {
                var key = KeyAt(i);
                if (!IsDoneOrCached(videoId, key))
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
        }

        var stockSeconds = Math.Max(0, lines[u].StartMs - positionMs) / 1000.0;

        // 6. 庫存還夠：這一輪不動作（急件不受庫存判斷限制：這句就是現在要顯示的字幕）
        if (!urgent && stockSeconds >= lowWaterSeconds)
        {
            return;
        }

        // 7. 組這一批：從 u 開始往後取「連續」的行 — 已翻好的行不再跳過（跳過會讓送出去的句子前後不連貫，
        //    模型看不到完整前後文），批內已翻過的照樣一起送、一起重翻（SaveCacheUnlessManual 照舊覆蓋
        //    非手動修正的快取）。只略過：key 空白或不需要翻譯的行、同一批已出現過的 key、失敗次數 ≥ 2 的 key。
        //    急件批次只送少量：急件是即時翻譯正在等結果的句子（PotPlayer 提前約 110 秒請求，常不在剛翻好的批次裡），
        //    照一般規則湊滿 20 句實測約 3.5 秒，會超過即時翻譯逾時（1500 ms）而 Timeout；少量才能趕在逾時內回來。
        //    一般補庫存批次不變：庫存 0 時先送少量（約 1 秒內拿到第一段庫存），其他送整批
        var batchSize = Math.Max(1, settings.Vod.PrefetchBatchSize);
        int count;
        if (urgent)
        {
            count = Math.Min(batchSize, Math.Max(1, settings.Vod.UrgentBatchSize));
        }
        else
        {
            count = stockSeconds <= 0 ? Math.Min(batchSize, ColdStartBatchSize) : batchSize;
        }
        var batchKeys = new List<string>(count) { uKey! };
        var batchLines = new List<SubtitleLine>(count) { lines[u] };
        var seenKeys = new HashSet<string> { uKey! };
        for (var i = u + 1; i < lines.Count && batchKeys.Count < count; i++)
        {
            var key = KeyAt(i);
            if (string.IsNullOrWhiteSpace(key) || _text.ShouldSkipTranslation(key) ||
                !seenKeys.Add(key) || HasFailedTwice(key))
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

        // 8. 送出：以 Prefetch 優先度經排程器送批次 prompt，逾時比即時翻譯寬鬆
        var user = PromptBuilder.BuildBatchUserPrompt(batchKeys);
        var system = PromptBuilder.BuildBatchSystemPrompt(settings, _glossary?.PromptSection);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(settings.Vod.PrefetchTimeoutSeconds));

        // 登記正在跑的批次，範圍涵蓋等回覆、解析與所有 SaveCacheUnlessManual：
        // 批次結束時快取已經寫好，等待中的即時翻譯查快取就會命中（失敗的 catch 也在 Dispose 之前結束）
        using var inFlightHandle = _inFlight?.Begin(batchKeys);

        var sw = Stopwatch.StartNew();
        var saved = 0;
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

            // 9. 解析：每個有回來的編號後處理後寫入快取；沒回來、空白或崩壞的算失敗
            var replies = PromptBuilder.ParseBatchReply(reply.Content);
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

            _logger.LogInformation("預先翻譯：{Count} 句（{From}～{To}），{Elapsed} ms，庫存 {Stock} 秒，門檻 {Threshold} 秒",
                saved, FormatClock(batchLines[0].StartMs), FormatClock(batchLines[^1].EndMs),
                sw.ElapsedMilliseconds, stockSeconds, lowWaterSeconds);
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

        // 10. 批次結束後預熱即時對話：這批有成功寫入快取、而且即時翻譯最近還在送模型
        //    （LastLiveModelCallAt 在 60 秒內）時，才值得把被批次擠掉的即時前綴補回來；
        //    沒有即時翻譯在跑就不預熱，省電。背景執行不等待
        if (saved > 0 && _translation.LastLiveModelCallAt is { } lastLiveCall &&
            _timeProvider.GetUtcNow() - lastLiveCall <= LiveWarmUpWindow)
        {
            _ = WarmUpLiveConversationAsync();
        }
    }

    /// <summary>背景預熱即時對話：例外只寫 Warning log，不影響預翻迴圈。</summary>
    private async Task WarmUpLiveConversationAsync()
    {
        try
        {
            await _translation.WarmUpAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "預先翻譯後預熱即時對話失敗");
        }
    }

    /// <summary>把即時翻譯的請求紀錄（<see cref="PrefetchInFlight.TakeRequested"/>）換算成提前距離：
    /// lead = 行.StartMs − (目前位置 − (現在 − 請求時間))，即用請求當下的播放位置推算（假設 1 倍速）。
    /// lead ≤ 0（已在播或播過）或找不到行時忽略；只記住最近 10 分鐘內的最大值（換影片時清空）。</summary>
    private void UpdateObservedLead(IReadOnlyList<SubtitleLine> lines, Func<int, string> keyAt, int startIndex, long positionMs)
    {
        var requests = _inFlight?.TakeRequested();
        if (requests is not { Count: > 0 })
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        long maxLeadMs = 0;
        foreach (var (key, at) in requests)
        {
            var index = IndexOfKey(lines, keyAt, key, startIndex);
            if (index < 0)
            {
                continue;
            }

            var positionAtRequest = positionMs - (now - at).TotalMilliseconds;
            var leadMs = (long)Math.Round(lines[index].StartMs - positionAtRequest);
            if (leadMs > maxLeadMs)
            {
                maxLeadMs = leadMs;
            }
        }

        if (maxLeadMs <= 0)
        {
            return;
        }

        lock (_lock)
        {
            // 逾期的舊觀察值先失效，再套用這次的最大值（觀察值只保留最近 10 分鐘內的最大值）
            if (now - _observedLeadAt > LeadObservationWindow)
            {
                _observedLeadMs = 0;
            }

            if (maxLeadMs > _observedLeadMs)
            {
                _observedLeadMs = maxLeadMs;
            }

            _observedLeadAt = now;
        }
    }

    /// <summary>算實際庫存門檻（秒）= max(設定值, 觀察最大值 + 30 秒)，上限 600 秒。
    /// 比設定值大、而且比上次寫 log 時增加 ≥ 10 秒時寫 Information log（不每輪都寫）。</summary>
    private int EffectiveLowWaterSeconds(int configuredSeconds)
    {
        long observedMs;
        int lastLogged;
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            // 觀察值超過 10 分鐘：失效，門檻回到設定值
            if (now - _observedLeadAt > LeadObservationWindow)
            {
                _observedLeadMs = 0;
            }

            observedMs = _observedLeadMs;
            lastLogged = _lastLoggedThresholdSeconds;
        }

        if (observedMs <= 0)
        {
            return configuredSeconds;
        }

        var leadSeconds = (int)Math.Ceiling(TimeSpan.FromMilliseconds(observedMs).TotalSeconds);
        var effectiveSeconds = Math.Min(
            Math.Max(configuredSeconds, leadSeconds + LeadMarginSeconds),
            MaxEffectiveLowWaterSeconds);
        if (effectiveSeconds > configuredSeconds && effectiveSeconds - lastLogged >= 10)
        {
            lock (_lock)
            {
                _lastLoggedThresholdSeconds = effectiveSeconds;
            }

            _logger.LogInformation("預先翻譯：PotPlayer 提前 {Lead} 秒請求，庫存門檻調為 {Threshold} 秒",
                leadSeconds, effectiveSeconds);
        }

        return effectiveSeconds;
    }

    /// <summary>找急件 key 的第一個符合行：優先找播放位置之後的第一個符合行，找不到再從頭找；都沒有回傳 -1。</summary>
    private static int IndexOfKey(IReadOnlyList<SubtitleLine> lines, Func<int, string> keyAt, string key, int startIndex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            if (string.Equals(keyAt(i), key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (var i = 0; i < startIndex; i++)
        {
            if (string.Equals(keyAt(i), key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>這個 key 在本次播放是否視為已翻好：空白、不需翻譯、已成功、或失敗次數已達 2 不再重試。
    /// 只看本次播放的記憶體狀態；「資料庫快取已有譯文也算已翻好（重看不重翻）」由 IsDoneOrCached 負責。</summary>
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

    /// <summary>這個 key 是否視為已翻好：本次播放已翻好（<see cref="IsDone"/>），或資料庫快取已有譯文
    /// （重看不重翻）。快取查到時 MarkDone 記進本次播放的集合（之後的輪詢不再查資料庫）；查不到
    /// （或查詢丟例外，記 Debug log）也記住「這支影片查過、沒有」，同一個 key 在同一支影片
    /// 最多查資料庫一次（記錄換影片時一起清掉）。</summary>
    private bool IsDoneOrCached(string videoId, string key)
    {
        if (IsDone(key))
        {
            return true;
        }

        lock (_lock)
        {
            // 這支影片已經查過資料庫（沒有譯文或查詢失敗）：不再重查，避免每秒輪詢打資料庫
            if (!_cacheChecked.Add(key))
            {
                return false;
            }
        }

        try
        {
            if (_store.GetCache(key) is not null)
            {
                // 資料庫已有譯文：視同已翻好並記進本次播放的集合
                MarkDone(videoId, key);
                return true;
            }
        }
        catch (Exception ex)
        {
            // 查詢失敗（例如資料庫暫時不可用）：視為沒有譯文，不得讓輪詢迴圈中斷
            _logger.LogDebug(ex, "預先翻譯：查翻譯快取失敗，視為沒有譯文：{Key}", key);
        }

        return false;
    }

    /// <summary>這個 key 的失敗次數是否已達 2（不再重試，組批次時略過）。以 _lock 保護讀取。</summary>
    private bool HasFailedTwice(string key)
    {
        lock (_lock)
        {
            return _failures.GetValueOrDefault(key) >= 2;
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
            _cacheChecked.Clear();
            cts = _loopCts;
            task = _loopTask;
            _loopCts = null;
            _loopTask = null;
        }

        // 只取消不 Dispose：迴圈收尾期間還會讀 token，避免與註冊競爭
        cts?.Cancel();
        // 預翻不再運作：清掉已知推送字幕行，還在等的急件一併完成
        _inFlight?.SetActive(false);
        _inFlight?.SetKnownKeys(null);
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
