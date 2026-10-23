using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests.Lanes;

/// <summary>LlmLane：單一執行位置的排程器（一次只執行一個、依優先度排序、插隊門檻取消重排）。</summary>
public class LlmLaneTests
{
    private static LlmLane CreateLane(ManualTimeProvider time, int threshold = 80) =>
        new("測試車道", () => threshold, time, NullLogger<LlmLane>.Instance);

    /// <summary>輪詢直到條件成立（逾時 5 秒視為失敗）：不用固定的 Delay 等背景工作。</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等候條件逾時");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // 1. 沒有耗時紀錄不插隊：A（Background）執行中依序加入 B（PreprocessBackfill）、C（Mining）、D（PreprocessNear），
    //    A 完成後的執行順序是 C、D、B。
    [Fact]
    public async Task RunAsync_NoDurationRecords_OrdersByPriorityThenSequence()
    {
        var lane = CreateLane(new ManualTimeProvider());
        var order = new List<string>();
        var gate = new object();
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Work(string name, CancellationToken ct)
        {
            lock (gate)
            {
                order.Add(name);
            }

            if (name == "A")
            {
                await releaseA.Task;
            }

            return name;
        }

        var a = lane.RunAsync(LlmPriority.Background, ct => Work("A", ct), CancellationToken.None);
        // A 已開始執行（work 的同步段落已加入 order）
        Assert.Equal(["A"], order);

        var b = lane.RunAsync(LlmPriority.PreprocessBackfill, ct => Work("B", ct), CancellationToken.None);
        var c = lane.RunAsync(LlmPriority.Mining, ct => Work("C", ct), CancellationToken.None);
        var d = lane.RunAsync(LlmPriority.PreprocessNear, ct => Work("D", ct), CancellationToken.None);

        releaseA.SetResult();
        await Task.WhenAll(a, b, c, d);

        Assert.Equal(["A", "C", "D", "B"], order);
        Assert.Equal("A", a.Result);
        Assert.Equal("C", c.Result);
        Assert.Equal("D", d.Result);
        Assert.Equal("B", b.Result);
    }

