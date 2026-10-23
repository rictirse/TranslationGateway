namespace Translation.Bridge.Core.Tests.Lanes;

/// <summary>可手動推進時間的 TimeProvider，避免測試真的等待。
/// 支援 <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> 需要的計時器：
/// Advance 時觸發到期的回呼（虛擬時間）。放在 Lanes 子命名空間：ConversationSessionTests.cs
/// 已有同名的類別（不能改動該檔）。</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get => _utcNow; set => _utcNow = value; }

    public void Advance(TimeSpan time)
    {
        lock (_lock)
        {
            _utcNow += time;
        }

        // 在鎖外取快照觸發：回呼的接續可能建立或處置計時器（Task.Delay 完成時）
        ManualTimer[] timers;
        lock (_lock)
        {
            timers = _timers.ToArray();
        }

        foreach (var timer in timers)
        {
            timer.FireIfDue(GetUtcNow());
        }
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        lock (_lock)
        {
            timer.Initialize(_utcNow);
            _timers.Add(timer);
        }

        return timer;
    }

    private void Remove(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    /// <summary>以虛擬時間運作的計時器：到期的回呼在 Advance 時觸發；單次計時器觸發一次後不再重來。</summary>
    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly object _gate = new();
        private TimeSpan _dueTime;
        private TimeSpan _period;
        private DateTimeOffset _nextDue;
        private bool _stopped;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _dueTime = dueTime;
            _period = period;
        }

        /// <summary>註冊時依虛擬時間計算首次到期時間（owner 持鎖呼叫）。</summary>
        public void Initialize(DateTimeOffset now) => _nextDue = now + _dueTime;

        /// <summary>虛擬時間已推進到（或超過）到期時間時觸發回呼；週期計時器接著排下一次。</summary>
        public void FireIfDue(DateTimeOffset now)
        {
            while (true)
            {
                bool oneShot;
                lock (_gate)
                {
                    if (_stopped || now < _nextDue)
                    {
                        return;
                    }

                    oneShot = _period == Timeout.InfiniteTimeSpan;
                    if (oneShot)
                    {
                        _stopped = true;
                    }
                    else
                    {
                        _nextDue += _period;
                    }
                }

                _callback(_state);
                if (oneShot)
                {
                    return;
                }
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    return false;
                }

                _dueTime = dueTime;
                _period = period;
                _nextDue = _owner.GetUtcNow() + dueTime;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _stopped = true;
            }

            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
