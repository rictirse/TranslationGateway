namespace Translation.Bridge.Core.Services;

/// <summary>ApiHost 字串比較：正規化後判斷兩個 Host 是否指向同一個位置。</summary>
public static class ApiHostComparer
{
    /// <summary>兩個 ApiHost 是否指向同一個位置：去掉頭尾空白與結尾的 /，不分大小寫比較；任一個是空白時回傳 false。</summary>
    public static bool SameHost(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        return left is not null && right is not null &&
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>去掉頭尾空白與結尾的 /；空白回傳 null。</summary>
    private static string? Normalize(string? host)
    {
        var trimmed = host?.Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
