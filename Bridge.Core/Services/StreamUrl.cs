using System.Diagnostics.CodeAnalysis;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>從剪貼簿文字解析 YouTube／Twitch 的直播網址。</summary>
public static class StreamUrl
{
    public static bool TryParse(string? text, [NotNullWhen(true)] out string? url, out StreamPlatform platform)
    {
        url = null;
        platform = default;

        var line = FirstNonBlankLine(text);
        if (line is null) return false;

        if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host["www.".Length..];
        }
        else if (host.StartsWith("m.", StringComparison.Ordinal))
        {
            host = host["m.".Length..];
        }

        switch (host)
        {
            case "youtube.com":
            case "youtu.be":
                platform = StreamPlatform.YouTube;
                break;
            case "twitch.tv":
                platform = StreamPlatform.Twitch;
                break;
            default:
                return false;
        }

        // 回傳修剪後的原字串，不改寫（保留查詢字串與大小寫）
        url = line;
        return true;
    }

    /// <summary>取第一個非空白行並修剪；整段都空白時回傳 null。</summary>
    private static string? FirstNonBlankLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var raw in text.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length > 0) return trimmed;
        }

        return null;
    }
}
