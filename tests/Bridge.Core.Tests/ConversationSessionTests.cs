using System.Threading;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>可手動推進時間的 TimeProvider，避免測試真的等待。</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get => _utcNow; set => _utcNow = value; }

    public override DateTimeOffset GetUtcNow() => _utcNow;
}

public class ConversationSessionTests
{
    private static ConversationSession Create(ManualTimeProvider time) => new(time);

    [Fact]
    public void Append_ThenGetHistory_ReturnsTurnsInOrder()
    {
        var sut = Create(new ManualTimeProvider());

        sut.Append("u1", "a1");
        sut.Append("u2", "a2");

        var history = sut.GetHistory("sys");
        Assert.Equal(2, history.Count);
        Assert.Equal(new ChatTurn("u1", "a1"), history[0]);
        Assert.Equal(new ChatTurn("u2", "a2"), history[1]);
    }

    [Fact]
    public void GetHistory_ReturnsCopy_ModifyingResultDoesNotAffectSession()
    {
        var sut = Create(new ManualTimeProvider());
        sut.Append("u1", "a1");

        var history = sut.GetHistory("sys");
        var asList = Assert.IsType<List<ChatTurn>>(history);
        asList.Add(new ChatTurn("u2", "a2"));

        Assert.Equal(1, sut.Count);
        Assert.Single(sut.GetHistory("sys"));
    }

    [Fact]
    public void GetHistory_FirstCall_OnlyRecordsPrompt_DoesNotClear()
    {
        var sut = Create(new ManualTimeProvider());
        sut.Append("u1", "a1");

        Assert.Single(sut.GetHistory("sys"));
    }

    [Fact]
    public void GetHistory_SystemPromptChanged_ClearsHistory()
    {
        var sut = Create(new ManualTimeProvider());
        sut.Append("u1", "a1");
        sut.GetHistory("sys1");

        Assert.Empty(sut.GetHistory("sys2"));
    }

    [Fact]
    public void GetHistory_SameSystemPrompt_KeepsHistory()
    {
        var sut = Create(new ManualTimeProvider());
        sut.Append("u1", "a1");
        sut.GetHistory("sys1");

        Assert.Single(sut.GetHistory("sys1"));
    }

    [Fact]
    public void GetHistory_IdleOverIdleReset_ClearsHistory()
    {
        var time = new ManualTimeProvider();
        var sut = Create(time);
        sut.Append("u1", "a1");
        sut.GetHistory("sys");

        time.UtcNow = time.UtcNow.AddSeconds(91); // IdleReset 預設 90 秒

        Assert.Empty(sut.GetHistory("sys"));
    }

    [Fact]
    public void GetHistory_IdleNotOverIdleReset_KeepsHistory()
    {
        var time = new ManualTimeProvider();
        var sut = Create(time);
        sut.Append("u1", "a1");
        sut.GetHistory("sys");

        time.UtcNow = time.UtcNow.AddSeconds(89);

        Assert.Single(sut.GetHistory("sys"));
    }

    [Fact]
    public void GetHistory_ReachesMaxTurns_KeepsLastKeepTurnsInOrder()
    {
        var sut = new ConversationSession(new ManualTimeProvider()) { MaxTurns = 5, KeepTurns = 2 };
        for (var i = 1; i <= 5; i++)
        {
            sut.Append($"u{i}", $"a{i}");
        }

        var history = sut.GetHistory("sys");

        Assert.Equal(2, history.Count);
        Assert.Equal(new ChatTurn("u4", "a4"), history[0]);
        Assert.Equal(new ChatTurn("u5", "a5"), history[1]);
    }

    [Fact]
    public void GetHistory_KeepTurnsBelowZero_TreatedAsZero()
    {
        var sut = new ConversationSession(new ManualTimeProvider()) { MaxTurns = 3, KeepTurns = -1 };
        for (var i = 1; i <= 3; i++)
        {
            sut.Append($"u{i}", $"a{i}");
        }

        Assert.Empty(sut.GetHistory("sys"));
    }

