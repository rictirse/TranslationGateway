using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests.Lanes;

/// <summary>ScheduledLorebookLlm：經 LlmLane 排隊，優先度取自 LlmPriorityScope（沒有時為 Background）。</summary>
public class ScheduledLorebookLlmTests
{
    private static LlmLane CreateLane() =>
        new("測試車道", () => 100, new ManualTimeProvider(), NullLogger<LlmLane>.Instance);

    /// <summary>輪詢直到條件成立（逾時 5 秒視為失敗）。</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等候條件逾時");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>記錄呼叫順序、以閘門控制何時完成的假知識庫模型。</summary>
    private sealed class GatedLorebookLlm : ILorebookLlm
    {
        public List<string> Users { get; } = [];

        public Queue<TaskCompletionSource> Gates { get; } = new();

        public int TestCount { get; private set; }

        public async Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            Users.Add(userPrompt);
            if (Gates.TryDequeue(out var gate))
            {
                await gate.Task;
            }

            return new LlmJsonResult("{}", 1, 1, 1, "fake-model");
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            TestCount++;
            return Task.FromResult((true, "連線成功"));
        }
    }

    // 在 LlmPriorityScope.Enter(Mining) 裡呼叫：請求排在 Background 前面。
    [Fact]
    public async Task CompleteJsonAsync_InsideMiningScope_RunsBeforeBackground()
    {
        var inner = new GatedLorebookLlm();
        var lane = CreateLane();
        var sut = new ScheduledLorebookLlm(inner, lane);
        TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateA = Gate();
        var gateB = Gate();
        var gateC = Gate();
        inner.Gates.Enqueue(gateA);
        inner.Gates.Enqueue(gateB);
        inner.Gates.Enqueue(gateC);

        Task<LlmJsonResult> a, b, c;
        using (LlmPriorityScope.Enter(LlmPriority.Background))
        {
            a = sut.CompleteJsonAsync("sys", "a", CancellationToken.None);
        }

        using (LlmPriorityScope.Enter(LlmPriority.Mining))
        {
            b = sut.CompleteJsonAsync("sys", "b", CancellationToken.None);
        }

        using (LlmPriorityScope.Enter(LlmPriority.Background))
        {
            c = sut.CompleteJsonAsync("sys", "c", CancellationToken.None);
        }

        gateA.SetResult();
        gateB.SetResult();
        gateC.SetResult();
        await Task.WhenAll(a, b, c);

        // a 先到先跑；釋放後 b（Mining）在 c（Background）之前
        Assert.Equal(["a", "b", "c"], inner.Users);
    }

    // 沒有 scope 時算 Background：排在 Mining 之後。
    [Fact]
    public async Task CompleteJsonAsync_WithoutScope_CountsAsBackground()
    {
        var inner = new GatedLorebookLlm();
        var lane = CreateLane();
        var sut = new ScheduledLorebookLlm(inner, lane);
        var releaseOccupier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 先占住執行位置
        var occupier = lane.RunAsync<string>(LlmPriority.Background, async ct =>
        {
            await releaseOccupier.Task;
            return "occupier";
        }, CancellationToken.None);

        Task<LlmJsonResult> x, y;
        x = sut.CompleteJsonAsync("sys", "x", CancellationToken.None); // 沒有 scope
        using (LlmPriorityScope.Enter(LlmPriority.Mining))
        {
            y = sut.CompleteJsonAsync("sys", "y", CancellationToken.None);
        }

        releaseOccupier.SetResult();
        await Task.WhenAll(occupier, x, y);

        // x 是 Background：y（Mining）先執行
        Assert.Equal(["y", "x"], inner.Users);
    }

    // TestConnectionAsync 經過 lane：佔著執行位置時不會碰到 inner，釋放後才執行。
    [Fact]
    public async Task TestConnectionAsync_GoesThroughLane()
    {
        var inner = new GatedLorebookLlm();
        var lane = CreateLane();
        var sut = new ScheduledLorebookLlm(inner, lane);
        var releaseOccupier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var occupier = lane.RunAsync<string>(LlmPriority.Background, async ct =>
        {
            await releaseOccupier.Task;
            return "occupier";
        }, CancellationToken.None);

        var test = sut.TestConnectionAsync(CancellationToken.None);
        Assert.Equal(0, inner.TestCount);

        releaseOccupier.SetResult();
        var (success, message) = await test;
        await occupier;

        Assert.True(success);
        Assert.Equal("連線成功", message);
        Assert.Equal(1, inner.TestCount);
    }

    /// <summary>前 transientFailures 次擲暫時性例外、之後成功的假主要模型。</summary>
    private sealed class FlakyLorebookLlm(int transientFailures) : ILorebookLlm
    {
        public int CallCount { get; private set; }

        public Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount <= transientFailures)
            {
                throw new LorebookLlmException($"知識庫模型連線失敗（第 {CallCount} 次）", isTransient: true);
            }

            return Task.FromResult(new LlmJsonResult("{\"from\":\"primary\"}", 1, 1, 1, "primary"));
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }

    /// <summary>一律擲非暫時性例外（回應格式不正確）的假主要模型。</summary>
    private sealed class BrokenFormatLlm : ILorebookLlm
    {
        public int CallCount { get; private set; }

        public Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new LorebookLlmException("知識庫模型回應格式不正確");
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }

    /// <summary>記錄呼叫順序、固定回覆的假備援模型（翻譯模型）。</summary>
    private sealed class StubFallbackLlm : ILorebookLlm
    {
        public List<string> Users { get; } = [];

        public Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            Users.Add(userPrompt);
            return Task.FromResult(new LlmJsonResult("{\"from\":\"fallback\"}", 2, 2, 2, "fallback"));
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }

    private static (ScheduledLorebookLlm Sut, LlmLane Lane, LlmLane FallbackLane, ManualTimeProvider Time, LlmHealth Health) CreateWithFallback(
        ILorebookLlm inner, StubFallbackLlm fallback, Func<bool>? allowFallback = null, Func<bool>? primaryConfigured = null)
    {
        var lane = CreateLane();
        var fallbackLane = CreateLane();
        var time = new ManualTimeProvider();
        var health = new LlmHealth(time, NullLogger<LlmHealth>.Instance);
        return (new ScheduledLorebookLlm(inner, lane, fallback, fallbackLane, health, allowFallback, time, primaryConfigured: primaryConfigured), lane, fallbackLane, time, health);
    }

    // 主要模型連續 3 次擲暫時性例外：前兩次呼叫端拿到例外，第 3 次改走 fallback 並拿到結果。
    [Fact]
    public async Task CompleteJsonAsync_TransientFailures_ThirdCallFallsBack()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 3);
        var fallback = new StubFallbackLlm();
        var (sut, _, _, _, health) = CreateWithFallback(inner, fallback);

        await Assert.ThrowsAsync<LorebookLlmException>(() => sut.CompleteJsonAsync("sys", "1", CancellationToken.None));
        await Assert.ThrowsAsync<LorebookLlmException>(() => sut.CompleteJsonAsync("sys", "2", CancellationToken.None));
        Assert.True(health.IsAvailable);

        var third = await sut.CompleteJsonAsync("sys", "3", CancellationToken.None);

        Assert.Equal("fallback", third.Model);
        Assert.Equal(3, inner.CallCount);
        Assert.Equal(["3"], fallback.Users);
        Assert.False(health.IsAvailable);
    }

    // 不可用期間：直接走 fallback，主要模型沒有被呼叫。
    [Fact]
    public async Task CompleteJsonAsync_WhileUnavailable_GoesStraightToFallback()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 0);
        var fallback = new StubFallbackLlm();
        var (sut, _, _, _, health) = CreateWithFallback(inner, fallback);
        health.RecordFailure();
        health.RecordFailure();
        health.RecordFailure();

        var result = await sut.CompleteJsonAsync("sys", "x", CancellationToken.None);

        Assert.Equal("fallback", result.Model);
        Assert.Equal(0, inner.CallCount);
        Assert.Equal(["x"], fallback.Users);
    }

    // 時間前進 5 分鐘後（試試看）：先試主要模型，成功就恢復。
    [Fact]
    public async Task CompleteJsonAsync_AfterDownDuration_RetriesPrimaryAndRecovers()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 3);
        var fallback = new StubFallbackLlm();
        var (sut, _, _, time, health) = CreateWithFallback(inner, fallback);
        // 前三次失敗（第三次走 fallback），標記 5 分鐘
        await Assert.ThrowsAsync<LorebookLlmException>(() => sut.CompleteJsonAsync("sys", "1", CancellationToken.None));
        await Assert.ThrowsAsync<LorebookLlmException>(() => sut.CompleteJsonAsync("sys", "2", CancellationToken.None));
        await sut.CompleteJsonAsync("sys", "3", CancellationToken.None);
        Assert.False(health.IsAvailable);

        time.Advance(TimeSpan.FromMinutes(5));
        var result = await sut.CompleteJsonAsync("sys", "after", CancellationToken.None);

        Assert.Equal("primary", result.Model);
        Assert.Equal(4, inner.CallCount);
        Assert.Single(fallback.Users);
        Assert.True(health.IsAvailable);
        Assert.Null(health.DownUntil);
    }

    // 非暫時性例外（回應格式不正確）不計入失敗次數：三次都原樣往外拋、不走 fallback。
    [Fact]
    public async Task CompleteJsonAsync_NonTransientException_DoesNotAffectHealth()
    {
        var inner = new BrokenFormatLlm();
        var fallback = new StubFallbackLlm();
        var (sut, _, _, _, health) = CreateWithFallback(inner, fallback);

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<LorebookLlmException>(() => sut.CompleteJsonAsync("sys", $"{i}", CancellationToken.None));
        }

        Assert.Equal(3, inner.CallCount);
        Assert.Empty(fallback.Users);
        Assert.True(health.IsAvailable);
        Assert.Null(health.DownUntil);
    }

    // 改走備援時沿用原本的優先度：不可用期間 Mining 的請求排在 Background 之前（fallback lane 內排序）。
    [Fact]
    public async Task CompleteJsonAsync_FallbackKeepsCallerPriority()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 0);
        var fallback = new StubFallbackLlm();
        var (sut, _, fallbackLane, _, health) = CreateWithFallback(inner, fallback);
        health.RecordFailure();
        health.RecordFailure();
        health.RecordFailure();

        var releaseOccupier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupier = fallbackLane.RunAsync<string>(LlmPriority.Background, async ct =>
        {
            await releaseOccupier.Task;
            return "occupier";
        }, CancellationToken.None);

        Task<LlmJsonResult> background, mining;
        background = sut.CompleteJsonAsync("sys", "bg", CancellationToken.None); // 沒有 scope → Background
        using (LlmPriorityScope.Enter(LlmPriority.Mining))
        {
            mining = sut.CompleteJsonAsync("sys", "mining", CancellationToken.None);
        }

        releaseOccupier.SetResult();
        await Task.WhenAll(occupier, background, mining);

        Assert.Equal(["mining", "bg"], fallback.Users);
    }

    // ===== 直播中不借用翻譯模型（allowFallback 回傳 false）：等原模型恢復 =====

    /// <summary>標記三次失敗（門檻 3）的共用前置。</summary>
    private static void MarkUnhealthy(LlmHealth health)
    {
        health.RecordFailure();
        health.RecordFailure();
        health.RecordFailure();
        Assert.False(health.IsAvailable);
    }

    // 被標記且直播中：不走備援、HasWaiters 是 true；時間推過 DownUntil 後再試原模型，成功就回傳結果。
    [Fact]
    public async Task CompleteJsonAsync_WaitingForRecovery_RetriesPrimaryAfterDownUntil()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 0);
        var fallback = new StubFallbackLlm();
        var (sut, _, _, time, health) = CreateWithFallback(inner, fallback, allowFallback: () => false);
        MarkUnhealthy(health);

        var task = sut.CompleteJsonAsync("sys", "x", CancellationToken.None);
        await UntilAsync(() => health.HasWaiters);
        Assert.Empty(fallback.Users);

        time.Advance(TimeSpan.FromMinutes(5));
        var result = await task;

        Assert.Equal("primary", result.Model);
        Assert.Equal(1, inner.CallCount);
        Assert.Empty(fallback.Users);
        Assert.False(health.HasWaiters);
        Assert.True(health.IsAvailable);
    }

    // 等待中原模型又暫時性失敗：再被標記、繼續等（不走備援）；下一輪恢復後成功回傳。
    [Fact]
    public async Task CompleteJsonAsync_WaitingTransientFailure_KeepsWaitingWithoutFallback()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 1);
        var fallback = new StubFallbackLlm();
        var (sut, _, _, time, health) = CreateWithFallback(inner, fallback, allowFallback: () => false);
        MarkUnhealthy(health);
        var firstDownUntil = health.DownUntil;

        var task = sut.CompleteJsonAsync("sys", "x", CancellationToken.None);
        await UntilAsync(() => health.HasWaiters);

        // DownUntil 到期 → 試原模型 → 暫時性失敗（第 1 次）→ 再標記 5 分鐘、繼續等
        time.Advance(TimeSpan.FromMinutes(5));
        await UntilAsync(() => health.DownUntil != firstDownUntil);
        Assert.False(health.IsAvailable);
        Assert.True(health.HasWaiters);
        Assert.Empty(fallback.Users);

        // 新的 DownUntil 到期 → 再試 → 成功
        time.Advance(TimeSpan.FromMinutes(5));
        var result = await task;

        Assert.Equal("primary", result.Model);
        Assert.Equal(2, inner.CallCount);
        Assert.Empty(fallback.Users);
        Assert.False(health.HasWaiters);
    }

    // 等待中 allowFallback 變成 true（例如換成錄影）：下一次醒來改送備援。
    [Fact]
    public async Task CompleteJsonAsync_WaitingAllowFallbackBecomesTrue_SwitchesToFallback()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 0);
        var fallback = new StubFallbackLlm();
        var allow = false;
        var (sut, _, _, time, health) = CreateWithFallback(inner, fallback, allowFallback: () => allow);
        MarkUnhealthy(health);

        var task = sut.CompleteJsonAsync("sys", "x", CancellationToken.None);
        await UntilAsync(() => health.HasWaiters);

        allow = true;
        // 還沒到 DownUntil，但等最多 30 秒就會醒來重新判斷
        time.Advance(TimeSpan.FromSeconds(30));
        var result = await task;

        Assert.Equal("fallback", result.Model);
        Assert.Equal(0, inner.CallCount);
        Assert.Equal(["x"], fallback.Users);
        Assert.False(health.HasWaiters);
    }

    // 等待中取消 CancellationToken：擲 OperationCanceledException，HasWaiters 變回 false，不走備援。
    [Fact]
    public async Task CompleteJsonAsync_WaitingCancelled_ThrowsAndStopsWaiting()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 0);
        var fallback = new StubFallbackLlm();
        var (sut, _, _, time, health) = CreateWithFallback(inner, fallback, allowFallback: () => false);
        MarkUnhealthy(health);

        var cts = new CancellationTokenSource();
        var task = sut.CompleteJsonAsync("sys", "x", cts.Token);
        await UntilAsync(() => health.HasWaiters);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.False(health.HasWaiters);
        Assert.Empty(fallback.Users);
        Assert.Equal(0, inner.CallCount);
    }

    // 這次失敗剛好達到門檻而且直播中：進入等待恢復，不改送備援。
    [Fact]
    public async Task CompleteJsonAsync_ThresholdReachedWhileLive_WaitsInsteadOfFallingBack()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 3);
        var fallback = new StubFallbackLlm();
        var (sut, _, _, time, health) = CreateWithFallback(inner, fallback, allowFallback: () => false);

        await Assert.ThrowsAsync<LorebookLlmException>(() => sut.CompleteJsonAsync("sys", "1", CancellationToken.None));
        await Assert.ThrowsAsync<LorebookLlmException>(() => sut.CompleteJsonAsync("sys", "2", CancellationToken.None));

        // 第 3 次失敗剛好達到門檻：不改送備援，進入等待
        var third = sut.CompleteJsonAsync("sys", "3", CancellationToken.None);
        await UntilAsync(() => health.HasWaiters);
        Assert.True(health.HasWaiters);
        Assert.Empty(fallback.Users);

        time.Advance(TimeSpan.FromMinutes(5));
        var result = await third;

        Assert.Equal("primary", result.Model);
        Assert.Equal(4, inner.CallCount);
        Assert.Empty(fallback.Users);
        Assert.False(health.HasWaiters);
    }

    // allowFallback 回傳 true：照現在的行為直接走備援（錄影的備援不變）。
    [Fact]
    public async Task CompleteJsonAsync_AllowFallbackTrue_GoesStraightToFallback()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 0);
        var fallback = new StubFallbackLlm();
        var (sut, _, _, _, health) = CreateWithFallback(inner, fallback, allowFallback: () => true);
        MarkUnhealthy(health);

        var result = await sut.CompleteJsonAsync("sys", "x", CancellationToken.None);

        Assert.Equal("fallback", result.Model);
        Assert.Equal(0, inner.CallCount);
        Assert.Equal(["x"], fallback.Users);
        Assert.False(health.HasWaiters);
    }

    // ===== 主要模型未設定（primaryConfigured 回傳 false）：直接改送備援 =====

    // 未設定時不看健康、不記錄成敗：直接走備援，主要模型沒有被呼叫，健康狀態維持可用。
    [Fact]
    public async Task CompleteJsonAsync_PrimaryNotConfigured_GoesStraightToFallbackWithoutTouchingHealth()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 0);
        var fallback = new StubFallbackLlm();
        var (sut, _, _, _, health) = CreateWithFallback(inner, fallback, primaryConfigured: () => false);

        var result = await sut.CompleteJsonAsync("sys", "x", CancellationToken.None);

        Assert.Equal("fallback", result.Model);
        Assert.Equal(0, inner.CallCount);
        Assert.Equal(["x"], fallback.Users);
        Assert.True(health.IsAvailable);
        Assert.Null(health.DownUntil);
    }

    // 未設定而且直播中（allowFallback 回傳 false）：仍直接走備援——沒有主要模型可以試或等，不進入等待恢復；
    // 即使健康狀態已被標記也不影響。
    [Fact]
    public async Task CompleteJsonAsync_PrimaryNotConfiguredWhileLive_StillGoesStraightToFallback()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 0);
        var fallback = new StubFallbackLlm();
        var (sut, _, _, _, health) = CreateWithFallback(inner, fallback, allowFallback: () => false, primaryConfigured: () => false);
        MarkUnhealthy(health);

        var result = await sut.CompleteJsonAsync("sys", "x", CancellationToken.None);

        Assert.Equal("fallback", result.Model);
        Assert.Equal(0, inner.CallCount);
        Assert.Equal(["x"], fallback.Users);
        Assert.False(health.HasWaiters);
        Assert.False(health.IsAvailable);
    }

    // primaryConfigured 回傳 true：照既有行為走主要模型。
    [Fact]
    public async Task CompleteJsonAsync_PrimaryConfiguredTrue_UsesPrimary()
    {
        var inner = new FlakyLorebookLlm(transientFailures: 0);
        var fallback = new StubFallbackLlm();
        var (sut, _, _, _, _) = CreateWithFallback(inner, fallback, primaryConfigured: () => true);

        var result = await sut.CompleteJsonAsync("sys", "x", CancellationToken.None);

        Assert.Equal("primary", result.Model);
        Assert.Equal(1, inner.CallCount);
        Assert.Empty(fallback.Users);
    }
}
