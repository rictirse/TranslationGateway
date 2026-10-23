using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class CaptionTrackStateTests
{
    private static CaptionCue Cue(long startMs, string text = "字幕") => new(startMs, 1000, text);

    [Fact]
    public void GetRange_BoundariesAreInclusive()
    {
        var state = new CaptionTrackState();
        state.Set("vid", [Cue(1000), Cue(2000), Cue(3000), Cue(4000)]);

        var range = state.GetRange(2000, 3000);

        Assert.Equal([2000L, 3000L], range.Select(c => c.StartMs).ToArray());

        // 邊界等於 fromMs／toMs 都要包含
        Assert.Equal([1000L, 2000L], state.GetRange(1000, 2000).Select(c => c.StartMs).ToArray());
        // 完全沒有交集時為空清單
        Assert.Empty(state.GetRange(5000, 6000));
        Assert.Empty(state.GetRange(0, 999));
    }

    [Fact]
    public void Set_SortsCuesByStartMs_AndExposesVideoId()
    {
        var state = new CaptionTrackState();

        state.Set("vod1", [Cue(3000), Cue(1000), Cue(2000)]);

        Assert.Equal("vod1", state.VideoId);
        Assert.Equal([1000L, 2000L, 3000L], state.Cues.Select(c => c.StartMs).ToArray());
        Assert.True(state.IsLoaded);
    }

    [Fact]
    public void Clear_ResetsToEmpty()
    {
        var state = new CaptionTrackState();
        state.Set("vod1", [Cue(1000), Cue(2000)]);

        state.Clear();

        Assert.Null(state.VideoId);
        Assert.Empty(state.Cues);
        Assert.False(state.IsLoaded);
        Assert.Empty(state.GetRange(0, long.MaxValue));
    }

    [Fact]
    public async Task ConcurrentSetAndGetRange_DoesNotThrow()
    {
        var state = new CaptionTrackState();
        state.Set("vod1", [Cue(1000), Cue(2000), Cue(3000)]);

        var tasks = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 500; n++)
            {
                if (i % 2 == 0)
                {
                    state.Set($"vod{n % 3}", [Cue(n * 10), Cue(n * 10 + 500), Cue(n * 10 + 1000)]);
                }
                else
                {
                    _ = state.GetRange(0, long.MaxValue).Count;
                    _ = state.IsLoaded;
                    _ = state.VideoId;
                    _ = state.Cues;
                }
            }
        }));
        await Task.WhenAll(tasks);

        // 收尾狀態仍然一致
        Assert.Equal(state.Cues.Count, state.GetRange(long.MinValue, long.MaxValue).Count);
    }
}
