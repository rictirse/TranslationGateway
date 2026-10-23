using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>抓 YouTube 影片頁面的 HTML，解析裡面的 ytInitialPlayerResponse（不需要 API Key）；查不到時回 null，不擲例外。</summary>
public sealed class YouTubeWatchPageClient(HttpClient httpClient, ILogger<YouTubeWatchPageClient> logger) : IYouTubePageInfoProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public async Task<YouTubePageInfo?> GetPageInfoAsync(string url, CancellationToken cancellationToken = default) =>
        (await CheckPageAsync(url, cancellationToken)).Info;

    /// <summary>讀影片頁並辨認是否被 YouTube 擋下（匿名存取次數太多）：HTTP 429、被轉到 Google 的 /sorry
    /// 機器人驗證頁，或 playabilityStatus 是 LOGIN_REQUIRED 而且原因提到機器人確認。每次判斷的依據都寫進 log。</summary>
    public async Task<YouTubePageCheck> CheckPageAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", "ja,zh-TW;q=0.8");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);

            using var response = await httpClient.SendAsync(request, cts.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning("YouTube 影片頁面回傳 429（{Url}）：判定為被擋（匿名存取次數太多）", url);
                return new YouTubePageCheck(null, "YouTube 回傳 429（請求太多）");
            }

            // 被擋時會被轉到 Google 的機器人驗證頁（/sorry）：看回應最後的網址
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null &&
                string.Equals(finalUri.Host, "www.google.com", StringComparison.OrdinalIgnoreCase) &&
                finalUri.AbsolutePath.StartsWith("/sorry", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("YouTube 影片頁面被轉到 Google 機器人驗證頁（最後網址 {FinalUrl}，原網址 {Url}）：判定為被擋",
                    finalUri, url);
                return new YouTubePageCheck(null, "YouTube 要求機器人驗證");
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("YouTube 影片頁面回傳 {StatusCode}，讀不到 {Url} 的資訊", (int)response.StatusCode, url);
                return new YouTubePageCheck(null, null);
            }

            var html = await response.Content.ReadAsStringAsync(cts.Token);
            var (playabilityStatus, playabilityReason) = ReadPlayabilityStatus(html);
            if (playabilityStatus == "LOGIN_REQUIRED" && ContainsBotConfirmationText(playabilityReason))
            {
                logger.LogWarning(
                    "YouTube 影片頁面 playabilityStatus={Status}，reason={Reason}（{Url}）：判定為被擋（要求登入確認不是機器人）",
                    playabilityStatus, playabilityReason, url);
                return new YouTubePageCheck(null, $"YouTube 要求登入確認不是機器人：{playabilityReason}");
            }

            // 不是 OK 但也不算被擋（UNPLAYABLE、ERROR、其他原因的 LOGIN_REQUIRED 等）：一律記 Warning，
            // 使用者實測被擋時靠這些紀錄調整規則；行為不變（照常回傳 Info 或 null）
            if (playabilityStatus is not null && !string.Equals(playabilityStatus, "OK", StringComparison.Ordinal))
            {
                logger.LogWarning("YouTube 影片頁面 playabilityStatus={Status}，reason={Reason}（{Url}）",
                    playabilityStatus, playabilityReason, url);
            }

            var info = Parse(html);
            if (info is null)
            {
                logger.LogWarning("YouTube 影片頁面裡找不到可解析的 ytInitialPlayerResponse：{Url}", url);
                return new YouTubePageCheck(null, null);
            }

            logger.LogInformation(
                "YouTube 頁面：{Author}「{Title}」，直播中 {IsLiveNow}，待機 {IsUpcoming}，分類 {Category}",
                info.Author, info.Title, info.IsLiveNow, info.IsUpcoming, info.Category);
            return new YouTubePageCheck(info, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or ArgumentException or InvalidOperationException)
        {
            // 逾時、網路錯誤、格式不符都視為查不到
            logger.LogWarning(ex, "YouTube 影片頁面讀取失敗：{Url}", url);
            return new YouTubePageCheck(null, null);
        }
    }

    /// <summary>LOGIN_REQUIRED 的原因文字是否提到機器人確認（不分大小寫；日文頁面是「ログインして」「ボットではない」）。</summary>
    private static bool ContainsBotConfirmationText(string? reason) =>
        reason is not null &&
        (reason.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
         reason.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
         reason.Contains("ログインして", StringComparison.Ordinal) ||
         reason.Contains("ボットではない", StringComparison.Ordinal));

    /// <summary>從頁面 HTML 的 ytInitialPlayerResponse 讀 playabilityStatus 的 status 與 reason
    /// （reason 先讀 playabilityStatus.reason，找不到再往 errorScreen 裡任意深度找 reason 文字）。</summary>
    private static (string? Status, string? Reason) ReadPlayabilityStatus(string html)
    {
        if (!TryGetPlayerResponseJson(html, out var json)) return (null, null);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("playabilityStatus", out var status) ||
                status.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var statusText = GetStringField(status, "status");
            var reason = GetStringField(status, "reason") ??
                (status.TryGetProperty("errorScreen", out var errorScreen) ? FindReasonText(errorScreen) : null);
            return (statusText, reason);
        }
        catch (JsonException)
        {
            return (null, null);
        }
        catch (ArgumentException)
        {
            return (null, null);
        }
    }

    /// <summary>在 errorScreen 物件裡（任意深度）找第一個名為 reason 的字串值（物件時讀它的 simpleText）；找不到為 null。</summary>
    private static string? FindReasonText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("reason", out var reason))
                {
                    if (reason.ValueKind == JsonValueKind.String && reason.GetString() is { } direct)
                    {
                        return direct;
                    }
                    // reason 是物件時：讀它的 simpleText（errorScreen 常見的巢狀形態）
                    if (reason.ValueKind == JsonValueKind.Object &&
                        GetStringField(reason, "simpleText") is { } simpleText)
                    {
                        return simpleText;
                    }
                }
                foreach (var child in element.EnumerateObject())
                {
                    if (child.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array &&
                        FindReasonText(child.Value) is { } nested)
                    {
                        return nested;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    if (FindReasonText(child) is { } nested)
                    {
                        return nested;
                    }
                }
                break;
        }
        return null;
    }

    private static string? GetStringField(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>從影片頁 HTML 切出 ytInitialPlayerResponse 的 JSON 物件字串；找不到回 false。</summary>
    private static bool TryGetPlayerResponseJson(string html, out string json)
    {
        json = string.Empty;
        var marker = html.IndexOf("ytInitialPlayerResponse", StringComparison.Ordinal);
        if (marker < 0) return false;

        var start = html.IndexOf('{', marker);
        if (start < 0) return false;

        var end = FindObjectEnd(html, start);
        if (end < 0) return false;

        json = html[start..(end + 1)];
        return true;
    }

    /// <summary>從影片頁 HTML 切出 ytInitialPlayerResponse 的 JSON 物件並解析；找不到或解析失敗回傳 null。</summary>
    public static YouTubePageInfo? Parse(string html)
    {
        if (!TryGetPlayerResponseJson(html, out var json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var info = ReadInfo(document.RootElement);
            return info is null ? null : info with { VisitorData = FindVisitorData(html) };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // 空字串等無效輸入
            return null;
        }
    }

    /// <summary>讀取 ytInitialPlayerResponse 的欄位；缺少 videoDetails 時回傳 null。</summary>
    private static YouTubePageInfo? ReadInfo(JsonElement root)
    {
        if (!root.TryGetProperty("videoDetails", out var videoDetails) || videoDetails.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? Field(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        string? channelUrl = null;
        string? category = null;
        var isLiveNow = false;
        DateTimeOffset? startTime = null;
        DateTimeOffset? endTime = null;
        if (root.TryGetProperty("microformat", out var microformat) &&
            microformat.ValueKind == JsonValueKind.Object &&
            microformat.TryGetProperty("playerMicroformatRenderer", out var renderer) &&
            renderer.ValueKind == JsonValueKind.Object)
        {
            category = Field(renderer, "category");
            var ownerProfileUrl = Field(renderer, "ownerProfileUrl");
            if (ownerProfileUrl is not null)
            {
                // 頁面上的頻道網址是 http 時統一改成 https
                channelUrl = ownerProfileUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    ? "https://" + ownerProfileUrl["http://".Length..]
                    : ownerProfileUrl;
            }

            if (renderer.TryGetProperty("liveBroadcastDetails", out var broadcast) && broadcast.ValueKind == JsonValueKind.Object)
            {
                isLiveNow = GetBool(broadcast, "isLiveNow");
                startTime = GetDateTime(broadcast, "startTimestamp");
                endTime = GetDateTime(broadcast, "endTimestamp");
            }
        }

        // 正在直播：liveBroadcastDetails.isLiveNow 或 videoDetails.isLive 任一成立
        var isLive = GetBool(videoDetails, "isLive");
        var (captionLanguages, autoCaptionLanguage) = ReadCaptionLanguages(root);
        return new YouTubePageInfo(
            Field(videoDetails, "videoId"),
            Field(videoDetails, "title"),
            Field(videoDetails, "author"),
            Field(videoDetails, "channelId"),
            channelUrl,
            category,
            Field(videoDetails, "shortDescription"),
            StringList(videoDetails, "keywords"),
            IsLiveNow: isLiveNow || isLive,
            IsLiveContent: GetBool(videoDetails, "isLiveContent"),
            IsUpcoming: GetBool(videoDetails, "isUpcoming"),
            startTime,
            endTime,
            GetInt(videoDetails, "lengthSeconds"),
            captionLanguages,
            AutoCaptionLanguage: autoCaptionLanguage);
    }

    /// <summary>從 HTML 找 "VISITOR_DATA":"…" 的值（呼叫 InnerTube API 的 visionOS 身分需要）；找不到時為 null。</summary>
    private static string? FindVisitorData(string html)
    {
        const string marker = "\"VISITOR_DATA\":\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        start += marker.Length;
        var end = html.IndexOf('"', start);
        if (end < 0) return null;

        var value = html[start..end];
        return value.Length == 0 ? null : value;
    }

    /// <summary>讀字幕軌的 languageCode 清單（去重），順便記下第一個生成式字幕軌（kind == "asr"）的語言。</summary>
    private static (IReadOnlyList<string> Languages, string? AutoCaptionLanguage) ReadCaptionLanguages(JsonElement root)
    {
        if (!root.TryGetProperty("captions", out var captions) ||
            captions.ValueKind != JsonValueKind.Object ||
            !captions.TryGetProperty("playerCaptionsTracklistRenderer", out var tracklist) ||
            tracklist.ValueKind != JsonValueKind.Object ||
            !tracklist.TryGetProperty("captionTracks", out var tracks) ||
            tracks.ValueKind != JsonValueKind.Array)
        {
            return ([], null);
        }

        List<string> languages = [];
        string? autoCaptionLanguage = null;
        foreach (var track in tracks.EnumerateArray())
        {
            if (track.ValueKind != JsonValueKind.Object ||
                !track.TryGetProperty("languageCode", out var languageCode) ||
                languageCode.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var code = languageCode.GetString();
            if (!string.IsNullOrEmpty(code) && !languages.Contains(code))
            {
                languages.Add(code);
            }

            if (autoCaptionLanguage is null &&
                code is not null &&
                track.TryGetProperty("kind", out var kind) &&
                kind.ValueKind == JsonValueKind.String &&
                kind.GetString() == "asr")
            {
                autoCaptionLanguage = code;
            }
        }

        return (languages, autoCaptionLanguage);
    }

    private static IReadOnlyList<string> StringList(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<string> values = [];
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrEmpty(value)) values.Add(value);
            }
        }

        return values;
    }

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        int.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? GetDateTime(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    /// <summary>從 openBrace 開始做括號配對，回傳與它成對的 '}' 位置；找不到回 -1。
    /// 字串字面量裡的 '{'、'}' 不算，反斜線會跳過下一個字元（\\" 是跳脫的反斜線，不會結束字串）。</summary>
    private static int FindObjectEnd(string text, int openBrace)
    {
        var depth = 0;
        var inString = false;
        for (var i = openBrace; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }
}
