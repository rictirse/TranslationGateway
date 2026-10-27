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
        var srt = await ReadSrtAsync(Assert.Single(h.PotPlayer.Paths));
        Assert.Contains("こんにちは", srt);
        Assert.Contains("さようなら", srt);
    }

    // 第一次推送要等播放位置確實在前進：clock 一直回 null／卡在 0 → 不推送；開始遞增後才推一次。
    [Fact]
    public async Task FirstPush_WaitsUntilPositionAdvances()
    {
        var clock = new FakePlaybackClock(null, null, 0L);
        using var h = new Harness(playbackClock: clock);
        await h.LoadForPlayingVideoAsync();

        // 過了 debounce 之後位置始終沒在前進：一直不推送
        await PassAsync(TimeSpan.FromMilliseconds(400));
        Assert.Equal(0, h.PotPlayer.Loads);

        clock.Advance();
        await UntilAsync(() => h.PotPlayer.Loads == 1);
        Assert.True(File.Exists(Path.Combine(h.Dir.FullName, $"{VideoId}.ja.srt")));
    }

    // 位置一直卡住：超過 PlaybackStartTimeout 後照常推送（不會永遠不推），而且只推一次。
    [Fact]
    public async Task FirstPush_PositionStuck_PushesAfterTimeout()
    {
        var clock = new FakePlaybackClock(null, 0L);
        using var h = new Harness(playbackClock: clock);
        h.Pusher.PlaybackStartTimeout = TimeSpan.FromMilliseconds(300);
        await h.LoadForPlayingVideoAsync();

        // 逾時前不推送
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(0, h.PotPlayer.Loads);

        await UntilAsync(() => h.PotPlayer.Loads == 1);
        await PassAsync(TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, h.PotPlayer.Loads);
    }

    // 同一支影片確認過開始播放後：位置又卡住也照常推送，不再等。
    [Fact]
    public async Task SecondPushSameVideo_DoesNotWait()
    {
        var clock = new FakePlaybackClock(null, 0L);
        using var h = new Harness(playbackClock: clock);
        await h.LoadForPlayingVideoAsync();
        clock.Advance();
        await UntilAsync(() => h.PotPlayer.Loads == 1);

        // 逾時仍是預設的 90 秒：若有等到，UntilAsync 會逾時失敗
        clock.Hold();
        h.Sentences.Reset(VideoId, 1);
        h.Sentences.Add(VideoId, [Sentence(1000, 3000, "こんにちは。")]);
        await UntilAsync(() => h.PotPlayer.Loads == 2);
    }

    // 換影片後要重新等播放開始（舊影片確認過不算）。
    [Fact]
    public async Task NewVideo_WaitsAgain()
    {
        const string videoB = "bbbbbbbbbbb";
        var clock = new FakePlaybackClock(null, 0L);
        using var h = new Harness(playbackClock: clock);
        await h.LoadForPlayingVideoAsync();
        clock.Advance();
        await UntilAsync(() => h.PotPlayer.Loads == 1);

        clock.Hold();
        await h.Loader.LoadAsync(videoB, null);
        await PassAsync(TimeSpan.FromMilliseconds(400));
        Assert.Equal(1, h.PotPlayer.Loads);

        clock.Advance();
        await UntilAsync(() => h.PotPlayer.Loads == 2);
        Assert.Equal($"{videoB}.ja.srt", Path.GetFileName(h.PotPlayer.Paths[^1]));
    }

    // 重開同一支影片（開片時字幕狀態先被清空）：要重新等播放開始，不能沿用上一次的確認
    //（使用者實測：第一次看正常，重開同一支時開片 10 秒就推送、打斷 PotPlayer 載入）。
    [Fact]
    public async Task ReopenSameVideo_WaitsAgain()
    {
        var clock = new FakePlaybackClock(null, 0L);
        using var h = new Harness(playbackClock: clock);
        await h.LoadForPlayingVideoAsync();
        clock.Advance();
        await UntilAsync(() => h.PotPlayer.Loads == 1);

        // 開片流程：CaptionLoader.Reset（清空字幕狀態）後重新載入同一支影片；播放位置卡住 → 不推送
        clock.Hold();
        h.Loader.Reset();
        await h.LoadForPlayingVideoAsync();
        await PassAsync(TimeSpan.FromMilliseconds(400));
        Assert.Equal(1, h.PotPlayer.Loads);

        clock.Advance();
        await UntilAsync(() => h.PotPlayer.Loads == 2);
    }

    // 沒有播放時鐘（playbackClock = null）：行為完全不變，載入字幕就照 debounce 推送，不多等。
    [Fact]
    public async Task NoClock_BehaviorUnchanged()
    {
        using var h = new Harness();
        await h.LoadForPlayingVideoAsync();

        await UntilAsync(() => h.PotPlayer.Loads == 1);
        var srt = await ReadSrtAsync(Assert.Single(h.PotPlayer.Paths));
        Assert.Contains("こんにちは", srt);
    }

    // 等播放開始期間 Dispose：取消等待、不推送、不寫檔。
    [Fact]
    public async Task Dispose_WhileWaiting_NoPush()
    {
        var clock = new FakePlaybackClock(null, 0L);
        using var h = new Harness(playbackClock: clock);
        h.Pusher.PlaybackStartTimeout = TimeSpan.FromSeconds(30);
        await h.LoadForPlayingVideoAsync();

        // 確認已進入等待（時鐘被讀了幾次）再 Dispose
        await UntilAsync(() => clock.Reads >= 2);
        h.Pusher.Dispose();
        await PassAsync(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, h.PotPlayer.Loads);
        Assert.False(File.Exists(Path.Combine(h.Dir.FullName, $"{VideoId}.ja.srt")));
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

    // 有章節疊加時：寫出的 SRT 含 {\an7\fs12} 的章節名（只放日文原文；中文由 PotPlayer 送來翻譯時直接回傳）。
    [Fact]
    public async Task Push_WithChapterOverlay_SrtContainsChapterTitle()
    {
        var overlay = NewChapterOverlay("""{"translations": ["配信開始", "第10名 回る空うさぎ - Orangestar", "第9名 Booo! ／ TOKOTOKO"]}""");
        using var h = new Harness(chapterOverlay: overlay, vod: new VodSettings());

        await h.LoadForPlayingVideoAsync();
        await overlay.LoadAsync(VideoId, DescriptionChapters, TestContext.Current.CancellationToken);

        var srt = await WaitSrtContainsAsync(h, "{\\an7\\fs12}");
        Assert.Contains("{\\an7\\fs12}10位 回る空うさぎ - Orangestar", srt, StringComparison.Ordinal);
        // 譯文不進 SRT（雙語顯示由 PotPlayer 處理）
        Assert.DoesNotContain("第10名", srt, StringComparison.Ordinal);
        // 一般字幕照常在 SRT 裡
        Assert.Contains("こんにちは", srt, StringComparison.Ordinal);
    }

    // 章節標題翻譯還沒結束（TranslationSettled = false）：SRT 不含 overlay；翻譯結束後才會含。
    [Fact]
    public async Task Push_ChapterTitleTranslationUnsettled_SrtHasNoOverlayUntilSettled()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = NewGatedChapterOverlay(
            gate, """{"translations": ["配信開始", "第10名 回る空うさぎ - Orangestar", "第9名 Booo! ／ TOKOTOKO"]}""");
        using var h = new Harness(chapterOverlay: overlay, vod: new VodSettings());

        await h.LoadForPlayingVideoAsync();
        // 章節載入了（第一次 Changed），但標題翻譯卡在 gate 上：推送不含 overlay
        var load = overlay.LoadAsync(VideoId, DescriptionChapters, TestContext.Current.CancellationToken);
        await UntilAsync(() => h.PotPlayer.Loads == 1);
        await PassAsync(TimeSpan.FromMilliseconds(250));
        var srtBefore = await ReadSrtAsync(Path.Combine(h.Dir.FullName, $"{VideoId}.ja.srt"));
        Assert.False(overlay.TranslationSettled);
        Assert.DoesNotContain("\\an7", srtBefore, StringComparison.Ordinal);

        // 翻譯結束（Changed 觸發重新推送）：SRT 含章節名
        gate.SetResult();
        await load;
        var srtAfter = await WaitSrtContainsAsync(h, "{\\an7\\fs12}");
        Assert.Contains("10位 回る空うさぎ - Orangestar", srtAfter, StringComparison.Ordinal);
    }

    // ShowChapterTitles = false：不加章節疊加。
    [Fact]
    public async Task Push_ShowChapterTitlesDisabled_SrtHasNoOverlay()
    {
        var overlay = NewChapterOverlay("""{"translations": ["配信開始", "第10名 回る空うさぎ - Orangestar", "第9名 Booo! ／ TOKOTOKO"]}""");
        using var h = new Harness(chapterOverlay: overlay, vod: new VodSettings { ShowChapterTitles = false });

        await h.LoadForPlayingVideoAsync();
        await overlay.LoadAsync(VideoId, DescriptionChapters, TestContext.Current.CancellationToken);

        var srt = await WaitSrtContainsAsync(h, "こんにちは");
        Assert.DoesNotContain("\\an7", srt, StringComparison.Ordinal);
    }

    // Pushed 事件仍只傳一般字幕的 lines：章節名不能被當成字幕拿去預翻。
    [Fact]
    public async Task Push_PushedEventLines_DoNotContainOverlay()
    {
        var overlay = NewChapterOverlay("""{"translations": ["配信開始", "第10名 回る空うさぎ - Orangestar", "第9名 Booo! ／ TOKOTOKO"]}""");
        using var h = new Harness(chapterOverlay: overlay, vod: new VodSettings());
        IReadOnlyList<SubtitleLine>? pushedLines = null;
        h.Pusher.Pushed += (_, lines) => pushedLines = lines;

        await h.LoadForPlayingVideoAsync();
        await overlay.LoadAsync(VideoId, DescriptionChapters, TestContext.Current.CancellationToken);
        await WaitSrtContainsAsync(h, "{\\an7\\fs12}");

        Assert.NotNull(pushedLines);
        Assert.All(pushedLines!, static line => Assert.DoesNotContain("\\an7", line.Text, StringComparison.Ordinal));
        // 只有程式合併的那一句字幕（兩段 cue 沒有句尾符號、相距不到 3 秒 → 合併成一句）
        var line = Assert.Single(pushedLines!);
        Assert.Equal("こんにちはさようなら", line.Text);
    }

    // 章節狀態變動（Changed）也走 debounce 推送。
    [Fact]
    public async Task ChapterOverlayChanged_TriggersPush()
    {
        var overlay = NewChapterOverlay("""{"translations": ["配信開始", "第10名 回る空うさぎ - Orangestar", "第9名 Booo! ／ TOKOTOKO"]}""");
        using var h = new Harness(chapterOverlay: overlay, vod: new VodSettings());

        await h.LoadForPlayingVideoAsync();
        await UntilAsync(() => h.PotPlayer.Loads == 1);
        var srtAfterFirstPush = await ReadSrtAsync(Path.Combine(h.Dir.FullName, $"{VideoId}.ja.srt"));
        Assert.DoesNotContain("\\an7", srtAfterFirstPush, StringComparison.Ordinal);

        // 章節載入＋譯文回來（兩次 Changed）：觸發第二次推送，SRT 有章節名
        await overlay.LoadAsync(VideoId, DescriptionChapters, TestContext.Current.CancellationToken);
        await WaitSrtContainsAsync(h, "{\\an7\\fs12}");
        Assert.True(h.PotPlayer.Loads >= 2);
    }

    /// <summary>說明欄章節：第一章 0:00、三章（其中「開始」不會出現，items 共 3 章）。</summary>
    private const string DescriptionChapters =
        "0:00 配信開始\n" +
        "13:12 10位 回る空うさぎ - Orangestar\n" +
        "17:33 9位 Booo! ／ TOKOTOKO";

    /// <summary>組一個用假知識庫模型的 ChapterOverlayState（說明欄有章節，不會打到留言提供者）。</summary>
    private static ChapterOverlayState NewChapterOverlay(string llmReply) => new(
        new VideoChapterService(new EmptyCommentProvider(), NullLogger<VideoChapterService>.Instance),
        new ChapterTitleTranslator(
            new StaticReplyLlm(llmReply), new GlossaryState(), new TextProcessingService(),
            NullLogger<ChapterTitleTranslator>.Instance),
        NullLogger<ChapterOverlayState>.Instance);

    /// <summary>同 <see cref="NewChapterOverlay"/>，但標題翻譯卡在 gate 上：gate 完成前 TranslationSettled 是 false。</summary>
    private static ChapterOverlayState NewGatedChapterOverlay(TaskCompletionSource gate, string llmReply) => new(
        new VideoChapterService(new EmptyCommentProvider(), NullLogger<VideoChapterService>.Instance),
        new ChapterTitleTranslator(
            new GatedLlm(gate, llmReply), new GlossaryState(), new TextProcessingService(),
            NullLogger<ChapterTitleTranslator>.Instance),
        NullLogger<ChapterOverlayState>.Instance);

    /// <summary>等 gate 完成後才回覆的假知識庫模型（讓章節標題翻譯停在進行中）。</summary>
    private sealed class GatedLlm(TaskCompletionSource gate, string reply) : ILorebookLlm
    {
        public async Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            await gate.Task;
            return new LlmJsonResult(reply, 1, 1, 1, "fake-glm");
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }

    /// <summary>輪詢讀字幕檔，直到內容含 marker（逾時 5 秒視為失敗）：推送有 debounce，內容不會馬上出現。</summary>
    private static async Task<string> WaitSrtContainsAsync(Harness h, string marker)
    {
        var path = Path.Combine(h.Dir.FullName, $"{VideoId}.ja.srt");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            Assert.True(DateTime.UtcNow < deadline, $"等候字幕內容「{marker}」逾時");
            if (File.Exists(path))
            {
                var srt = await ReadSrtAsync(path);
                if (srt.Contains(marker, StringComparison.Ordinal))
                {
                    return srt;
                }
            }

            await Task.Delay(10);
        }
    }

    /// <summary>讀字幕檔；推送端正在寫時（Windows 上讀的一方會擲 IOException）稍等重讀，最多 2 秒。</summary>
    private static async Task<string> ReadSrtAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            try
            {
                return await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
        }
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

        public Harness(
            Action<DirectoryInfo>? beforeCreatePusher = null,
            ChapterOverlayState? chapterOverlay = null,
            VodSettings? vod = null,
            IPlaybackClock? playbackClock = null)
        {
            Loader = new CaptionLoader(Captions, new StubProvider());
            beforeCreatePusher?.Invoke(Dir);
            Pusher = new PotPlayerSubtitlePusher(
                Sentences, Captions, Loader, PotPlayer, () => Enabled, Dir.FullName,
                NullLogger<PotPlayerSubtitlePusher>.Instance,
                chapterOverlay, vod is null ? null : () => vod, playbackClock)
            {
                Debounce = TimeSpan.FromMilliseconds(50),
                PlaybackPollInterval = TimeSpan.FromMilliseconds(10),
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

    /// <summary>可由測試控制的假播放時鐘：先照劇本回傳，之後重複最後一個值（位置卡住）；
    /// 呼叫 <see cref="Advance"/> 之後每次讀取位置加 1 秒（播放在前進），<see cref="Hold"/> 再停住。</summary>
    private sealed class FakePlaybackClock(params long?[] scripted) : IPlaybackClock
    {
        private readonly object _sync = new();
        private readonly Queue<long?> _scripted = new(scripted);
        private long? _last;
        private bool _advancing;

        /// <summary>GetPositionMs 被讀的次數（大於 0 表示等待已經開始）。</summary>
        public int Reads { get; private set; }

        /// <summary>之後每次讀取位置加 1 秒（模擬播放在前進）。</summary>
        public void Advance()
        {
            lock (_sync)
            {
                _advancing = true;
            }
        }

        /// <summary>之後位置停住（重複最後一個值）。</summary>
        public void Hold()
        {
            lock (_sync)
            {
                _advancing = false;
            }
        }

        public long? GetPositionMs()
        {
            lock (_sync)
            {
                Reads++;
                if (_scripted.TryDequeue(out var value))
                {
                    _last = value;
                }
                else if (_advancing)
                {
                    _last = (_last ?? 0) + 1000;
                }

                return _last;
            }
        }
    }

    /// <summary>不連網的假字幕下載：固定回傳兩段日文字幕。</summary>
    private sealed class StubProvider : IYouTubeCaptionProvider
    {
        public Task<YouTubeCaptionResult?> GetJapaneseCaptionsAsync(
            string videoId, string? visitorData, CancellationToken cancellationToken = default) =>
            Task.FromResult<YouTubeCaptionResult?>(new YouTubeCaptionResult("ja", true, "IOS", TwoCues));
    }

    /// <summary>不連網的假留言提供者：一律回空清單（章節用說明欄時不會被用到）。</summary>
    private sealed class EmptyCommentProvider : IYouTubeCommentProvider
    {
        public Task<IReadOnlyList<YouTubeComment>?> GetTopCommentsAsync(string videoId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<YouTubeComment>?>([]);
    }

    /// <summary>固定回覆的假知識庫模型（章節標題翻譯用）。</summary>
    private sealed class StaticReplyLlm(string reply) : ILorebookLlm
    {
        public Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmJsonResult(reply, 1, 1, 1, "fake-glm"));

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
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
