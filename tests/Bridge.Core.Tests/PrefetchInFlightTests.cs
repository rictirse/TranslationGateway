using System.Diagnostics;
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

    // 6. RequestUrgent 的 Task：取走後，在「含有該 key 的批次」End 之後完成（批次進行中不完成）。
    [Fact]
    public async Task RequestUrgent_Taken_CompletesAfterBatchContainingKeyEnds()
    {
        var sut = new PrefetchInFlight();
        var task = sut.RequestUrgent("甲");
        Assert.False(task.IsCompleted);

        Assert.Equal("甲", sut.TakeUrgent());
        using (var handle = sut.Begin(["甲", "乙"]))
        {
            Assert.False(task.IsCompleted);
        }

        Assert.True(task.IsCompleted);
    }

    // 6b. 取走後開始的批次不含該 key 時，Task 不會被那批完成（留給之後含該 key 的批次）。
    [Fact]
    public async Task RequestUrgent_Taken_BatchWithoutKeyDoesNotCompleteTask()
    {
        var sut = new PrefetchInFlight();
        var task = sut.RequestUrgent("甲");
        sut.TakeUrgent();

        using (var handle = sut.Begin(["乙"]))
        {
        }

        Assert.False(task.IsCompleted);

        using (var handle = sut.Begin(["丙", "甲"]))
        {
        }

        Assert.True(task.IsCompleted);
    }

    // 7. 同一個 key 重複要求，回傳同一個 Task。
    [Fact]
    public void RequestUrgent_SameKeyTwice_ReturnsSameTask()
    {
        var sut = new PrefetchInFlight();

        var first = sut.RequestUrgent("甲");
        var second = sut.RequestUrgent("甲");

        Assert.Same(first, second);
    }

    // 8. 被新要求取代的舊要求，它的 Task 也會完成（呼叫端不會永遠等）。
    [Fact]
    public void RequestUrgent_ReplacedByNewKey_CompletesOldTask()
    {
        var sut = new PrefetchInFlight();

        var old = sut.RequestUrgent("甲");
        var current = sut.RequestUrgent("乙");

        Assert.True(old.IsCompleted);
        Assert.False(current.IsCompleted);
    }

    // 9. SetActive(false) 時，所有還在等的 urgent Task（含已被取走、還在等批次的）都完成。
    [Fact]
    public void SetActiveFalse_CompletesWaitingUrgentTasks()
    {
        var sut = new PrefetchInFlight();
        sut.SetActive(true);
        var claimed = sut.RequestUrgent("甲");
        Assert.Equal("甲", sut.TakeUrgent());
        var pending = sut.RequestUrgent("乙");
        Assert.False(pending.IsCompleted);
        Assert.False(claimed.IsCompleted);

        sut.SetActive(false);

        Assert.True(pending.IsCompleted);
        Assert.True(claimed.IsCompleted);
    }

    // 9b. SetKnownKeys(null)（換影片或停止）時，所有還在等的 urgent Task 完成。
    [Fact]
    public void SetKnownKeysNull_CompletesWaitingUrgentTasks()
    {
        var sut = new PrefetchInFlight();
        var pending = sut.RequestUrgent("甲");

        sut.SetKnownKeys(null);

        Assert.True(pending.IsCompleted);
    }

    // 10. TakeUrgent 取一次後回傳 null。
    [Fact]
    public void TakeUrgent_AfterTake_ReturnsNull()
    {
        var sut = new PrefetchInFlight();

        Assert.Null(sut.TakeUrgent());
        sut.RequestUrgent("甲");
        Assert.Equal("甲", sut.TakeUrgent());
        Assert.Null(sut.TakeUrgent());
    }

    // 11. WaitForWorkAsync：有新的 RequestUrgent 時立刻完成。
    [Fact]
    public async Task WaitForWorkAsync_NewRequestUrgent_CompletesImmediately()
    {
        var sut = new PrefetchInFlight();
        var wait = sut.WaitForWorkAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(wait.IsCompleted);

        _ = sut.RequestUrgent("甲");

        await wait.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    // 11b. WaitForWorkAsync：已有還沒取走的急件時直接完成。
    [Fact]
    public async Task WaitForWorkAsync_PendingUrgent_ReturnsImmediately()
    {
        var sut = new PrefetchInFlight();
        _ = sut.RequestUrgent("甲");

        var wait = sut.WaitForWorkAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(wait.IsCompleted);
        await wait;
    }

    // 11c. WaitForWorkAsync：沒有要求時等到逾時。
    [Fact]
    public async Task WaitForWorkAsync_NoUrgent_WaitsUntilTimeout()
    {
        var sut = new PrefetchInFlight();
        var sw = Stopwatch.StartNew();

        await sut.WaitForWorkAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        sw.Stop();
        // 等到逾時才返回（沒有提早醒來；計時器對碼錶可能提早約 1 ms，保留寬限），
        // 而且 TakeUrgent 仍取不到東西
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(80), $"提早返回：{sw.ElapsedMilliseconds} ms");
        Assert.Null(sut.TakeUrgent());
    }

    // 12. IsActive 初始為 false，SetActive 切換。
    [Fact]
    public void IsActive_DefaultFalse_SetActiveToggles()
    {
        var sut = new PrefetchInFlight();
        Assert.False(sut.IsActive);

        sut.SetActive(true);
        Assert.True(sut.IsActive);

        sut.SetActive(false);
        Assert.False(sut.IsActive);
    }

    // 13. SetKnownKeys / IsKnown：清空後全部不是已知 key。
    [Fact]
    public void SetKnownKeys_IsKnown_ClearedWithNull()
    {
        var sut = new PrefetchInFlight();
        Assert.False(sut.IsKnown("甲"));

        sut.SetKnownKeys(["甲", "乙"]);
        Assert.True(sut.IsKnown("甲"));
        Assert.True(sut.IsKnown("乙"));
        Assert.False(sut.IsKnown("丙"));

        sut.SetKnownKeys(null);
        Assert.False(sut.IsKnown("甲"));
    }

    // 14. NoteRequested 後 TakeRequested 取到 key 與請求時間（建構子傳入的 TimeProvider），取完清空。
    [Fact]
    public void TakeRequested_AfterNoteRequested_ReturnsKeyAndTime_ThenEmpty()
    {
        var time = new ManualTimeProvider();
        var sut = new PrefetchInFlight(time);
        time.UtcNow = time.UtcNow.AddSeconds(3);

        sut.NoteRequested("甲");

        var requests = sut.TakeRequested();
        var request = Assert.Single(requests);
        Assert.Equal("甲", request.Key);
        Assert.Equal(time.UtcNow, request.At);
        Assert.Empty(sut.TakeRequested());
    }

    // 15. 超過 200 筆時只保留最新的 200 筆（丟最舊的）。
    [Fact]
    public void TakeRequested_OverTwoHundred_KeepsLatestTwoHundred()
    {
        var sut = new PrefetchInFlight();

        for (var i = 0; i < 202; i++)
        {
            sut.NoteRequested($"行{i}");
        }

        var requests = sut.TakeRequested();
        Assert.Equal(200, requests.Count);
        Assert.Equal("行2", requests[0].Key);
        Assert.Equal("行201", requests[^1].Key);
    }
}
