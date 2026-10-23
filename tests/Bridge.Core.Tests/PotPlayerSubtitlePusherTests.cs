using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>PotPlayerSubtitlePusher：前處理句子有變動時（debounce）重新產生 SRT 交給 PotPlayer
/// （假的 IPotPlayerSubtitleLoader 與假的字幕下載，不連網、不啟動任何程式）。</summary>
public class PotPlayerSubtitlePusherTests
{
    private const string VideoId = "dQw4w9WgXcQ";

    private static readonly CaptionCue[] TwoCues =
    [
        new CaptionCue(1000, 2000, "こんにちは"),
        new CaptionCue(3000, 2000, "さようなら"),
    ];

    /// <summary>預期的 SRT 內容（兩句前處理句子蓋住兩段 cue，結束時間裁到下一句開始）。</summary>
    private const string ExpectedSrt =
        "1\r\n00:00:01,000 --> 00:00:03,000\r\nこんにちは。\r\n" +
        "\r\n" +
        "2\r\n00:00:03,000 --> 00:00:05,000\r\nさようなら。\r\n";

    [Fact]
    public async Task Changed_MultipleTriggersWithinDebounce_PushesOnceWithSrtFile()
    {
        using var h = new Harness();
        await h.LoadForPlayingVideoAsync();

        // 連續觸發三次（Reset + 兩次 Add），全部落在同一個 debounce 窗口內
        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);
        h.Sentences.Add(VideoId, [Sentence(3000, 5000, "さようなら。")]);

        await UntilAsync(() => h.PotPlayer.Loads == 1);
        // 等到遠超過 debounce 之後確認沒有排第二次
        await PassAsync(TimeSpan.FromMilliseconds(250));
        Assert.Equal(1, h.PotPlayer.Loads);

        var path = Path.Combine(h.Dir.FullName, $"{VideoId}.ja.srt");
        Assert.Equal(path, Assert.Single(h.PotPlayer.Paths));
        Assert.True(File.Exists(path));

        // 內容是 UTF-8 加 BOM 的 SRT
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("\uFEFF" + ExpectedSrt, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Changed_Disabled_DoesNotPushOrWriteFile()
    {
        using var h = new Harness();
        await h.LoadForPlayingVideoAsync();
        h.Enabled = false;

        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);

        await PassAsync(TimeSpan.FromMilliseconds(250));
        Assert.Equal(0, h.PotPlayer.Loads);
        Assert.False(File.Exists(Path.Combine(h.Dir.FullName, $"{VideoId}.ja.srt")));
    }

    [Fact]
    public async Task Changed_PlayingVideoMismatch_DoesNotPush()
    {
        using var h = new Harness();
        // 不經過 LoadAsync：PotPlayer 沒有正在播放這支影片（PlayingVideoId 為 null）
        h.Captions.Set(VideoId, TwoCues);

        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);

