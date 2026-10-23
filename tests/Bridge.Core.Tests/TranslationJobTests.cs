using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Tests;

public class TranslationJobTests
{
    [Fact]
    public void Defaults_AreEmptyAndIdIsEightChars()
    {
        var job = new TranslationJob();

        Assert.Equal(8, job.Id.Length);
        Assert.Equal(string.Empty, job.Current);
        Assert.Equal(0, job.TotalTokens);
    }

    [Fact]
    public void Defaults_SourceIsModel()
    {
        var job = new TranslationJob();

        Assert.Equal(TranslationSource.Model, job.Source);
    }

    [Fact]
    public void Tokens_AsciiCountsQuarterPerChar_RoundedUp()
    {
        var job = new TranslationJob { UserPrompt = "abcde" }; // 5 / 4 → 2

        Assert.Equal(2, job.UserTokens);
    }

    [Fact]
    public void Tokens_NonAsciiCountsOnePointFiveEach()
    {
        var job = new TranslationJob { SystemPrompt = "你好" }; // 2 * 1.5 → 3

        Assert.Equal(3, job.SystemTokens);
    }

    [Fact]
    public void TotalTokens_IsSumOfUserAndSystem()
    {
        var job = new TranslationJob { UserPrompt = "abcd", SystemPrompt = "你" };

        Assert.Equal(job.UserTokens + job.SystemTokens, job.TotalTokens);
        Assert.Equal(1 + 1, job.TotalTokens);
    }

    [Fact]
    public void LatencyProperties_RaisePropertyChanged()
    {
        var job = new TranslationJob();
        var changed = new List<string?>();
        job.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        job.TotalLatency = 5;

        Assert.Contains(nameof(TranslationJob.TotalLatency), changed);
    }
}
