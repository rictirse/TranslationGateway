using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>影片章節服務（GET /chapters 端點用）：呼叫 <see cref="IYouTubeCommentProvider"/> 抓熱門留言（最多 3 頁，每取得一頁就找一次セトリ），
/// 交給 <see cref="ChapterExtractor"/> 整理成章節。結果（含空清單）在記憶體快取 30 分鐘；
/// 讀取失敗與逾時回空清單、不快取。同一個 videoId 同時多個請求共用同一個進行中的 Task，只打一次 YouTube。
/// PotPlayer 開片時同步等待：整體逾時 15 秒（多頁翻頁），逾時回空清單；呼叫端的 token 取消照樣拋出。</summary>
public sealed class VideoChapterService(IYouTubeCommentProvider comments, ILogger<VideoChapterService> logger)
{
    /// <summary>結果在記憶體快取的時間（key = videoId）。</summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    /// <summary>留言最多往下翻幾頁（セトリ常被 YouTube 排在熱門留言第 2 頁）。</summary>
    private const int MaxCommentPages = 3;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <summary>進行中的載入（key = videoId）：同時多個請求共用同一個 Task。</summary>
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<VideoChapter>>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>整體逾時（PotPlayer 開片時同步等待）；測試可注入短逾時，預設 15 秒。</summary>
    internal TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>取得影片章節；查不到或失敗回空清單，不擲例外（呼叫端取消除外）。</summary>
    public async Task<IReadOnlyList<VideoChapter>> GetChaptersAsync(string videoId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(videoId, out var entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            return entry.Chapters;
        }

        return await GetOrStartLoad(videoId).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取得（或共用）這個 videoId 進行中的載入 Task；搶不到就建一個新的，完成時自己從 <see cref="_inFlight"/> 移除。</summary>
    private Task<IReadOnlyList<VideoChapter>> GetOrStartLoad(string videoId)
    {
        if (_inFlight.TryGetValue(videoId, out var existing))
        {
            return existing;
        }

        var completion = new TaskCompletionSource<IReadOnlyList<VideoChapter>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inFlight.TryAdd(videoId, completion.Task))
        {
            // 別人搶先一步建了：用別人的
            return _inFlight[videoId];
        }

        _ = LoadAsync(videoId, completion);
        return completion.Task;
    }

    /// <summary>背景載入：把 <see cref="FetchAsync"/> 的結果回給等待者，結束時把登記的 Task 移除。</summary>
    private async Task LoadAsync(string videoId, TaskCompletionSource<IReadOnlyList<VideoChapter>> completion)
    {
        try
        {
            completion.TrySetResult(await FetchAsync(videoId).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            // FetchAsync 已把預期的失敗轉成空清單，這裡只剩非預期例外：照樣回給等待者
            completion.TrySetException(ex);
        }
        finally
        {
            _inFlight.TryRemove(KeyValuePair.Create(videoId, completion.Task));
        }
    }

    /// <summary>抓留言（最多 <see cref="MaxCommentPages"/> 頁）並整理章節：每取得一頁就把累積的留言跑一次
    /// <see cref="ChapterExtractor.FromComments"/>，有章節就停止翻頁並回傳。
    /// 一頁都沒拿到（第一頁就失敗）、逾時或例外回空清單且不快取；成功（含 0 章）的結果快取 30 分鐘。
    /// 逾時用的是自己的 CTS，不連動呼叫端的 token。</summary>
    private async Task<IReadOnlyList<VideoChapter>> FetchAsync(string videoId)
    {
        var accumulated = new List<YouTubeComment>();
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var pageCount = 0;
            await foreach (var page in comments.GetCommentPagesAsync(videoId, MaxCommentPages, cts.Token).ConfigureAwait(false))
            {
                pageCount++;
                accumulated.AddRange(page);

                if (ChapterExtractor.FromComments(accumulated, durationMs: null) is { Count: > 0 } found)
                {
                    logger.LogInformation("影片章節：{VideoId} 在第 {Page} 頁留言找到セトリ，整理出 {Count} 個章節", videoId, pageCount, found.Count);
                    return Cache(videoId, found);
                }
            }

            if (pageCount == 0)
            {
                // 第一頁就失敗（一頁都沒拿到）：視為讀不到留言
                logger.LogWarning("影片章節：{VideoId} 讀不到留言，這次不提供章節（失敗不快取）", videoId);
                return [];
            }

            logger.LogInformation("影片章節：{VideoId} 留言裡沒有找到セトリ（翻了 {Pages} 頁、{Count} 則留言）", videoId, pageCount, accumulated.Count);
            return Cache(videoId, []);
        }
        catch (OperationCanceledException)
        {
            // 逾時：若已累積出合格章節就回傳它，否則照舊空清單、不快取
            if (ChapterExtractor.FromComments(accumulated, durationMs: null) is { Count: > 0 } found)
            {
                logger.LogInformation("影片章節：{VideoId} 逾時前已在留言找到セトリ，整理出 {Count} 個章節", videoId, found.Count);
                return Cache(videoId, found);
            }

            logger.LogWarning("影片章節：{VideoId} 讀取逾時（{Seconds:0.#} 秒），這次不提供章節", videoId, Timeout.TotalSeconds);
            return [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "影片章節：{VideoId} 讀取失敗，這次不提供章節", videoId);
            return [];
        }
    }

    /// <summary>把結果寫進快取並回傳。</summary>
    private VideoChapter[] Cache(string videoId, IReadOnlyList<VideoChapter> chapters)
    {
        _cache[videoId] = new CacheEntry(chapters, DateTimeOffset.UtcNow + CacheDuration);
        return [.. chapters];
    }

    /// <summary>快取項：章節與到期時間（UTC）。</summary>
    private readonly record struct CacheEntry(IReadOnlyList<VideoChapter> Chapters, DateTimeOffset ExpiresAtUtc);
}
