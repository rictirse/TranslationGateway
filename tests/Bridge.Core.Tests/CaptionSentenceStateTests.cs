using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>CaptionSentenceState：前處理句子的記憶體狀態（排序、取代、時間重疊查詢與 thread-safe）。</summary>
public class CaptionSentenceStateTests
{
    private static CaptionSentence Sentence(long startMs, long endMs, string text) =>
        new(startMs, endMs, $"raw:{text}", text, "model");

    [Fact]
    public void Add_SortsByStartMs()
    {
        var state = new CaptionSentenceState();
        state.Reset("video1", 5);

        state.Add("video1", [Sentence(300, 400, "丙"), Sentence(100, 200, "甲"), Sentence(200, 250, "乙")]);

        Assert.Equal([100L, 200L, 300L], state.Sentences.Select(s => s.StartMs).ToList());
    }

    [Fact]
    public void Add_SameStartMs_ReplacesPrevious()
    {
        var state = new CaptionSentenceState();
        state.Reset("video1", 5);

        state.Add("video1", [Sentence(100, 200, "舊")]);
        state.Add("video1", [Sentence(100, 300, "新")]);

        var sentence = Assert.Single(state.Sentences);
        Assert.Equal("新", sentence.Text);
        Assert.Equal(300, sentence.EndMs);
    }

    [Fact]
    public void Add_DifferentVideoId_IgnoresBatch()
    {
        var state = new CaptionSentenceState();
        state.Reset("video1", 5);

        state.Add("video2", [Sentence(100, 200, "別支影片")]);

        Assert.Empty(state.Sentences);
        Assert.Equal("video1", state.VideoId);
    }

    [Fact]
    public void GetOverlapping_ReturnsSentencesOverlappingRange()
    {
        var state = new CaptionSentenceState();
        state.Reset("video1", 5);
        // 句子：[0,100]、[200,300]、[400,500]
        state.Add("video1", [Sentence(0, 100, "一"), Sentence(200, 300, "二"), Sentence(400, 500, "三")]);

        // 邊界：EndMs == fromMs（200 == 200）算重疊；StartMs == toMs（200 == 200）算重疊
        Assert.Equal(["二"], state.GetOverlapping(200, 200).Select(s => s.Text).ToList());
        // 完全涵蓋
        Assert.Equal(["一", "二", "三"], state.GetOverlapping(0, 600).Select(s => s.Text).ToList());
        // 部分重疊：[50, 250) 碰到一與二
        Assert.Equal(["一", "二"], state.GetOverlapping(50, 250).Select(s => s.Text).ToList());
        // 完全不重疊：101～199 之間沒有句子（一 EndMs=100、二 StartMs=200）
        Assert.Empty(state.GetOverlapping(101, 199));
        // 前後都不重疊
        Assert.Empty(state.GetOverlapping(-100, -1));
        Assert.Empty(state.GetOverlapping(501, 900));
    }

    [Fact]
    public void Reset_ClearsSentencesAndSwitchesTarget()
    {
        var state = new CaptionSentenceState();
        state.Reset("video1", 5);
        state.Add("video1", [Sentence(100, 200, "甲")]);
        Assert.Single(state.Sentences);

        state.Reset("video2", 6);

        Assert.Empty(state.Sentences);
        Assert.Equal("video2", state.VideoId);
        Assert.Equal(6, state.CaptionId);

        // 舊影片的句子不再被接受
        state.Add("video1", [Sentence(300, 400, "舊影片")]);
        Assert.Empty(state.Sentences);
    }

    [Fact]
    public void ResetAndAdd_RaiseChangedEvent()
    {
        var state = new CaptionSentenceState();
        var changed = 0;
        state.Changed += () => changed++;

        state.Reset("video1", 5);
        Assert.Equal(1, changed);

        // 內容有改變才觸發
        state.Add("video1", [Sentence(100, 200, "甲")]);
        Assert.Equal(2, changed);

        // 加進完全相同的句子：不觸發
        state.Add("video1", [Sentence(100, 200, "甲")]);
        Assert.Equal(2, changed);

        // videoId 不同：整批忽略，也不觸發
        state.Add("video2", [Sentence(300, 400, "乙")]);
        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task ConcurrentAddAndRead_DoesNotThrow()
    {
        var state = new CaptionSentenceState();
        state.Reset("video1", 5);

        var writer1 = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                state.Add("video1", [Sentence(i, i + 10, $"w1-{i}")]);
            }
        }, TestContext.Current.CancellationToken);
        var writer2 = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                state.Add("video1", [Sentence(i, i + 10, $"w2-{i}")]);
            }
        }, TestContext.Current.CancellationToken);
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                var sentences = state.Sentences;
                var overlapping = state.GetOverlapping(0, long.MaxValue / 2);
                Assert.True(sentences.Count <= 500);
                Assert.True(overlapping.Count <= 500);
                Assert.NotNull(state.VideoId);
            }
        }, TestContext.Current.CancellationToken);
        var resetter = Task.Run(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                state.Reset("video1", 5);
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(writer1, writer2, reader, resetter);
        // 沒有擲例外即通過（最後一次 Reset 之後可能又加了句子，不對計數做斷言）
    }
}
