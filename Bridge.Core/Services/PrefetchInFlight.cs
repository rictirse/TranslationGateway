namespace Translation.Bridge.Core.Services;

/// <summary>正在執行的預先翻譯批次（同一時間最多一批）：即時翻譯要翻的句子在批次裡時，等批次完成後改查快取。
/// 獨立成類別讓 TranslationManager 不必依賴 VodPrefetcher（VodPrefetcher 已依賴 TranslationManager，直接依賴會循環）。</summary>
public sealed class PrefetchInFlight
{
    private readonly object _lock = new();

    /// <summary>目前這批的 key（修正後原文）；沒有批次在跑時為 null。</summary>
    private HashSet<string>? _keys;

    /// <summary>目前這批的結束訊號：新批次開始或 handle.Dispose 時設為完成（一律正常完成，不擲例外、不取消）。</summary>
    private TaskCompletionSource _completion = Create();

    private static TaskCompletionSource Create() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>開始一批：記下這批的 key（修正後原文），回傳結束時要 Dispose 的 handle。
    /// 已有一批時，舊的那批視為結束，等待中的呼叫端會被放行。</summary>
    public IDisposable Begin(IReadOnlyCollection<string> keys)
    {
        TaskCompletionSource? previous;
        TaskCompletionSource completion;
        lock (_lock)
        {
            previous = _completion;
            _keys = new HashSet<string>(keys, StringComparer.Ordinal);
            completion = _completion = Create();
        }

        previous.TrySetResult();
        return new BatchHandle(this, completion);
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
    /// 並把它的結束訊號設為完成；重複呼叫沒有副作用。</summary>
    private void End(TaskCompletionSource completion)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_completion, completion))
            {
                _keys = null;
            }
        }

        completion.TrySetResult();
    }

    /// <summary>批次的結束 handle：Dispose 時結束這批（成功或失敗都算），放行等待中的即時翻譯。</summary>
    private sealed class BatchHandle(PrefetchInFlight owner, TaskCompletionSource completion) : IDisposable
    {
        public void Dispose() => owner.End(completion);
    }
}
