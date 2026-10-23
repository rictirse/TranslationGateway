using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class KnowledgeSourcesClientTests
{
    private const string WikidataSearchJson = """
        {
          "search": [
            {
              "id": "Q17001754",
              "label": "ホロライブプロダクション",
              "description": "日本の企業",
              "display": { "label": { "value": "ホロライブプロダクション", "language": "ja" } }
            },
            {
              "id": "Q96027237",
              "description": "バーチャルYouTuber",
              "display": { "label": { "value": "hololive（中國版）", "language": "ja" } }
            }
          ],
          "success": 1
        }
        """;

    private const string WikidataEntityJson = """
        {
          "entities": {
            "Q17001754": {
              "type": "item",
              "id": "Q17001754",
              "labels": {
                "ja": { "language": "ja", "value": "ホロライブプロダクション" },
                "en": { "language": "en", "value": "Hololive Production" },
                "zh": { "language": "zh", "value": "hololive Production" }
              },
              "descriptions": {
                "ja": { "language": "ja", "value": "日本の企業" },
                "en": { "language": "en", "value": "Japanese entertainment company" }
              },
              "aliases": {
                "ja": [
                  { "language": "ja", "value": "ホロライブ" },
                  { "language": "ja", "value": "カバー株式会社" }
                ],
                "en": [ { "language": "en", "value": "Cover Corp" } ]
              },
              "sitelinks": {
                "jawiki": { "site": "jawiki", "title": "ホロライブプロダクション" },
                "zhwiki": { "site": "zhwiki", "title": "Hololive Production" },
                "enwiki": { "site": "enwiki", "title": "Hololive Production" },
                "frwiki": { "site": "frwiki", "title": "不該出現的語言" }
              }
            }
          },
          "success": 1
        }
        """;

    private const string WikipediaSearchJson = """
        {
          "query": {
            "search": [
              { "ns": 0, "title": "ホロライブプロダクション", "pageid": 1 },
              { "ns": 0, "title": "にじさんじ", "pageid": 2 }
            ]
          }
        }
        """;

    private const string WikipediaRedirectJson = """
        {
          "query": {
            "redirects": [ { "from": "ホロライブ", "to": "ホロライブプロダクション" } ],
            "pages": {
              "4746292": {
                "pageid": 4746292,
                "ns": 0,
                "title": "ホロライブプロダクション",
                "extract": "ホロライブプロダクションは、日本の企業。",
                "fullurl": "https://ja.wikipedia.org/wiki/ホロライブプロダクション",
                "langlinks": [ { "lang": "zh", "title": "Hololive", "*": "Hololive Production" } ],
                "pageprops": { "wikibase_item": "Q17001754" }
              }
            }
          }
        }
        """;

    private const string WikipediaMissingJson = """
        {
          "query": {
            "pages": {
              "-1": { "ns": 0, "title": "完全不存在的頁面", "missing": "" }
            }
          }
        }
        """;

    private const string FandomSiteinfoJson = """
        { "query": { "general": { "sitename": "Virtual YouTuber Wiki", "wikiid": "vyt" } } }
        """;

    private const string FandomSearchJson = """
        { "query": { "search": [ { "ns": 0, "title": "Hakos Baelz", "pageid": 100 } ] } }
        """;

    private const string FandomParseJson = """
        {
          "parse": {
            "title": "Hakos Baelz",
            "pageid": 100,
            "text": { "*": "<p><b>Hakos Baelz</b>（はこす・べーるず）は、\nオーストラリア出身のバーチャルYouTuber。</p>\n<p>ホロライブプロダクション所属。</p>" }
          }
        }
        """;

    private static (KnowledgeSourcesClient Sut, StubHandler Handler) CreateSut(string response,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new StubHandler(response, statusCode);
        return (new KnowledgeSourcesClient(new HttpClient(handler), NullLogger<KnowledgeSourcesClient>.Instance), handler);
    }

    [Fact]
    public async Task SearchWikidataAsync_ParsesHits_AndSendsUserAgent()
    {
        var (sut, handler) = CreateSut(WikidataSearchJson);

        var hits = await sut.SearchWikidataAsync("ホロライブ", ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, hits.Count);
        Assert.Equal(new WikidataHit("Q17001754", "ホロライブプロダクション", "日本の企業"), hits[0]);
        // 頂層 label 缺漏時用 display.label.value
        Assert.Equal(new WikidataHit("Q96027237", "hololive（中國版）", "バーチャルYouTuber"), hits[1]);
        AssertHasUserAgent(handler);
        Assert.Contains("action=wbsearchentities", handler.LastRequestUri);
        Assert.Contains($"search={Uri.EscapeDataString("ホロライブ")}", handler.LastRequestUri);
        Assert.Contains("format=json", handler.LastRequestUri);
    }

    [Fact]
    public async Task GetWikidataEntityAsync_ParsesLabelsAliasesAndSitelinks()
    {
        var (sut, handler) = CreateSut(WikidataEntityJson);

        var entity = await sut.GetWikidataEntityAsync("Q17001754", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(entity);
        Assert.Equal("ホロライブプロダクション", entity.Labels["ja"]);
        Assert.Equal("Hololive Production", entity.Labels["en"]);
        Assert.Equal("日本の企業", entity.Descriptions["ja"]);
        // 別名以換行串起
        Assert.Equal("ホロライブ\nカバー株式会社", entity.Aliases["ja"]);
        Assert.Equal("Cover Corp", entity.Aliases["en"]);
        Assert.Equal("ホロライブプロダクション", entity.JaWikiTitle);
        Assert.Equal("Hololive Production", entity.ZhWikiTitle);
        Assert.Equal("Hololive Production", entity.EnWikiTitle);
        Assert.Equal("https://www.wikidata.org/wiki/Q17001754", entity.Url);
        AssertHasUserAgent(handler);
        Assert.Contains("action=wbgetentities", handler.LastRequestUri);
    }

    [Fact]
    public async Task SearchWikipediaAsync_ReturnsTitles()
    {
        var (sut, handler) = CreateSut(WikipediaSearchJson);

        var titles = await sut.SearchWikipediaAsync("ja", "ホロライブ", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["ホロライブプロダクション", "にじさんじ"], titles);
        AssertHasUserAgent(handler);
        Assert.Contains("action=query", handler.LastRequestUri);
        Assert.Contains("list=search", handler.LastRequestUri);
    }

    [Fact]
    public async Task GetWikipediaPageAsync_FollowsRedirect_AndPicksLanglinkAndWikidataId()
    {
        var (sut, _) = CreateSut(WikipediaRedirectJson);

        var page = await sut.GetWikipediaPageAsync("ja", "ホロライブ", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        // 標題取轉向後的標題
        Assert.Equal("ホロライブプロダクション", page.Title);
        Assert.Equal("ja.wikipedia.org", page.Site);
        Assert.Equal("https://ja.wikipedia.org/wiki/ホロライブプロダクション", page.Url);
        Assert.Equal("ホロライブプロダクションは、日本の企業。", page.Extract);
        Assert.Equal("Hololive Production", page.ZhTitle);
        Assert.Equal("Q17001754", page.WikidataId);
    }

    [Fact]
    public async Task GetWikipediaPageAsync_MissingPage_ReturnsNull()
    {
        var (sut, _) = CreateSut(WikipediaMissingJson);

        var page = await sut.GetWikipediaPageAsync("ja", "完全不存在的頁面", ct: TestContext.Current.CancellationToken);

        Assert.Null(page);
    }

    [Fact]
    public async Task GetWikipediaPageAsync_TruncatesExtractTo1500Chars()
    {
        var longExtract = new string('あ', 1600);
        var json = $$"""
            {
              "query": {
                "pages": {
                  "1": { "pageid": 1, "ns": 0, "title": "長文", "extract": "{{longExtract}}", "fullurl": "https://ja.wikipedia.org/wiki/%E9%95%B7%E6%96%87" }
                }
              }
            }
            """;
        var (sut, _) = CreateSut(json);

        var page = await sut.GetWikipediaPageAsync("ja", "長文", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(1500, page.Extract!.Length);
    }

    [Fact]
    public async Task FandomExistsAsync_SiteinfoWithSitename_ReturnsTrue()
    {
        var (sut, handler) = CreateSut(FandomSiteinfoJson);

        var exists = await sut.FandomExistsAsync("virtualyoutuber", ct: TestContext.Current.CancellationToken);

        Assert.True(exists);
        AssertHasUserAgent(handler);
        Assert.Equal("https://virtualyoutuber.fandom.com/api.php?action=query&meta=siteinfo&format=json", handler.LastRequestUri);
    }

    [Fact]
    public async Task FandomExistsAsync_NoSitename_ReturnsFalse()
    {
        var (sut, _) = CreateSut("""{ "error": { "code": "unknown_action" } }""");

        var exists = await sut.FandomExistsAsync("virtualyoutuber", ct: TestContext.Current.CancellationToken);

        Assert.False(exists);
    }

    [Fact]
    public async Task SearchFandomAsync_ReturnsTitles()
    {
        var (sut, handler) = CreateSut(FandomSearchJson);

        var titles = await sut.SearchFandomAsync("virtualyoutuber", "baelz", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["Hakos Baelz"], titles);
        AssertHasUserAgent(handler);
        Assert.Equal("https://virtualyoutuber.fandom.com/api.php?action=query&list=search&srsearch=baelz&srlimit=3&format=json", handler.LastRequestUri);
    }

    [Fact]
    public async Task GetFandomPageAsync_ConvertsLeadHtmlToPlainText()
    {
        var (sut, handler) = CreateSut(FandomParseJson);

        var page = await sut.GetFandomPageAsync("virtualyoutuber", "Hakos Baelz", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal("virtualyoutuber.fandom.com", page.Site);
        Assert.Equal("Hakos Baelz", page.Title);
        Assert.Null(page.ZhTitle);
        Assert.Null(page.WikidataId);
        // HTML 標籤去除、來源換行壓成單一空白
        Assert.Equal("Hakos Baelz（はこす・べーるず）は、 オーストラリア出身のバーチャルYouTuber。 ホロライブプロダクション所属。", page.Extract);
        // Url：空白換底線再做 URL 編碼（底線屬保留字，不會再被編碼）
        Assert.Equal("https://virtualyoutuber.fandom.com/wiki/Hakos_Baelz", page.Url);
        AssertHasUserAgent(handler);
    }

    [Fact]
    public async Task GetFandomPageAsync_TruncatesExtractTo1500Chars()
    {
        var html = "<p>" + new string('あ', 1600) + "</p>";
        var json = $$"""{ "parse": { "title": "長文", "text": { "*": "{{html}}" } } }""";
        var (sut, _) = CreateSut(json);

        var page = await sut.GetFandomPageAsync("virtualyoutuber", "長文", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(1500, page.Extract!.Length);
    }

    [Fact]
    public async Task HttpError_ReturnsEmptyOrNull_WithoutThrowing()
    {
        var (wikiSearch, wikiHandler) = CreateSut("server error", HttpStatusCode.InternalServerError);
        Assert.Empty(await wikiSearch.SearchWikipediaAsync("ja", "ホロライブ", ct: TestContext.Current.CancellationToken));
        Assert.Single(wikiHandler.Requests);

        var (fandomExists, _) = CreateSut("server error", HttpStatusCode.InternalServerError);
        Assert.False(await fandomExists.FandomExistsAsync("virtualyoutuber", ct: TestContext.Current.CancellationToken));

        var (page, _) = CreateSut("server error", HttpStatusCode.InternalServerError);
        Assert.Null(await page.GetWikipediaPageAsync("ja", "ホロライブ", ct: TestContext.Current.CancellationToken));

        var (wikidata, _) = CreateSut("server error", HttpStatusCode.InternalServerError);
        Assert.Empty(await wikidata.SearchWikidataAsync("ホロライブ", ct: TestContext.Current.CancellationToken));

        var (entity, _) = CreateSut("server error", HttpStatusCode.InternalServerError);
        Assert.Null(await entity.GetWikidataEntityAsync("Q17001754", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidJson_ReturnsEmptyOrNull_WithoutThrowing()
    {
        var (sut, _) = CreateSut("not json at all");

        Assert.Empty(await sut.SearchWikipediaAsync("ja", "ホロライブ", ct: TestContext.Current.CancellationToken));
        Assert.Null(await sut.GetWikipediaPageAsync("ja", "ホロライブ", ct: TestContext.Current.CancellationToken));
        Assert.False(await sut.FandomExistsAsync("virtualyoutuber", ct: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Bad_Sub")]    // 大寫與底線
    [InlineData("sub.domain")] // 點
    [InlineData("sub!")]       // 符號
    [InlineData("")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")] // 超過 60 字
    public async Task InvalidSubdomain_ReturnsEmpty_AndSendsNoRequest(string subdomain)
    {
        var (sut, handler) = CreateSut(FandomSiteinfoJson);

        Assert.False(await sut.FandomExistsAsync(subdomain, ct: TestContext.Current.CancellationToken));
        Assert.Empty(await sut.SearchFandomAsync(subdomain, "text", ct: TestContext.Current.CancellationToken));
        Assert.Null(await sut.GetFandomPageAsync(subdomain, "title", ct: TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ValidShortSubdomain_SendsRequest()
    {
        var (sut, handler) = CreateSut(FandomSiteinfoJson);

        Assert.True(await sut.FandomExistsAsync("x", ct: TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task InvalidSubdomain_Uppercase_SendsNoRequest()
    {
        var (sut, handler) = CreateSut(FandomSiteinfoJson);

        Assert.False(await sut.FandomExistsAsync("VirtualYoutuber", ct: TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InvalidLanguage_ReturnsEmpty_AndSendsNoRequest()
    {
        var (sut, handler) = CreateSut(WikipediaSearchJson);

        Assert.Empty(await sut.SearchWikipediaAsync("fr", "ホロライブ", ct: TestContext.Current.CancellationToken));
        Assert.Null(await sut.GetWikipediaPageAsync("fr", "ホロライブ", ct: TestContext.Current.CancellationToken));
        Assert.Empty(await sut.SearchWikidataAsync("ホロライブ", language: "fr", ct: TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    private static void AssertHasUserAgent(StubHandler handler) =>
        Assert.Equal("TranslationGateway/1.0 (personal subtitle glossary tool)",
            handler.LastRequest?.Headers.UserAgent.ToString());

    /// <summary>攔截請求並回傳固定回應的假 handler。</summary>
    private sealed class StubHandler(string response, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public HttpRequestMessage? LastRequest => Requests.Count > 0 ? Requests[^1] : null;

        // AbsoluteUri 保留百分號編碼（ToString 會把 %XX 解回 Unicode）
        public string LastRequestUri => LastRequest?.RequestUri?.AbsoluteUri ?? "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
