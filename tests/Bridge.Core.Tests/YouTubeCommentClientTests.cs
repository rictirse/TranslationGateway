using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class YouTubeCommentClientTests
{
    // ===== FindCommentsContinuation =====

    [Fact]
    public void FindCommentsContinuation_ReturnsTokenFromCommentSection()
    {
        // 兩個 itemSectionRenderer：只有 comment-item-section 那個的 token 被取出
        var response = ParseJson(
            """
            {"contents":{"twoColumnWatchNextResults":{"results":{"results":{"contents":[
                {"itemSectionRenderer":{"sectionIdentifier":"related-items","contents":[
                    {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"wrong-token"}}}}
                ]}},
                {"videoPrimaryInfoRenderer":{"title":{}}},
                {"itemSectionRenderer":{"sectionIdentifier":"comment-item-section","contents":[
                    {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"comment-token-1"}}}}
                ]}}
            ]}}}}}
            """);

        Assert.Equal("comment-token-1", YouTubeCommentClient.FindCommentsContinuation(response));
    }

    [Fact]
    public void FindCommentsContinuation_NoCommentSection_ReturnsNull()
    {
        var response = ParseJson(
            """
            {"contents":{"twoColumnWatchNextResults":{"results":{"results":{"contents":[
                {"itemSectionRenderer":{"sectionIdentifier":"related-items","contents":[
                    {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"wrong-token"}}}}
                ]}}
            ]}}}}}
            """);

        Assert.Null(YouTubeCommentClient.FindCommentsContinuation(response));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"contents":{}}""")]
    [InlineData("""{"contents":{"twoColumnWatchNextResults":{"results":{"results":{"contents":[]}}}}}""")]
    public void FindCommentsContinuation_BrokenOrEmptyShape_ReturnsNull(string json)
    {
        Assert.Null(YouTubeCommentClient.FindCommentsContinuation(ParseJson(json)));
    }

    // ===== ParseComments =====

    [Fact]
    public void ParseComments_JoinsThreadOrderWithEntityPayloads()
    {
        // mutations 裡的順序與 thread 相反：結果仍照 thread 出現順序
        var response = ParseJson(
            """
            {"onResponseReceivedEndpoints":[
                {"reloadContinuationItemsCommand":{"continuationItems":[
                    {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-a"}}}},
                    {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-b"}}}}
                ]}}
            ],
            "frameworkUpdates":{"entityBatchUpdate":{"mutations":[
                {"payload":{"commentEntityPayload":{"key":"key-b","properties":{"commentId":"comment-b","content":{"content":"セトリはこちら"}},"toolbar":{"likeCountNotliked":"1,234"}}}},
                {"payload":{"commentEntityPayload":{"key":"key-a","properties":{"commentId":"comment-a","content":{"content":"10位 8:35 星座になれたら"}},"toolbar":{"likeCountNotliked":"68"}}}}
            ]}}}
            """);

        var comments = YouTubeCommentClient.ParseComments(response, "vid1");

        Assert.Equal(2, comments.Count);
        Assert.Equal("comment-a", comments[0].CommentId);
        Assert.Equal("10位 8:35 星座になれたら", comments[0].Text);
        Assert.Equal(68, comments[0].LikeCount);
        Assert.Empty(comments[0].Timestamps);
        Assert.Equal("comment-b", comments[1].CommentId);
        Assert.Equal("セトリはこちら", comments[1].Text);
        Assert.Equal(1234, comments[1].LikeCount);
    }

    [Fact]
    public void ParseComments_KeepsOnlyTimestampsOfSameVideo()
    {
        // 三個 run：本影片含秒數、別支影片、本影片但沒有 startTimeSeconds → 只剩第一個
        var response = ParseJson(
            """
            {"onResponseReceivedEndpoints":[
                {"reloadContinuationItemsCommand":{"continuationItems":[
                    {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-a"}}}}
                ]}}
            ],
            "frameworkUpdates":{"entityBatchUpdate":{"mutations":[
                {"payload":{"commentEntityPayload":{
                    "key":"key-a",
                    "properties":{
                        "commentId":"comment-a",
                        "content":{
                            "content":"8:35 はここ 17:33 は別の動画 26:10 は秒數なし",
                            "commandRuns":[
                                {"startIndex":0,"length":4,"onTap":{"innertubeCommand":{"watchEndpoint":{"videoId":"vid1","startTimeSeconds":515}}}},
                                {"startIndex":11,"length":5,"onTap":{"innertubeCommand":{"watchEndpoint":{"videoId":"other9","startTimeSeconds":99}}}},
                                {"startIndex":22,"length":5,"onTap":{"innertubeCommand":{"watchEndpoint":{"videoId":"vid1"}}}}
                            ]
                        }
                    },
                    "toolbar":{"likeCountNotliked":"68"}
                }}}
            ]}}}
            """);

        var comments = YouTubeCommentClient.ParseComments(response, "vid1");

        var comment = Assert.Single(comments);
        var timestamp = Assert.Single(comment.Timestamps);
        Assert.Equal(new CommentTimestamp(0, 4, 515), timestamp);
    }

    [Fact]
    public void ParseComments_ReadsAppendContinuationItemsAction()
    {
        // 第一個 endpoint 是 reload（只有 commentsHeaderRenderer）、第二個是 append：兩者都讀，其他項目略過
        var response = ParseJson(
            """
            {"onResponseReceivedEndpoints":[
                {"reloadContinuationItemsCommand":{"continuationItems":[
                    {"commentsHeaderRenderer":{"commentCount":{"simpleText":"3"}}}
                ]}},
                {"appendContinuationItemsAction":{"continuationItems":[
                    {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-a"}}}},
                    {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"next-token"}}}},
                    {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-b"}}}}
                ]}}
            ],
            "frameworkUpdates":{"entityBatchUpdate":{"mutations":[
                {"payload":{"commentEntityPayload":{"key":"key-a","properties":{"commentId":"comment-a","content":{"content":"一則目"}},"toolbar":{}}}},
                {"payload":{"commentEntityPayload":{"key":"key-b","properties":{"commentId":"comment-b","content":{"content":"二則目"}},"toolbar":{"likeCountNotliked":"1.2K"}}}}
            ]}}}
            """);

        var comments = YouTubeCommentClient.ParseComments(response, "vid1");

        Assert.Equal(2, comments.Count);
        Assert.Equal("comment-a", comments[0].CommentId);
        Assert.Equal(0, comments[0].LikeCount); // toolbar 存在但欄位不存在 → 當 ""
        Assert.Equal("comment-b", comments[1].CommentId);
        Assert.Equal(1200, comments[1].LikeCount);
    }

    [Fact]
    public void ParseComments_BrokenOrMissingFields_DoesNotThrow()
    {
        // 空物件
        Assert.Empty(YouTubeCommentClient.ParseComments(ParseJson("{}"), "vid1"));

        // mutations 缺 payload：commentKey 對不到 → 略過
        var noPayload = ParseJson(
            """
            {"onResponseReceivedEndpoints":[
                {"reloadContinuationItemsCommand":{"continuationItems":[
                    {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-a"}}}}
                ]}}
            ],
            "frameworkUpdates":{"entityBatchUpdate":{"mutations":[{"entityKey":"key-a"}]}}}
            """);
        Assert.Empty(YouTubeCommentClient.ParseComments(noPayload, "vid1"));

        // commentKey 對不到 payload（payload 是別的 key）→ 略過
        var unmatchedKey = ParseJson(
            """
            {"onResponseReceivedEndpoints":[
                {"reloadContinuationItemsCommand":{"continuationItems":[
                    {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-a"}}}}
                ]}}
            ],
            "frameworkUpdates":{"entityBatchUpdate":{"mutations":[
                {"payload":{"commentEntityPayload":{"key":"key-z","properties":{"commentId":"comment-z","content":{"content":"對不到"}},"toolbar":{}}}}
            ]}}}
            """);
        Assert.Empty(YouTubeCommentClient.ParseComments(unmatchedKey, "vid1"));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("68", 68)]
    [InlineData("1,234", 1234)]
    [InlineData("1.2K", 1200)]
    [InlineData("3.4万", 34000)]
    [InlineData("5千", 5000)]
    [InlineData("abc", 0)]
    public void ParseLikeCount_HandlesYouTubeFormats(string? text, int expected)
    {
        Assert.Equal(expected, YouTubeCommentClient.ParseLikeCount(text));
    }

    // ===== FindNextPageContinuation =====

    [Fact]
    public void FindNextPageContinuation_ContinuationEndpointPath_ReturnsLastToken()
    {
        // 兩個 continuationItemRenderer（continuationEndpoint 路徑）：取最後一個的 token
        var response = ParseJson(
            """
            {"onResponseReceivedEndpoints":[
                {"reloadContinuationItemsCommand":{"continuationItems":[
                    {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-a"}}}},
                    {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"token-1"}}}},
                    {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"token-2"}}}}
                ]}}
            ]}
            """);

        Assert.Equal("token-2", YouTubeCommentClient.FindNextPageContinuation(response));
    }

    [Fact]
    public void FindNextPageContinuation_ButtonPath_ReturnsToken()
    {
        // button.buttonRenderer.command.continuationCommand.token 路徑（append 頁也適用）
        var response = ParseJson(CommentsPageAppendButtonNext("button-next-token"));

        Assert.Equal("button-next-token", YouTubeCommentClient.FindNextPageContinuation(response));
    }

    [Fact]
    public void FindNextPageContinuation_NoContinuationItem_ReturnsNull()
    {
        // 只有留言、沒有 continuationItemRenderer（最後一頁）
        Assert.Null(YouTubeCommentClient.FindNextPageContinuation(ParseJson(CommentsPageAppendNoNext)));

        // 形狀不符也不擲例外
        Assert.Null(YouTubeCommentClient.FindNextPageContinuation(ParseJson("{}")));
        Assert.Null(YouTubeCommentClient.FindNextPageContinuation(ParseJson("""{"onResponseReceivedEndpoints":[]}""")));
    }

    // ===== GetTopCommentsAsync（假的 HttpMessageHandler，不連網）=====

    private const string NextEndpoint = "https://www.youtube.com/youtubei/v1/next?prettyPrint=false";

    private static string FirstPageWithComments(string token = "cont-token-1") =>
        """
        {"contents":{"twoColumnWatchNextResults":{"results":{"results":{"contents":[
            {"videoPrimaryInfoRenderer":{}},
            {"itemSectionRenderer":{"sectionIdentifier":"comment-item-section","contents":[
                {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"__TOKEN__"}}}}
            ]}}
        ]}}}}}
        """.Replace("__TOKEN__", token, StringComparison.Ordinal);

    private const string CommentsPageTwo =
        """
        {"onResponseReceivedEndpoints":[
            {"reloadContinuationItemsCommand":{"continuationItems":[
                {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-a"}}}},
                {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-b"}}}}
            ]}}
        ],
        "frameworkUpdates":{"entityBatchUpdate":{"mutations":[
            {"payload":{"commentEntityPayload":{"key":"key-b","properties":{"commentId":"comment-b","content":{"content":"09位 17:33 Booo! ／ TOKOTOKO","commandRuns":[{"startIndex":5,"length":5,"onTap":{"innertubeCommand":{"watchEndpoint":{"videoId":"vid1","startTimeSeconds":1053}}}}]}},"toolbar":{"likeCountNotliked":"12"}}}},
            {"payload":{"commentEntityPayload":{"key":"key-a","properties":{"commentId":"comment-a","content":{"content":"10位 8:35 星座になれたら","commandRuns":[{"startIndex":4,"length":4,"onTap":{"innertubeCommand":{"watchEndpoint":{"videoId":"vid1","startTimeSeconds":515}}}}]}},"toolbar":{"likeCountNotliked":"68"}}}}
        ]}}}
        """;

    /// <summary>reload 版的留言頁（key-c1），結尾帶下一頁 token 的 continuationItemRenderer（continuationEndpoint 路徑）。</summary>
    private static string CommentsPageReloadWithNext(string nextToken) =>
        """
        {"onResponseReceivedEndpoints":[
            {"reloadContinuationItemsCommand":{"continuationItems":[
                {"commentsHeaderRenderer":{"commentCount":{"simpleText":"100"}}},
                {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-c1"}}}},
                {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"__TOKEN__"}}}}
            ]}}
        ],
        "frameworkUpdates":{"entityBatchUpdate":{"mutations":[
            {"payload":{"commentEntityPayload":{"key":"key-c1","properties":{"commentId":"comment-c1","content":{"content":"一頁目"}},"toolbar":{}}}}
        ]}}}
        """.Replace("__TOKEN__", nextToken, StringComparison.Ordinal);

    /// <summary>append 版的留言頁（key-c2），結尾沒有 continuationItemRenderer（沒有下一頁）。</summary>
    private const string CommentsPageAppendNoNext =
        """
        {"onResponseReceivedEndpoints":[
            {"appendContinuationItemsAction":{"continuationItems":[
                {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-c2"}}}}
            ]}}
        ],
        "frameworkUpdates":{"entityBatchUpdate":{"mutations":[
            {"payload":{"commentEntityPayload":{"key":"key-c2","properties":{"commentId":"comment-c2","content":{"content":"二頁目"}},"toolbar":{}}}}
        ]}}}
        """;

    /// <summary>append 版的留言頁（key-c3），結尾的 continuationItemRenderer 走 button.buttonRenderer 路徑。</summary>
    private static string CommentsPageAppendButtonNext(string nextToken) =>
        """
        {"onResponseReceivedEndpoints":[
            {"appendContinuationItemsAction":{"continuationItems":[
                {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"key-c3"}}}},
                {"continuationItemRenderer":{"button":{"buttonRenderer":{"command":{"continuationCommand":{"token":"__TOKEN__"}}}}}}
            ]}}
        ],
        "frameworkUpdates":{"entityBatchUpdate":{"mutations":[
            {"payload":{"commentEntityPayload":{"key":"key-c3","properties":{"commentId":"comment-c3","content":{"content":"三頁目"}},"toolbar":{}}}}
        ]}}}
        """.Replace("__TOKEN__", nextToken, StringComparison.Ordinal);

    [Fact]
    public async Task GetTopCommentsAsync_TwoRequests_ReturnsComments()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((FirstPageWithComments(), HttpStatusCode.OK));
        handler.Responses.Enqueue((CommentsPageTwo, HttpStatusCode.OK));

        var comments = await new YouTubeCommentClient(new HttpClient(handler), NullLogger<YouTubeCommentClient>.Instance)
            .GetTopCommentsAsync("vid1", TestContext.Current.CancellationToken);

        Assert.NotNull(comments);
        Assert.Equal(2, comments.Count);
        Assert.Equal("comment-a", comments[0].CommentId);
        Assert.Equal(515, comments[0].Timestamps[0].Seconds);
        Assert.Equal("comment-b", comments[1].CommentId);

        // 兩次都是 POST 到 next 端點、JSON body
        Assert.Equal(2, handler.Requests.Count);
        foreach (var (method, uri, contentType, _) in handler.Requests)
        {
            Assert.Equal("POST", method);
            Assert.Equal(NextEndpoint, uri);
            Assert.Equal("application/json", contentType);
        }

        // 第一次帶 videoId、第二次帶 continuation token
        Assert.Contains("\"videoId\":\"vid1\"", handler.Requests[0].Body);
        Assert.Contains("\"continuation\":\"cont-token-1\"", handler.Requests[1].Body);
    }

    [Fact]
    public async Task GetTopCommentsAsync_CommentsDisabled_ReturnsEmpty()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((
            """{"contents":{"twoColumnWatchNextResults":{"results":{"results":{"contents":[{"itemSectionRenderer":{"sectionIdentifier":"related-items"}}]}}}}}""",
            HttpStatusCode.OK));

        var comments = await new YouTubeCommentClient(new HttpClient(handler), NullLogger<YouTubeCommentClient>.Instance)
            .GetTopCommentsAsync("vid1", TestContext.Current.CancellationToken);

        Assert.NotNull(comments);
        Assert.Empty(comments);
        Assert.Single(handler.Requests); // 沒有留言區就不打第二次
    }

    [Fact]
    public async Task GetTopCommentsAsync_HttpError_ReturnsNull()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((string.Empty, HttpStatusCode.InternalServerError));

        var comments = await new YouTubeCommentClient(new HttpClient(handler), NullLogger<YouTubeCommentClient>.Instance)
            .GetTopCommentsAsync("vid1", TestContext.Current.CancellationToken);

        Assert.Null(comments);
    }

    // ===== GetCommentPagesAsync =====

    /// <summary>把頁列舉收成清單（測試用）。</summary>
    private static async Task<List<IReadOnlyList<YouTubeComment>>> CollectPagesAsync(
        YouTubeCommentClient client, string videoId, int maxPages, CancellationToken cancellationToken)
    {
        var pages = new List<IReadOnlyList<YouTubeComment>>();
        await foreach (var page in client.GetCommentPagesAsync(videoId, maxPages, cancellationToken))
        {
            pages.Add(page);
        }

        return pages;
    }

    private static YouTubeCommentClient CreateClient(StubHandler handler) =>
        new(new HttpClient(handler), NullLogger<YouTubeCommentClient>.Instance);

    [Fact]
    public async Task GetCommentPagesAsync_FollowsContinuationAcrossPages()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((FirstPageWithComments("cont-token-1"), HttpStatusCode.OK));
        handler.Responses.Enqueue((CommentsPageReloadWithNext("cont-token-2"), HttpStatusCode.OK));
        handler.Responses.Enqueue((CommentsPageAppendNoNext, HttpStatusCode.OK));

        var pages = await CollectPagesAsync(CreateClient(handler), "vid1", maxPages: 3, TestContext.Current.CancellationToken);

        // 兩頁：第一頁 reload、第二頁 append（沒有下一頁 token 就停）
        Assert.Equal(2, pages.Count);
        Assert.Equal("comment-c1", Assert.Single(pages[0]).CommentId);
        Assert.Equal("comment-c2", Assert.Single(pages[1]).CommentId);

        // 三次請求：videoId → 第一頁 token → 第二頁 token
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("\"videoId\":\"vid1\"", handler.Requests[0].Body);
        Assert.Contains("\"continuation\":\"cont-token-1\"", handler.Requests[1].Body);
        Assert.Contains("\"continuation\":\"cont-token-2\"", handler.Requests[2].Body);
    }

    [Fact]
    public async Task GetCommentPagesAsync_StopsAtMaxPages()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((FirstPageWithComments("cont-token-1"), HttpStatusCode.OK));
        handler.Responses.Enqueue((CommentsPageReloadWithNext("cont-token-2"), HttpStatusCode.OK));
        handler.Responses.Enqueue((CommentsPageAppendButtonNext("cont-token-3"), HttpStatusCode.OK));
        handler.Responses.Enqueue((CommentsPageAppendNoNext, HttpStatusCode.OK)); // 第 3 頁不應被請求

        var pages = await CollectPagesAsync(CreateClient(handler), "vid1", maxPages: 2, TestContext.Current.CancellationToken);

        // 只到 maxPages（2 頁）就停：第三次請求帶第二頁 token，之後不再打
        Assert.Equal(2, pages.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("\"continuation\":\"cont-token-2\"", handler.Requests[2].Body);
    }

    [Fact]
    public async Task GetCommentPagesAsync_SecondPageFails_ReturnsFirstPage()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((FirstPageWithComments("cont-token-1"), HttpStatusCode.OK));
        handler.Responses.Enqueue((CommentsPageReloadWithNext("cont-token-2"), HttpStatusCode.OK));
        handler.Responses.Enqueue((string.Empty, HttpStatusCode.InternalServerError));

        var pages = await CollectPagesAsync(CreateClient(handler), "vid1", maxPages: 3, TestContext.Current.CancellationToken);

        // 第二頁失敗：只回第一頁，不擲例外
        var page = Assert.Single(pages);
        Assert.Equal("comment-c1", Assert.Single(page).CommentId);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("\"continuation\":\"cont-token-2\"", handler.Requests[2].Body);
    }

    [Fact]
    public async Task GetCommentPagesAsync_ButtonPathToken_FollowsToLastPage()
    {
        var handler = new StubHandler();
        handler.Responses.Enqueue((FirstPageWithComments("cont-token-1"), HttpStatusCode.OK));
        handler.Responses.Enqueue((CommentsPageAppendButtonNext("cont-token-2"), HttpStatusCode.OK));
        handler.Responses.Enqueue((CommentsPageAppendNoNext, HttpStatusCode.OK));

        var pages = await CollectPagesAsync(CreateClient(handler), "vid1", maxPages: 3, TestContext.Current.CancellationToken);

        // button 路徑的 token 也能往下翻：第一頁（button token 版）→ 第二頁（沒有下一頁）
        Assert.Equal(2, pages.Count);
        Assert.Equal("comment-c3", Assert.Single(pages[0]).CommentId);
        Assert.Equal("comment-c2", Assert.Single(pages[1]).CommentId);
    }

    private static JsonElement ParseJson(string json)
    {
        var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>依序回傳排入回應、記下每個請求的 method／URL／Content-Type／body 的假 handler。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Queue<(string Body, HttpStatusCode Status)> Responses { get; } = new();

        public List<(string Method, string Uri, string? ContentType, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri?.ToString() ?? string.Empty,
                request.Content?.Headers.ContentType?.MediaType, body));
            var (responseBody, status) = Responses.Dequeue();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
