using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>PlaybackLineTracker：記住 PotPlayerSubtitlePusher 推送的字幕行，依播放位置找目前那一句
///（沿用 PotPlayerSubtitlePusherTests 的 Harness 寫法，Pushed 事件由真的推送一次觸發）。</summary>
public class PlaybackLineTrackerTests
{
    private const string VideoId = "dQw4w9WgXcQ";

    // 還沒推送過：任何位置都找不到。
    [Fact]
    public void FindTextAt_NoPush_ReturnsNull()
    {
        using var h = new Harness();

        Assert.Null(h.Tracker.FindTextAt(1500));
        Assert.Null(h.Tracker.FindTextAt(0));
    }

    // 位置在某一行內：回傳該行文字（經 CueText：換行換空白、「-->」換「->」）。
    [Fact]
    public async Task FindTextAt_PositionInsideLine_ReturnsCueText()
    {
        using var h = new Harness();
        await h.PushAsync(Sentence(1000, 3000, "こんにちは\n世界 --> テスト"));

        Assert.Equal("こんにちは 世界 -> テスト", h.Tracker.FindTextAt(1000));
        Assert.Equal("こんにちは 世界 -> テスト", h.Tracker.FindTextAt(2999));
    }

    // 位置在兩行之間的空檔（Build 會把每一行至少撐到 StartMs + 500，之後的空檔）與第一行之前：回 null。
    [Fact]
    public async Task FindTextAt_BetweenLines_ReturnsNull()
    {
        using var h = new Harness();
        await h.PushAsync(Sentence(1000, 1200, "おはよう"), Sentence(2000, 2200, "こんにちは"));

        // 第一行 [1000,1500)、第二行 [2000,2500)（結束時間都至少撐到 StartMs + 500）
        Assert.Equal("おはよう", h.Tracker.FindTextAt(1499));
        Assert.Null(h.Tracker.FindTextAt(1500));
        Assert.Null(h.Tracker.FindTextAt(1999));
        Assert.Equal("こんにちは", h.Tracker.FindTextAt(2000));
        // 第一行之前
        Assert.Null(h.Tracker.FindTextAt(999));
    }

    // 多行重疊時取 StartMs 最大的那一行（Build 撐出的最少顯示時間會讓相鄰兩行重疊 100ms）。
    [Fact]
    public async Task FindTextAt_OverlappingLines_ReturnsLargestStartMs()
    {
        using var h = new Harness();
        await h.PushAsync(Sentence(1000, 1200, "おはよう"), Sentence(1400, 1600, "こんにちは"));

        // 第一行 [1000,1500)、第二行 [1400,1900)：[1400,1500) 重疊，取第二行（StartMs 較大）
        Assert.Equal("おはよう", h.Tracker.FindTextAt(1399));
        Assert.Equal("こんにちは", h.Tracker.FindTextAt(1400));
        Assert.Equal("こんにちは", h.Tracker.FindTextAt(1499));
    }

    // 再推送一次（內容不同）：查詢改用新內容，舊的字幕行不再出現。
    [Fact]
    public async Task SecondPush_ReplacesLines()
    {
        using var h = new Harness();
        await h.PushAsync(Sentence(1000, 3000, "一句目"));
        Assert.Equal("一句目", h.Tracker.FindTextAt(1500));
        Assert.Null(h.Tracker.FindTextAt(4500));

        await h.PushAsync(Sentence(1000, 2000, "違う句"), Sentence(4000, 6000, "二句目"));
        Assert.Equal("違う句", h.Tracker.FindTextAt(1500));
        Assert.Equal("二句目", h.Tracker.FindTextAt(4500));
    }

    // Normalize：去頭尾空白、連續空白（含全形空白、換行、定位字元）合併成一個半形空白。
    [Fact]
    public void Normalize_CollapsesAllWhitespaceIntoSingleSpaces()
    {
        Assert.Equal("あ い う", PlaybackLineTracker.Normalize("  あ　い う  "));
        Assert.Equal("a b c d", PlaybackLineTracker.Normalize("a\n\nb\r\nc\td"));
        Assert.Equal("テスト", PlaybackLineTracker.Normalize("テスト"));
        Assert.Equal(string.Empty, PlaybackLineTracker.Normalize("　 \n"));
        Assert.Equal(string.Empty, PlaybackLineTracker.Normalize(string.Empty));
    }

