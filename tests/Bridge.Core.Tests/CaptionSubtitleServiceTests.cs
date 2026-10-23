using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>CaptionSubtitleService：GET /subtitle 的 WebVTT 組裝（假 provider／store，不連網）。</summary>
public class CaptionSubtitleServiceTests
{
    private const string VideoId = "dQw4w9WgXcQ";

    private static readonly DateTimeOffset CachedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly CaptionCue[] ThreeCues =
    [
        new CaptionCue(1000, 2000, "こんにちは"),
        new CaptionCue(3000, 2000, "ありがとう"),
        new CaptionCue(5000, 2000, "さようなら"),
    ];

    private static CaptionSubtitleService CreateService(CaptionTrackState state, FakeProvider provider,
        FakeStore? store = null) =>
        new(new CaptionLoader(state, provider, null, NullLogger<CaptionLoader>.Instance, store),
            state, store, NullLogger<CaptionSubtitleService>.Instance);

    [Fact]
    public async Task GetWebVttAsync_NotLoadedYet_TriggersDownloadAndOutputsMergedLines()
    {
        var state = new CaptionTrackState();
        var provider = new FakeProvider();
        var sut = CreateService(state, provider);

        var vtt = await sut.GetWebVttAsync(VideoId, CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        // 三段 cue 接成一句（沒有句尾符號、長度不到 40、cue 相差不到 3000 ms）
        Assert.Contains("こんにちはありがとうさようなら", vtt);
        Assert.StartsWith("WEBVTT\n\n", vtt);
    }

    [Fact]
    public async Task GetWebVttAsync_StoreHasSentences_UsesPreprocessedText()
    {
        var state = new CaptionTrackState();
        var provider = new FakeProvider();
        var store = new FakeStore
        {
            Caption = new StoredCaption(5, VideoId, "ja", true, "IOS", ThreeCues, CachedAt, CachedAt),
            Sentences = [new CaptionSentence(1000, 5000, "raw", "前処理された一句です。", "m")],
        };
        var sut = CreateService(state, provider, store);

        var vtt = await sut.GetWebVttAsync(VideoId, CancellationToken.None);

        // 句子涵蓋全部 cue（5000 含兩端）：只用前處理文字，不用程式合併；快取命中也不下載
        Assert.Contains("前処理された一句です。", vtt);
        Assert.DoesNotContain("ありがとう", vtt);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal([5L], store.SentenceQueryIds);
    }

    [Fact]
    public async Task GetWebVttAsync_CalledWhileLoading_DownloadsOnlyOnce()
    {
        var state = new CaptionTrackState();
        var provider = new FakeProvider
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var sut = CreateService(state, provider);
        sut.LoadTimeout = TimeSpan.FromSeconds(5);

        var first = sut.GetWebVttAsync(VideoId, CancellationToken.None);
        await UntilAsync(() => provider.CallCount == 1);
        var second = sut.GetWebVttAsync(VideoId, CancellationToken.None);

        provider.Gate.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 下載中途的第二次要求等同一個載入工作：只下載一次，兩邊都拿到合併結果
        Assert.Equal(1, provider.CallCount);
        Assert.All(results, vtt => Assert.Contains("こんにちはありがとうさようなら", vtt));
    }

    [Fact]
    public async Task GetWebVttAsync_StoreThrows_ReturnsMergedLines()
    {
        var state = new CaptionTrackState();
        var provider = new FakeProvider();
        var store = new FakeStore { Throw = new InvalidOperationException("db down") };
        var sut = CreateService(state, provider, store);

        var vtt = await sut.GetWebVttAsync(VideoId, CancellationToken.None);

        Assert.Contains("こんにちはありがとうさようなら", vtt);
    }

    [Fact]
    public async Task GetWebVttAsync_ProviderNeverResponds_ReturnsEmptyTrackAfterTimeout()
    {
        var state = new CaptionTrackState();
        var provider = new FakeProvider
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var sut = CreateService(state, provider);

        // 逾時可注入：測試用很短的逾時，不真的等 20 秒
        sut.LoadTimeout = TimeSpan.FromMilliseconds(50);

        var vtt = await sut.GetWebVttAsync(VideoId, CancellationToken.None);

        Assert.Equal("WEBVTT\n", vtt);
    }

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
    private sealed class FakeProvider : IYouTubeCaptionProvider
    {
        public TaskCompletionSource? Gate { get; set; }

        public int CallCount { get; private set; }

        public async Task<YouTubeCaptionResult?> GetJapaneseCaptionsAsync(
            string videoId, string? visitorData, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            return new YouTubeCaptionResult("ja", true, "IOS", ThreeCues);
        }
    }

    /// <summary>不連資料庫的假字幕快取：記錄 GetSentences 的呼叫。</summary>
    private sealed class FakeStore : ICaptionStore
    {
        public StoredCaption? Caption { get; set; }

        public IReadOnlyList<CaptionSentence> Sentences { get; set; } = [];

        public Exception? Throw { get; set; }

        public List<long> SentenceQueryIds { get; } = [];

        public StoredCaption? GetCaption(string videoId, string language)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            return Caption;
        }

        public long? SaveCaption(string videoId, string language, bool isAuto, string? clientName, IReadOnlyList<CaptionCue> cues) => 1;

        public void TouchCaption(long id)
        {
        }

        public IReadOnlyList<CaptionSentence> GetSentences(long captionId)
        {
            SentenceQueryIds.Add(captionId);
            return Sentences;
        }

        public void SaveSentences(long captionId, IReadOnlyList<CaptionSentence> sentences)
        {
        }
    }
}
