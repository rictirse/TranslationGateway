using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class VideoChapterServiceTests
{
    private const string VideoId = "dQw4w9WgXcQ";

    /// <summary>假的留言提供者：設 <see cref="Pages"/> 時走多頁模式（null 頁＝那頁讀不到，之後停止）；
    /// 沒設時 <see cref="Handler"/> 決定回什麼（回 null 模擬失敗），null 時一直等到 token 取消。</summary>
    private sealed class FakeCommentProvider : IYouTubeCommentProvider
    {
        public int CallCount { get; private set; }

        public Func<string, CancellationToken, Task<IReadOnlyList<YouTubeComment>?>>? Handler { get; set; }

        /// <summary>多頁模式依序提供的頁（null＝讀不到，之後不再有頁）。</summary>
        public IReadOnlyList<IReadOnlyList<YouTubeComment>?>? Pages { get; set; }

        /// <summary>多頁模式實際被消費者讀走的頁數（呼叫端停止翻頁就不會再增加）。</summary>
        public int ConsumedPages { get; private set; }

        public async Task<IReadOnlyList<YouTubeComment>?> GetTopCommentsAsync(string videoId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Handler is null)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return [];
            }

            return await Handler(videoId, cancellationToken);
        }

        public async IAsyncEnumerable<IReadOnlyList<YouTubeComment>> GetCommentPagesAsync(string videoId, int maxPages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Pages is null)
            {
                // 退回單頁模式（同介面預設實作）
                var first = await GetTopCommentsAsync(videoId, cancellationToken);
                if (first is not null)
                {
                    ConsumedPages++;
                    yield return first;
                }

                yield break;
            }

            CallCount++;
            foreach (var page in Pages.Take(maxPages))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (page is null)
                {
                    yield break; // 這頁讀不到：停止（已取得的頁照常回傳）
                }

                ConsumedPages++;
                yield return page;
            }
        }
    }

    /// <summary>三行時間軸的セトリ留言（ChapterExtractor 會整理出「開始」＋三章）。</summary>
    private static IReadOnlyList<YouTubeComment> SetlistComments()
    {
        // "1:00 A\n2:00 B\n3:00 C"：每行第一個時間戳連結在行首（0、7、14）
        var text = "1:00 A\n2:00 B\n3:00 C";
        return
        [
            new YouTubeComment("setlist", text, 5,
                [new CommentTimestamp(0, 4, 60), new CommentTimestamp(7, 4, 120), new CommentTimestamp(14, 4, 180)]),
        ];
    }

    /// <summary>SetlistComments 應整理出的章節（第一章補「開始」）。</summary>
    private static readonly IReadOnlyList<VideoChapter> ExpectedChapters =
    [
        new VideoChapter(0, "開始"),
        new VideoChapter(60_000, "A"),
        new VideoChapter(120_000, "B"),
        new VideoChapter(180_000, "C"),
    ];

    [Fact]
    public async Task GetChapters_CachesResult()
    {
        var provider = new FakeCommentProvider
        {
            Handler = (_, _) => Task.FromResult<IReadOnlyList<YouTubeComment>?>(SetlistComments()),
        };
        var service = new VideoChapterService(provider, NullLogger<VideoChapterService>.Instance);

        var first = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);
        var second = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedChapters, first);
        Assert.Equal(ExpectedChapters, second);
        // 第二次用快取，只打一次 provider
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task GetChapters_ProviderNull_ReturnsEmpty_NotCached()
    {
        var provider = new FakeCommentProvider
        {
            Handler = (_, _) => Task.FromResult<IReadOnlyList<YouTubeComment>?>(null),
        };
        var service = new VideoChapterService(provider, NullLogger<VideoChapterService>.Instance);

        var first = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);
        var second = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);

        Assert.Empty(first);
        Assert.Empty(second);
        // 失敗（null）不快取：兩次都重打
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task GetChapters_ConcurrentCalls_ShareOneRequest()
    {
        // 等兩個請求都進來才放行，確保真的同時在跑
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeCommentProvider
        {
            Handler = async (_, _) =>
            {
                await gate.Task;
                return SetlistComments();
            },
        };
        var service = new VideoChapterService(provider, NullLogger<VideoChapterService>.Instance);

        var task1 = service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);
        var task2 = service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);
        gate.SetResult();
        var results = await Task.WhenAll(task1, task2);

        // 同時多個請求共用同一個進行中的 Task，只打一次 YouTube
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(ExpectedChapters, results[0]);
        Assert.Equal(results[0], results[1]);
    }

    [Fact]
    public async Task GetChapters_Timeout_ReturnsEmpty()
    {
        var provider = new FakeCommentProvider();
        IReadOnlyList<YouTubeComment>? comments = SetlistComments();
        var service = new VideoChapterService(provider, NullLogger<VideoChapterService>.Instance)
        {
            // 測試注入短逾時（internal），不真的等 8 秒
            Timeout = TimeSpan.FromMilliseconds(100),
        };

        var timedOut = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);

        // 逾時回空清單
        Assert.Empty(timedOut);
        Assert.Equal(1, provider.CallCount);

        // 逾時不快取：下一次重打（這次馬上回章節）
        provider.Handler = (_, _) => Task.FromResult(comments);
        var second = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);
        Assert.Equal(ExpectedChapters, second);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task GetChapters_CallerCancelled_Throws()
    {
        var provider = new FakeCommentProvider();
        var service = new VideoChapterService(provider, NullLogger<VideoChapterService>.Instance)
        {
            Timeout = TimeSpan.FromMilliseconds(200),
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // 呼叫端的 token 取消照樣拋出（WaitAsync 擲的是 OperationCanceledException 的子類別）
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetChaptersAsync(VideoId, cts.Token));
    }

    /// <summary>沒有時間軸的普通留言（湊不出章節）。</summary>
    private static IReadOnlyList<YouTubeComment> PlainComments(int count) =>
        Enumerable.Range(0, count)
            .Select(static i => new YouTubeComment($"comment-{i}", "草", 1, []))
            .ToList();

    [Fact]
    public async Task GetChapters_SetlistOnSecondPage_FindsItAndStopsPaging()
    {
        var provider = new FakeCommentProvider
        {
            Pages =
            [
                PlainComments(20),
                SetlistComments(),
                PlainComments(20), // 第 3 頁不應被讀走
            ],
        };
        var service = new VideoChapterService(provider, NullLogger<VideoChapterService>.Instance);

        var chapters = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedChapters, chapters);
        // 找到就停：只消費到第 2 頁
        Assert.Equal(2, provider.ConsumedPages);

        // 快取：第二次不再打 provider
        var second = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);
        Assert.Equal(ExpectedChapters, second);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task GetChapters_SetlistOnFirstPage_DoesNotPageFurther()
    {
        var provider = new FakeCommentProvider
        {
            Pages =
            [
                SetlistComments(),
                PlainComments(20), // 不應被讀走
            ],
        };
        var service = new VideoChapterService(provider, NullLogger<VideoChapterService>.Instance);

        var chapters = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedChapters, chapters);
        Assert.Equal(1, provider.ConsumedPages);
    }

    [Fact]
    public async Task GetChapters_NoSetlistInThreePages_ReturnsEmptyAndCaches()
    {
        var provider = new FakeCommentProvider
        {
            Pages = [PlainComments(20), PlainComments(20), PlainComments(20)],
        };
        var service = new VideoChapterService(provider, NullLogger<VideoChapterService>.Instance);

        var first = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);

        // 三頁都沒有 → 空清單，但照樣快取（第二次不打 provider）
        Assert.Empty(first);
        Assert.Equal(3, provider.ConsumedPages);
        var second = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);
        Assert.Empty(second);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task GetChapters_SecondPageFails_UsesAccumulatedPages()
    {
        var provider = new FakeCommentProvider
        {
            Pages =
            [
                PlainComments(20),
                null, // 第 2 頁讀不到：停止
            ],
        };
        var service = new VideoChapterService(provider, NullLogger<VideoChapterService>.Instance);

        var chapters = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);

        // 第一頁照常整理（沒有セトリ → 空清單）且快取
        Assert.Empty(chapters);
        Assert.Equal(1, provider.ConsumedPages);
        var second = await service.GetChaptersAsync(VideoId, TestContext.Current.CancellationToken);
        Assert.Empty(second);
        Assert.Equal(1, provider.CallCount);
    }
}
