using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>章節名稱疊加狀態：假的留言提供者與假的知識庫模型（可控制回覆與延遲），
/// 驗證章節來源（說明欄優先）、「開始」的處理、譯文更新與換片／Reset 的版本控制。</summary>
public class ChapterOverlayStateTests
{
    private const string VideoId = "dQw4w9WgXcQ";

    /// <summary>說明欄章節：第一章在 0:00（標題「開始」，之後會被去掉）。</summary>
    private static string Description(params string[] titles) =>
        "0:00 開始\n" + string.Join("\n", titles.Select((title, i) => $"{i + 1}:00 {title}"));

    private static readonly string[] DescATitles = ["曲A1", "曲A2", "曲A3"];

    /// <summary>留言セトリ（ChapterExtractor 會補「開始」）："1:00 A\n2:00 B\n3:00 C"。</summary>
    private static IReadOnlyList<YouTubeComment> SetlistComments()
    {
        var text = "1:00 A\n2:00 B\n3:00 C";
        return
        [
            new YouTubeComment("setlist", text, 5,
                [new CommentTimestamp(0, 4, 60), new CommentTimestamp(7, 4, 120), new CommentTimestamp(14, 4, 180)]),
        ];
    }

    private static string ReplyJson(params string[] translations) =>
        """{"translations": ["$$"]}""".Replace("$$", string.Join("\",\"", translations));

    /// <summary>組出待測物件：假留言提供者（Handler 決定回什麼）與假知識庫模型（Handler 決定回什麼）；
    /// songInfo 預設 null（不查原曲長度）、store 預設 null（不讀寫資料庫），需要時另外傳入。</summary>
    private sealed class Harness
    {
        public FakeCommentProvider Comments { get; } = new();

        public ScriptedLlm Llm { get; } = new();

        public ChapterOverlayState State { get; }

        public int ChangedCount { get; set; }

        public Harness(ISongInfoProvider? songInfo = null, IVideoChapterStore? store = null)
        {
            State = new ChapterOverlayState(
                new VideoChapterService(Comments, NullLogger<VideoChapterService>.Instance),
                new ChapterTitleTranslator(Llm, new GlossaryState(), new TextProcessingService(), NullLogger<ChapterTitleTranslator>.Instance),
                NullLogger<ChapterOverlayState>.Instance,
                songInfo,
                store);
            State.Changed += () => ChangedCount++;
        }
    }

    [Fact]
    public async Task LoadAsync_DescriptionHasChapters_CommentsNotUsed()
    {
        var h = new Harness();
        h.Comments.Handler = (_, _) => Task.FromResult<IReadOnlyList<YouTubeComment>?>(SetlistComments());
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"));

        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);

