using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>PrefetchInFlight：正在執行的預先翻譯批次登記，即時翻譯依 key 等批次完成。</summary>
public class PrefetchInFlightTests
{
    // 1. 沒有批次時，WaitTaskFor 回傳 null。
    [Fact]
    public void WaitTaskFor_NoBatch_ReturnsNull()
    {
        var sut = new PrefetchInFlight();

        Assert.Null(sut.WaitTaskFor("テスト"));
    }

    // 2. Begin 後，批次內的 key 拿到未完成的 Task，批次外的 key 拿到 null。
    [Fact]
    public void Begin_KeyInBatch_GetsIncompleteTask_KeyOutside_ReturnsNull()
    {
        var sut = new PrefetchInFlight();

        using var handle = sut.Begin(["甲", "乙"]);
        var task = sut.WaitTaskFor("甲");

        Assert.NotNull(task);
        Assert.False(task!.IsCompleted);
        Assert.Null(sut.WaitTaskFor("丙"));
    }

    // 3. Dispose 後 Task 完成，WaitTaskFor 回傳 null。
    [Fact]
    public async Task Dispose_CompletesTask_ClearsBatch()
    {
        var sut = new PrefetchInFlight();
        var handle = sut.Begin(["甲"]);
        var task = sut.WaitTaskFor("甲");

        handle.Dispose();

        Assert.NotNull(task);
        await task!;
        Assert.Null(sut.WaitTaskFor("甲"));
    }

    // 4. 連續 Begin 兩次：第一批的 Task 完成；第一個 handle 之後再 Dispose 不影響第二批。
    [Fact]
    public void BeginTwice_CompletesFirstBatchTask_OldDisposeDoesNotAffectSecond()
    {
        var sut = new PrefetchInFlight();
        var handle1 = sut.Begin(["甲"]);

        var task1 = sut.WaitTaskFor("甲");
        Assert.NotNull(task1);

        var handle2 = sut.Begin(["乙"]);

        // 新批次開始時，舊的那批視為結束，等待中的呼叫端被放行
        Assert.True(task1!.IsCompleted);

        var task2 = sut.WaitTaskFor("乙");
        Assert.NotNull(task2);
        Assert.False(task2!.IsCompleted);

        // 舊 handle 的 Dispose 不能清掉新的那批
        handle1.Dispose();
        Assert.False(task2.IsCompleted);
        Assert.NotNull(sut.WaitTaskFor("乙"));

        handle2.Dispose();
        Assert.True(task2.IsCompleted);
        Assert.Null(sut.WaitTaskFor("乙"));
    }

    // 5. 重複 Dispose 沒有副作用。
    [Fact]
    public async Task DisposeTwice_HasNoSideEffects()
    {
        var sut = new PrefetchInFlight();
        var handle = sut.Begin(["甲"]);
        var task = sut.WaitTaskFor("甲");

        handle.Dispose();
        handle.Dispose();

        Assert.NotNull(task);
        await task!;
        Assert.Null(sut.WaitTaskFor("甲"));
    }
}
