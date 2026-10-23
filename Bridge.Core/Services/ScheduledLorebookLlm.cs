using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;

namespace Translation.Bridge.Core.Services;

/// <summary>知識庫模型的排程包裝：所有請求經過 LlmLane 排隊（一次只送一個、依優先度），
/// 優先度取自呼叫端的 LlmPriorityScope（沒有時為 Background）。
/// 有備援（fallback／fallbackLane／health 三者都傳）時，主要模型暫時性連續失敗被 LlmHealth 標記期間，
/// 知識庫工作改送翻譯模型的 lane（沿用原本的優先度：即時翻譯與預先翻譯仍排在前面）；
/// 直播中（allowFallback 回傳 false）不改送備援，等恢復。
/// 主要模型沒有設定（<paramref name="primaryConfigured"/> 回傳 false；null 視為已設定）時，
/// 一律直接改送備援——沒有主要模型可以試或等，不看健康狀態、不受 allowFallback 影響。</summary>
public sealed class ScheduledLorebookLlm(
    ILorebookLlm inner,
    LlmLane lane,
    ILorebookLlm? fallback = null,
    LlmLane? fallbackLane = null,
    LlmHealth? health = null,
    Func<bool>? allowFallback = null,
    TimeProvider? timeProvider = null,
    ILogger<ScheduledLorebookLlm>? logger = null,
    Func<bool>? primaryConfigured = null) : ILorebookLlm
{
    /// <summary>fallback、fallbackLane、health 三者都不是 null 時才啟用備援。</summary>
    private bool FallbackEnabled => fallback is not null && fallbackLane is not null && health is not null;

    /// <summary>時間來源（等待恢復的計時用）；沒有傳時用系統時間。</summary>
    private TimeProvider Time => timeProvider ?? TimeProvider.System;

    public async Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var call = LlmPriorityScope.Current ?? new LlmCallInfo(LlmPriority.Background);

        // 主要模型（知識庫模型）沒有設定：根本沒有可以試或等的對象，直接改送備援（翻譯模型，沿用原本的優先度）；
        // 不看健康狀態、不記錄成功或失敗，也不受 allowFallback 影響
        if (fallback is not null && fallbackLane is not null && primaryConfigured is not null && !primaryConfigured())
        {
            return await RunFallbackAsync(systemPrompt, userPrompt, call.Priority, cancellationToken).ConfigureAwait(false);
        }

        if (FallbackEnabled && !health!.IsAvailable)
        {
            // 標記期間：允許備援時直接走翻譯模型（沿用原本的優先度）；直播中不借用，等恢復再送
            if (allowFallback?.Invoke() ?? true)
            {
                return await RunFallbackAsync(systemPrompt, userPrompt, call.Priority, cancellationToken).ConfigureAwait(false);
            }

            return await WaitForRecoveryAsync(systemPrompt, userPrompt, call, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var result = await lane.RunAsync(call.Priority,
                t => inner.CompleteJsonAsync(systemPrompt, userPrompt, t), cancellationToken, call).ConfigureAwait(false);
            health?.RecordSuccess();
            return result;
        }
        catch (LorebookLlmException ex) when (ex.IsTransient)
        {
            health?.RecordFailure();
            // 這次失敗剛好達到門檻時：允許備援就改送翻譯模型（結果直接交給呼叫端）；直播中不借用，等恢復再送
            if (FallbackEnabled && !health!.IsAvailable)
            {
                if (allowFallback?.Invoke() ?? true)
                {
                    return await RunFallbackAsync(systemPrompt, userPrompt, call.Priority, cancellationToken).ConfigureAwait(false);
                }

                return await WaitForRecoveryAsync(systemPrompt, userPrompt, call, cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>直播中（allowFallback 回傳 false）被標記時的等待恢復流程：不借用翻譯模型，
    /// 每次醒來重新判斷——allowFallback 變成 true（例如換成錄影）改送備援；模型可用就試原模型
    /// （暫時性失敗會再標記、繼續等下一輪；其他例外照樣往外拋）；取消令牌觸發時擲 OperationCanceledException。</summary>
    private async Task<LlmJsonResult> WaitForRecoveryAsync(string systemPrompt, string userPrompt, LlmCallInfo call, CancellationToken cancellationToken)
    {
        using var waiting = health!.BeginWaiting();
        logger?.LogInformation("知識庫模型暫時無法使用，直播中不借用翻譯模型，等恢復後再送（{DownUntil:HH:mm:ss}）",
            health.DownUntil?.LocalDateTime);
        while (true)
        {
            await Task.Delay(WaitDelay(), Time, cancellationToken).ConfigureAwait(false);

            if (allowFallback?.Invoke() ?? true)
            {
                // 等待中換成允許備援（例如換成錄影）：改走翻譯模型
                return await RunFallbackAsync(systemPrompt, userPrompt, call.Priority, cancellationToken).ConfigureAwait(false);
            }

            if (!health.IsAvailable)
            {
                continue;
            }

            try
            {
                var result = await lane.RunAsync(call.Priority,
                    t => inner.CompleteJsonAsync(systemPrompt, userPrompt, t), cancellationToken, call).ConfigureAwait(false);
                health.RecordSuccess();
                return result;
            }
            catch (LorebookLlmException ex) when (ex.IsTransient)
            {
                health.RecordFailure();
                if (!health.IsAvailable)
                {
                    // 再被標記：繼續等下一輪
                    continue;
                }

                throw;
            }
        }
    }

    /// <summary>等待恢復的單次睡眠時間：min(DownUntil - now, 30 秒)，最少 1 秒（DownUntil 缺漏時固定 30 秒）。</summary>
    private TimeSpan WaitDelay()
    {
        if (health!.DownUntil is not { } until)
        {
            return TimeSpan.FromSeconds(30);
        }

        var remaining = until - Time.GetUtcNow();
        if (remaining <= TimeSpan.FromSeconds(1))
        {
            return TimeSpan.FromSeconds(1);
        }

        return remaining > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : remaining;
    }

    /// <summary>改走備援：新建一個 LlmCallInfo（沿用原本的優先度），避免和主要 lane 的 call 互相干擾。</summary>
    private Task<LlmJsonResult> RunFallbackAsync(string systemPrompt, string userPrompt, LlmPriority priority, CancellationToken cancellationToken) =>
        fallbackLane!.RunAsync(priority, t => fallback!.CompleteJsonAsync(systemPrompt, userPrompt, t), cancellationToken, new LlmCallInfo(priority));

    /// <summary>一律測主要模型（設定頁的測試連線是測原模型），不影響健康狀態；
    /// 主要模型有沒有設定（primaryConfigured）不影響這裡的行為。</summary>
    public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        lane.RunAsync(LlmPriority.Mining, t => inner.TestConnectionAsync(t), cancellationToken);
}