    // 大量行（2000 行）：各行內找得到自己的文字、行與行之間的空檔是 null。
    [Fact]
    public async Task FindTextAt_ManyLines_FindsEachLine()
    {
        const int count = 2000;
        var sentences = Enumerable.Range(0, count)
            .Select(i => Sentence(i * 2000L, i * 2000L + 1500, $"行{i}"))
            .ToArray();
        using var h = new Harness();
        await h.PushAsync(sentences);

        Assert.Equal("行0", h.Tracker.FindTextAt(100));
        Assert.Equal("行1000", h.Tracker.FindTextAt(1000 * 2000 + 700));
        Assert.Equal("行1999", h.Tracker.FindTextAt(1999L * 2000 + 700));
        // 每行 [i*2000, i*2000+1500)，其後 500ms 是空檔
        Assert.Null(h.Tracker.FindTextAt(1600));
        Assert.Null(h.Tracker.FindTextAt(2001600));
        // 第一行之前與最後一行之後
        Assert.Null(h.Tracker.FindTextAt(-1));
        Assert.Null(h.Tracker.FindTextAt(1999L * 2000 + 1500));
    }

    private static CaptionSentence Sentence(long startMs, long endMs, string text) =>
        new(startMs, endMs, text, text, "m");

    /// <summary>組出待測物件：沿用 PotPlayerSubtitlePusherTests 的做法（暫存資料夾、假的字幕下載與 PotPlayer），
    /// Pushed 事件由 CaptionSentenceState 的變動經真的推送觸發。</summary>
    private sealed class Harness : IDisposable
    {
        public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory("playback-tracker-");

        public CaptionSentenceState Sentences { get; } = new();

        public CaptionTrackState Captions { get; } = new();

        public CaptionLoader Loader { get; }

        public FakePotPlayerLoader PotPlayer { get; } = new();

        public PotPlayerSubtitlePusher Pusher { get; }

        public PlaybackLineTracker Tracker { get; }

        public Harness()
        {
            Loader = new CaptionLoader(Captions, new StubProvider());
            Pusher = new PotPlayerSubtitlePusher(
                Sentences, Captions, Loader, PotPlayer, () => true, Dir.FullName,
                NullLogger<PotPlayerSubtitlePusher>.Instance)
            {
                Debounce = TimeSpan.FromMilliseconds(50),
            };
            Tracker = new PlaybackLineTracker(Pusher);
        }

        /// <summary>載入影片（PotPlayer 正在播放、cue 已就緒）後推送句子，等到 Pushed 真的帶著這批句子觸發：
        /// 載入字幕當下的第一次推送可能只有程式合併的 cue（不含句子），要比對內容而不是只看推送次數。</summary>
        public async Task PushAsync(params CaptionSentence[] sentences)
        {
            await Loader.LoadAsync(VideoId, null);
            var last = sentences[^1];
            var expected = CaptionSubtitleBuilder.CueText(last.Text);
            Sentences.Reset(VideoId, 1);
            Sentences.Add(VideoId, sentences);
            await UntilAsync(() => Tracker.FindTextAt(last.StartMs) == expected);
        }

        public void Dispose()
        {
            Tracker.Dispose();
            Pusher.Dispose();
            try { Dir.Delete(recursive: true); } catch (IOException) { /* 寫入中的檔案留給系統清 */ }
        }
    }

    /// <summary>記錄呼叫次數的假 PotPlayer 字幕載入器（一律成功）。</summary>
    private sealed class FakePotPlayerLoader : IPotPlayerSubtitleLoader
    {
        public int Loads { get; private set; }

        public bool Load(string subtitlePath)
        {
            Loads++;
            return true;
        }
    }

    /// <summary>不連網的假字幕下載：固定回一段 cue（Push 需要 Cues 非空；文字用「ダミー」，
    /// 讓只有 cue 的推送與帶句子的推送可以區分）。</summary>
    private sealed class StubProvider : IYouTubeCaptionProvider
    {
        public Task<YouTubeCaptionResult?> GetJapaneseCaptionsAsync(
            string videoId, string? visitorData, CancellationToken cancellationToken = default) =>
            Task.FromResult<YouTubeCaptionResult?>(new YouTubeCaptionResult(
                "ja", true, "IOS", [new CaptionCue(1000, 2000, "ダミー")]));
    }

    /// <summary>輪詢直到條件成立（逾時 5 秒視為失敗）：不用固定的 Delay 等背景推送。</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等候條件逾時");
            await Task.Delay(10);
        }
    }
}
