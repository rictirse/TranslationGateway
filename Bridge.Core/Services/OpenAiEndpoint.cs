using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

public static class OpenAiEndpoint
{
    /// <summary>UseLocalModel 為 true 時使用的本地端點。</summary>
    public const string LocalModelHost = "http://192.168.81.50:11434";

    /// <summary>依設定組出 OpenAI 相容端點（網址沒有路徑時才補 /v1）；Host 未設定或格式不正確時回傳 null。</summary>
    public static Uri? TryBuild(TranslationSettings settings)
    {
        var host = settings.UseLocalModel ? LocalModelHost : settings.ApiHost;
        host = (host ?? "").Trim();
        host = TrimTrailingSlashes(host);

        // 防呆：如果字串內含有完整的 /chat/completions，直接把它切掉
        var index = host.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            host = TrimTrailingSlashes(host.Substring(0, index));
        }

        if (host.Length == 0)
        {
            return null;
        }

        // 確保有 http:// 或 https:// 前綴
        if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            host = "http://" + host;
        }

        if (!Uri.TryCreate(host, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        // 只有網址本身沒有路徑（修剪結尾斜線後 AbsolutePath 為空或 "/"，例如 Ollama 的 host:port）
        // 才自動補 /v1；已經帶路徑的網址（例如 Z.AI 的 https://api.z.ai/api/coding/paas/v4）照原樣使用，
        // 不能再串 /v1 變成 .../v4/v1
        if (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/")
        {
            return new Uri(host + "/v1", UriKind.Absolute);
        }

        return uri;
    }

    /// <summary>去結尾 '/'，但保留 scheme 的 "://" 分隔符。</summary>
    private static string TrimTrailingSlashes(string value)
    {
        while (value.Length > 0 && value.EndsWith('/') && !value.EndsWith("://"))
        {
            value = value[..^1];
        }
        return value;
    }
}
