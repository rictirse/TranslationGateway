using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Translation.Bridge.Core.Services;

/// <summary>譯名修正：在輸出時把錯誤譯名替換為正確譯名。</summary>
public static class ReplacementRules
{
    /// <summary>快取上限：超過後清空重建，避免長期佔用記憶體。</summary>
    private const int MaxCacheSize = 16;

    /// <summary>多份規則文字的編譯快取（原文修正與譯名修正會交替使用，不能只快取最後一份）。</summary>
    private static readonly ConcurrentDictionary<string, CacheSnapshot> Cache = new(StringComparer.Ordinal);

    /// <summary>解析規則文字；回傳 (From, To) 清單，順序同輸入、重複的 From 以最後一條為準。</summary>
    public static IReadOnlyList<(string From, string To)> Parse(string? rulesText)
    {
        if (string.IsNullOrEmpty(rulesText)) return [];

        var order = new List<string>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in rulesText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // 以第一個 = 分割；沒有 =（-1）或 From 為空（0）的行略過
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var from = line[..eq].Trim();
            var to = line[(eq + 1)..].Trim();

            if (!map.ContainsKey(from)) order.Add(from);
            map[from] = to;
        }

        return order.Select(f => (f, map[f])).ToList();
    }

    /// <summary>依規則替換 text；rulesText 為空或沒有有效規則時原樣回傳。</summary>
    public static string Apply(string text, string? rulesText)
    {
        if (string.IsNullOrEmpty(rulesText) || string.IsNullOrEmpty(text)) return text;

        var snapshot = GetSnapshot(rulesText);
        if (snapshot.Regex is null || snapshot.Map.Count == 0) return text;

        // 單次掃描 + MatchEvaluator：替換結果不會再被掃描，避免連鎖替換
        return snapshot.Regex.Replace(text, m => snapshot.Map[m.Value]);
    }

    /// <summary>取得 rulesText 對應的編譯快照；同一份文字只解析、編譯一次。</summary>
    private static CacheSnapshot GetSnapshot(string rulesText)
    {
        // 超過上限時先清空再加入（ConcurrentDictionary 保證讀寫安全；競態下最壞只是重編一次）
        if (Cache.Count > MaxCacheSize)
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd(rulesText, static text => Compile(text));
    }

    /// <summary>解析並編譯 rulesText，產生不可變快照。</summary>
    private static CacheSnapshot Compile(string rulesText)
    {
        var rules = Parse(rulesText);

        if (rules.Count == 0)
        {
            return new CacheSnapshot(
                null,
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in rules) map[from] = to;

        // From 較長者優先（穩定排序，同長度維持輸入順序）
        var pattern = string.Join("|", rules
            .Select(r => r.From)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(f => f.Length)
            .Select(Regex.Escape));

        return new CacheSnapshot(
            new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant),
            map);
    }

    /// <summary>rulesText 與其編譯結果的不可變快照。</summary>
    private sealed record CacheSnapshot(
        Regex? Regex,
        IReadOnlyDictionary<string, string> Map);
}
