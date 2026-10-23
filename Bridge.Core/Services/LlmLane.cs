using Microsoft.Extensions.Logging;

namespace Translation.Bridge.Core.Services;

/// <summary>各模型的排程器；translation 未提供時為 null（該模型還沒接上排程）。</summary>
public sealed class LlmLanes(LlmLane lorebook, LlmLane? translation = null)
{
    public LlmLane Lorebook { get; } = lorebook;

    /// <summary>翻譯模型的排程 lane（即時翻譯 Realtime、預先翻譯 Prefetch）。</summary>
    public LlmLane? Translation { get; } = translation;
}

/// <summary>
/// 單一執行位置的 LLM 請求排程器：同一個模型的全部請求排同一個隊、一次只執行一個，
/// 等待中的請求依優先度（數字小的先）、再依到達序號排序。高優先請求進隊時，正在執行的低優先請求
/// 依插隊門檻決定取消（預估進度 × 100 &lt; 門檻時取消，保留序號放回隊伍之後自動重跑）或讓它跑完。
/// 被插隊的呼叫端感覺不到取消，只是等比較久，work 之後會再被呼叫一次。
/// </summary>
public sealed class LlmLane(string name, Func<int> preemptThresholdPercent, TimeProvider timeProvider, ILogger<LlmLane> logger)
{
    private enum ItemStatus
    {
        Waiting,
        Running,
        Completed,
        Failed,
        Cancelled,
    }

    /// <summary>把結果交給呼叫端的非泛型介面（隊伍裡的請求有不同的 T）。</summary>
    private interface ICompletion
    {
        void SetResult(object? result);

        void SetCancelled(CancellationToken token);

        void SetException(Exception exception);
    }

    private sealed class Completion<T> : ICompletion
    {
        public TaskCompletionSource<T> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetResult(object? result) => Source.TrySetResult((T)result!);

        public void SetCancelled(CancellationToken token) => Source.TrySetException(new OperationCanceledException(token));

        public void SetException(Exception exception) => Source.TrySetException(exception);
    }

    private sealed class CallItem(LlmCallInfo call, CancellationToken callerToken, Func<CancellationToken, Task<object?>> work, ICompletion completion)
    {
        public LlmCallInfo Call { get; } = call;

        public CancellationToken CallerToken { get; } = callerToken;

        public Func<CancellationToken, Task<object?>> Work { get; } = work;

        public ICompletion Completion { get; } = completion;

        /// <summary>進隊時分配的遞增序號；被插隊重排時保留，所以會排在同優先度的最前面。</summary>
        public long Sequence { get; set; }

        public ItemStatus Status { get; set; } = ItemStatus.Waiting;

        /// <summary>執行中的 linked CTS；等待中或已結束時為 null。</summary>
        public CancellationTokenSource? LinkedCts { get; set; }

        /// <summary>被插隊標記：這個請求的取消是 LlmLane 自己做的。</summary>
        public bool Preempted { get; set; }

        /// <summary>標記插隊當下算出的預估進度（記 log 用）。</summary>
        public double PreemptedProgress { get; set; }

        /// <summary>等待期間監看呼叫端 token 的註冊；抵達終態後清除。</summary>
        public CancellationTokenRegistration? Registration { get; set; }
    }

    public string Name { get; } = name;

    private readonly object _lock = new();
    private readonly List<CallItem> _queue = [];
    private readonly Dictionary<LlmPriority, DurationEstimator> _durations = [];
    private long _sequence;
    private CallItem? _running;

