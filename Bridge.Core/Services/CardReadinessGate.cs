namespace Translation.Bridge.Core.Services;

/// <summary>錄影開片的詞彙卡準備門（DI singleton）：一鍵開台套用詞彙卡、需要時啟動開台資料探勘時 <see cref="Begin"/>，
/// 探勘結束（或失敗）時 <see cref="Complete"/>；生成式字幕前處理與預先翻譯等模型工作等門打開（或逾時）才開始，
/// prompt 才會帶到新建的詞彙卡，不會先用沒有卡的版本寫進資料庫與快取。
/// 等待期間字幕推送與 PotPlayer 即時翻譯不受影響。</summary>
public sealed class CardReadinessGate
{
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private TaskCompletionSource _tcs = NewCompletion();
    private DateTimeOffset? _beganAt;

    public CardReadinessGate(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>開始等待：標記為等待中並重新計時。已在等待中時再呼叫會重新開始計時、換一個新的等待，
    /// 舊的等待視為完成（繼續往下跑）。</summary>
    public void Begin()
    {
        lock (_lock)
        {
            var old = _tcs;
            _tcs = NewCompletion();
            _beganAt = _timeProvider.GetUtcNow();
            old.TrySetResult();
        }
    }

    /// <summary>結束等待（探勘完成或失敗）：不再等待中，並讓等待者繼續。重複呼叫沒有副作用。</summary>
    public void Complete()
    {
        TaskCompletionSource tcs;
        lock (_lock)
        {
            _beganAt = null;
            tcs = _tcs;
        }

        tcs.TrySetResult();
    }

    /// <summary>目前是否在等待中（Begin 之後、Complete 之前）。</summary>
    public bool IsPending
    {
        get
        {
            lock (_lock)
            {
                return _beganAt is not null;
            }
        }
    }

    /// <summary>門是否已開：不在等待中，或已經等了超過（含等於）timeout。</summary>
    public bool IsOpen(TimeSpan timeout)
    {
        lock (_lock)
        {
            return _beganAt is not { } began || _timeProvider.GetUtcNow() - began >= timeout;
        }
    }

    /// <summary>等門打開：不在等待中時立刻回傳 true；否則等到 <see cref="Complete"/>（回傳 true）、
    /// 剩餘時間到了（回傳 false）或 ct 取消（擲 <see cref="OperationCanceledException"/>）。
    /// 剩餘時間 ≤ 0 時立刻回傳 false。</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task waitTask;
        DateTimeOffset began;
        lock (_lock)
        {
            if (_beganAt is not { } start)
            {
                return true;
            }

            began = start;
            waitTask = _tcs.Task;
        }

        var remaining = timeout - (_timeProvider.GetUtcNow() - began);
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        var delay = Task.Delay(remaining, _timeProvider, ct);
        var completed = await Task.WhenAny(waitTask, delay).ConfigureAwait(false);
        if (completed == waitTask)
        {
            return true;
        }

        ct.ThrowIfCancellationRequested();
        return false;
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
