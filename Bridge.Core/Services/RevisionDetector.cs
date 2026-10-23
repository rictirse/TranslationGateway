using System.Text;

namespace Translation.Bridge.Core.Services;

/// <summary>新字幕相對於前一句的關係。</summary>
public enum RevisionKind { None, Identical, Shrink, Revise }

/// <summary>
/// 重複字幕判斷：PotPlayer 的 Whisper 以滑動視窗辨識，同一段話幾秒內會送來好幾個版本
/// （只差標點、延伸、改一兩個字）。用正規化後的字串比對判斷新字幕是否為前一句的修正版，
/// 讓 TranslationManager 決定沿用譯文或取代上一輪。
/// </summary>
public static class RevisionDetector
{
    /// <summary>正規化後長度低於此值的句子不參與判斷（太短容易誤判）。</summary>
    private const int MinNormalizedLength = 4;

    /// <summary>p 包含 c 時，c 長度至少要達 p 的一半才算 Shrink（短片段多半是下一句的開頭）。</summary>
    private const int ShrinkMinRatioPercent = 50;

    /// <summary>Levenshtein 相似度門檻。</summary>
    private const double SimilarityThreshold = 0.6;

    /// <summary>最長共同子字串的絕對長度下限。</summary>
    private const int MinCommonSubstringLength = 6;

    /// <summary>最長共同子字串相對於較短句長度的比例門檻。</summary>
    private const double CommonSubstringRatio = 0.6;

    /// <summary>NFKC 正規化後只保留 char.IsLetterOrDigit 的字元（去掉標點、空白、符號）。</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var normalized = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>判斷 current 是否為 previous 的修正版（規則見類別說明）。</summary>
    public static RevisionKind Classify(string previous, string current)
    {
        var p = Normalize(previous);
        var c = Normalize(current);

        // 1. 太短的句子不判斷
        if (p.Length < MinNormalizedLength || c.Length < MinNormalizedLength)
        {
            return RevisionKind.None;
        }

        // 2. 正規化後完全相同（只差標點、空白、全半形）
        if (p == c) return RevisionKind.Identical;

        // 3. previous 包含 current：新字幕變短，且保留了至少一半的內容才算 Shrink
        if (p.Contains(c, StringComparison.Ordinal))
        {
            return c.Length * 100 >= p.Length * ShrinkMinRatioPercent
                ? RevisionKind.Shrink
                : RevisionKind.None;
        }

        // 4. current 包含 previous：新字幕是原句的延伸
        if (c.Contains(p, StringComparison.Ordinal)) return RevisionKind.Revise;

        // 5. 編輯距離夠近：改了一兩個字的版本
        var maxLength = Math.Max(p.Length, c.Length);
        var similarity = 1.0 - (double)Levenshtein(p, c) / maxLength;
        if (similarity >= SimilarityThreshold) return RevisionKind.Revise;

        // 6. 有夠長的共同片段：部分重疊（例如重新斷句的前後版本）
        var lcs = LongestCommonSubstring(p, c);
        var lcsThreshold = Math.Max(
            (double)MinCommonSubstringLength,
            CommonSubstringRatio * Math.Min(p.Length, c.Length));
        if (lcs >= lcsThreshold) return RevisionKind.Revise;

        // 7. 其他：無關句子
        return RevisionKind.None;
    }

    /// <summary>以字元計算的 Levenshtein 編輯距離（兩列陣列的 DP）。</summary>
    internal static int Levenshtein(string p, string c)
    {
        if (p.Length == 0) return c.Length;
        if (c.Length == 0) return p.Length;

        var prev = new int[c.Length + 1];
        var curr = new int[c.Length + 1];
        for (var j = 0; j <= c.Length; j++) prev[j] = j;

        for (var i = 1; i <= p.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= c.Length; j++)
            {
                var cost = p[i - 1] == c[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[c.Length];
    }

    /// <summary>以字元計算的最長共同子字串長度（兩列陣列的 DP）。</summary>
    internal static int LongestCommonSubstring(string p, string c)
    {
        if (p.Length == 0 || c.Length == 0) return 0;

        var prev = new int[c.Length + 1];
        var curr = new int[c.Length + 1];
        var best = 0;

        for (var i = 1; i <= p.Length; i++)
        {
            for (var j = 1; j <= c.Length; j++)
            {
                curr[j] = p[i - 1] == c[j - 1]
                    ? prev[j - 1] + 1
                    : 0;
                if (curr[j] > best) best = curr[j];
            }
            (prev, curr) = (curr, prev);
            Array.Clear(curr);
        }
        return best;
    }
}
