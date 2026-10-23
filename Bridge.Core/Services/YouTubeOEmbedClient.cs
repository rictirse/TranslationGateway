using System.Text.Json;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>用 YouTube 公開的 oEmbed 介面查影片標題與頻道（不需要 API Key）；查不到時回 null，不擲例外。</summary>
public sealed class YouTubeOEmbedClient(HttpClient httpClient, ILogger<YouTubeOEmbedClient> logger) : IVideoInfoProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<StreamMetadata?> GetYouTubeInfoAsync(string url, CancellationToken cancellationToken = default)
    {
        var requestUrl = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(url)}&format=json";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);

            using var response = await httpClient.GetAsync(requestUrl, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("YouTube oEmbed 回傳 {StatusCode}，查不到 {Url} 的影片資訊", (int)response.StatusCode, url);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string? Field(string name) => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;

            return new StreamMetadata(
                StreamPlatform.YouTube,
                null,
                Field("author_name"),
                null,
                Field("title"),
                ChannelUrl: Field("author_url"),
                IsVod: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or ArgumentException or InvalidOperationException)
        {
            // 逾時、網路錯誤、非 JSON 都視為查不到
            logger.LogWarning(ex, "YouTube oEmbed 查詢失敗：{Url}", url);
            return null;
        }
    }
}
