namespace Translation.Bridge.Core.Services;

/// <summary>正在執行的預先翻譯批次（同一時間最多一批）：即時翻譯要翻的句子在批次裡時，等批次完成後改查快取。
/// 獨立成類別讓 TranslationManager 不必依賴 VodPrefetcher（VodPrefetcher 已依賴 TranslationManager，直接依賴會循環）。
/// 另外登記預先翻譯的運作狀態（<see cref="IsActive"/>）、目前影片全部推送字幕行的 key（<see cref="SetKnownKeys"/>）
/// 與急件要求（<see cref="RequestUrgent"/>）：錄影時即時翻譯把沒命中快取的推送字幕行交給預先翻譯處理，不另外送模型。</summary>
public sealed class PrefetchInFlight
{
    private readonly object _lock = new();

    /// <summary>目前這批的 key（修正後原文）；沒有批次在跑時為 null。</summary>
    private HashSet<string>? _keys;

    /// <summary>目前這批的結束訊號：新批次開始或 handle.Dispose 時設為完成（一律正常完成，不擲例外、不取消）。</summary>
    private TaskCompletionSource _completion = Create();

    /// <summary>預先翻譯是否運作中（背景迴圈已通過詞彙卡準備門）。</summary>
    private bool _active;

    /// <summary>目前影片全部推送字幕行的 key（修正後原文）；停止時為 null。</summary>
    private HashSet<string>? _knownKeys;

    /// <summary>最新一個還沒被預翻迴圈取走的急件要求；同時只保留最新一個。</summary>
    private UrgentRequest? _pendingUrgent;

    /// <summary>已被預翻迴圈取走、等含有這個 key 的批次結束時完成的急件要求。</summary>
    private UrgentRequest? _claimedUrgent;

    /// <summary>即時翻譯的請求紀錄（key 與請求時間），等預翻迴圈取走；最多保留最近 200 筆。</summary>
    private readonly Queue<(string Key, DateTimeOffset At)> _requested = new();

    /// <summary>請求紀錄的保留上限：超過時丟掉最舊的。</summary>
    private const int MaxRequestedRecords = 200;