        // 說明欄有章節就用說明欄：不打留言
        Assert.Equal(0, h.Comments.CallCount);
        Assert.Equal(VideoId, h.State.VideoId);
        Assert.Equal(
        [
            new ChapterOverlayItem(60000, "曲A1", "譯A1"),
            new ChapterOverlayItem(120000, "曲A2", "譯A2"),
            new ChapterOverlayItem(180000, "曲A3", "譯A3"),
        ], h.State.Items);
    }

    [Fact]
    public async Task LoadAsync_DescriptionEmpty_FallsBackToComments()
    {
        var h = new Harness();
        h.Comments.Handler = (_, _) => Task.FromResult<IReadOnlyList<YouTubeComment>?>(SetlistComments());
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A", "譯B", "譯C"), 1, 1, 1, "fake"));

        await h.State.LoadAsync(VideoId, null, TestContext.Current.CancellationToken);

        // 說明欄沒有章節才用留言セトリ；留言整理出的「開始」也要去掉
        Assert.Equal(1, h.Comments.CallCount);
        Assert.Equal(
        [
            new ChapterOverlayItem(60000, "A", "譯A"),
            new ChapterOverlayItem(120000, "B", "譯B"),
            new ChapterOverlayItem(180000, "C", "譯C"),
        ], h.State.Items);
    }

    [Fact]
    public async Task LoadAsync_StartChapterRemoved()
    {
        var h = new Harness();
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"));

        // Description 的第一章是 0:00 開始：FromDescription 不補、也不該出現在 Items
        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(h.State.Items, static item => item.StartMs == 0 && item.Title == "開始");
    }

    [Fact]
    public async Task LoadAsync_TranslationArrives_TriggersSecondChanged()
    {
        var h = new Harness();
        var translations = 0;
        h.Llm.Handler = (_, _, _) =>
        {
            translations++;
            return Task.FromResult(new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"));
        };

        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);

        // 載入章節一次、譯文回來一次
        Assert.Equal(1, translations);
        Assert.Equal(2, h.ChangedCount);
        Assert.All(h.State.Items, static item => Assert.NotNull(item.Translation));
    }

    [Fact]
    public async Task LoadAsync_TranslationOverallFails_ItemsStayUntranslated_StillSettlesWithChanged()
    {
        var h = new Harness();
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult("""{"translations": ["只有一個"]}""", 1, 1, 1, "fake"));

        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);

        // 整體翻譯失敗（數量不符）：維持只有原文的 Items，但翻譯已結束、也要觸發 Changed 讓推送端知道可以推了
        Assert.Equal(3, h.State.Items.Count);
        Assert.All(h.State.Items, static item => Assert.Null(item.Translation));
        Assert.True(h.State.TranslationSettled);
        Assert.Equal(2, h.ChangedCount);
    }

    [Fact]
    public async Task LoadAsync_NoChapters_EmptyItemsStillRaisesChanged()
    {
        var h = new Harness();
        h.Comments.Handler = (_, _) => Task.FromResult<IReadOnlyList<YouTubeComment>?>([]);

        await h.State.LoadAsync(VideoId, null, TestContext.Current.CancellationToken);

        Assert.Equal(VideoId, h.State.VideoId);
        Assert.Empty(h.State.Items);
        Assert.True(h.State.TranslationSettled);
        Assert.Equal(1, h.ChangedCount);
        Assert.Equal(0, h.Llm.CallCount);
    }

    [Fact]
    public async Task LoadAsync_NewVideo_DiscardsStaleTranslation()
    {
        var h = new Harness();
        // 影片 A 的翻譯卡在 gate 上；影片 B 的翻譯立刻回來
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Llm.Handler = (_, user, _) => user.Contains("曲A1", StringComparison.Ordinal)
            ? WaitAndReturnAsync(gate, new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"))
            : Task.FromResult(new LlmJsonResult(ReplyJson("譯B1", "譯B2", "譯B3"), 1, 1, 1, "fake"));

        var loadA = h.State.LoadAsync("videoA", Description(DescATitles), TestContext.Current.CancellationToken);
        await UntilAsync(() => h.Llm.CallCount == 1);
        await h.State.LoadAsync("videoB", Description("曲B1", "曲B2", "曲B3"), TestContext.Current.CancellationToken);

        // 換片完成：VideoId 與 Items 都是 B 的
        Assert.Equal("videoB", h.State.VideoId);
        Assert.Equal("譯B1", h.State.Items[0].Translation);
        var changedAfterB = h.ChangedCount;

        gate.SetResult();
        await loadA;

        // A 的舊譯文回來了：版本已變，整批丟掉（不清掉 B 的內容、也不觸發 Changed）
        Assert.Equal("videoB", h.State.VideoId);
        Assert.Equal("譯B1", h.State.Items[0].Translation);
        Assert.Equal(changedAfterB, h.ChangedCount);
    }

    [Fact]
    public async Task Reset_ClearsVideoIdAndItems()
    {
        var h = new Harness();
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"));
        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);
        Assert.Equal(2, h.ChangedCount);
        Assert.True(h.State.TranslationSettled);

        h.State.Reset();

        Assert.Null(h.State.VideoId);
        Assert.Empty(h.State.Items);
        Assert.Equal(3, h.ChangedCount);
        Assert.False(h.State.TranslationSettled);
    }

    // 尚未 LoadAsync：翻譯還沒結束（overlay 先不推送）。
    [Fact]
    public void TranslationSettled_BeforeLoad_False()
    {
        var h = new Harness();

        Assert.False(h.State.TranslationSettled);
    }

    // 章節已載入但譯文還沒回來：仍是 false；譯文回來後才 true。
    [Fact]
    public async Task TranslationSettled_TranslationPending_FalseUntilTranslationReturns()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Llm.Handler = (_, _, _) => WaitAndReturnAsync(gate, new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"));

        var load = h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);
        await UntilAsync(() => h.State.Items.Count == 3);

        // 章節載入了（Changed 已觸發）但翻譯還在跑：未結束
        Assert.False(h.State.TranslationSettled);

        gate.SetResult();
        await load;

        Assert.True(h.State.TranslationSettled);
    }

    // 模型擲出例外：載入失敗也算翻譯結束（可推送只有原文的 overlay），並觸發 Changed。
    [Fact]
    public async Task TranslationSettled_LlmThrows_SettlesAndTriggersChanged()
    {
        var h = new Harness();
        h.Llm.Handler = (_, _, _) => throw new InvalidOperationException("boom");

        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);

        // 章節載入一次、例外結束一次
        Assert.True(h.State.TranslationSettled);
        Assert.Equal(2, h.ChangedCount);
    }

    // FindTranslation：與章節標題完全相同（正規化後）且已有譯文時回傳譯文。
    [Fact]
    public async Task FindTranslation_MatchesNormalizedTitle_ReturnsTranslation()
    {
        var h = new Harness();
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"));
        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);

        // 完全相同
        Assert.Equal("譯A1", h.State.FindTranslation("曲A1"));
        // PotPlayer 送來的樣子：{\an7\fs12} 標記被去掉、換行變空白、頭尾空白
        Assert.Equal("譯A2", h.State.FindTranslation("  {\\an7\\fs12}曲A2  "));
        Assert.Equal("譯A3", h.State.FindTranslation("曲A3"));
    }

    // FindTranslation：全形空白、多個連續空白與換行合併成一個半形空白後比對。
    [Fact]
    public async Task FindTranslation_FullWidthAndNewlineDifferences_Match()
    {
        var h = new Harness();
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(
            ReplyJson("第10名 若能成為星座 ／ 結束樂團", "第9名 Booo! ／ TOKOTOKO"), 1, 1, 1, "fake"));
        await h.State.LoadAsync(
            VideoId,
            "0:00 開始\n1:00 10位 星座になれたら ／ 結束バンド\n2:00 09位 Booo! ／ TOKOTOKO",
            TestContext.Current.CancellationToken);

        // 標題用半形空白、查詢用全形空白
        Assert.Equal("第10名 若能成為星座 ／ 結束樂團", h.State.FindTranslation("10位　星座になれたら　／　結束バンド"));
        // 換行（cue 的雙行寫法）與連續空白
        Assert.Equal("第10名 若能成為星座 ／ 結束樂團", h.State.FindTranslation("{\\an7\\fs12}10位  星座になれたら\n／ 結束バンド"));
    }

    // FindTranslation：不是章節標題（或還沒載入）時回 null。
    [Fact]
    public async Task FindTranslation_NotAChapterTitle_ReturnsNull()
    {
        var h = new Harness();
        Assert.Null(h.State.FindTranslation("曲A1"));

        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"));
        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);

        // 不是任何章節標題、只是某個標題的前半、或空白字串
        Assert.Null(h.State.FindTranslation("別的句子"));
        Assert.Null(h.State.FindTranslation("曲A"));
        Assert.Null(h.State.FindTranslation("   "));
        // 標記去掉後是空的
        Assert.Null(h.State.FindTranslation("{\\an7\\fs12}  "));
    }

    // FindTranslation：章節標題命中但譯文是 null（翻譯失敗）也回 null，走一般翻譯流程。
    [Fact]
    public async Task FindTranslation_TitleWithoutTranslation_ReturnsNull()
    {
        var h = new Harness();
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult("""{"translations": ["只有一個"]}""", 1, 1, 1, "fake"));
        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);

        Assert.Null(h.State.FindTranslation("曲A1"));
    }

    // 原曲長度與譯文都回來：Items 同時有 DurationMs 與譯文（章節標題會先拆曲名／歌手再查）。
    [Fact]
    public async Task LoadAsync_DurationsAndTranslations_BothWrittenBack()
    {
        var songs = new FakeSongInfo();
        songs.Handler = (query, _) => Task.FromResult<long?>(query switch
        {
            { Title: "星座になれたら" } => 200000,
            { Title: "Booo!" } => 184000,
            _ => null,
        });
        var h = new Harness(songs);
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯1", "譯2", "譯3"), 1, 1, 1, "fake"));

        await h.State.LoadAsync(
            VideoId,
            "0:00 開始\n1:00 10位 星座になれたら ／ 結束バンド\n2:00 09位 Booo! ／ TOKOTOKO\n3:00 配信開始",
            TestContext.Current.CancellationToken);

        // 章節標題先拆曲名／歌手再查：名次去掉、歌手分開
        Assert.Equal(
        [
            new SongQuery("星座になれたら", "結束バンド"),
            new SongQuery("Booo!", "TOKOTOKO"),
            new SongQuery("配信開始", null),
        ], songs.Calls);
        Assert.Equal(
        [
            new ChapterOverlayItem(60000, "10位 星座になれたら ／ 結束バンド", "譯1", 200000),
            new ChapterOverlayItem(120000, "09位 Booo! ／ TOKOTOKO", "譯2", 184000),
            new ChapterOverlayItem(180000, "配信開始", "譯3", null),
        ], h.State.Items);
        // 載入章節、長度回來、譯文回來各一次
        Assert.Equal(3, h.ChangedCount);
    }

    // 長度先回來、譯文還在跑：DurationMs 先寫回 Items，但不影響 TranslationSettled（還不能推送）。
    [Fact]
    public async Task LoadAsync_DurationsSettleBeforeTranslation_SettledUnchanged()
    {
        var songs = new FakeSongInfo();
        songs.Handler = (_, _) => Task.FromResult<long?>(123000);
        var h = new Harness(songs);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Llm.Handler = (_, _, _) => WaitAndReturnAsync(gate, new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"));

        var load = h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);
        await UntilAsync(() => h.State.Items.All(static item => item.DurationMs is not null));

        // 長度回來了（Items 已有 DurationMs、Changed 已觸發），但譯文還沒：不算翻譯結束
        Assert.False(h.State.TranslationSettled);
        Assert.All(h.State.Items, static item => Assert.Null(item.Translation));

        gate.SetResult();
        await load;

        // 譯文回來：DurationMs 保留、譯文補上
        Assert.True(h.State.TranslationSettled);
        Assert.All(h.State.Items, static item =>
        {
            Assert.Equal(123000, item.DurationMs);
            Assert.NotNull(item.Translation);
        });
        Assert.Equal(3, h.ChangedCount);
    }

    // 長度查詢依序進行（不同時打），順序照章節順序。
    [Fact]
    public async Task LoadAsync_DurationQueries_SequentialInChapterOrder()
    {
        var songs = new FakeSongInfo();
        songs.Handler = async (_, _) =>
        {
            await Task.Delay(20);
            return 100000;
        };
        var h = new Harness(songs);
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"));

        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);

        Assert.Equal(["曲A1", "曲A2", "曲A3"], songs.Calls.Select(static query => query.Title));
        Assert.Equal(1, songs.MaxInFlight);
    }

    // 換片／Reset 之後回來的舊長度結果丟掉（不寫回、不觸發 Changed）。
    [Fact]
    public async Task LoadAsync_ResetWhileQuerying_DiscardsStaleDurations()
    {
        var songs = new FakeSongInfo();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        songs.Handler = (_, _) => WaitAndReturnAsync(gate, 100000L);
        var h = new Harness(songs);
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"));

        var load = h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);
        await UntilAsync(() => songs.Calls.Count == 1);

        h.State.Reset();
        var changedAfterReset = h.ChangedCount;

        gate.SetResult();
        await load;

        // 版本已變：長度不寫回（Items 維持 Reset 後的空清單）、不觸發 Changed
        Assert.Null(h.State.VideoId);
        Assert.Empty(h.State.Items);
        Assert.Equal(changedAfterReset, h.ChangedCount);
    }

    // ===== IVideoChapterStore（章節存資料庫：人工 > 說明欄 > 留言）=====

    // 存過的章節直接用：不打留言、不翻譯、不查長度（譯文與長度都照存的），也不重存。
    [Fact]
    public async Task LoadAsync_StoredChapters_UsedDirectlyWithoutProviderOrModel()
    {
        var store = new FakeChapterStore
        {
            OnGet = _ =>
            [
                new StoredVideoChapter(60000, "曲A1", "譯A1", 200000, VideoChapterSources.Description),
                new StoredVideoChapter(120000, "曲A2", "譯A2", null, VideoChapterSources.Description),
            ],
        };
        var songs = new FakeSongInfo();
        var h = new Harness(songs, store);
        h.Comments.Handler = (_, _) => throw new InvalidOperationException("不該抓留言");
        h.Llm.Handler = (_, _, _) => throw new InvalidOperationException("不該呼叫模型");

        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);

        Assert.Single(store.GetCalls);
        Assert.Equal(0, h.Comments.CallCount);
        Assert.Equal(0, h.Llm.CallCount);
        Assert.Empty(songs.Calls);
        Assert.Equal(VideoChapterSources.Description, h.State.Source);
        Assert.True(h.State.TranslationSettled);
        Assert.Equal(
        [
            new ChapterOverlayItem(60000, "曲A1", "譯A1", 200000),
            new ChapterOverlayItem(120000, "曲A2", "譯A2", null),
        ], h.State.Items);
        Assert.Empty(store.ReplaceCalls);
    }

    // 存的章節譯文全部是 null：重新翻譯（長度有存就不重查），結束後回存譯文。
    [Fact]
    public async Task LoadAsync_StoredWithoutTranslations_RetranslatesAndSaves()
    {
        var store = new FakeChapterStore
        {
            OnGet = _ =>
            [
                new StoredVideoChapter(60000, "曲A1", null, 100000, VideoChapterSources.Comment),
                new StoredVideoChapter(120000, "曲A2", null, 100000, VideoChapterSources.Comment),
            ],
        };
        var songs = new FakeSongInfo();
        var h = new Harness(songs, store);
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A1", "譯A2"), 1, 1, 1, "fake"));

        await h.State.LoadAsync(VideoId, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, h.Llm.CallCount);
        Assert.Empty(songs.Calls);
        Assert.Equal(
        [
            new ChapterOverlayItem(60000, "曲A1", "譯A1", 100000),
            new ChapterOverlayItem(120000, "曲A2", "譯A2", 100000),
        ], h.State.Items);

        var call = Assert.Single(store.ReplaceCalls);
        Assert.All(call.Chapters, chapter => Assert.Equal(VideoChapterSources.Comment, chapter.Source));
        Assert.Equal("譯A1", call.Chapters[0].Translation);
    }

    // 說明欄章節：翻譯與長度查詢都結束後存檔（Source = Description、含譯文與長度）。
    [Fact]
    public async Task LoadAsync_DescriptionChapters_SavedWithSourceDescription()
    {
        var store = new FakeChapterStore();
        var songs = new FakeSongInfo();
        songs.Handler = (query, _) => Task.FromResult<long?>(query.Title == "曲A1" ? 111000 : null);
        var h = new Harness(songs, store);
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"));

        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);

        Assert.Equal(VideoChapterSources.Description, h.State.Source);
        var call = Assert.Single(store.ReplaceCalls);
        Assert.Equal(VideoId, call.VideoId);
        Assert.Equal(
        [
            new StoredVideoChapter(60000, "曲A1", "譯A1", 111000, VideoChapterSources.Description),
            new StoredVideoChapter(120000, "曲A2", "譯A2", null, VideoChapterSources.Description),
            new StoredVideoChapter(180000, "曲A3", "譯A3", null, VideoChapterSources.Description),
        ], call.Chapters);
    }

    // 說明欄沒有章節、留言有：存檔 Source = Comment。
    [Fact]
    public async Task LoadAsync_CommentChapters_SavedWithSourceComment()
    {
        var store = new FakeChapterStore();
        var h = new Harness(store: store);
        h.Comments.Handler = (_, _) => Task.FromResult<IReadOnlyList<YouTubeComment>?>(SetlistComments());
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A", "譯B", "譯C"), 1, 1, 1, "fake"));

        await h.State.LoadAsync(VideoId, null, TestContext.Current.CancellationToken);

        Assert.Equal(VideoChapterSources.Comment, h.State.Source);
        var call = Assert.Single(store.ReplaceCalls);
        Assert.All(call.Chapters, chapter => Assert.Equal(VideoChapterSources.Comment, chapter.Source));
        Assert.Equal("譯A", call.Chapters[0].Translation);
    }

    // 說明欄與留言都沒有章節：不存檔、Source 為 null。
    [Fact]
    public async Task LoadAsync_NoChapters_NothingSaved()
    {
        var store = new FakeChapterStore();
        var h = new Harness(store: store);
        h.Comments.Handler = (_, _) => Task.FromResult<IReadOnlyList<YouTubeComment>?>([]);

        await h.State.LoadAsync(VideoId, null, TestContext.Current.CancellationToken);

        Assert.Empty(store.ReplaceCalls);
        Assert.Null(h.State.Source);
    }

    // 人工貼上成功：回傳章節數、Source = Manual、Items 換新（譯文與長度清空）、背景翻譯結束後存檔。
    [Fact]
    public async Task ApplyManualAsync_ValidText_ReplacesItemsAndSaves()
    {
        var store = new FakeChapterStore();
        var h = new Harness(store: store);
        // 人工章節的翻譯（第二次模型呼叫）卡在 gate 上：驗證方法不等翻譯就回傳
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmCalls = 0;
        h.Llm.Handler = (_, _, _) => Interlocked.Increment(ref llmCalls) == 1
            ? Task.FromResult(new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"))
            : WaitAndReturnAsync(gate, new LlmJsonResult(ReplyJson("譯M1", "譯M2", "譯M3", "譯M4"), 1, 1, 1, "fake"));
        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);
        Assert.Single(store.ReplaceCalls);

        var count = await h.State.ApplyManualAsync(
            "1:00 曲M1\n2:00 曲M2\n3:00 曲M3\n4:00 曲M4", TestContext.Current.CancellationToken);

        Assert.Equal(4, count);
        Assert.Equal(VideoChapterSources.Manual, h.State.Source);
        Assert.Equal(4, h.State.Items.Count);
        Assert.All(h.State.Items, item =>
        {
            Assert.Null(item.Translation);
            Assert.Null(item.DurationMs);
        });
        // 翻譯還在跑（方法已回傳）：還不能視為已結束，也還沒存檔
        Assert.False(h.State.TranslationSettled);
        Assert.Single(store.ReplaceCalls);

        gate.SetResult();
        // 存檔在背景翻譯結束後（不等翻譯完成：輪詢等它出現）
        await UntilAsync(() => store.ReplaceCalls.Count == 2);
        var call = store.ReplaceCalls[1];
        Assert.Equal(VideoId, call.VideoId);
        Assert.Equal(4, call.Chapters.Count);
        Assert.All(call.Chapters, chapter => Assert.Equal(VideoChapterSources.Manual, chapter.Source));
        Assert.Equal(new StoredVideoChapter(60000, "曲M1", "譯M1", null, VideoChapterSources.Manual), call.Chapters[0]);
        await UntilAsync(() => h.State.TranslationSettled);
    }

    // 人工貼上失敗（解析不足 3 章）：回 0、Items 與 Source 不變、不再存檔。
    [Fact]
    public async Task ApplyManualAsync_InvalidText_ReturnsZeroKeepsItems()
    {
        var store = new FakeChapterStore();
        var h = new Harness(store: store);
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3"), 1, 1, 1, "fake"));
        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);
        var itemsBefore = h.State.Items;

        var count = await h.State.ApplyManualAsync("1:00 只有一章\n2:00 兩章", TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
        Assert.Equal(itemsBefore, h.State.Items);
        Assert.Equal(VideoChapterSources.Description, h.State.Source);
        Assert.Single(store.ReplaceCalls);
    }

    // 還沒開片（VideoId 為 null）：回 0、Items 不變。
    [Fact]
    public async Task ApplyManualAsync_NoVideo_ReturnsZero()
    {
        var h = new Harness(store: new FakeChapterStore());

        Assert.Equal(0, await h.State.ApplyManualAsync("1:00 A\n2:00 B\n3:00 C", TestContext.Current.CancellationToken));
        Assert.Empty(h.State.Items);
        Assert.Null(h.State.Source);
    }

    // store 為 null（沒有資料庫）：人工貼上照樣套用（不存檔），行為與沒有 store 時一致。
    [Fact]
    public async Task ApplyManualAsync_WithoutStore_StillApplies()
    {
        var h = new Harness();
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯1", "譯2", "譯3"), 1, 1, 1, "fake"));
        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);

        var count = await h.State.ApplyManualAsync("1:00 曲M1\n2:00 曲M2\n3:00 曲M3", TestContext.Current.CancellationToken);

        Assert.Equal(3, count);
        Assert.Equal(VideoChapterSources.Manual, h.State.Source);
        await UntilAsync(() => h.State.TranslationSettled);
        Assert.All(h.State.Items, item => Assert.StartsWith("曲M", item.Title, StringComparison.Ordinal));
    }

    // 清除人工章節：刪掉資料庫的章節後，重新依說明欄載入（Source 回 Description）並再存檔。
    [Fact]
    public async Task ClearManualAsync_DeletesStoredAndReloadsFromDescription()
    {
        var store = new FakeChapterStore();
        var h = new Harness(store: store);
        h.Llm.Handler = (_, _, _) => Task.FromResult(
            new LlmJsonResult(ReplyJson("譯A1", "譯A2", "譯A3", "譯M1", "譯M2", "譯M3"), 1, 1, 1, "fake"));
        await h.State.LoadAsync(VideoId, Description(DescATitles), TestContext.Current.CancellationToken);
        Assert.Equal(3, await h.State.ApplyManualAsync("1:00 曲M1\n2:00 曲M2\n3:00 曲M3", TestContext.Current.CancellationToken));
        await UntilAsync(() => store.ReplaceCalls.Count == 2);
        Assert.Equal(VideoChapterSources.Manual, h.State.Source);

        await h.State.ClearManualAsync(TestContext.Current.CancellationToken);

        Assert.Equal([VideoId], store.Deleted);
        Assert.Equal(VideoChapterSources.Description, h.State.Source);
        Assert.Equal("曲A1", h.State.Items[0].Title);
        await UntilAsync(() => store.ReplaceCalls.Count == 3);
        Assert.All(store.ReplaceCalls[2].Chapters, chapter => Assert.Equal(VideoChapterSources.Description, chapter.Source));
    }

    // 清除人工章節：說明欄沒有章節時改回留言（Source 回 Comment）。
    [Fact]
    public async Task ClearManualAsync_NoDescription_FallsBackToComments()
    {
        var store = new FakeChapterStore();
        var h = new Harness(store: store);
        h.Comments.Handler = (_, _) => Task.FromResult<IReadOnlyList<YouTubeComment>?>(SetlistComments());
        h.Llm.Handler = (_, _, _) => Task.FromResult(new LlmJsonResult(ReplyJson("譯A", "譯B", "譯C"), 1, 1, 1, "fake"));
        await h.State.LoadAsync(VideoId, null, TestContext.Current.CancellationToken);
        Assert.Equal(3, await h.State.ApplyManualAsync("1:00 曲M1\n2:00 曲M2\n3:00 曲M3", TestContext.Current.CancellationToken));
        await UntilAsync(() => store.ReplaceCalls.Count == 2);
        Assert.Equal(VideoChapterSources.Manual, h.State.Source);

        await h.State.ClearManualAsync(TestContext.Current.CancellationToken);

        Assert.Equal([VideoId], store.Deleted);
        Assert.Equal(VideoChapterSources.Comment, h.State.Source);
        Assert.Equal("A", h.State.Items[0].Title);
    }

    /// <summary>等 gate 完成後回指定的結果。</summary>
    private static async Task<LlmJsonResult> WaitAndReturnAsync(TaskCompletionSource gate, LlmJsonResult result)
    {
        await gate.Task;
        return result;
    }

    /// <summary>等 gate 完成後回指定的結果（原曲長度版）。</summary>
    private static async Task<long?> WaitAndReturnAsync(TaskCompletionSource gate, long? result)
    {
        await gate.Task;
        return result;
    }

    /// <summary>輪詢直到條件成立（逾時 5 秒視為失敗）。</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等候條件逾時");
            await Task.Delay(10);
        }
    }

    /// <summary>假的留言提供者：Handler 決定回什麼（回 null 模擬失敗）。</summary>
    private sealed class FakeCommentProvider : IYouTubeCommentProvider
    {
        public int CallCount { get; private set; }

        public Func<string, CancellationToken, Task<IReadOnlyList<YouTubeComment>?>>? Handler { get; set; }

        public async Task<IReadOnlyList<YouTubeComment>?> GetTopCommentsAsync(string videoId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Handler is null ? [] : await Handler(videoId, cancellationToken);
        }
    }

    /// <summary>假的歌曲資訊提供者：Handler 決定回什麼（回 null 模擬查不到）；
    /// 記下呼叫順序與同一時間的最大查詢數（驗證依序查、不同時打）。</summary>
    private sealed class FakeSongInfo : ISongInfoProvider
    {
        private int _inFlight;

        public List<SongQuery> Calls { get; } = [];

        public Func<SongQuery, CancellationToken, Task<long?>>? Handler { get; set; }

        public int MaxInFlight { get; private set; }

        public async Task<long?> GetDurationMsAsync(SongQuery query, CancellationToken cancellationToken = default)
        {
            Calls.Add(query);
            MaxInFlight = Math.Max(MaxInFlight, Interlocked.Increment(ref _inFlight));
            try
            {
                return Handler is null ? null : await Handler(query, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>假的影片章節存放區：OnGet 決定讀到什麼（null 時回空清單）；記下 Replace／Delete 的呼叫。</summary>
    private sealed class FakeChapterStore : IVideoChapterStore
    {
        public Func<string, IReadOnlyList<StoredVideoChapter>>? OnGet { get; set; }

        public List<string> GetCalls { get; } = [];

        public List<(string VideoId, IReadOnlyList<StoredVideoChapter> Chapters)> ReplaceCalls { get; } = [];

        public List<string> Deleted { get; } = [];

        public IReadOnlyList<StoredVideoChapter> GetVideoChapters(string videoId)
        {
            GetCalls.Add(videoId);
            return OnGet is null ? [] : OnGet(videoId);
        }

        public void ReplaceVideoChapters(string videoId, IReadOnlyList<StoredVideoChapter> chapters) =>
            ReplaceCalls.Add((videoId, chapters));

        public void DeleteVideoChapters(string videoId) => Deleted.Add(videoId);
    }

    /// <summary>假的知識庫模型：Handler 決定回什麼（回例外模擬失敗）；null 時回空 JSON。</summary>
    private sealed class ScriptedLlm : ILorebookLlm
    {
        public int CallCount { get; private set; }

        public Func<string, string, CancellationToken, Task<LlmJsonResult>>? Handler { get; set; }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));

        public async Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Handler is null)
            {
                return new LlmJsonResult("{}", 1, 1, 1, "fake");
            }

            return await Handler(systemPrompt, userPrompt, cancellationToken);
        }
    }
}
