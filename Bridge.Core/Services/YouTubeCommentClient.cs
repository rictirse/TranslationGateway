using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>匿名呼叫 InnerTube next API 取得影片的熱門留言（不需要 API Key）：
/// 先帶 videoId 拿留言區的 continuation token，再帶 token 拿留言內容；
/// <see cref="GetCommentPagesAsync"/> 可依 continuation token 往下翻頁（熱門留言的セトリ常在第 2、3 頁）。
/// 關閉留言回空清單；網路錯誤、逾時、格式不符回 null，不擲例外。</summary>
public sealed class YouTubeCommentClient(HttpClient httpClient, ILogger<YouTubeCommentClient> logger) : IYouTubeCommentProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string NextEndpoint = "https://www.youtube.com/youtubei/v1/next?prettyPrint=false";

    /// <summary>桌面 Chrome 的 User-Agent（與 <see cref="YouTubeWatchPageClient"/> 用同一個字串）。</summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>WEB 用戶端的 context；每次請求都帶同一份。用繁中／台灣（使用者的瀏覽器設定）：「人気順」會依語言地區等條件調整排序，
    /// 但每次查詢不一定相同（9o6oRDN7iog 的セトリ實測排過第 1、第 2 頁、第 33 則，2026-10-26），所以章節服務會往下翻頁。</summary>
    private static readonly object Context = new
    {
        client = new { clientName = "WEB", clientVersion = "2.20251020.01.00", hl = "zh-TW", gl = "TW" }
    };

    /// <summary>一頁留言的結果：<see cref="Comments"/> 為 null 表示這頁讀不到（已在內部記 log、不擲例外）；
    /// 讀得到時 <see cref="NextToken"/> 是下一頁的 continuation token（沒有下一頁為 null）。</summary>
    private sealed record CommentPage(IReadOnlyList<YouTubeComment>? Comments, string? NextToken);

    public async Task<IReadOnlyList<YouTubeComment>?> GetTopCommentsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var page = await GetFirstPageAsync(videoId, cancellationToken).ConfigureAwait(false);
        return page.Comments;
    }

    /// <summary>依序取得熱門留言的每一頁（最多 <paramref name="maxPages"/> 頁）：
    /// 第一頁流程同 <see cref="GetTopCommentsAsync"/>，之後用上一頁回應的下一頁 token 繼續。
    /// 沒有下一頁 token 或已達上限就結束；某一頁 HTTP 失敗／JSON 錯誤／逾時就記 Warning、結束（不擲例外）；
    /// 每頁逾時 10 秒（各自計時）；呼叫端取消照樣往外拋。</summary>
    public async IAsyncEnumerable<IReadOnlyList<YouTubeComment>> GetCommentPagesAsync(string videoId, int maxPages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (maxPages <= 0)
        {
            yield break;
        }

        var page = await GetFirstPageAsync(videoId, cancellationToken).ConfigureAwait(false);
        if (page.Comments is null)
        {
            // 第一頁就讀不到：一頁都沒有（GetTopCommentsAsync 回 null 的情況，已記 log）
            yield break;
        }

        yield return page.Comments;

        for (var pageNumber = 2; pageNumber <= maxPages && page.NextToken is { } nextToken; pageNumber++)
        {
            page = await GetNextPageAsync(videoId, nextToken, cancellationToken).ConfigureAwait(false);
            if (page.Comments is null)
            {
                // 這頁失敗：停止翻頁，已 yield 的頁照常回傳
                yield break;
            }

            yield return page.Comments;
        }
    }

    /// <summary>第一頁的流程（同 <see cref="GetTopCommentsAsync"/>）：先帶 videoId 取留言區 token、再取第一頁留言；
    /// 兩次請求共用同一個 10 秒逾時。失敗（含逾時）記 Warning 並回 <see cref="CommentPage.Comments"/> = null。</summary>
    private async Task<CommentPage> GetFirstPageAsync(string videoId, CancellationToken cancellationToken)
    {
        try
        {
            // 每頁逾時 10 秒（各自計時）：用 linked CTS，呼叫端取消時照樣往外拋
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);

            var firstBody = JsonSerializer.Serialize(new { context = Context, videoId });
            using var firstResponse = await PostAsync(firstBody, cts.Token).ConfigureAwait(false);
            if (!firstResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("YouTube 留言：第一次 next 請求回傳 {StatusCode}（{VideoId}）", (int)firstResponse.StatusCode, videoId);
                return new CommentPage(null, null);
            }

            var firstJson = await firstResponse.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            string? continuation;
            using (var firstDocument = JsonDocument.Parse(firstJson))
            {
                continuation = FindCommentsContinuation(firstDocument.RootElement);
            }

            if (continuation is null)
            {
                logger.LogInformation("YouTube 留言：{VideoId} 找不到留言區（關閉留言或沒有留言）", videoId);
                return new CommentPage([], null);
            }

            return await ReadPageAsync(videoId, continuation, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedFailure(ex, cancellationToken))
        {
            // 逾時、網路錯誤、格式不符都視為讀不到；呼叫端自己取消的 OperationCanceledException 照樣往外拋
            logger.LogWarning(ex, "YouTube 留言讀取失敗：{VideoId}", videoId);
            return new CommentPage(null, null);
        }
    }

    /// <summary>帶下一頁的 continuation token 取一頁留言（自己的 10 秒逾時）；
    /// 失敗（含逾時）記 Warning 並回 <see cref="CommentPage.Comments"/> = null。</summary>
    private async Task<CommentPage> GetNextPageAsync(string videoId, string continuation, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);
            return await ReadPageAsync(videoId, continuation, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedFailure(ex, cancellationToken))
        {
            logger.LogWarning(ex, "YouTube 留言讀取失敗（{VideoId}，第 2 頁起）", videoId);
            return new CommentPage(null, null);
        }
    }

    /// <summary>帶 continuation token 讀一頁留言回應：解析留言與下一頁 token；HTTP 失敗回 <see cref="CommentPage.Comments"/> = null。</summary>
    private async Task<CommentPage> ReadPageAsync(string videoId, string continuation, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { context = Context, continuation });
        using var response = await PostAsync(body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("YouTube 留言：留言頁請求回傳 {StatusCode}（{VideoId}）", (int)response.StatusCode, videoId);
            return new CommentPage(null, null);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<YouTubeComment> comments;
        string? nextToken;
        using (var document = JsonDocument.Parse(json))
        {
            comments = ParseComments(document.RootElement, videoId);
            nextToken = FindNextPageContinuation(document.RootElement);
        }

        logger.LogInformation("YouTube 留言：{VideoId} 取得 {Count} 則熱門留言（含時間戳連結 {TimestampCount} 則）",
            videoId, comments.Count, comments.Count(static c => c.Timestamps.Count > 0));
        return new CommentPage(comments, nextToken);
    }

    /// <summary>留言讀取要吞掉的例外：網路錯誤／JSON 格式錯誤／自己的逾時；
    /// 呼叫端 token 取消的 OperationCanceledException 不吞（要往外拋）。</summary>
    private static bool IsExpectedFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or JsonException or ArgumentException or InvalidOperationException ||
        (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>POST next 端點（JSON body、桌面 Chrome 的 User-Agent）。</summary>
    private async Task<HttpResponseMessage> PostAsync(string body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, NextEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return await httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>從第一次 next 回應找留言區的 continuation token；找不到（關閉留言）回 null。</summary>
    public static string? FindCommentsContinuation(JsonElement nextResponse)
    {
        if (nextResponse.ValueKind != JsonValueKind.Object ||
            !nextResponse.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Object ||
            !contents.TryGetProperty("twoColumnWatchNextResults", out var twoColumn) || twoColumn.ValueKind != JsonValueKind.Object ||
            !twoColumn.TryGetProperty("results", out var resultsOuter) || resultsOuter.ValueKind != JsonValueKind.Object ||
            !resultsOuter.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object ||
            !results.TryGetProperty("contents", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("itemSectionRenderer", out var section) || section.ValueKind != JsonValueKind.Object ||
                !string.Equals(GetString(section, "sectionIdentifier"), "comment-item-section", StringComparison.Ordinal) ||
                !section.TryGetProperty("contents", out var sectionContents) || sectionContents.ValueKind != JsonValueKind.Array ||
                sectionContents.GetArrayLength() == 0)
            {
                continue;
            }

            var first = sectionContents[0];
            if (first.ValueKind == JsonValueKind.Object &&
                first.TryGetProperty("continuationItemRenderer", out var continuation) && continuation.ValueKind == JsonValueKind.Object &&
                continuation.TryGetProperty("continuationEndpoint", out var endpoint) && endpoint.ValueKind == JsonValueKind.Object &&
                endpoint.TryGetProperty("continuationCommand", out var command) && command.ValueKind == JsonValueKind.Object &&
                command.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String &&
                token.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>從留言頁回應找下一頁的 continuation token；沒有回 null。
    /// token 在 continuationItems 的最後一個 continuationItemRenderer 裡（continuationEndpoint 或 button.buttonRenderer 兩種路徑擇一）。</summary>
    public static string? FindNextPageContinuation(JsonElement commentsResponse)
    {
        if (commentsResponse.ValueKind != JsonValueKind.Object ||
            !commentsResponse.TryGetProperty("onResponseReceivedEndpoints", out var endpoints) ||
            endpoints.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // 掃所有 endpoint 的 continuationItems，取最後一個讀得到 token 的 continuationItemRenderer
        string? token = null;
        foreach (var endpoint in endpoints.EnumerateArray())
        {
            var items = GetContinuationItems(endpoint);
            if (items is null)
            {
                continue;
            }

            foreach (var item in items.Value.EnumerateArray())
            {
                if (ReadContinuationToken(item) is { } found)
                {
                    token = found;
                }
            }
        }

        return token;
    }

    /// <summary>continuationItemRenderer 的下一頁 token：continuationEndpoint.continuationCommand 或 button.buttonRenderer.command 兩種路徑擇一；其他項目回 null。</summary>
    private static string? ReadContinuationToken(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("continuationItemRenderer", out var renderer) || renderer.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (renderer.TryGetProperty("continuationEndpoint", out var endpoint) && endpoint.ValueKind == JsonValueKind.Object &&
            endpoint.TryGetProperty("continuationCommand", out var command) && command.ValueKind == JsonValueKind.Object &&
            command.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String &&
            token.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        if (renderer.TryGetProperty("button", out var button) && button.ValueKind == JsonValueKind.Object &&
            button.TryGetProperty("buttonRenderer", out var buttonRenderer) && buttonRenderer.ValueKind == JsonValueKind.Object &&
            buttonRenderer.TryGetProperty("command", out var buttonCommand) && buttonCommand.ValueKind == JsonValueKind.Object &&
            buttonCommand.TryGetProperty("continuationCommand", out var buttonContinuation) && buttonContinuation.ValueKind == JsonValueKind.Object &&
            buttonContinuation.TryGetProperty("token", out var buttonToken) && buttonToken.ValueKind == JsonValueKind.String &&
            buttonToken.GetString() is { Length: > 0 } buttonValue)
        {
            return buttonValue;
        }

        return null;
    }

    /// <summary>從留言頁回應解析頂層留言；只收 videoId 相同的時間戳連結。</summary>
    public static IReadOnlyList<YouTubeComment> ParseComments(JsonElement commentsResponse, string videoId)
    {
        if (commentsResponse.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        // 頂層留言的 commentKey，照 onResponseReceivedEndpoints → continuationItems 的出現順序
        List<string> commentKeys = [];
        if (commentsResponse.TryGetProperty("onResponseReceivedEndpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Array)
        {
            foreach (var endpoint in endpoints.EnumerateArray())
            {
                var items = GetContinuationItems(endpoint);
                if (items is null) continue;

                foreach (var item in items.Value.EnumerateArray())
                {
                    // commentThreadRenderer.commentViewModel.commentViewModel.commentKey；
                    // commentsHeaderRenderer、continuationItemRenderer 等其他項目略過
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("commentThreadRenderer", out var thread) && thread.ValueKind == JsonValueKind.Object &&
                        thread.TryGetProperty("commentViewModel", out var viewModelOuter) && viewModelOuter.ValueKind == JsonValueKind.Object &&
                        viewModelOuter.TryGetProperty("commentViewModel", out var viewModel) && viewModel.ValueKind == JsonValueKind.Object &&
                        viewModel.TryGetProperty("commentKey", out var key) && key.ValueKind == JsonValueKind.String &&
                        key.GetString() is { Length: > 0 } commentKey)
                    {
                        commentKeys.Add(commentKey);
                    }
                }
            }
        }

        // entityBatchUpdate.mutations[] 的 commentEntityPayload，用 key 對 commentKey
        Dictionary<string, JsonElement> payloads = new(StringComparer.Ordinal);
        if (commentsResponse.TryGetProperty("frameworkUpdates", out var updates) && updates.ValueKind == JsonValueKind.Object &&
            updates.TryGetProperty("entityBatchUpdate", out var batch) && batch.ValueKind == JsonValueKind.Object &&
            batch.TryGetProperty("mutations", out var mutations) && mutations.ValueKind == JsonValueKind.Array)
        {
            foreach (var mutation in mutations.EnumerateArray())
            {
                if (mutation.ValueKind == JsonValueKind.Object &&
                    mutation.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("commentEntityPayload", out var commentPayload) && commentPayload.ValueKind == JsonValueKind.Object &&
                    commentPayload.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String &&
                    key.GetString() is { Length: > 0 } payloadKey)
                {
                    payloads[payloadKey] = commentPayload;
                }
            }
        }

        List<YouTubeComment> comments = [];
        foreach (var commentKey in commentKeys)
        {
            if (payloads.TryGetValue(commentKey, out var payload) && ReadComment(payload, videoId) is { } comment)
            {
                comments.Add(comment);
            }
        }

        return comments;
    }

    /// <summary>endpoint 的 continuationItems：reloadContinuationItemsCommand 或 appendContinuationItemsAction 擇一。</summary>
    private static JsonElement? GetContinuationItems(JsonElement endpoint)
    {
        if (endpoint.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (endpoint.TryGetProperty("reloadContinuationItemsCommand", out var reload) &&
            reload.ValueKind == JsonValueKind.Object &&
            reload.TryGetProperty("continuationItems", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            return items;
        }

        if (endpoint.TryGetProperty("appendContinuationItemsAction", out var append) &&
            append.ValueKind == JsonValueKind.Object &&
            append.TryGetProperty("continuationItems", out var appendItems) &&
            appendItems.ValueKind == JsonValueKind.Array)
        {
            return appendItems;
        }

        return null;
    }

    /// <summary>把 commentEntityPayload 轉成 <see cref="YouTubeComment"/>；缺欄位、型別不符回 null（略過該則，不擲例外）。</summary>
    private static YouTubeComment? ReadComment(JsonElement payload, string videoId)
    {
        if (!payload.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            GetString(properties, "commentId") is not { Length: > 0 } commentId ||
            !properties.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object ||
            GetString(content, "content") is not { } text)
        {
            return null;
        }

        // likeCountNotliked 不存在當 ""（沒有人按讚時 YouTube 不會給這個欄位）
        var likeCountText = payload.TryGetProperty("toolbar", out var toolbar) && toolbar.ValueKind == JsonValueKind.Object
            ? GetString(toolbar, "likeCountNotliked")
            : null;

        List<CommentTimestamp> timestamps = [];
        if (content.TryGetProperty("commandRuns", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            foreach (var run in runs.EnumerateArray())
            {
                if (ReadTimestamp(run, videoId) is { } timestamp)
                {
                    timestamps.Add(timestamp);
                }
            }
        }

        return new YouTubeComment(commentId, text, ParseLikeCount(likeCountText), timestamps);
    }

    /// <summary>commandRun → 時間戳連結：只收連到同支影片（videoId 相同）且有 startTimeSeconds 的 run。</summary>
    private static CommentTimestamp? ReadTimestamp(JsonElement run, string videoId)
    {
        if (run.ValueKind != JsonValueKind.Object ||
            GetInt(run, "startIndex") is not { } startIndex ||
            GetInt(run, "length") is not { } length ||
            !run.TryGetProperty("onTap", out var onTap) || onTap.ValueKind != JsonValueKind.Object ||
            !onTap.TryGetProperty("innertubeCommand", out var command) || command.ValueKind != JsonValueKind.Object ||
            !command.TryGetProperty("watchEndpoint", out var watchEndpoint) || watchEndpoint.ValueKind != JsonValueKind.Object ||
            !string.Equals(GetString(watchEndpoint, "videoId"), videoId, StringComparison.Ordinal) ||
            GetInt(watchEndpoint, "startTimeSeconds") is not { } seconds)
        {
            return null;
        }

        return new CommentTimestamp(startIndex, length, seconds);
    }

    /// <summary>"" → 0、"68" → 68、"1,234" → 1234、"1.2K" → 1200、"3.4万" → 34000、"5千" → 5000；看不懂回 0。</summary>
    public static int ParseLikeCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        // 千分位逗號拿掉再解析
        var value = text.Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        if (value.Length == 0)
        {
            return 0;
        }

        var multiplier = value[^1] switch
        {
            'K' or 'k' => 1_000,
            'M' or 'm' => 1_000_000,
            '万' or '萬' => 10_000,
            '千' => 1_000,
            _ => 1
        };
        if (multiplier != 1)
        {
            value = value[..^1];
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? (int)Math.Round(number * multiplier, MidpointRounding.AwayFromZero)
            : 0;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
