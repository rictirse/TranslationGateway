using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>查詢 Wikidata／Wikipedia／Fandom（都是 MediaWiki API，不用 API Key）。
/// 每個請求逾時 8 秒；HTTP 錯誤、逾時或 JSON 解析失敗時回空結果並記 Warning，不擲例外。</summary>
public sealed class KnowledgeSourcesClient(HttpClient httpClient, ILogger<KnowledgeSourcesClient> logger) : IKnowledgeSources
{
    private const string UserAgent = "TranslationGateway/1.0 (personal subtitle glossary tool)";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private const int MaxExtractLength = 1500;
    private const string WikidataApi = "https://www.wikidata.org/w/api.php";
    private const string EntityLanguages = "ja|en|zh|zh-tw|zh-hant|zh-hk";
    private const string SiteFilter = "jawiki|zhwiki|enwiki";

    private static readonly Regex FandomSubdomainPattern = new("^[a-z0-9-]{1,60}$", RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    public async Task<IReadOnlyList<WikidataHit>> SearchWikidataAsync(string text, string language = "ja", int limit = 3, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = (text ?? string.Empty).Trim();
        if (query.Length == 0 || !IsValidLanguage(language))
        {
            return [];
        }

        var url = $"{WikidataApi}?action=wbsearchentities&search={Uri.EscapeDataString(query)}&language={language}&uselang={language}&type=item&limit={limit}&format=json";
        var json = await GetJsonAsync(url, $"Wikidata 搜尋「{query}」", ct).ConfigureAwait(false);
        if (json is null)
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var hits = new List<WikidataHit>();
            if (doc.RootElement.TryGetProperty("search", out var search) && search.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in search.EnumerateArray())
                {
                    var id = GetString(item, "id");
                    if (id is null)
                    {
                        continue;
                    }
                    var label = GetString(item, "label") ?? DisplayName(item);
                    hits.Add(new WikidataHit(id, label ?? string.Empty, GetString(item, "description")));
                }
            }
            logger.LogInformation("Wikidata 搜尋「{Query}」：{Count} 筆結果（{ElapsedMs} ms）", query, hits.Count, stopwatch.ElapsedMilliseconds);
            return hits;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Wikidata 搜尋「{Query}」的回應解析失敗", query);
            return [];
        }
    }

    public async Task<WikidataEntity?> GetWikidataEntityAsync(string id, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var entityId = (id ?? string.Empty).Trim();
        if (entityId.Length == 0)
        {
            return null;
        }

        var url = $"{WikidataApi}?action=wbgetentities&ids={Uri.EscapeDataString(entityId)}&props=labels|descriptions|aliases|sitelinks&languages={EntityLanguages}&sitefilter={SiteFilter}&format=json";
        var json = await GetJsonAsync(url, $"Wikidata 實體 {entityId}", ct).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entities", out var entities) ||
                !entities.TryGetProperty(entityId, out var entity) ||
                entity.TryGetProperty("missing", out _))
            {
                logger.LogInformation("Wikidata 實體 {Id}：找不到（{ElapsedMs} ms）", entityId, stopwatch.ElapsedMilliseconds);
                return null;
            }

            var labels = ParseLanguageValues(entity, "labels");
            var descriptions = ParseLanguageValues(entity, "descriptions");
            var aliases = new Dictionary<string, string>();
            if (entity.TryGetProperty("aliases", out var aliasGroups) && aliasGroups.ValueKind == JsonValueKind.Object)
            {
                foreach (var group in aliasGroups.EnumerateObject())
                {
                    if (group.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    var values = group.Value.EnumerateArray()
                        .Select(a => GetString(a, "value"))
                        .Where(v => v is not null)
                        .ToList();
                    if (values.Count > 0)
                    {
                        aliases[group.Name] = string.Join("\n", values!);
                    }
                }
            }

            string? SiteTitle(string site) =>
                entity.TryGetProperty("sitelinks", out var sitelinks) &&
                sitelinks.TryGetProperty(site, out var link) ? GetString(link, "title") : null;

            var result = new WikidataEntity(entityId, labels, descriptions, aliases,
                SiteTitle("jawiki"), SiteTitle("zhwiki"), SiteTitle("enwiki"));
            logger.LogInformation("Wikidata 實體 {Id}（{Label}）：{ElapsedMs} ms",
                entityId, labels.GetValueOrDefault("ja") ?? entityId, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Wikidata 實體 {Id} 的回應解析失敗", entityId);
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> SearchWikipediaAsync(string lang, string text, int limit = 3, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = (text ?? string.Empty).Trim();
        if (query.Length == 0 || !IsValidLanguage(lang))
        {
            return [];
        }

        var json = await GetJsonAsync(SearchUrl(lang, query, limit), $"Wikipedia（{lang}）搜尋「{query}」", ct).ConfigureAwait(false);
        if (json is null)
        {
            return [];
        }

        var titles = ParseSearchTitles(json);
        logger.LogInformation("Wikipedia（{Lang}）搜尋「{Query}」：{Count} 筆結果（{ElapsedMs} ms）", lang, query, titles.Count, stopwatch.ElapsedMilliseconds);
        return titles;
    }

    public async Task<WikiPage?> GetWikipediaPageAsync(string lang, string title, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var pageTitle = (title ?? string.Empty).Trim();
        if (pageTitle.Length == 0 || !IsValidLanguage(lang))
        {
            return null;
        }

        var url = $"https://{lang}.wikipedia.org/w/api.php?action=query&titles={Uri.EscapeDataString(pageTitle)}&redirects=1&prop=extracts|langlinks|pageprops|info&exintro=1&explaintext=1&lllang=zh&ppprop=wikibase_item&inprop=url&format=json";
        var json = await GetJsonAsync(url, $"Wikipedia（{lang}）頁面「{pageTitle}」", ct).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("pages", out var pages) ||
                pages.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var page in pages.EnumerateObject())
            {
                if (page.Value.TryGetProperty("missing", out _))
                {
                    continue;
                }

                // 標題用轉向後的標題（pages 內已是轉向目標）
                var resolvedTitle = GetString(page.Value, "title") ?? pageTitle;
                var pageUrl = GetString(page.Value, "fullurl") ?? $"https://{lang}.wikipedia.org/wiki/{EncodeWikiTitle(resolvedTitle)}";
                var zhTitle = GetStringListValue(page.Value, "langlinks", "zh");
                var wikidataId = page.Value.TryGetProperty("pageprops", out var pageProps) ? GetString(pageProps, "wikibase_item") : null;
                var result = new WikiPage($"{lang}.wikipedia.org", resolvedTitle, pageUrl, Truncate(GetString(page.Value, "extract")), zhTitle, wikidataId);
                logger.LogInformation("Wikipedia（{Lang}）頁面「{Title}」：{ElapsedMs} ms", lang, resolvedTitle, stopwatch.ElapsedMilliseconds);
                return result;
            }

            logger.LogInformation("Wikipedia（{Lang}）頁面「{Title}」：不存在（{ElapsedMs} ms）", lang, pageTitle, stopwatch.ElapsedMilliseconds);
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Wikipedia（{Lang}）頁面「{Title}」的回應解析失敗", lang, pageTitle);
            return null;
        }
    }

    public async Task<bool> FandomExistsAsync(string subdomain, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!IsValidSubdomain(subdomain))
        {
            return false;
        }

        var json = await GetJsonAsync(FandomApiUrl(subdomain, "action=query&meta=siteinfo"), $"Fandom「{subdomain}」站台資訊", ct).ConfigureAwait(false);
        if (json is null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var exists = doc.RootElement.TryGetProperty("query", out var query) &&
                query.TryGetProperty("general", out var general) &&
                general.TryGetProperty("sitename", out var sitename) &&
                sitename.ValueKind == JsonValueKind.String;
            logger.LogInformation("Fandom「{Sub}」站台檢查：{Result}（{ElapsedMs} ms）", subdomain, exists ? "存在" : "不存在", stopwatch.ElapsedMilliseconds);
            return exists;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Fandom「{Sub}」站台資訊解析失敗", subdomain);
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> SearchFandomAsync(string subdomain, string text, int limit = 3, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = (text ?? string.Empty).Trim();
        if (query.Length == 0 || !IsValidSubdomain(subdomain))
        {
            return [];
        }

        var json = await GetJsonAsync(FandomApiUrl(subdomain, $"action=query&list=search&srsearch={Uri.EscapeDataString(query)}&srlimit={limit}"), $"Fandom「{subdomain}」搜尋「{query}」", ct).ConfigureAwait(false);
        if (json is null)
        {
            return [];
        }

        var titles = ParseSearchTitles(json);
        logger.LogInformation("Fandom「{Sub}」搜尋「{Query}」：{Count} 筆結果（{ElapsedMs} ms）", subdomain, query, titles.Count, stopwatch.ElapsedMilliseconds);
        return titles;
    }

    public async Task<WikiPage?> GetFandomPageAsync(string subdomain, string title, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var pageTitle = (title ?? string.Empty).Trim();
        if (pageTitle.Length == 0 || !IsValidSubdomain(subdomain))
        {
            return null;
        }

        var url = FandomApiUrl(subdomain, $"action=parse&page={Uri.EscapeDataString(pageTitle)}&prop=text&section=0&redirects=1");
        var json = await GetJsonAsync(url, $"Fandom「{subdomain}」頁面「{pageTitle}」", ct).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("parse", out var parse))
            {
                return null;
            }

            // 轉向後的標題（parse.title），沒有時退回輸入的標題
            var resolvedTitle = GetString(parse, "title") ?? pageTitle;
            var html = parse.TryGetProperty("text", out var textElement) ? GetString(textElement, "*") : null;
            var result = new WikiPage(
                $"{subdomain}.fandom.com",
                resolvedTitle,
                $"https://{subdomain}.fandom.com/wiki/{EncodeWikiTitle(resolvedTitle)}",
                Truncate(HtmlToText(html ?? string.Empty)),
                ZhTitle: null,
                WikidataId: null);
            logger.LogInformation("Fandom「{Sub}」頁面「{Title}」：{ElapsedMs} ms", subdomain, resolvedTitle, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Fandom「{Sub}」頁面「{Title}」的回應解析失敗", subdomain, pageTitle);
            return null;
        }
    }

    /// <summary>共用的 GET：帶 User-Agent（Wikimedia 規定）、8 秒逾時；失敗回 null 並記 Warning。</summary>
    private async Task<string?> GetJsonAsync(string url, string description, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!request.Headers.UserAgent.TryParseAdd(UserAgent))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            }
            using var response = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Description}：HTTP {StatusCode}（逾時上限 {TimeoutSeconds} 秒）", description, (int)response.StatusCode, Timeout.TotalSeconds);
                return null;
            }
            return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 使用者主動取消（外部 token）時例外照常往外拋；其餘視為逾時
            logger.LogWarning("{Description}：逾時（{TimeoutSeconds} 秒）", description, Timeout.TotalSeconds);
            return null;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "{Description}：連線失敗", description);
            return null;
        }
    }

    /// <summary>MediaWiki 的 list=search 回應解析出標題清單；失敗時記 Warning 並回空清單。</summary>
    private List<string> ParseSearchTitles(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var titles = new List<string>();
            if (doc.RootElement.TryGetProperty("query", out var query) &&
                query.TryGetProperty("search", out var search) &&
                search.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in search.EnumerateArray())
                {
                    var title = GetString(item, "title");
                    if (title is not null)
                    {
                        titles.Add(title);
                    }
                }
            }
            return titles;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "MediaWiki 搜尋回應解析失敗");
            return [];
        }
    }

    /// <summary>labels／descriptions：語言 → {value} 的字串值。</summary>
    private static Dictionary<string, string> ParseLanguageValues(JsonElement entity, string property)
    {
        var values = new Dictionary<string, string>();
        if (entity.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var lang in element.EnumerateObject())
            {
                var value = GetString(lang.Value, "value");
                if (value is not null)
                {
                    values[lang.Name] = value;
                }
            }
        }
        return values;
    }

    /// <summary>langlinks 中指定語言的標題（值在 "*" 屬性）。</summary>
    private static string? GetStringListValue(JsonElement parent, string property, string lang)
    {
        if (!parent.TryGetProperty(property, out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var link in links.EnumerateArray())
        {
            if (GetString(link, "lang") == lang)
            {
                return GetString(link, "*");
            }
        }
        return null;
    }

    /// <summary>wbsearchentities 的 display.label.value（頂層 label 缺漏時的備援）。</summary>
    private static string? DisplayName(JsonElement item) =>
        item.TryGetProperty("display", out var display) &&
        display.TryGetProperty("label", out var label) ? GetString(label, "value") : null;

    private static string? GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    /// <summary>導言最長 1500 字。</summary>
    private static string? Truncate(string? text) =>
        text is null || text.Length <= MaxExtractLength ? text : text[..MaxExtractLength];

    /// <summary>維基標題轉網址：空白換底線再 URL 編碼。</summary>
    private static string EncodeWikiTitle(string title) =>
        Uri.EscapeDataString(title.Replace(' ', '_'));

    private static string FandomApiUrl(string subdomain, string parameters) =>
        $"https://{subdomain}.fandom.com/api.php?{parameters}&format=json";

    private static string SearchUrl(string lang, string query, int limit) =>
        $"https://{lang}.wikipedia.org/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&srlimit={limit}&format=json";

    private static bool IsValidLanguage(string lang) =>
        lang is "ja" or "zh" or "en";

    private static bool IsValidSubdomain(string subdomain) =>
        FandomSubdomainPattern.IsMatch(subdomain ?? string.Empty);

    /// <summary>去掉 HTML 標籤、還原 HTML 實體並壓縮多餘空白，成為單行純文字。</summary>
    internal static string HtmlToText(string html)
    {
        var text = HtmlTagPattern.Replace(html, string.Empty);
        text = WebUtility.HtmlDecode(text);
        return WhitespacePattern.Replace(text, " ").Trim();
    }
}
