using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class DurationEstimatorTests
{
    [Fact]
    public void Median_NoSamples_IsNull()
    {
        var sut = new DurationEstimator();

        Assert.Null(sut.Median);
    }

    [Fact]
    public void Median_OddCount_IsMiddleValue()
    {
        var sut = new DurationEstimator();
        sut.Add(TimeSpan.FromSeconds(1));
        sut.Add(TimeSpan.FromSeconds(10));
        sut.Add(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), sut.Median);
    }

    [Fact]
    public void Median_EvenCount_IsAverageOfMiddleTwo()
    {
        var sut = new DurationEstimator();
        sut.Add(TimeSpan.FromSeconds(1));
        sut.Add(TimeSpan.FromSeconds(4));
        sut.Add(TimeSpan.FromSeconds(3));
        sut.Add(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(3.5), sut.Median);
    }

    [Fact]
    public void Add_MoreThanCapacity_KeepsLastFive()
    {
        var sut = new DurationEstimator();
        for (var seconds = 1; seconds <= 7; seconds++)
        {
            sut.Add(TimeSpan.FromSeconds(seconds));
        }

        // 最近 5 筆是 3、4、5、6、7 秒，中位數 5 秒（1、2 秒已被擠出）
        Assert.Equal(TimeSpan.FromSeconds(5), sut.Median);
    }
}