        await PassAsync(TimeSpan.FromMilliseconds(250));
        Assert.Equal(0, h.PotPlayer.Loads);
        Assert.False(File.Exists(Path.Combine(h.Dir.FullName, $"{VideoId}.ja.srt")));
    }

    [Fact]
    public async Task Push_UnchangedContent_SkipsSecondPush_NewSentencePushesAgain()
    {
        using var h = new Harness();
        await h.LoadForPlayingVideoAsync();

        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);
        await UntilAsync(() => h.PotPlayer.Loads == 1);

        // 再觸發一次但句子內容相同：不推送
        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);
        await PassAsync(TimeSpan.FromMilliseconds(250));
        Assert.Equal(1, h.PotPlayer.Loads);

        // 句子增加（內容變了）：會再推送
        h.Sentences.Add(VideoId, [Sentence(3000, 5000, "さようなら。")]);
        await UntilAsync(() => h.PotPlayer.Loads == 2);
        Assert.Equal(2, h.PotPlayer.Paths.Count);
    }

    [Fact]
    public async Task Push_PotPlayerLoadFails_RetriesOnNextTriggerWithSameContent()
    {
        using var h = new Harness();
        await h.LoadForPlayingVideoAsync();
        h.PotPlayer.Result = false;

        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);
        await UntilAsync(() => h.PotPlayer.Loads == 1);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(1, h.PotPlayer.Loads);

        // 失敗不記內容：下一次觸發即使內容相同也會再推送
        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);
        await UntilAsync(() => h.PotPlayer.Loads == 2);
    }

    [Fact]
    public async Task Push_PotPlayerLoadThrows_DoesNotPropagate()
    {
        using var h = new Harness();
        await h.LoadForPlayingVideoAsync();
        h.PotPlayer.Throw = new InvalidOperationException("boom");

        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);

        // 例外被 pusher 吸收：不往外拋（測試沒失敗即通過），也沒有重試風暴
        await UntilAsync(() => h.PotPlayer.Loads == 1);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(1, h.PotPlayer.Loads);
    }

    [Fact]
    public async Task CaptionsLoaded_WithoutSentences_PushesMergedCues()
    {
        using var h = new Harness();
        // 只載入 YouTube 字幕、前處理還沒開始：字幕一載入就推送程式合併的版本
        await h.LoadForPlayingVideoAsync();

        await UntilAsync(() => h.PotPlayer.Loads == 1);
        var srt = await File.ReadAllTextAsync(Assert.Single(h.PotPlayer.Paths), TestContext.Current.CancellationToken);
        Assert.Contains("こんにちは", srt);
        Assert.Contains("さようなら", srt);
    }

    // 建構時清掉輸出資料夾裡所有舊字幕檔。
    [Fact]
    public void Constructor_DeletesExistingSrtFiles()
    {
        using var h = new Harness(dir => File.WriteAllText(Path.Combine(dir.FullName, "old.ja.srt"), "舊字幕"));

        Assert.False(File.Exists(Path.Combine(h.Dir.FullName, "old.ja.srt")));
    }

    // 推送成功後：只留目前正在播放的影片那份，其他影片的字幕檔刪掉。
    [Fact]
    public async Task Push_LoadSucceeds_DeletesOtherVideosSrtFiles()
    {
        using var h = new Harness();
        File.WriteAllText(Path.Combine(h.Dir.FullName, "videoB.ja.srt"), "其他影片的字幕");
        await h.LoadForPlayingVideoAsync();

        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);
        await UntilAsync(() => h.PotPlayer.Loads == 1);

        Assert.False(File.Exists(Path.Combine(h.Dir.FullName, "videoB.ja.srt")));
        Assert.True(File.Exists(Path.Combine(h.Dir.FullName, $"{VideoId}.ja.srt")));
    }

    // PotPlayer 不在執行（Load 回 false）：不清理其他檔案，下次再試。
    [Fact]
    public async Task Push_LoadFails_KeepsOtherSrtFiles()
    {
        using var h = new Harness();
        h.PotPlayer.Result = false;
        File.WriteAllText(Path.Combine(h.Dir.FullName, "videoB.ja.srt"), "其他影片的字幕");
        await h.LoadForPlayingVideoAsync();

        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);
        await UntilAsync(() => h.PotPlayer.Loads == 1);

        Assert.True(File.Exists(Path.Combine(h.Dir.FullName, "videoB.ja.srt")));
    }

    // 推送成功時觸發 Pushed：參數是影片與這次推送的字幕行。
    [Fact]
    public async Task Push_LoadSucceeds_TriggersPushedEventWithVideoAndLines()
    {
        using var h = new Harness();
        string? pushedVideoId = null;
        IReadOnlyList<SubtitleLine>? pushedLines = null;
        h.Pusher.Pushed += (videoId, lines) => { pushedVideoId = videoId; pushedLines = lines; };

        await h.LoadForPlayingVideoAsync();
        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);

        await UntilAsync(() => pushedVideoId is not null);
        Assert.Equal(VideoId, pushedVideoId);
        Assert.NotNull(pushedLines);
        // 前處理句子蓋住兩段 cue（含結束時間重疊的第二段）：只剩這一句
        var line = Assert.Single(pushedLines!);
        Assert.Equal("こんにちは。", line.Text);
        Assert.True(line.FromSentence);
    }

    // 推送失敗（找不到正在執行的 PotPlayer）時不觸發 Pushed。
    [Fact]
    public async Task Push_LoadFails_DoesNotTriggerPushed()
    {
        using var h = new Harness();
        h.PotPlayer.Result = false;
        var count = 0;
        h.Pusher.Pushed += (_, _) => count++;

        await h.LoadForPlayingVideoAsync();
        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);

        await UntilAsync(() => h.PotPlayer.Loads == 1);
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(0, count);
    }

    // 訂閱端擲例外時：只記 Warning log，不往外拋，推送照樣完成。
    [Fact]
    public async Task Push_SubscriberThrows_DoesNotPropagate()
    {
        using var h = new Harness();
        h.Pusher.Pushed += (_, _) => throw new InvalidOperationException("boom");

        await h.LoadForPlayingVideoAsync();
        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);

        await UntilAsync(() => h.PotPlayer.Loads == 1);
        Assert.True(File.Exists(Path.Combine(h.Dir.FullName, $"{VideoId}.ja.srt")));
    }

    private static CaptionSentence Sentence(long startMs, long endMs, string text) =>
        new(startMs, endMs, text, text, "m");

    /// <summary>組出待測物件：暫存輸出資料夾、假的字幕下載（固定兩段 cue）與假的 PotPlayer。</summary>
    private sealed class Harness : IDisposable
    {
        public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory("potplayer-pusher-");

        public CaptionSentenceState Sentences { get; } = new();

        public CaptionTrackState Captions { get; } = new();

        public CaptionLoader Loader { get; }

        public FakePotPlayerLoader PotPlayer { get; } = new();

        public bool Enabled { get; set; } = true;

        public PotPlayerSubtitlePusher Pusher { get; }

        public Harness(Action<DirectoryInfo>? beforeCreatePusher = null)
        {
            Loader = new CaptionLoader(Captions, new StubProvider());
            beforeCreatePusher?.Invoke(Dir);
            Pusher = new PotPlayerSubtitlePusher(
                Sentences, Captions, Loader, PotPlayer, () => Enabled, Dir.FullName,
                NullLogger<PotPlayerSubtitlePusher>.Instance)
            {
                Debounce = TimeSpan.FromMilliseconds(50),
            };
        }

        /// <summary>經由 LoadAsync 載入影片：PotPlayer 正在播放這支影片（PlayingVideoId）且原始 cue 已就緒。</summary>
        public Task LoadForPlayingVideoAsync() => Loader.LoadAsync(VideoId, null);

        public void Dispose()
        {
            Pusher.Dispose();
            try { Dir.Delete(recursive: true); } catch (IOException) { /* 寫入中的檔案留給系統清 */ }
        }
    }

    /// <summary>記錄呼叫次數與路徑、可控制回傳值與例外的假 PotPlayer 字幕載入器。</summary>
    private sealed class FakePotPlayerLoader : IPotPlayerSubtitleLoader
    {
        public List<string> Paths { get; } = [];

        public int Loads { get; private set; }

        public bool Result { get; set; } = true;

        public Exception? Throw { get; set; }

        public bool Load(string subtitlePath)
        {
            Loads++;
            if (Throw is not null)
            {
                throw Throw;
            }

            Paths.Add(subtitlePath);
            return Result;
        }
    }

    /// <summary>不連網的假字幕下載：固定回傳兩段日文字幕。</summary>
    private sealed class StubProvider : IYouTubeCaptionProvider
    {
        public Task<YouTubeCaptionResult?> GetJapaneseCaptionsAsync(
            string videoId, string? visitorData, CancellationToken cancellationToken = default) =>
            Task.FromResult<YouTubeCaptionResult?>(new YouTubeCaptionResult("ja", true, "IOS", TwoCues));
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

    /// <summary>輪詢度過指定的時間：供「沒有再推送」這類反面條件等到超過 debounce 窗口再斷言。</summary>
    private static async Task PassAsync(TimeSpan time)
    {
        var deadline = DateTime.UtcNow + time;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }
}
