using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>以 App 用戶端身分（visionOS → iOS 依序嘗試）呼叫 InnerTube player API，
/// 取得 captionTracks 裡的日文字幕軌並下載 json3 內容；任何失敗都回傳 null，不擲例外。</summary>
public sealed class YouTubeCaptionClient(HttpClient httpClient, ILogger<YouTubeCaptionClient> logger) : IYouTubeCaptionProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string PlayerEndpoint = "https://www.youtube.com/youtubei/v1/player";

    /// <summary>visionOS 身分（PotPlayer 內建 YouTube 解析也用這個）；必須帶 visitorData。</summary>
    private const string VisionOsUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.5 Safari/605.1.15";

    private const string VisionOsBody = """
        {"context":{"client":{"clientName":"VISIONOS","clientVersion":"1.02","deviceMake":"Apple","deviceModel":"RealityDevice17,1","hl":"ja","osName":"visionOS","osVersion":"26.5.23O471"}},"videoId":"{VideoId}","contentCheckOk":true,"racyCheckOk":true}
        """;

    /// <summary>iOS 身分；不需要 visitorData。</summary>
    private const string IosUserAgent =
        "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)";

    private const string IosBody = """
        {"context":{"client":{"clientName":"IOS","clientVersion":"20.10.4","deviceMake":"Apple","deviceModel":"iPhone16,2","hl":"ja","osName":"iPhone","osVersion":"18.3.2.22D82"}},"videoId":"{VideoId}"}
        """;

    public async Task<YouTubeCaptionResult?> GetJapaneseCaptionsAsync(string videoId, string? visitorData, CancellationToken cancellationToken = default)
    {
        try
        {
            // visionOS 需要 visitorData，沒有時直接跳過
            if (visitorData is not null)
            {
                var visionOs = await TryClientAsync("VISIONOS", VisionOsUserAgent, VisionOsBody, videoId, visitorData, cancellationToken);
                if (visionOs.Outcome == AttemptOutcome.NoJapaneseTrack)
                {
                    logger.LogInformation("這支影片沒有日文字幕：{VideoId}", videoId);
                    return null;
                }

                if (visionOs.Result is not null)
                {
                    return visionOs.Result;
                }
            }
            else
            {
                logger.LogInformation("沒有 visitorData，略過 visionOS 身分，直接用 iOS：{VideoId}", videoId);
            }

            var ios = await TryClientAsync("IOS", IosUserAgent, IosBody, videoId, null, cancellationToken);
            if (ios.Outcome == AttemptOutcome.NoJapaneseTrack)
            {
                logger.LogInformation("這支影片沒有日文字幕：{VideoId}", videoId);
            }

            return ios.Result;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(ex, "YouTube 字幕下載失敗：{VideoId}", videoId);
            return null;
        }
    }

    private enum AttemptOutcome
    {
        Success,
        /// <summary>player API 回 OK 且有 captionTracks，但沒有日文軌（換身分也一樣）。</summary>
        NoJapaneseTrack,
        Failed
    }

    private sealed record Attempt(AttemptOutcome Outcome, YouTubeCaptionResult? Result = null);

    /// <summary>用單一身分呼叫 player API → 選軌 → 下載 json3；失敗只記 Information log，交由呼叫端換下一個身分。</summary>
    private async Task<Attempt> TryClientAsync(
        string clientName, string userAgent, string bodyTemplate, string videoId, string? visitorData, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, PlayerEndpoint)
            {
                Content = new StringContent(bodyTemplate.Replace("{VideoId}", videoId, StringComparison.Ordinal), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            if (clientName == "VISIONOS")
            {
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "101");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "1.02");
                request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);
            }

            using var response = await httpClient.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("YouTube 字幕：{Client} 身分回傳 {StatusCode}（{VideoId}），改試下一個", clientName, (int)response.StatusCode, videoId);
                return new Attempt(AttemptOutcome.Failed);
            }

            var playerJson = await response.Content.ReadAsStringAsync(cts.Token);
            using var document = JsonDocument.Parse(playerJson);
            var root = document.RootElement;

            var statusText = root.TryGetProperty("playabilityStatus", out var playability) &&
                playability.ValueKind == JsonValueKind.Object &&
                playability.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String
                    ? status.GetString()
                    : null;
            if (!string.Equals(statusText, "OK", StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "YouTube 字幕：{Client} 身分的 playabilityStatus 不是 OK（{Status}，{VideoId}），改試下一個",
                    clientName, statusText ?? "未知", videoId);
                return new Attempt(AttemptOutcome.Failed);
            }

            if (!HasCaptionTracks(root))
            {
                logger.LogInformation("YouTube 字幕：{Client} 身分拿不到 captionTracks（{VideoId}），改試下一個", clientName, videoId);
                return new Attempt(AttemptOutcome.Failed);
            }

            var trackUrl = SelectTrackUrl(root, out var languageCode, out var isAuto);
            if (trackUrl is null)
            {
                return new Attempt(AttemptOutcome.NoJapaneseTrack);
            }

            var cues = await DownloadCuesAsync(trackUrl, userAgent, clientName, videoId, cancellationToken);
            if (cues is null)
            {
                return new Attempt(AttemptOutcome.Failed);
            }

            logger.LogInformation(
                "YouTube 字幕：{VideoId} 載入 {Count} 段（{Lang}，{Kind}，用戶端 {Client}）",
                videoId, cues.Count, languageCode, isAuto ? "自動產生" : "人工", clientName);
            return new Attempt(AttemptOutcome.Success, new YouTubeCaptionResult(languageCode, isAuto, clientName, cues));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or ArgumentException or InvalidOperationException)
        {
            logger.LogInformation(ex, "YouTube 字幕：{Client} 身分失敗（{VideoId}），改試下一個", clientName, videoId);
            return new Attempt(AttemptOutcome.Failed);
        }
    }

    /// <summary>下載 json3 字幕內容並解析（baseUrl 已含查詢字串，用 &amp; 接 fmt=json3）。</summary>
    private async Task<IReadOnlyList<CaptionCue>?> DownloadCuesAsync(
        string baseUrl, string userAgent, string clientName, string videoId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "&fmt=json3");
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        using var response = await httpClient.SendAsync(request, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("YouTube 字幕：字幕下載回傳 {StatusCode}（{Client}，{VideoId}）", (int)response.StatusCode, clientName, videoId);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cts.Token);
        return ParseJson3(json);
    }

    /// <summary>player 回應裡是否找得到 captionTracks 陣列（空陣列視為沒有）。</summary>
    private static bool HasCaptionTracks(JsonElement playerResponse) =>
        playerResponse.TryGetProperty("captions", out var captions) &&
        captions.ValueKind == JsonValueKind.Object &&
        captions.TryGetProperty("playerCaptionsTracklistRenderer", out var tracklist) &&
        tracklist.ValueKind == JsonValueKind.Object &&
        tracklist.TryGetProperty("captionTracks", out var tracks) &&
        tracks.ValueKind == JsonValueKind.Array &&
        tracks.GetArrayLength() > 0;

    /// <summary>選日文字幕軌：languageCode 是 ja 或 ja- 開頭，沒有 kind 的（人工上傳）優先，其次 kind == "asr"；都沒有回 null。</summary>
    public static string? SelectTrackUrl(JsonElement playerResponse, out string languageCode, out bool isAuto)
    {
        languageCode = string.Empty;
        isAuto = false;

        if (!playerResponse.TryGetProperty("captions", out var captions) ||
            captions.ValueKind != JsonValueKind.Object ||
            !captions.TryGetProperty("playerCaptionsTracklistRenderer", out var tracklist) ||
            tracklist.ValueKind != JsonValueKind.Object ||
            !tracklist.TryGetProperty("captionTracks", out var tracks) ||
            tracks.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? manualUrl = null;
        string? manualLanguage = null;
        string? asrUrl = null;
        string? asrLanguage = null;
        foreach (var track in tracks.EnumerateArray())
        {
            if (track.ValueKind != JsonValueKind.Object ||
                !track.TryGetProperty("languageCode", out var code) ||
                code.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var language = code.GetString();
            if (language is null || !(language == "ja" || language.StartsWith("ja-", StringComparison.Ordinal)))
            {
                continue;
            }

            if (!track.TryGetProperty("baseUrl", out var url) ||
                url.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(url.GetString()))
            {
                continue;
            }

            var isAsr = track.TryGetProperty("kind", out var kind) &&
                kind.ValueKind == JsonValueKind.String &&
                string.Equals(kind.GetString(), "asr", StringComparison.Ordinal);
            if (isAsr)
            {
                asrUrl ??= url.GetString();
                asrLanguage ??= language;
            }
            else
            {
                manualUrl ??= url.GetString();
                manualLanguage ??= language;
            }
        }

        if (manualUrl is not null)
        {
            languageCode = manualLanguage!;
            isAuto = false;
            return manualUrl;
        }

        if (asrUrl is not null)
        {
            languageCode = asrLanguage!;
            isAuto = true;
            return asrUrl;
        }

        return null;
    }

    /// <summary>解析 json3 字幕格式：每個 event 的 segs 串接成一段（換行換成空白、修剪、丟掉空白），
    /// 沒有 segs 的 event 也丟掉，結果照 StartMs 排序；格式不符回空清單。</summary>
    public static IReadOnlyList<CaptionCue> ParseJson3(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<CaptionCue> cues = [];
            foreach (var item in events.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("segs", out var segs) ||
                    segs.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var text = new StringBuilder();
                foreach (var seg in segs.EnumerateArray())
                {
                    if (seg.ValueKind == JsonValueKind.Object &&
                        seg.TryGetProperty("utf8", out var utf8) &&
                        utf8.ValueKind == JsonValueKind.String)
                    {
                        text.Append(utf8.GetString());
                    }
                }

                var trimmed = text.Replace('\n', ' ').Replace('\r', ' ').ToString().Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                cues.Add(new CaptionCue(GetLong(item, "tStartMs"), GetLong(item, "dDurationMs"), trimmed));
            }

            return [.. cues.OrderBy(c => c.StartMs)];
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return [];
        }
    }

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var parsed)
            ? parsed
            : 0;
}
