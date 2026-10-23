using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class TranslationStatsTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0);

    private static TranslationJob Job(TranslationSource source, long modelLatency, DateTime? timestamp = null) => new()
    {
        Source = source,
        ModelLatency = modelLatency,
        Timestamp = timestamp ?? Now
    };

    [Fact]
    public void Compute_EmptyJobs_CountZeroAvgAndP95NullHitRateZero()
    {
        var snapshot = TranslationStats.Compute([], Now, TimeSpan.FromMinutes(5));

        Assert.Equal(0, snapshot.Count);
        Assert.Equal(0, snapshot.ModelCount);
        Assert.Null(snapshot.ModelAvgMs);
        Assert.Null(snapshot.ModelP95Ms);
        Assert.Equal(0, snapshot.CacheHitRate);
        Assert.Equal(0, snapshot.TimeoutCount);
    }

    [Fact]
    public void Compute_JobOutsideWindow_Excluded()
    {
        var jobs = new[]
        {
            Job(TranslationSource.Model, 100, Now - TimeSpan.FromMinutes(6)),
            Job(TranslationSource.Model, 200)
        };

        var snapshot = TranslationStats.Compute(jobs, Now, TimeSpan.FromMinutes(5));

        Assert.Equal(1, snapshot.Count);
        Assert.Equal(1, snapshot.ModelCount);
        Assert.Equal(200, snapshot.ModelAvgMs);
        Assert.Equal(200, snapshot.ModelP95Ms);
    }

    [Fact]
    public void Compute_JobExactlyAtWindowBoundary_Included()
    {
        var jobs = new[] { Job(TranslationSource.Cache, 0, Now - TimeSpan.FromMinutes(5)) };

        var snapshot = TranslationStats.Compute(jobs, Now, TimeSpan.FromMinutes(5));

        Assert.Equal(1, snapshot.Count);
        Assert.Equal(1, snapshot.CacheHitRate, 5);
    }

    [Fact]
    public void Compute_MixedSources_CountsEachKind()
    {
        var jobs = new[]
        {
            Job(TranslationSource.Model, 100),
            Job(TranslationSource.Model, 300),
            Job(TranslationSource.Cache, 0),
            Job(TranslationSource.Timeout, 500),
            Job(TranslationSource.ThroughPass, 10)
        };

        var snapshot = TranslationStats.Compute(jobs, Now, TimeSpan.FromMinutes(5));

        Assert.Equal(5, snapshot.Count);
        Assert.Equal(2, snapshot.ModelCount);
        Assert.Equal(200, snapshot.ModelAvgMs);
        Assert.Equal(0.2, snapshot.CacheHitRate, 5);
        Assert.Equal(1, snapshot.TimeoutCount);
    }

    [Fact]
    public void Compute_AverageRoundsHalfAwayFromZero()
    {
        var jobs = new[]
        {
            Job(TranslationSource.Model, 100),
            Job(TranslationSource.Model, 101)
        };

        var snapshot = TranslationStats.Compute(jobs, Now, TimeSpan.FromMinutes(5));

        // 100.5 四捨五入（AwayFromZero）為 101；銀行家捨入會得到 100
        Assert.Equal(101, snapshot.ModelAvgMs);
    }

    [Fact]
    public void Compute_P95_TwentyLatencies_ReturnsNineteen()
    {
        var jobs = Enumerable.Range(1, 20)
            .Select(i => Job(TranslationSource.Model, i))
            .ToList();

        var snapshot = TranslationStats.Compute(jobs, Now, TimeSpan.FromMinutes(5));

        Assert.Equal(19, snapshot.ModelP95Ms);
    }

    [Fact]
    public void Compute_P95_SingleJob_ReturnsThatValue()
    {
        var jobs = new[] { Job(TranslationSource.Model, 123) };

        var snapshot = TranslationStats.Compute(jobs, Now, TimeSpan.FromMinutes(5));

        Assert.Equal(123, snapshot.ModelP95Ms);
        Assert.Equal(123, snapshot.ModelAvgMs);
    }
}