    /// <summary>把一個請求排進隊伍，回傳會在執行完成後拿到結果的 Task。
    /// call 是 null 時自己建立；不是 null 時以 call.Priority 為準。</summary>
    public Task<T> RunAsync<T>(LlmPriority priority, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken, LlmCallInfo? call = null)
    {
        call ??= new LlmCallInfo(priority);
        var completion = new Completion<T>();
        var item = new CallItem(call, cancellationToken, ct => WrapWork(work, ct), completion);

        CancellationTokenSource? ctsToCancel = null;
        lock (_lock)
        {
            item.Sequence = ++_sequence;
            _queue.Add(item);
            ctsToCancel = TryPreemptRunning(call.Priority);
        }

        // 插隊取消在 lock 外執行（Cancel 會等 token 回呼完成，不能拿著 lock 等）；
        // 被插隊的請求可能剛好在這之間結束並 Dispose 了 CTS，這時已經不用取消
        try
        {
            ctsToCancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // 監看等待期間的呼叫端取消；token 已取消時回呼會同步執行（此時不在 lock 內，安全）
        var registration = cancellationToken.Register(state => OnCallerCancelled((CallItem)state!), item);
        var disposeRegistration = false;
        lock (_lock)
        {
            // 極短生命週期的請求可能在註冊前就已結束：直接丟棄註冊
            if (item.Registration is null && item.Status is ItemStatus.Completed or ItemStatus.Failed or ItemStatus.Cancelled)
            {
                disposeRegistration = true;
            }
            else
            {
                item.Registration = registration;
            }
        }

        if (disposeRegistration)
        {
            registration.Dispose();
        }

        Pump();
        return completion.Source.Task;
    }

    /// <summary>這個請求的預估進度 = 已執行時間 ÷ 同優先度最近 5 次成功請求耗時的中位數（可以大於 1）。
    /// 尚未開始或沒有耗時紀錄時為 null。</summary>
    public double? EstimateProgress(LlmCallInfo call)
    {
        lock (_lock)
        {
            return EstimateProgressLocked(call);
        }
    }

    /// <summary>加一筆該優先度的耗時紀錄（測試和預熱用；正常情況由 lane 在 work 成功後自動記錄）。</summary>
    public void RecordDuration(LlmPriority priority, TimeSpan duration)
    {
        lock (_lock)
        {
            GetEstimator(priority).Add(duration);
        }
    }

    private static async Task<object?> WrapWork<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        await work(cancellationToken).ConfigureAwait(false);

    /// <summary>必須持有 _lock 呼叫：新請求進隊時，若正在執行的請求優先度較低且預估進度未達門檻，標記為被插隊。
    /// 回傳要取消的 linked CTS（由呼叫端在 lock 外取消）。</summary>
    private CancellationTokenSource? TryPreemptRunning(LlmPriority arrivingPriority)
    {
        var running = _running;
        if (running is null || running.Preempted || running.Call.Priority <= arrivingPriority)
        {
            return null;
        }

        if (EstimateProgressLocked(running.Call) is not { } progress)
        {
            // 沒有耗時紀錄：不知道進度，不插隊
            return null;
        }

        var threshold = Math.Clamp(preemptThresholdPercent(), 0, 100);
        if (progress * 100 >= threshold)
        {
            // 預估進度達到門檻：讓它跑完
            return null;
        }

        running.Preempted = true;
        running.PreemptedProgress = progress;
        return running.LinkedCts;
    }

    private double? EstimateProgressLocked(LlmCallInfo call)
    {
        if (call.StartedAt is not { } started)
        {
            return null;
        }

        if (!_durations.TryGetValue(call.Priority, out var estimator) ||
            estimator.Median is not { } median ||
            median <= TimeSpan.Zero)
        {
            return null;
        }

        return (timeProvider.GetUtcNow() - started) / median;
    }

    private DurationEstimator GetEstimator(LlmPriority priority)
    {
        if (!_durations.TryGetValue(priority, out var estimator))
        {
            estimator = new DurationEstimator();
            _durations[priority] = estimator;
        }
        return estimator;
    }

    /// <summary>執行位置空著時取出隊伍中最前面的請求開始執行（不在 lock 裡呼叫 work）。</summary>
    private void Pump()
    {
        CallItem? toStart = null;
        lock (_lock)
        {
            if (_running is not null)
            {
                return;
            }

            foreach (var candidate in _queue)
            {
                if (candidate.Status != ItemStatus.Waiting)
                {
                    continue;
                }

                if (toStart is null ||
                    candidate.Call.Priority < toStart.Call.Priority ||
                    (candidate.Call.Priority == toStart.Call.Priority && candidate.Sequence < toStart.Sequence))
                {
                    toStart = candidate;
                }
            }

            if (toStart is null)
            {
                return;
            }

            _queue.Remove(toStart);
            toStart.Status = ItemStatus.Running;
            toStart.Call.StartedAt = timeProvider.GetUtcNow();
            toStart.LinkedCts = CancellationTokenSource.CreateLinkedTokenSource(toStart.CallerToken);
            _running = toStart;
        }

        _ = RunItemAsync(toStart);
    }

    private async Task RunItemAsync(CallItem item)
    {
        object? result = null;
        Exception? error = null;
        try
        {
            result = await item.Work(item.LinkedCts!.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        CancellationTokenSource? ctsToDispose = null;
        CancellationTokenRegistration? registrationToDispose = null;
        CallItem? requeued = null;
        Exception? toCaller = null;
        lock (_lock)
        {
            _running = null;
            ctsToDispose = item.LinkedCts;
            item.LinkedCts = null;

            if (error is null)
            {
                // 成功：記一筆該優先度的耗時，把結果交給呼叫端
                var duration = timeProvider.GetUtcNow() - (item.Call.StartedAt ?? timeProvider.GetUtcNow());
                GetEstimator(item.Call.Priority).Add(duration);
                item.Status = ItemStatus.Completed;
                registrationToDispose = item.Registration;
                item.Registration = null;
            }
            else if (error is OperationCanceledException && item.Preempted && !item.CallerToken.IsCancellationRequested)
            {
                // 被插隊取消：放回隊伍、保留序號（排在同優先度最前面），之後重跑；不記耗時
                item.Status = ItemStatus.Waiting;
                item.Call.StartedAt = null;
                item.Preempted = false;
                _queue.Add(item);
                requeued = item;
            }
            else
            {
                // 呼叫端取消或其他例外：原樣交給呼叫端
                item.Status = ItemStatus.Failed;
                toCaller = error;
                registrationToDispose = item.Registration;
                item.Registration = null;
            }
        }

        // Dispose 會等進行中的回呼完成，不能在 lock 裡做（回呼也會拿 lock，會死結）
        ctsToDispose?.Dispose();
        registrationToDispose?.Dispose();

        if (requeued is not null)
        {
            logger.LogInformation("{Lane}：插隊，暫停 {Priority}（預估進度 {Progress:P0}），之後重跑",
                Name, requeued.Call.Priority, requeued.PreemptedProgress);
        }
        else if (toCaller is not null)
        {
            item.Completion.SetException(toCaller);
        }
        else
        {
            item.Completion.SetResult(result);
        }

        // 接著開始下一個
        Pump();
    }

    /// <summary>等待中的請求被呼叫端取消：從隊伍移除並讓呼叫端拿到 OperationCanceledException。</summary>
    private void OnCallerCancelled(CallItem item)
    {
        lock (_lock)
        {
            if (item.Status != ItemStatus.Waiting || !_queue.Remove(item))
            {
                // 執行中（linked CTS 會處理）或已結束：不動作
                return;
            }

            item.Status = ItemStatus.Cancelled;
            item.Completion.SetCancelled(item.CallerToken);
        }
    }
}