    [Fact]
    public void GetHistory_KeepTurnsOverMaxTurns_TreatedAsMaxTurns()
    {
        var sut = new ConversationSession(new ManualTimeProvider()) { MaxTurns = 2, KeepTurns = 5 };
        sut.Append("u1", "a1");
        sut.Append("u2", "a2");

        var history = sut.GetHistory("sys");

        Assert.Equal(2, history.Count);
        Assert.Equal(new ChatTurn("u1", "a1"), history[0]);
        Assert.Equal(new ChatTurn("u2", "a2"), history[1]);
    }

    [Fact]
    public void Reset_ClearsHistory()
    {
        var sut = Create(new ManualTimeProvider());
        sut.Append("u1", "a1");
        sut.Append("u2", "a2");
        sut.GetHistory("sys");

        sut.Reset();

        Assert.Equal(0, sut.Count);
        Assert.Empty(sut.GetHistory("sys"));
    }

    [Fact]
    public void Remove_RemovesAllMatchingTurns_AndReturnsTrue()
    {
        var sut = Create(new ManualTimeProvider());
        sut.Append("u1", "a1");
        sut.Append("u2", "a2");
        sut.Append("u1", "a1b");

        var removed = sut.Remove("u1");

        Assert.True(removed);
        var turn = Assert.Single(sut.GetHistory("sys"));
        Assert.Equal(new ChatTurn("u2", "a2"), turn);
    }

    [Fact]
    public void Remove_NoMatch_ReturnsFalse()
    {
        var sut = Create(new ManualTimeProvider());
        sut.Append("u1", "a1");

        Assert.False(sut.Remove("uX"));
        Assert.Single(sut.GetHistory("sys"));
    }

    [Fact]
    public void Remove_DoesNotUpdateLastAppendTime()
    {
        var time = new ManualTimeProvider();
        var sut = Create(time);
        sut.Append("u1", "a1");
        sut.GetHistory("sys");

        time.UtcNow = time.UtcNow.AddSeconds(80);
        sut.Remove("u1");
        time.UtcNow = time.UtcNow.AddSeconds(20); // 距最後 Append 已超過 90 秒

        Assert.Empty(sut.GetHistory("sys"));
    }

    [Fact]
    public void Replace_ReplacesAllMatchingTurnsInPlace_AndReturnsTrue()
    {
        var sut = Create(new ManualTimeProvider());
        sut.Append("u1", "a1");
        sut.Append("u2", "a2");
        sut.Append("u3", "a3");

        var replaced = sut.Replace("u2", "newU", "newA");

        Assert.True(replaced);
        var history = sut.GetHistory("sys");
        Assert.Equal(3, history.Count);
        Assert.Equal(new ChatTurn("u1", "a1"), history[0]);
        Assert.Equal(new ChatTurn("newU", "newA"), history[1]);
        Assert.Equal(new ChatTurn("u3", "a3"), history[2]);
    }

    [Fact]
    public void Replace_NoMatch_ReturnsFalse()
    {
        var sut = Create(new ManualTimeProvider());
        sut.Append("u1", "a1");

        Assert.False(sut.Replace("uX", "newU", "newA"));
        Assert.Single(sut.GetHistory("sys"));
    }

    [Fact]
    public void Replace_DoesNotUpdateLastAppendTime()
    {
        var time = new ManualTimeProvider();
        var sut = Create(time);
        sut.Append("u1", "a1");
        sut.GetHistory("sys");

        time.UtcNow = time.UtcNow.AddSeconds(80);
        sut.Replace("u1", "u1", "new");
        time.UtcNow = time.UtcNow.AddSeconds(20); // 距最後 Append 已超過 90 秒

        Assert.Empty(sut.GetHistory("sys"));
    }

    [Fact]
    public void Concurrent_AppendAndGetHistory_NoException()
    {
        var sut = Create(new ManualTimeProvider());

        Parallel.For(0, 1000, i =>
        {
            if (i % 2 == 0)
            {
                sut.Append($"u{i}", $"a{i}");
            }
            else
            {
                sut.GetHistory("sys");
            }
        });

        Assert.True(sut.Count > 0);
    }
}
