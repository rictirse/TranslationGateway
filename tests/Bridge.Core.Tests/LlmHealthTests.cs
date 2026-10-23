using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests.Lanes;

/// <summary>LlmHealth：連續失敗達門檻（預設 3 次）時標記 5 分鐘不可用，
/// 時間到後進入「試試看」狀態（先回原模型），成功才真正恢復。</summary>
public class LlmHealthTests
{
    private static (LlmHealth Health, ManualTimeProvider Time) Create()
    {
        var time = new ManualTimeProvider();
        return (new LlmHealth(time, NullLogger<LlmHealth>.Instance), time);
    }

    // 失敗 2 次仍可用，也沒觸發 Changed。
    [Fact]
    public void RecordFailure_BelowThreshold_StaysAvailable()
    {
        var (health, _) = Create();
        var changed = 0;
        health.Changed += () => changed++;

        health.RecordFailure();
        health.RecordFailure();

        Assert.True(health.IsAvailable);
        Assert.Null(health.DownUntil);
        Assert.Equal(0, changed);
    }

    // 第 3 次變成不可用：DownUntil 是 now + 5 分鐘，觸發 Changed。
    [Fact]
    public void RecordFailure_ReachesThreshold_MarksDownFiveMinutes()
    {
        var (health, time) = Create();
        var changed = 0;
        health.Changed += () => changed++;
        var before = time.GetUtcNow();

        health.RecordFailure();
        health.RecordFailure();
        health.RecordFailure();

        Assert.False(health.IsAvailable);
        Assert.Equal(before + TimeSpan.FromMinutes(5), health.DownUntil);
        Assert.Equal(1, changed);
    }

    // 中間成功一次：連續失敗次數歸零，之後要再 3 次才會標記。
    [Fact]
    public void RecordSuccess_ResetsConsecutiveFailures()
    {
        var (health, _) = Create();

        health.RecordFailure();
        health.RecordFailure();
        health.RecordSuccess();
        health.RecordFailure();
        health.RecordFailure();

        Assert.True(health.IsAvailable);
        Assert.Null(health.DownUntil);
    }

    // 時間前進 5 分鐘後 IsAvailable 是 true（試試看）；這時失敗一次（次數仍 ≥ 門檻）又變成不可用。
    [Fact]
    public void DownUntilPassed_AvailableAgain_FailureReMarks()
    {
        var (health, time) = Create();
        health.RecordFailure();
        health.RecordFailure();
        health.RecordFailure();
        Assert.False(health.IsAvailable);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(health.IsAvailable);

        var before = time.GetUtcNow();
        health.RecordFailure();
        Assert.False(health.IsAvailable);
        Assert.Equal(before + TimeSpan.FromMinutes(5), health.DownUntil);
    }

    // 不可用時 RecordSuccess：恢復、DownUntil 清空，觸發 Changed。
    [Fact]
    public void RecordSuccess_WhileMarked_RecoversAndRaisesChanged()
    {
        var (health, _) = Create();
        health.RecordFailure();
        health.RecordFailure();
        health.RecordFailure();
        Assert.False(health.IsAvailable);
        var changed = 0;
        health.Changed += () => changed++;

        health.RecordSuccess();

        Assert.True(health.IsAvailable);
        Assert.Null(health.DownUntil);
        Assert.Equal(1, changed);
    }

    // 沒有被標記時的成功不觸發 Changed（不是狀態變化）。
    [Fact]
    public void RecordSuccess_WhileHealthy_DoesNotRaiseChanged()
    {
        var (health, _) = Create();
        var changed = 0;
        health.Changed += () => changed++;

        health.RecordSuccess();

        Assert.True(health.IsAvailable);
        Assert.Equal(0, changed);
    }

    // BeginWaiting：等待數 0→1 時 HasWaiters 變 true 並觸發 Changed；Dispose 後 1→0 再觸發一次。
    [Fact]
    public void BeginWaiting_ThenDispose_TogglesHasWaitersAndRaisesChanged()
    {
        var (health, _) = Create();
        var changed = 0;
        health.Changed += () => changed++;

        Assert.False(health.HasWaiters);
        var waiting = health.BeginWaiting();

        Assert.True(health.HasWaiters);
        Assert.Equal(1, changed);

        waiting.Dispose();

        Assert.False(health.HasWaiters);
        Assert.Equal(2, changed);
    }

    // 兩個同時等待：1→2 不觸發 Changed，要兩個都結束 HasWaiters 才變回 false（最後一個結束時觸發一次）。
    [Fact]
    public void TwoConcurrentWaiters_BothMustEndBeforeHasWaitersIsFalse()
    {
        var (health, _) = Create();
        var changed = 0;
        health.Changed += () => changed++;

        var a = health.BeginWaiting();
        var b = health.BeginWaiting();
        Assert.Equal(1, changed);

        a.Dispose();
        Assert.True(health.HasWaiters);
        Assert.Equal(1, changed);

        b.Dispose();
        Assert.False(health.HasWaiters);
        Assert.Equal(2, changed);
    }

    // Dispose 重複呼叫只算一次：HasWaiters 不會變負、Changed 不會多觸發。
    [Fact]
    public void WaitingHandle_DisposeCalledTwice_CountsOnce()
    {
        var (health, _) = Create();
        var changed = 0;
        health.Changed += () => changed++;

        var waiting = health.BeginWaiting();
        waiting.Dispose();
        waiting.Dispose();

        Assert.False(health.HasWaiters);
        Assert.Equal(2, changed);
    }
}
