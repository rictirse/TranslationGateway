using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>CaptionLoader：開台與 PotPlayer 腳本通知共用的 YouTube 字幕下載（假 provider／pageInfo，不連網）。</summary>
public class CaptionLoaderTests
{
    private const string VideoId = "dQw4w9WgXcQ";
    private const string WatchUrl = "https://www.youtube.com/watch?v=" + VideoId;
    private const string ShortUrl = "https://youtu.be/" + VideoId;

    private static readonly CaptionCue[] ThreeCues =
    [
        new CaptionCue(1000, 2000, "こんにちは"),
        new CaptionCue(3000, 2000, "ありがとう"),
        new CaptionCue(5000, 2000, "さようなら"),
    ];

    [Fact]
    public async Task NotifyPlaying_YouTubeUrl_DownloadsAndSetsCaptions()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var sut = CreateLoader(state, provider);
        var statuses = new ConcurrentQueue<string>();
        sut.StatusChanged += statuses.Enqueue;

        sut.NotifyPlaying(WatchUrl);

        await UntilAsync(() => state.IsLoaded && statuses.Count == 1);
        Assert.Equal(VideoId, state.VideoId);
        Assert.Equal(3, state.Cues.Count);
        Assert.Equal("YouTube 字幕：已載入 3 段", Assert.Single(statuses));
    }

    [Fact]
    public async Task NotifyPlaying_SameVideoTwice_DownloadsOnlyOnce()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var sut = CreateLoader(state, provider);

        sut.NotifyPlaying(WatchUrl);
        await UntilAsync(() => state.IsLoaded);

        // 同一個網址（值相同）與同一支影片的另一種網址寫法（videoId 相同）都不再下載
        sut.NotifyPlaying(WatchUrl);
        sut.NotifyPlaying(ShortUrl);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal([VideoId], provider.VideoIds);
    }

    [Fact]
    public async Task NotifyPlaying_LiveVideo_SkipsDownloadAndDoesNotQueryAgain()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var pageInfo = new FakePageInfo { Result = Info(isLiveNow: true) };
        var sut = CreateLoader(state, provider, pageInfo);

        sut.NotifyPlaying(WatchUrl);

        // 頁面顯示正在直播：查了一次頁面，但不下載字幕
        await UntilAsync(() => pageInfo.CallCount == 1);
        Assert.Equal(0, provider.CallCount);
        Assert.False(state.IsLoaded);

        // 之後同一支影片再通知：不再查頁面、不再下載
        sut.NotifyPlaying(ShortUrl);
        Assert.Equal(1, pageInfo.CallCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task LoadAsync_ResetDuringDownload_DiscardsResult()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var sut = CreateLoader(state, provider);
        var gate = new TaskCompletionSource<YouTubeCaptionResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Gate = gate;

        var task = sut.LoadAsync(VideoId, null);
        await UntilAsync(() => provider.CallCount == 1);

        // 下載到一半換場（開台）：結果要被丟掉，狀態維持空的
        sut.Reset();
        gate.SetResult(new YouTubeCaptionResult("ja", true, "IOS", ThreeCues));
        await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(state.IsLoaded);
        Assert.Null(state.VideoId);
    }

    [Fact]
    public void NotifyPlaying_LocalStreamUrl_DoesNotDownloadAndDoesNotThrow()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var pageInfo = new FakePageInfo();
        var sut = CreateLoader(state, provider, pageInfo);

        sut.NotifyPlaying("http://127.0.0.1:12345/");

        Assert.Equal(0, pageInfo.CallCount);
        Assert.Equal(0, provider.CallCount);
        Assert.False(state.IsLoaded);
    }

    // ===== PlayingVideoId（PotPlayer 正在播放哪支影片）=====

    [Fact]
    public async Task LoadAsync_SetsPlayingVideoId()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var sut = CreateLoader(state, provider);

        Assert.Null(sut.PlayingVideoId);
        await sut.LoadAsync(VideoId, null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(VideoId, sut.PlayingVideoId);
    }

    [Fact]
    public void NotifyPlaying_LocalFile_ClearsPlayingVideoId()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var sut = CreateLoader(state, provider);

        // 解析出 videoId：設為該影片（同步完成，不等下載）
        sut.NotifyPlaying(WatchUrl);
        Assert.Equal(VideoId, sut.PlayingVideoId);

        // 本機檔案解析不出 videoId：清為 null
        sut.NotifyPlaying("C:\\video.mp4");
        Assert.Null(sut.PlayingVideoId);
    }

    [Fact]
    public void NotifyPlaying_LocalStreamUrl_ClearsPlayingVideoId()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var sut = CreateLoader(state, provider);

        sut.NotifyPlaying(WatchUrl);
        Assert.Equal(VideoId, sut.PlayingVideoId);

        // 本機串流網址（完整網址）：換片，清為 null
        sut.NotifyPlaying("http://127.0.0.1:1234/");
        Assert.Null(sut.PlayingVideoId);
    }

    [Fact]
    public void NotifyPlaying_WatchFileName_KeepsCurrentVideo()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var sut = CreateLoader(state, provider);

        sut.NotifyPlaying(WatchUrl);
        Assert.Equal(VideoId, sut.PlayingVideoId);

        // 播 YouTube 時 PotPlayer 只回報 watch 網址的檔名部分「watch」：認不得不當成換片，沿用目前的影片
        sut.NotifyPlaying("watch");
        Assert.Equal(VideoId, sut.PlayingVideoId);
    }

    [Fact]
    public void Reset_ClearsPlayingVideoId()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var sut = CreateLoader(state, provider);

        sut.NotifyPlaying(WatchUrl);
        Assert.Equal(VideoId, sut.PlayingVideoId);

        sut.Reset();
        Assert.Null(sut.PlayingVideoId);
    }

    [Fact]
    public async Task LoadAsync_OpenStreamPath_SkipsLiveCheck()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        // 開台路徑（skipIfLive 為 false）：即使頁面顯示正在直播也照常下載
        var pageInfo = new FakePageInfo { Result = Info(isLiveNow: true) };
        var sut = CreateLoader(state, provider, pageInfo);

        await sut.LoadAsync(VideoId, WatchUrl).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(VideoId, state.VideoId);
        Assert.True(state.IsLoaded);
    }

    [Fact]
    public async Task LoadAsync_ProviderThrows_SwallowsException()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider { Throw = new InvalidOperationException("boom") };
        var sut = CreateLoader(state, provider);

        var task = sut.LoadAsync(VideoId, null);

        await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(state.IsLoaded);
        Assert.Null(state.VideoId);
    }

    // ===== EnsureLoadedAsync（Gateway 字幕軌）=====

    [Fact]
    public async Task EnsureLoadedAsync_LoadInProgress_ReturnsSameTaskWithoutSecondDownload()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider
        {
            Gate = new TaskCompletionSource<YouTubeCaptionResult?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var sut = CreateLoader(state, provider);

        var first = sut.EnsureLoadedAsync(VideoId);
        await UntilAsync(() => provider.CallCount == 1);

        // 下載中再要求同支影片：回傳同一個載入工作，不重新下載
        var second = sut.EnsureLoadedAsync(VideoId);
        Assert.Same(first, second);

        provider.Gate.SetResult(new YouTubeCaptionResult("ja", true, "IOS", ThreeCues));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.CallCount);
        Assert.True(state.IsLoaded);
    }

    [Fact]
    public async Task EnsureLoadedAsync_AfterReset_Reloads()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var sut = CreateLoader(state, provider);

        await sut.EnsureLoadedAsync(VideoId).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.CallCount);
        Assert.True(state.IsLoaded);

        // Reset 之後再要求：重新下載
        sut.Reset();
        await sut.EnsureLoadedAsync(VideoId).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, provider.CallCount);
    }

    // ===== ICaptionStore 快取 =====

    private static readonly DateTimeOffset CachedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LoadAsync_CacheHit_LoadsFromCacheWithoutPageOrProvider()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        // 頁面顯示正在直播也不影響：快取命中不讀頁面、不檢查 skipIfLive
        var pageInfo = new FakePageInfo { Result = Info(isLiveNow: true) };
        var store = new FakeCaptionStore
        {
            Result = new StoredCaption(5, VideoId, "ja", true, "IOS", ThreeCues, CachedAt, CachedAt),
        };
        var sut = CreateLoader(state, provider, pageInfo, store);
        var statuses = new ConcurrentQueue<string>();
        sut.StatusChanged += statuses.Enqueue;

        await sut.LoadAsync(VideoId, null, skipIfLive: true).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await UntilAsync(() => store.TouchedIds.Count == 1);

        Assert.True(state.IsLoaded);
        Assert.Equal(VideoId, state.VideoId);
        Assert.Equal(3, state.Cues.Count);
        Assert.Equal("YouTube 字幕：已從快取載入 3 段", Assert.Single(statuses));
        Assert.Equal([5L], store.TouchedIds);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, pageInfo.CallCount);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task LoadAsync_CacheEmptyCues_FallsBackToDownload()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var store = new FakeCaptionStore { Result = new StoredCaption(5, VideoId, "ja", true, "IOS", [], CachedAt, CachedAt) };
        var sut = CreateLoader(state, provider, store: store);

        await sut.LoadAsync(VideoId, null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 快取的 cues 是空的：視為沒有快取，照常下載
        Assert.Equal(1, provider.CallCount);
        Assert.True(state.IsLoaded);
        await UntilAsync(() => store.Saves.Count == 1);
    }

    [Fact]
    public async Task LoadAsync_CacheMiss_SavesDownloadedCaptionsWithJaLanguage()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider { LanguageCode = "ja-JP" };
        var store = new FakeCaptionStore();
        var sut = CreateLoader(state, provider, store: store);

        await sut.LoadAsync(VideoId, null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await UntilAsync(() => store.Saves.Count == 1);

        var save = store.Saves.Single();
        Assert.Equal(VideoId, save.VideoId);
        // 查快取固定查 ja，所以 ja 開頭的語言一律存成 ja
        Assert.Equal("ja", save.Language);
        Assert.True(save.IsAuto);
        Assert.Equal("IOS", save.ClientName);
        Assert.Equal(ThreeCues, save.Cues);
    }

    [Fact]
    public async Task LoadAsync_NoCaptions_DoesNotSaveToStore()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider { Empty = true };
        var store = new FakeCaptionStore();
        var sut = CreateLoader(state, provider, store: store);
        var statuses = new ConcurrentQueue<string>();
        sut.StatusChanged += statuses.Enqueue;

        await sut.LoadAsync(VideoId, null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 查過但沒有字幕不寫入資料庫（下次開同一支影片會再查一次）
        Assert.Equal("YouTube 字幕：這支影片沒有可用的日文字幕", Assert.Single(statuses));
        Assert.False(state.IsLoaded);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task LoadAsync_GetCaptionThrows_FallsBackToDownload()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var store = new FakeCaptionStore { Throw = new InvalidOperationException("db down") };
        var sut = CreateLoader(state, provider, store: store);

        await sut.LoadAsync(VideoId, null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await UntilAsync(() => store.Saves.Count == 1);

        // 查快取失敗：照常下載，下載結果也照常存回
        Assert.Equal(1, provider.CallCount);
        Assert.True(state.IsLoaded);
    }

    [Fact]
    public async Task LoadAsync_CacheHitThenResetDuringLookup_DiscardsResult()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var gate = new TaskCompletionSource<StoredCaption?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeCaptionStore
        {
            Gate = gate,
            Result = new StoredCaption(5, VideoId, "ja", true, "IOS", ThreeCues, CachedAt, CachedAt),
        };
        var sut = CreateLoader(state, provider, store: store);

        var task = sut.LoadAsync(VideoId, null);
        await UntilAsync(() => store.GetCount == 1);

        // 查快取到一半換場（開台）：結果要被丟棄，也不 Touch
        sut.Reset();
        gate.SetResult(new StoredCaption(5, VideoId, "ja", true, "IOS", ThreeCues, CachedAt, CachedAt));
        await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(state.IsLoaded);
        Assert.Null(state.VideoId);
        Assert.Empty(store.TouchedIds);
        Assert.Equal(0, provider.CallCount);
    }

    // ===== 生成式字幕前處理（用真的 CaptionPreprocessor 加假 LLM，不連網）=====

    private static CaptionCue[] ManyCues(int count)
    {
        var cues = new CaptionCue[count];
        for (var i = 0; i < count; i++)
        {
            cues[i] = new CaptionCue(i * 1000, 500, $"セリフ{i}");
        }
        return cues;
    }

    /// <summary>組出帶前處理器的 CaptionLoader：知識庫模型已設定（Lorebook），使 Start 真的啟動背景工作。</summary>
    private static (CaptionLoader Loader, FakeLorebookLlm Llm, CaptionSentenceState SentenceState) CreateLoaderWithPreprocessor(
        CaptionTrackState state, FakeCaptionProvider provider, FakeCaptionStore store)
    {
        var settings = new FakeSettingsService();
        settings.Current.Lorebook.ApiHost = "http://127.0.0.1:8300/v1";
        settings.Current.Lorebook.Model = "glm-test";
        var llm = new FakeLorebookLlm();
        var sentenceState = new CaptionSentenceState();
        var preprocessor = new CaptionPreprocessor(
            llm, store, sentenceState, new GlossaryState(), settings, clock: null,
            NullLogger<CaptionPreprocessor>.Instance);
        var loader = new CaptionLoader(state, provider, null, NullLogger<CaptionLoader>.Instance, store, preprocessor);
        return (loader, llm, sentenceState);
    }

    [Fact]
    public async Task LoadAsync_CacheHit_StartsPreprocessorWithStoredCaptionId()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var store = new FakeCaptionStore
        {
            Result = new StoredCaption(5, VideoId, "ja", true, "IOS", ThreeCues, CachedAt, CachedAt),
        };
        var (sut, llm, sentenceState) = CreateLoaderWithPreprocessor(state, provider, store);

        await sut.LoadAsync(VideoId, null, skipIfLive: true).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 用 StoredCaption.Id 啟動前處理，且背景工作已開始（有向 LLM 發請求）
        Assert.Equal(5, sentenceState.CaptionId);
        Assert.Equal(VideoId, sentenceState.VideoId);
        await UntilAsync(() => llm.CallCount == 1);
        await UntilAsync(() => store.SentenceQueries.Count == 1);
        Assert.Equal([5L], store.SentenceQueries);
    }

    [Fact]
    public async Task LoadAsync_DownloadSaved_StartsPreprocessorWithReturnedId()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var store = new FakeCaptionStore();
        var (sut, llm, sentenceState) = CreateLoaderWithPreprocessor(state, provider, store);

        await sut.LoadAsync(VideoId, null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // SaveCaption 回傳 1：等存檔完成後才以前處理啟動（LoadAsync 內已 await）
        Assert.Equal(1, sentenceState.CaptionId);
        Assert.Equal(VideoId, sentenceState.VideoId);
        await UntilAsync(() => llm.CallCount == 1);
    }

    [Fact]
    public async Task LoadAsync_SaveCaptionReturnsNull_DoesNotStartPreprocessor()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var store = new FakeCaptionStore { ReturnNullOnSave = true };
        var (sut, llm, sentenceState) = CreateLoaderWithPreprocessor(state, provider, store);

        await sut.LoadAsync(VideoId, null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 資料庫不可用：沒有 caption Id 就不啟動前處理（狀態維持初始、不叫 LLM）
        Assert.Null(sentenceState.CaptionId);
        Assert.Null(sentenceState.VideoId);
        Assert.Equal(0, llm.CallCount);
        Assert.Empty(store.SentenceQueries);
    }

    [Fact]
    public async Task Reset_StopsPreprocessorAndClearsSentenceState()
    {
        var state = new CaptionTrackState();
        var provider = new FakeCaptionProvider();
        var cues = ManyCues(150);
        var store = new FakeCaptionStore
        {
            Result = new StoredCaption(5, VideoId, "ja", true, "IOS", cues, CachedAt, CachedAt),
        };
        var (sut, llm, sentenceState) = CreateLoaderWithPreprocessor(state, provider, store);
        // 第一次 LLM 呼叫擋在閘門上，回覆可以讓第 1 批存一句
        llm.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        llm.Replies.Enqueue("{\"sentences\":[{\"from\":0,\"to\":0,\"text\":\"セリフ0\"}]}");

        await sut.LoadAsync(VideoId, null, skipIfLive: true).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await UntilAsync(() => llm.CallCount == 1);
        Assert.Equal(5, sentenceState.CaptionId);

        // Reset：停掉前處理並清空句子狀態
        sut.Reset();
        Assert.Null(sentenceState.CaptionId);
        Assert.Null(sentenceState.VideoId);

        // 放行之後第 1 批照常存檔（已經在跑），但取消後不會再處理第 2 批
        llm.Gate.SetResult();
        await UntilAsync(() => store.SentenceSaves.Count == 1);
        Assert.Equal(1, llm.CallCount);
        // 狀態已清空：第 1 批的句子加不回去
        Assert.Empty(sentenceState.Sentences);
    }

    /// <summary>不連網的知識庫模型替身：可排腳本回覆、可閘門控制第一次呼叫。</summary>
    private sealed class FakeLorebookLlm : ILorebookLlm
    {
        public Queue<string> Replies { get; } = new();

        public TaskCompletionSource? Gate { get; set; }

        public int CallCount { get; private set; }

        public async Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            var content = Replies.Count > 0 ? Replies.Dequeue() : "{\"sentences\":[]}";
            return new LlmJsonResult(content, 100, 50, 5, "fake-model");
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }

    private static CaptionLoader CreateLoader(CaptionTrackState state, FakeCaptionProvider provider, FakePageInfo? pageInfo = null,
        ICaptionStore? store = null) =>
        new(state, provider, pageInfo, NullLogger<CaptionLoader>.Instance, store);

    private static YouTubePageInfo Info(bool isLiveNow, string? visitorData = null) =>
        new(VideoId, null, null, null, null, null, null, [], isLiveNow, false, false, null, null, null, [], visitorData);

    /// <summary>輪詢直到條件成立（逾時 5 秒視為失敗）：不用固定的 Delay 等背景工作。</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等候條件逾時");
            await Task.Delay(10);
        }
    }

    /// <summary>不連網的假字幕下載：可用閘門控制完成時間，預設回傳 3 段日文字幕。</summary>
    private sealed class FakeCaptionProvider : IYouTubeCaptionProvider
    {
        public TaskCompletionSource<YouTubeCaptionResult?>? Gate { get; set; }

        public Exception? Throw { get; set; }

        /// <summary>true 時回傳 null（模擬影片沒有日文字幕）。</summary>
        public bool Empty { get; set; }

        /// <summary>回傳結果的語言代碼（預設 ja）。</summary>
        public string LanguageCode { get; set; } = "ja";

        public int CallCount { get; private set; }

        public List<string> VideoIds { get; } = [];

        public async Task<YouTubeCaptionResult?> GetJapaneseCaptionsAsync(
            string videoId, string? visitorData, CancellationToken cancellationToken = default)
        {
            CallCount++;
            VideoIds.Add(videoId);
            if (Throw is not null)
            {
                throw Throw;
            }

            if (Gate is not null)
            {
                await Gate.Task;
            }

            return Empty ? null : new YouTubeCaptionResult(LanguageCode, true, "IOS", ThreeCues);
        }
    }

    /// <summary>不連資料庫的假字幕快取：記錄呼叫；可用閘門控制 GetCaption 的完成時間。</summary>
    private sealed class FakeCaptionStore : ICaptionStore
    {
        public StoredCaption? Result { get; set; }

        public TaskCompletionSource<StoredCaption?>? Gate { get; set; }

        public Exception? Throw { get; set; }

        /// <summary>true 時 SaveCaption 回 null（模擬資料庫不可用）。</summary>
        public bool ReturnNullOnSave { get; set; }

        /// <summary>GetSentences 回傳的種子句子。</summary>
        public IReadOnlyList<CaptionSentence> Sentences { get; set; } = [];

        public int GetCount { get; private set; }

        public List<(string VideoId, string Language)> Queries { get; } = [];

        public List<(string VideoId, string Language, bool IsAuto, string? ClientName, IReadOnlyList<CaptionCue> Cues)> Saves { get; } = [];

        public List<long> TouchedIds { get; } = [];

        public List<long> SentenceQueries { get; } = [];

        public List<(long CaptionId, IReadOnlyList<CaptionSentence> Sentences)> SentenceSaves { get; } = [];

        public StoredCaption? GetCaption(string videoId, string language)
        {
            GetCount++;
            Queries.Add((videoId, language));
            if (Gate is not null)
            {
                Gate.Task.Wait(TimeSpan.FromSeconds(5));
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            return Result;
        }

        public long? SaveCaption(string videoId, string language, bool isAuto, string? clientName, IReadOnlyList<CaptionCue> cues)
        {
            Saves.Add((videoId, language, isAuto, clientName, cues));
            return ReturnNullOnSave ? null : Saves.Count;
        }

        public void TouchCaption(long id) => TouchedIds.Add(id);

        public IReadOnlyList<CaptionSentence> GetSentences(long captionId)
        {
            SentenceQueries.Add(captionId);
            return Sentences;
        }

        public void SaveSentences(long captionId, IReadOnlyList<CaptionSentence> sentences) =>
            SentenceSaves.Add((captionId, sentences));
    }

    /// <summary>不連網的假影片頁面資訊：回傳指定的結果並計數。</summary>
    private sealed class FakePageInfo : IYouTubePageInfoProvider
    {
        public YouTubePageInfo? Result { get; set; }

        public int CallCount { get; private set; }

        public Task<YouTubePageInfo?> GetPageInfoAsync(string url, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }
}
