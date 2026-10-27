using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>呼叫 LRCLIB（https://lrclib.net，免金鑰）查歌曲長度：
/// 有歌手用 track_name＋artist_name 搜尋，沒有只用 q。LRCLIB 正常回應的結果（含查不到的 null）存在記憶體快取，
/// Gateway 執行期間同一個（曲名、歌手）不重查；伺服器忙碌（429、5xx）、逾時、網路錯誤時隔一下重試，
/// 最後仍失敗就回 null 但不快取（下次開片再查）。LRCLIB 常回 503「The server is busy」（2026-10-26 實測）。</summary>
public sealed class LrclibClient(HttpClient httpClient, ILogger<LrclibClient> logger) : ISongInfoProvider
{
    /// <summary>單次請求的逾時。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private const string SearchEndpoint = "https://lrclib.net/api/search";

    /// <summary>LRCLIB 建議的識別 User-Agent（附專案網址）。</summary>
    private const string UserAgent = "TranslationGateway (https://github.com/rictirse/TranslationGateway)";

    /// <summary>暫時性失敗後重試前的等待時間（次數 = 長度 + 1）；測試可縮短。</summary>
    internal TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>查詢結果的記憶體快取：LRCLIB 正常回應才記（查不到的 null 也記），暫時性失敗不記。</summary>
    private readonly Dictionary<(string Title, string? Artist), long?> _cache = new();

    private readonly object _lock = new();

    public async Task<long?> GetDurationMsAsync(SongQuery query, CancellationToken cancellationToken = default)
    {
        var key = (query.Title, query.Artist);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        for (var attempt = 0; ; attempt++)
        {
            var transient = false;
            try
            {
                var (definitive, durationMs) = await SearchAsync(query, cancellationToken).ConfigureAwait(false);
                if (definitive)
                {
                    lock (_lock)
                    {
                        _cache[key] = durationMs;
                    }

                    return durationMs;
                }

                transient = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException ||
                                        (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // 網路錯誤、逾時：當成暫時性失敗重試
                logger.LogWarning(ex, "LRCLIB 查詢失敗（第 {Attempt} 次）：{Title}／{Artist}", attempt + 1, query.Title, query.Artist);
                transient = true;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                // 回應格式不對：重試也一樣，不快取、直接放棄
                logger.LogWarning(ex, "LRCLIB 回應無法解析：{Title}／{Artist}", query.Title, query.Artist);
                return null;
            }

            if (!transient || attempt >= RetryDelays.Length)
            {
                logger.LogWarning("LRCLIB 重試 {Attempts} 次仍失敗，這次不顯示原曲長度（不快取，下次開片再查）：{Title}／{Artist}",
                    attempt + 1, query.Title, query.Artist);
                return null;
            }

            await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>打 LRCLIB 搜尋 API 並挑出最符合的一筆。Definitive = false 表示伺服器忙碌（429、5xx），呼叫端會重試；
    /// true 時 DurationMs 為 null 表示查不到（可快取）。</summary>
    private async Task<(bool Definitive, long? DurationMs)> SearchAsync(SongQuery query, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        var parameters = query.Artist is null
            ? $"q={Uri.EscapeDataString(query.Title)}"
            : $"track_name={Uri.EscapeDataString(query.Title)}&artist_name={Uri.EscapeDataString(query.Artist)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{SearchEndpoint}?{parameters}");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            logger.LogWarning("LRCLIB 搜尋回傳 {StatusCode}：{Title}／{Artist}", status, query.Title, query.Artist);
            // 429、5xx 是暫時性（伺服器忙碌）；其他 4xx 視為查不到
            return (status != 429 && status < 500, null);
        }

        var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        long? durationMs;
        using (var document = JsonDocument.Parse(json))
        {
            durationMs = PickDurationMs(document.RootElement, query);
        }

        if (durationMs is { } found)
        {
            logger.LogInformation("LRCLIB 查到原曲長度：{Title}／{Artist} → {Seconds:F1} 秒", query.Title, query.Artist, found / 1000.0);
        }
        else
        {
            logger.LogInformation("LRCLIB 查不到符合的原曲長度：{Title}／{Artist}", query.Title, query.Artist);
        }

        return (true, durationMs);
    }

    /// <summary>從搜尋結果挑最符合的一筆，回傳其長度（毫秒）；沒有合適的回 null。給測試用。</summary>
    internal static long? PickDurationMs(JsonElement results, SongQuery query)
    {
        if (results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var normalizedTitle = Normalize(query.Title);
        var normalizedArtist = query.Artist is null ? null : Normalize(query.Artist);
        if (normalizedTitle.Length == 0)
        {
            return null;
        }

        var bestScore = 0;
        var bestDurationSeconds = 0.0;
        var hasBest = false;
        foreach (var item in results.EnumerateArray())
        {
            // 沒有 duration 或是 0／負值的項目不考慮
            if (!TryGetDouble(item, "duration", out var duration) || duration <= 0)
            {
                continue;
            }

            var trackName = Normalize(GetString(item, "trackName"));
            if (trackName.Length == 0)
            {
                continue;
            }

            if (normalizedArtist is null && !string.Equals(trackName, normalizedTitle, StringComparison.Ordinal))
            {
                // 沒有歌手時曲名必須完全相同，否則任意 q 搜尋結果都會勉強配上
                continue;
            }

            var score = 0;
            if (trackName.Equals(normalizedTitle, StringComparison.Ordinal))
            {
                score += 2;
            }
            else if (trackName.Contains(normalizedTitle, StringComparison.Ordinal))
            {
                score += 1;
            }

            if (normalizedArtist is not null)
            {
                var artistName = Normalize(GetString(item, "artistName"));
                if (artistName.Length > 0 && artistName.Contains(normalizedArtist, StringComparison.Ordinal))
                {
                    // 回傳的歌手常是「TOKOTOKO(西沢さんP)」「初星学園; 月村手毬」這類寫法：包含就算命中
                    score += 2;
                }
            }

            // syncedLyrics 不是 null（字串，可能空）加一分：下不了判斷時優選有歌詞的
            if (TryGetString(item, "syncedLyrics", out _))
            {
                score += 1;
            }

            // 同分取陣列中較前者（嚴格大於才換）
            if (!hasBest || score > bestScore)
            {
                hasBest = true;
                bestScore = score;
                bestDurationSeconds = duration;
            }
        }

        if (!hasBest || (normalizedArtist is not null && bestScore < 2))
        {
            // 有歌手時至少要歌手或曲名對上（≥2 分），否則寧可回 null 用預設長度
            return null;
        }

        return (long)Math.Round(bestDurationSeconds * 1000, MidpointRounding.AwayFromZero);
    }

    /// <summary>比對用的正規化：全形轉半形（NFKC）→ 小寫 → 去掉所有空白。</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (!char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGetString(element, name, out var value) ? value : null;

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        return false;
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value);
    }
}
