namespace Translation.Bridge.Core.Services;

/// <summary>從文字（網址或檔名）解析 YouTube videoId；不是 YouTube 網址或 id 格式不符時回 null。</summary>
public static class YouTubeVideoId
{
    /// <summary>解析 videoId（v= 參數、youtu.be/、/live/、/shorts/）；解析不到，或不是 11 碼英數字、-、_ 時回 null。</summary>
    public static string? TryParse(string? text)
    {
        var videoId = ParseFromUrl(text);
        return videoId is { Length: 11 } && videoId.All(c =>
            c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_')
            ? videoId
            : null;
    }

    /// <summary>從 YouTube 網址解析 videoId（v= 參數、youtu.be/、/live/、/shorts/）；解析不到回 null。</summary>
    private static string? ParseFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal) || host.StartsWith("m.", StringComparison.Ordinal))
        {
            host = host[(host.IndexOf('.') + 1)..];
        }

        if (host == "youtu.be")
        {
            return DecodeSegment(uri.AbsolutePath);
        }

        if (!host.EndsWith("youtube.com", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = parameter.IndexOf('=');
            if (equals > 0 && parameter[..equals] == "v")
            {
                return DecodeSegment(parameter[(equals + 1)..]);
            }
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && (segments[0] == "live" || segments[0] == "shorts"))
        {
            return DecodeSegment(segments[1]);
        }

        return null;

        // videoId 只有字母、數字、-、_，不需解 URL 編碼
        string? DecodeSegment(string segment)
        {
            segment = segment.Trim('/');
            return segment.Length == 0 ? null : segment;
        }
    }
}
