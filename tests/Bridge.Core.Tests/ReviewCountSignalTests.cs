using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>側邊欄待審數量的通知與顯示文字格式。</summary>
public sealed class ReviewCountSignalTests
{
    [Theory]
    [InlineData(-1, "")]
    [InlineData(0, "")]
    [InlineData(1, "(1)")]
    [InlineData(12, "(12)")]
    [InlineData(99, "(99)")]
    [InlineData(100, "(99+)")]
    [InlineData(5000, "(99+)")]
    public void FormatBadge_CoversZero_Pending_AndOverflow(int count, string expected)
    {
        Assert.Equal(expected, ReviewCountSignal.FormatBadge(count));
    }

    [Fact]
    public void Notify_RaisesChanged()
    {
        var signal = new ReviewCountSignal();
        var raised = 0;
        signal.Changed += (_, _) => raised++;

        signal.Notify();
        signal.Notify();

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Notify_UnsubscribedHandler_IsNotRaised()
    {
        var signal = new ReviewCountSignal();
        var raised = 0;
        EventHandler handler = (_, _) => raised++;
        signal.Changed += handler;
        signal.Changed -= handler;

        signal.Notify();

        Assert.Equal(0, raised);
    }
}
