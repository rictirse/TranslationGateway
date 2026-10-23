using Microsoft.Extensions.Logging;

namespace Translation.Bridge.Core.Services;

/// <summary>模型的健康狀態：連續失敗達門檻時標記「不能用」一段時間（例如知識庫模型的 Z.AI 連不上時，
/// 期間知識庫工作改送翻譯模型）；時間到後不算恢復，而是讓下一個請求先試原模型（成功才真正恢復）。</summary>
public sealed class LlmHealth(TimeProvider timeProvider, ILogger<LlmHealth> logger, int failureThreshold = 3, TimeSpan? downDuration = null)
{
    private static readonly TimeSpan DefaultDownDuration = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _downUntil;
    private int _waiters;

    /// <summary>目前是否可用：沒有被標記，或已經過 DownUntil（「試試看」狀態：先讓請求回去用原模型）。</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                return _downUntil is not { } until || timeProvider.GetUtcNow() >= until;
            }
        }
    }

    /// <summary>被標記「不能用」的截止時間；沒有被標記時為 null（已過時間但尚未成功前仍保留舊值）。</summary>
    public DateTimeOffset? DownUntil
    {
        get
        {
            lock (_lock)
            {
                return _downUntil;
            }
        }
    }

    /// <summary>健康狀態變化（標記／恢復）時觸發；在 lock 外觸發。</summary>
    public event Action? Changed;

    /// <summary>目前有請求在等這個模型恢復。</summary>
    public bool HasWaiters
    {
        get
        {
            lock (_lock)
            {
                return _waiters > 0;
            }
        }
    }

    /// <summary>開始等待這個模型恢復；Dispose 結束等待。等待數從 0 變 1、從 1 變 0 時觸發 <see cref="Changed"/>（在 lock 外觸發）。</summary>
    public IDisposable BeginWaiting()
    {
        bool first;
        Action? changed;
        lock (_lock)
        {
            first = _waiters == 0;
            _waiters++;
            changed = Changed;
        }

        if (first)
        {
            changed?.Invoke();
        }

        return new WaitingHandle(this);
    }

    /// <summary>結束一次等待（WaitingHandle.Dispose 呼叫）：等待數減 1，從 1 變 0 時觸發 Changed（在 lock 外觸發）。</summary>
    private void EndWaiting()
    {
        bool last;
        Action? changed;
        lock (_lock)
        {
            _waiters--;
            last = _waiters == 0;
            changed = Changed;
        }

        if (last)
        {
            changed?.Invoke();
        }
    }

    /// <summary>一次等待的代表物件；Dispose 重複呼叫只結束一次。</summary>
    private sealed class WaitingHandle(LlmHealth owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndWaiting();
            }
        }
    }

    /// <summary>原模型成功：連續失敗次數歸零、解除標記。之前是被標記的狀態時記 log 並觸發 <see cref="Changed"/>。</summary>
    public void RecordSuccess()
    {
        bool wasMarked;
        Action? changed;
        lock (_lock)
        {
            wasMarked = _downUntil is not null;
            changed = Changed;
            _consecutiveFailures = 0;
            _downUntil = null;
        }

        if (wasMarked)
        {
            logger.LogInformation("知識庫模型恢復，改回原本的模型");
            changed?.Invoke();
        }
    }

    /// <summary>原模型失敗：連續失敗次數 +1；次數達門檻時標記 DownUntil = now + downDuration、
    /// 記 Warning log 並觸發 <see cref="Changed"/>（「試試看」時失敗次數仍 ≥ 門檻，會再標記一次）。</summary>
    public void RecordFailure()
    {
        bool marked;
        int count;
        int minutes;
        Action? changed;
        lock (_lock)
        {
            _consecutiveFailures++;
            count = _consecutiveFailures;
            minutes = (int)(downDuration ?? DefaultDownDuration).TotalMinutes;
            changed = Changed;
            marked = count >= failureThreshold;
            if (marked)
            {
                _downUntil = timeProvider.GetUtcNow() + (downDuration ?? DefaultDownDuration);
            }
        }

        if (marked)
        {
            logger.LogWarning("知識庫模型連續失敗 {Count} 次，{Minutes} 分鐘內改用翻譯模型", count, minutes);
            changed?.Invoke();
        }
    }
}