    /// <summary>請求時間的來源（測試可注入）。</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>時間來源可注入（測試用）；沒有參數的建構行為不變（使用 TimeProvider.System）。</summary>
    public PrefetchInFlight(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>有新的 <see cref="RequestUrgent"/> 時設為完成（<see cref="WaitForWorkAsync"/> 用）；
    /// 急件被取走時換成新的，下一次等待重新等到新要求為止。</summary>
    private TaskCompletionSource _workSignal = Create();

    private static TaskCompletionSource Create() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>即時翻譯的急件要求：要求的 key 與它完成用的訊號。</summary>
    private sealed record UrgentRequest(string Key, TaskCompletionSource Completion);

    /// <summary>預先翻譯是否運作中：背景迴圈已通過詞彙卡準備門、有推送字幕時為 true。</summary>
    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    /// <summary>更新預先翻譯的運作狀態；設為 false 時，所有還在等的急件 Task 一併完成。</summary>
    public void SetActive(bool active)
    {
        UrgentRequest? completePending;
        UrgentRequest? completeClaimed;
        lock (_lock)
        {
            if (_active == active)
            {
                return;
            }

            _active = active;
            if (active)
            {
                return;
            }

            // 預翻不運作了：急件不會有結果，直接完成讓呼叫端去查快取
            completePending = _pendingUrgent;
            completeClaimed = _claimedUrgent;
            _pendingUrgent = null;
            _claimedUrgent = null;
        }

        completePending?.Completion.TrySetResult();
        completeClaimed?.Completion.TrySetResult();
    }

    /// <summary>更新目前影片全部推送字幕行的 key（修正後原文）；換影片或停止時傳 null 清空，
    /// 並把所有還在等的急件 Task 完成。</summary>
    public void SetKnownKeys(IReadOnlyCollection<string>? keys)
    {
        UrgentRequest? completePending = null;
        UrgentRequest? completeClaimed = null;
        lock (_lock)
        {
            if (keys is null)
            {
                _knownKeys = null;
                // 沒有推送字幕了：急件不會有結果，直接完成讓呼叫端去查快取
                completePending = _pendingUrgent;
                completeClaimed = _claimedUrgent;
                _pendingUrgent = null;
                _claimedUrgent = null;
            }
            else
            {
                _knownKeys = new HashSet<string>(keys, StringComparer.Ordinal);
            }
        }

        completePending?.Completion.TrySetResult();
        completeClaimed?.Completion.TrySetResult();
    }

    /// <summary>這個 key 是否為目前影片的推送字幕行（修正後原文）。</summary>
    public bool IsKnown(string key)
    {
        lock (_lock)
        {
            return _knownKeys?.Contains(key) ?? false;
        }
    }

    /// <summary>即時翻譯收到推送字幕行的請求時呼叫（不論快取是否命中），記錄「誰、何時被請求」，交給預翻換算提前距離。</summary>
    public void NoteRequested(string key)
    {
        lock (_lock)
        {
            _requested.Enqueue((key, _timeProvider.GetUtcNow()));
            while (_requested.Count > MaxRequestedRecords)
            {
                _requested.Dequeue();
            }
        }
    }

    /// <summary>預翻迴圈取出累積的請求紀錄（key 與請求時間），取完清空；最多保留最近 200 筆，超過丟最舊的。</summary>
    public IReadOnlyList<(string Key, DateTimeOffset At)> TakeRequested()
    {
        lock (_lock)
        {
            var records = _requested.ToArray();
            _requested.Clear();
            return records;
        }
    }

    /// <summary>即時翻譯要求「從這句開始馬上翻一批」。回傳的 Task 在含有這個 key 的批次結束時完成
    /// （成功或失敗都算）。同一個 key 重複要求時回傳同一個 Task。只保留最新的一個待處理要求：
    /// 被新要求取代的舊要求，它的 Task 也會完成，呼叫端不會永遠等。</summary>
    public Task RequestUrgent(string key)
    {
        Task task;
        UrgentRequest? replaced;
        lock (_lock)
        {
            if (_pendingUrgent is { } pending && string.Equals(pending.Key, key, StringComparison.Ordinal))
            {
                return pending.Completion.Task;
            }

            replaced = _pendingUrgent;
            var request = new UrgentRequest(key, Create());
            _pendingUrgent = request;
            task = request.Completion.Task;
            _workSignal.TrySetResult();
        }

        replaced?.Completion.TrySetResult();
        return task;
    }

    /// <summary>預翻迴圈取出目前的待處理要求；沒有時回傳 null。取走後急件改由批次結束時完成。</summary>
    public string? TakeUrgent()
    {
        lock (_lock)
        {
            if (_pendingUrgent is not { } request)
            {
                return null;
            }

            _pendingUrgent = null;
            _claimedUrgent = request;
            // 急件已取走：等待訊號換新的，下一次 WaitForWorkAsync 重新等到新要求為止
            _workSignal = Create();
            return request.Key;
        }
    }

    /// <summary>預翻迴圈用來取代 Task.Delay(PollInterval)：有新的 <see cref="RequestUrgent"/>
    /// （或已有還沒取走的急件）時立刻完成，否則等到逾時；取消時照常拋出例外。</summary>
    public async Task WaitForWorkAsync(TimeSpan timeout, CancellationToken token)
    {
        Task signal;
        lock (_lock)
        {
            if (_pendingUrgent is not null)
            {
                return;
            }

            signal = _workSignal.Task;
        }

        var delay = Task.Delay(timeout, token);
        if (await Task.WhenAny(signal, delay) == delay)
        {
            // 逾時正常返回；取消時 Task.Delay 的例外照常往上拋（與直接 await Task.Delay 相同）
            await delay;
        }
    }

    /// <summary>開始一批：記下這批的 key（修正後原文），回傳結束時要 Dispose 的 handle。
    /// 已有一批時，舊的那批視為結束，等待中的呼叫端會被放行。已被取走的急件要求的 key
    /// 在這批裡時，由這批承接：批次結束（Dispose）時完成它的 Task。</summary>
    public IDisposable Begin(IReadOnlyCollection<string> keys)
    {
        TaskCompletionSource? previous;
        TaskCompletionSource completion;
        UrgentRequest? urgent = null;
        lock (_lock)
        {
            previous = _completion;
            _keys = new HashSet<string>(keys, StringComparer.Ordinal);
            completion = _completion = Create();
            if (_claimedUrgent is { } claimed && _keys.Contains(claimed.Key))
            {
                urgent = claimed;
                _claimedUrgent = null;
            }
        }

        previous.TrySetResult();
        return new BatchHandle(this, completion, urgent);
    }

    /// <summary>key 在正在執行的批次裡時，回傳批次結束（成功或失敗都算）時完成的 Task；否則回傳 null。</summary>
    public Task? WaitTaskFor(string key)
    {
        lock (_lock)
        {
            return _keys is not null && _keys.Contains(key) ? _completion.Task : null;
        }
    }

    /// <summary>結束一批：只有在它還是目前這批時才清掉 keys（舊 handle 的 Dispose 不能清掉新的那批），
    /// 並把它的結束訊號與承接的急件 Task 設為完成；重複呼叫沒有副作用。</summary>
    private void End(TaskCompletionSource completion, UrgentRequest? urgent)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_completion, completion))
            {
                _keys = null;
            }
        }

        completion.TrySetResult();
        urgent?.Completion.TrySetResult();
    }

    /// <summary>批次的結束 handle：Dispose 時結束這批（成功或失敗都算），放行等待中的即時翻譯與急件要求。</summary>
    private sealed class BatchHandle(PrefetchInFlight owner, TaskCompletionSource completion, UrgentRequest? urgent) : IDisposable
    {
        public void Dispose() => owner.End(completion, urgent);
    }
}