    // 2. 同優先度先進先出。
    [Fact]
    public async Task RunAsync_SamePriority_RunsInFifoOrder()
    {
        var lane = CreateLane(new ManualTimeProvider());
        var order = new List<string>();
        var gate = new object();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Work(string name, CancellationToken ct)
        {
            lock (gate)
            {
                order.Add(name);
            }

            if (name == "first")
            {
                await releaseFirst.Task;
            }

            return name;
        }

        var first = lane.RunAsync(LlmPriority.Mining, ct => Work("first", ct), CancellationToken.None);
        var second = lane.RunAsync(LlmPriority.Mining, ct => Work("second", ct), CancellationToken.None);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["first", "second"], order);
    }

    // 3. 任何時候同時執行中的 work 最多 1 個。
    [Fact]
    public async Task RunAsync_ManyRequests_RunsAtMostOneAtATime()
    {
        var lane = CreateLane(new ManualTimeProvider());
        var running = 0;
        var maxRunning = 0;
        var gate = new object();

        async Task<string> Work(int index, CancellationToken ct)
        {
            lock (gate)
            {
                running++;
                maxRunning = Math.Max(maxRunning, running);
            }

            await Task.Delay(20, ct);
            lock (gate)
            {
                running--;
            }

            return index.ToString();
        }

        var priorities = new[]
        {
            LlmPriority.Background, LlmPriority.Mining, LlmPriority.PreprocessNear,
            LlmPriority.PreprocessBackfill, LlmPriority.Mining,
        };
        var tasks = priorities.Select((p, i) => lane.RunAsync(p, ct => Work(i, ct), CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, maxRunning);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => i.ToString()), tasks.Select(t => t.Result));
    }

    // 4. 插隊：X（Background）執行 3 秒（30%）時加入 M（Mining），X 被取消；M 執行完後 X 的 work 再被呼叫一次，
    //    X 的呼叫端拿到第二次的結果。
    [Fact]
    public async Task RunAsync_PreemptBelowThreshold_CancelsAndRerunsKeptSequence()
    {
        var time = new ManualTimeProvider();
        var lane = CreateLane(time);
        lane.RecordDuration(LlmPriority.Background, TimeSpan.FromSeconds(10));
        var xCalls = 0;

        async Task<string> X(CancellationToken ct)
        {
            var n = ++xCalls;
            if (n == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return $"x-{n}";
        }

        var x = lane.RunAsync(LlmPriority.Background, X, CancellationToken.None);
        await UntilAsync(() => xCalls == 1);
        time.Advance(TimeSpan.FromSeconds(3)); // 預估進度 30% < 80%

        var m = lane.RunAsync(LlmPriority.Mining, ct => Task.FromResult("m"), CancellationToken.None);
        await UntilAsync(() => xCalls == 2);

        Assert.Equal("m", await m);
        Assert.Equal("x-2", await x);
        Assert.Equal(2, xCalls);
    }

    // 5. 不插隊：X 執行 9 秒（90% ≥ 門檻）時加入 M，X 沒被取消，M 等 X 完成才執行。
    [Fact]
    public async Task RunAsync_PreemptAtOrAboveThreshold_LetsRunningFinish()
    {
        var time = new ManualTimeProvider();
        var lane = CreateLane(time);
        lane.RecordDuration(LlmPriority.Background, TimeSpan.FromSeconds(10));
        var xCalls = 0;
        var mCalls = 0;
        var releaseX = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> X(CancellationToken ct)
        {
            xCalls++;
            await releaseX.Task.WaitAsync(ct);
            return "x";
        }

        Task<string> M(CancellationToken ct)
        {
            mCalls++;
            return Task.FromResult("m");
        }

        var x = lane.RunAsync(LlmPriority.Background, X, CancellationToken.None);
        await UntilAsync(() => xCalls == 1);
        time.Advance(TimeSpan.FromSeconds(9)); // 預估進度 90% ≥ 80%

        var m = lane.RunAsync(LlmPriority.Mining, M, CancellationToken.None);
        // X 還在跑而且沒有被取消：M 尚未開始
        var settle = DateTime.UtcNow.AddMilliseconds(150);
        while (DateTime.UtcNow < settle)
        {
            Assert.Equal(0, mCalls);
            Assert.False(x.IsCompleted);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        releaseX.SetResult();
        Assert.Equal("m", await m);
        Assert.Equal("x", await x);
        Assert.Equal(1, xCalls);
    }

    // 6. 門檻 0：從不插隊。
    [Fact]
    public async Task RunAsync_ThresholdZero_NeverPreempts()
    {
        var time = new ManualTimeProvider();
        var lane = CreateLane(time, threshold: 0);
        lane.RecordDuration(LlmPriority.Background, TimeSpan.FromSeconds(10));
        var xCalls = 0;
        var mCalls = 0;
        var releaseX = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> X(CancellationToken ct)
        {
            xCalls++;
            await releaseX.Task.WaitAsync(ct);
            return "x";
        }

        var x = lane.RunAsync(LlmPriority.Background, X, CancellationToken.None);
        await UntilAsync(() => xCalls == 1);
        time.Advance(TimeSpan.FromSeconds(3)); // 30%，但門檻 0：不插隊

        var m = lane.RunAsync(LlmPriority.Mining, ct => Task.FromResult("m"), CancellationToken.None);
        var settle = DateTime.UtcNow.AddMilliseconds(150);
        while (DateTime.UtcNow < settle)
        {
            Assert.Equal(0, mCalls);
            Assert.False(x.IsCompleted);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        releaseX.SetResult();
        Assert.Equal("m", await m);
        Assert.Equal("x", await x);
        Assert.Equal(1, xCalls);
    }

    // 7. 等待中的請求被呼叫端取消：拿到 OperationCanceledException，work 沒被呼叫。
    [Fact]
    public async Task RunAsync_WaitingCancelledByCaller_ThrowsWithoutCallingWork()
    {
        var lane = CreateLane(new ManualTimeProvider());
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedCalls = 0;

        var a = lane.RunAsync(LlmPriority.Background, async ct =>
        {
            await releaseA.Task;
            return "a";
        }, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var queued = lane.RunAsync(LlmPriority.Mining, ct =>
        {
            queuedCalls++;
            return Task.FromResult("q");
        }, cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(0, queuedCalls);

        releaseA.SetResult();
        Assert.Equal("a", await a);
    }

    // 8. work 擲 InvalidOperationException：呼叫端拿到同一個例外，下一個請求照常執行。
    [Fact]
    public async Task RunAsync_WorkThrows_SameExceptionToCallerAndNextStillRuns()
    {
        var lane = CreateLane(new ManualTimeProvider());
        var boom = new InvalidOperationException("boom");

        var failed = lane.RunAsync(LlmPriority.PreprocessNear, ct => Task.FromException<string>(boom), CancellationToken.None);
        Assert.Same(boom, await Assert.ThrowsAsync<InvalidOperationException>(() => failed));

        var next = lane.RunAsync(LlmPriority.PreprocessNear, ct => Task.FromResult("next"), CancellationToken.None);
        Assert.Equal("next", await next);
    }

    // 9. EstimateProgress：開始後 5 秒、中位數 10 秒 → 0.5；還沒開始或沒有紀錄 → null。
    [Fact]
    public async Task EstimateProgress_RunningWithMedian_ReturnsElapsedOverMedian()
    {
        var time = new ManualTimeProvider();
        var lane = CreateLane(time);
        lane.RecordDuration(LlmPriority.PreprocessNear, TimeSpan.FromSeconds(10));

        var call = new LlmCallInfo(LlmPriority.PreprocessNear);
        Assert.Null(lane.EstimateProgress(call)); // 尚未開始

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = lane.RunAsync(LlmPriority.PreprocessNear, async ct =>
        {
            started.SetResult();
            await release.Task;
            return "x";
        }, CancellationToken.None, call);

        await started.Task;
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0.5, lane.EstimateProgress(call));

        release.SetResult();
        Assert.Equal("x", await task);

        // 該優先度沒有耗時紀錄：即使已經開始，預估進度也是 null
        var mineCall = new LlmCallInfo(LlmPriority.Mining);
        var mineStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mineRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mineTask = lane.RunAsync(LlmPriority.Mining, async ct =>
        {
            mineStarted.SetResult();
            await mineRelease.Task;
            return "m";
        }, CancellationToken.None, mineCall);

        await mineStarted.Task;
        Assert.Null(lane.EstimateProgress(mineCall));
        mineRelease.SetResult();
        Assert.Equal("m", await mineTask);
    }
}
