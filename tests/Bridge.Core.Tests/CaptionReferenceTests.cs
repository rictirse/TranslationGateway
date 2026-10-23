using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class CaptionReferenceTests
{
    private static CaptionCue Cue(long startMs, string text) => new(startMs, 2000, text);

    /// <summary>可以切換回傳值的假播放時鐘。</summary>
    private sealed class FakePlaybackClock : IPlaybackClock
    {
        public long? PositionMs { get; set; }

        public long? GetPositionMs() => PositionMs;
    }

    [Fact]
    public void BuildUserPromptSection_WithClock_OnlySearchesCuesInRange()
    {
        var captions = new CaptionTrackState();
        captions.Set("vod", [Cue(40_000, "ぴんくの部屋着じゃなかったら"), Cue(200_000, "これで部屋着じゃなかったら")]);
        var sut = new CaptionReference(captions, new FakePlaybackClock { PositionMs = 45_000 });

        var section = sut.BuildUserPromptSection("これで部屋着じゃなかったら");

        // 只對到範圍內［30000, 48000］的 cue，範圍外即使完全一樣也不採用
        Assert.StartsWith("# 參考：同一時段的 YouTube 自動字幕", section);
        Assert.Contains("ぴんくの部屋着じゃなかったら", section);
        Assert.DoesNotContain("これで部屋着", section);
    }

    [Fact]
    public void BuildUserPromptSection_IdenticalCueOutOfRange_ReturnsEmpty()
    {
        var captions = new CaptionTrackState();
        captions.Set("vod", [Cue(200_000, "これで部屋着じゃなかったら")]);
        var sut = new CaptionReference(captions, new FakePlaybackClock { PositionMs = 45_000 });

        var section = sut.BuildUserPromptSection("これで部屋着じゃなかったら");

        Assert.Equal(string.Empty, section);
    }

    [Fact]
    public void BuildUserPromptSection_NoClockNeverMatched_ShortText_ReturnsEmpty()
    {
        var captions = new CaptionTrackState();
        captions.Set("vod", [Cue(1000, "うん、またね")]);
        var sut = new CaptionReference(captions, null);

        // 從來沒對到過：短於 8 字不參與全片比對，避免誤配
        Assert.Equal(string.Empty, sut.BuildUserPromptSection("うん"));
        Assert.Equal(string.Empty, sut.BuildUserPromptSection("どうしよう"));
    }

    [Fact]
    public void BuildUserPromptSection_NoClockNeverMatched_LongTextHighScore_Matches()
    {
        var captions = new CaptionTrackState();
        captions.Set("vod", [Cue(1000, "ぴんくの部屋着じゃなかったら")]);
        var sut = new CaptionReference(captions, null);

        var section = sut.BuildUserPromptSection("これで部屋着じゃなかったら");

        // 8 字以上而且分數 ≥ 0.6：在全片找得到（0.8 未達 0.9，仍放參考）
        Assert.StartsWith("# 參考：同一時段的 YouTube 自動字幕", section);
        Assert.Contains("ぴんくの部屋着じゃなかったら", section);
    }

    [Fact]
    public void BuildUserPromptSection_CueContainsSurrounding_ReturnsOnlyMatchedPart()
    {
        var captions = new CaptionTrackState();
        // 直播 23 的實際例子：cue 含前一句的結尾（もう橋）與後續（そんな），只留對到本句的那段
        captions.Set("vod", [Cue(50_000, "もう橋 接設計者の風にも受けないねえ。そんな")]);
        var sut = new CaptionReference(captions, new FakePlaybackClock { PositionMs = 55_000 });

        var section = sut.BuildUserPromptSection("私設計者の風上に設けない");

        Assert.StartsWith("# 參考：同一時段的 YouTube 自動字幕", section);
        Assert.Contains("設計者の風にも受けない", section);
        Assert.DoesNotContain("もう橋", section);
        Assert.DoesNotContain("そんな", section);
    }

    [Fact]
    public void BuildUserPromptSection_NearlyIdentical_ReturnsEmptyButKeepsLastMatch()
    {
        var captions = new CaptionTrackState();
        captions.Set("vod", [Cue(50_000, "これで部屋着じゃなかったら"), Cue(60_000, "今日はいい天気です")]);
        var clock = new FakePlaybackClock { PositionMs = 55_000 };
        var sut = new CaptionReference(captions, clock);

        // 和本句幾乎相同（≥ 0.9）：沒有要修正的地方，不放參考
        Assert.Equal(string.Empty, sut.BuildUserPromptSection("これで部屋着じゃなかったら"));

        // _lastMatchMs 仍有更新：時鐘失去位置後，下一句還能在上次對到位置附近對到
        clock.PositionMs = null;
        var next = sut.BuildUserPromptSection("今日はとてもいい天気です");

        Assert.StartsWith("# 參考：同一時段的 YouTube 自動字幕", next);
        Assert.Contains("今日はいい天気です", next);
    }

    [Fact]
    public void BuildUserPromptSection_AfterMatchClockLost_SearchesNearLastMatch()
    {
        var captions = new CaptionTrackState();
        captions.Set("vod",
        [
            Cue(50_000, "ぴんくの部屋着じゃなかったら"),
            Cue(60_000, "今日はいい天気です"),
            Cue(900_000, "今日はとてもいい天気です"),
        ]);
        var clock = new FakePlaybackClock { PositionMs = 55_000 };
        var sut = new CaptionReference(captions, clock);

        var first = sut.BuildUserPromptSection("これで部屋着じゃなかったら");
        Assert.StartsWith("# 參考：同一時段的 YouTube 自動字幕", first);

        // 播放器沒回應位置：改在上次對到位置［−30000, +60000］內找下一句
        clock.PositionMs = null;
        var second = sut.BuildUserPromptSection("今日はとてもいい天気です");

        Assert.StartsWith("# 參考：同一時段的 YouTube 自動字幕", second);
        // 對到範圍內的 cue（60 秒），不是範圍外完全一樣的（900 秒）
        Assert.Contains("今日はいい天気です", second);
        Assert.DoesNotContain("とても", second);
    }

    [Fact]
    public void BuildUserPromptSection_CaptionsNotLoaded_ReturnsEmpty()
    {
        var sut = new CaptionReference(new CaptionTrackState(), new FakePlaybackClock { PositionMs = 1000 });

        Assert.Equal(string.Empty, sut.BuildUserPromptSection("これで部屋着じゃなかったら"));
    }

    [Fact]
    public void BuildUserPromptSection_ResultHasNoCarriageReturn()
    {
        var captions = new CaptionTrackState();
        captions.Set("vod", [Cue(1000, "ぴんくの部屋着じゃなかったら")]);
        var sut = new CaptionReference(captions, null);

        var section = sut.BuildUserPromptSection("これで部屋着じゃなかったら");

        Assert.StartsWith("# 參考：同一時段的 YouTube 自動字幕", section);
        Assert.DoesNotContain('\r', section);
    }

    [Fact]
    public void CaptionTrackChanged_RaisesReset_OfLastMatch()
    {
        var captions = new CaptionTrackState();
        captions.Set("vod1", [Cue(50_000, "ぴんくの部屋着じゃなかったら")]);
        var clock = new FakePlaybackClock { PositionMs = 55_000 };
        var sut = new CaptionReference(captions, clock);

        var first = sut.BuildUserPromptSection("これで部屋着じゃなかったら");
        Assert.NotEqual(string.Empty, first);

        // 換影片（Set 觸發 Changed）：上次對到的位置重置，短句不再沿用它
        captions.Set("vod2", [Cue(60_000, "どうしよう")]);
        clock.PositionMs = null;

        // 若 _lastMatchMs 未重置，會在舊位置附近以門檻 0.35 對到「どうしよう」而回傳非空
        Assert.Equal(string.Empty, sut.BuildUserPromptSection("どうしよう"));
    }

    [Fact]
    public void CaptionTrackCleared_RaisesReset_OfLastMatch()
    {
        var captions = new CaptionTrackState();
        captions.Set("vod1", [Cue(50_000, "ぴんくの部屋着じゃなかったら")]);
        var clock = new FakePlaybackClock { PositionMs = 55_000 };
        var sut = new CaptionReference(captions, clock);

        Assert.NotEqual(string.Empty, sut.BuildUserPromptSection("これで部屋着じゃなかったら"));

        captions.Clear();
        clock.PositionMs = null;

        // 清空後沒有字幕：一律空字串
        Assert.Equal(string.Empty, sut.BuildUserPromptSection("これで部屋着じゃなかったら"));
    }
}
