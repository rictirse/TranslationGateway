using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>CardReadinessGate：詞彙卡準備門的等待、完成與逾時（ManualTimeProvider 推進虛擬時間，不真的等待）。</summary>
public class CardReadinessGateTests
{
    // 1. 沒有 Begin：WaitAsync 立刻回傳 true，IsPending 是 false，IsOpen 是 true。
    [Fact]
    public async Task WithoutBegin_WaitReturnsTrueImmediately()
    {
        var time = new Lanes.ManualTimeProvider();
        var sut = new CardReadinessGate(time);

        Assert.False(sut.IsPending);
        Assert.True(sut.IsOpen(TimeSpan.FromSeconds(180)));
        Assert.True(await sut.WaitAsync(TimeSpan.FromSeconds(180), CancellationToken.None));
    }

    // 2. Begin 後 Complete：IsPending 變 false，等待中的 WaitAsync 回傳 true；重複 Complete 沒有副作用。
    [Fact]
    public async Task BeginThenComplete_PendingWaitReturnsTrue()
    {
        var time = new Lanes.ManualTimeProvider();
        var sut = new CardReadinessGate(time);

        sut.Begin();
        Assert.True(sut.IsPending);
        Assert.False(sut.IsOpen(TimeSpan.FromSeconds(180)));

        var wait = sut.WaitAsync(TimeSpan.FromSeconds(180), CancellationToken.None);
        Assert.False(wait.IsCompleted);
        sut.Complete();
        Assert.True(await wait);
        Assert.False(sut.IsPending);
        Assert.True(sut.IsOpen(TimeSpan.FromSeconds(180)));

        sut.Complete();
        Assert.True(await sut.WaitAsync(TimeSpan.FromSeconds(180), CancellationToken.None));
    }

    // 3. Begin 後時間前進超過 timeout：IsOpen(timeout) 是 true，等待中的 WaitAsync 回傳 false。
    [Fact]
    public async Task Begin_TimeAdvancedBeyondTimeout_IsOpenTrueAndWaitReturnsFalse()
    {
        var time = new Lanes.ManualTimeProvider();
        var sut = new CardReadinessGate(time);

        sut.Begin();
        var wait = sut.WaitAsync(TimeSpan.FromSeconds(180), CancellationToken.None);
        Assert.False(wait.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(181));

        Assert.True(sut.IsOpen(TimeSpan.FromSeconds(180)));
        Assert.False(await wait);
        // 逾時後才 Complete：狀態一致（IsPending 回 false）
        sut.Complete();
        Assert.False(sut.IsPending);
    }

    // 3b. Begin 後時間前進、尚未超過 timeout 就呼叫 WaitAsync：剩餘時間到了（繼續推進）回傳 false。
    [Fact]
    public async Task Begin_WaitStartedLate_RemainingTimeoutApplies()
    {
        var time = new Lanes.ManualTimeProvider();
        var sut = new CardReadinessGate(time);

        sut.Begin();
        time.Advance(TimeSpan.FromSeconds(170));

        var wait = sut.WaitAsync(TimeSpan.FromSeconds(180), CancellationToken.None);
        Assert.False(wait.IsCompleted);
        Assert.False(sut.IsOpen(TimeSpan.FromSeconds(180)));

        time.Advance(TimeSpan.FromSeconds(11));
        Assert.False(await wait);
    }

    // 4. ct 取消時擲 OperationCanceledException。
    [Fact]
    public async Task Begin_CancellationTokenCancelled_ThrowsOperationCanceled()
    {
        var time = new Lanes.ManualTimeProvider();
        var sut = new CardReadinessGate(time);
        sut.Begin();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.WaitAsync(TimeSpan.FromSeconds(180), cts.Token));
    }

    // 5. 連續 Begin 兩次：舊的等待會完成（回 true），新的重新計時（先前的時間推進不算）。
    [Fact]
    public async Task BeginTwice_OldWaitCompletesAndNewWaitRestarts()
    {
        var time = new Lanes.ManualTimeProvider();
        var sut = new CardReadinessGate(time);

        sut.Begin();
        var first = sut.WaitAsync(TimeSpan.FromSeconds(180), CancellationToken.None);
        Assert.False(first.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(100));
        sut.Begin();
        Assert.True(await first);

        // 新的等待重新計時：等 179 秒還沒逾時，超過 180 秒才逾時
        var second = sut.WaitAsync(TimeSpan.FromSeconds(180), CancellationToken.None);
        Assert.False(second.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(179));
        Assert.False(sut.IsOpen(TimeSpan.FromSeconds(180)));
        Assert.True(sut.IsPending);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(sut.IsOpen(TimeSpan.FromSeconds(180)));
        Assert.False(await second);
    }
}
