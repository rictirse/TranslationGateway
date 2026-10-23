using System.Text;
using System.Text.RegularExpressions;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>Whisper 字幕對 YouTube 字幕的相似度比對（正規化後算字元 bigram 的 Dice 係數）。</summary>
public static class CaptionMatcher
{
    /// <summary>FindBest 嘗試串接的連續 cue 段數上限。</summary>
    private const int MaxCueCount = 3;

    /// <summary>YouTube 自動字幕的方括號標記（[音楽]、[拍手] 等）。</summary>
    private static readonly Regex BracketTags = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    /// <summary>正規化：NFKC、片假名轉平假名、移除所有非字母數字的字元（空白、標點、符號、括號）。</summary>
    public static string Normalize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            // 片假名（U+30A1～U+30F6）減 0x60 轉成對應的平假名
            var ch = c is >= '\u30A1' and <= '\u30F6' ? (char)(c - 0x60) : c;
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }
        return builder.ToString();
    }

    /// <summary>相似度：兩邊先 Normalize，再算字元 bigram 的 Dice 係數（2 × 多重集合交集數 ÷ 兩邊 bigram 總數）。
    /// 任一邊正規化後少於 2 個字元時回傳 0。</summary>
    public static double Similarity(string a, string b)
    {
        return DiceOfNormalized(Normalize(a), Normalize(b));
    }

    /// <summary>裁切 caption，只留跟 text 對到的那一小段：回傳裁切後的文字與裁切段對 text 的相似度。
    /// 先移除 [音楽]、[拍手] 這類方括號標記；caption 沒比 text 長多少（正規化後 ≤ 1.3 倍）時不裁切。</summary>
    public static (string Text, double Score) TrimToMatch(string caption, string text)
    {
        var cleaned = BracketTags.Replace(caption, string.Empty);

        // 正規化字元 → 原始索引對照：每個正規化字元記所屬原始字元的索引 k
        var ncBuilder = new StringBuilder(cleaned.Length);
        var map = new List<int>(cleaned.Length);
        for (var k = 0; k < cleaned.Length; k++)
        {
            var part = Normalize(cleaned[k].ToString());
            for (var p = 0; p < part.Length; p++)
            {
                ncBuilder.Append(part[p]);
                map.Add(k);
            }
        }
        var nc = ncBuilder.ToString();

        var nw = Normalize(text);
        var length = nw.Length;

        // caption 沒比本句長多少：不裁切
        if (nc.Length <= length * 1.3)
        {
            return (cleaned.Trim(), Similarity(cleaned, text));
        }

        // 在 nc 找 Dice 分數最高的連續子字串（外層起點、內層終點皆由小到大；同分取最先找到的）
        var minLength = Math.Max(2, (int)(length * 0.6));
        var maxLength = (int)(length * 1.5);
        var bestScore = double.MinValue;
        var bestStart = -1;
        var bestEnd = -1;
        for (var i = 0; i + minLength <= nc.Length; i++)
        {
            for (var len = minLength; len <= maxLength && i + len <= nc.Length; len++)
            {
                var score = DiceOfNormalized(nc.Substring(i, len), nw);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestStart = i;
                    bestEnd = i + len;
                }
            }
        }

        // 找不到任何候選（本句極短）：不裁切
        if (bestStart < 0)
        {
            return (cleaned.Trim(), Similarity(cleaned, text));
        }

        // 換回原始字串：終點之後緊接的標點（不是字母、數字、空白的字元）一起帶上
        var start = map[bestStart];
        var end = map[bestEnd - 1] + 1;
        while (end < cleaned.Length)
        {
            var c = cleaned[end];
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
            {
                break;
            }
            end++;
        }
        return (cleaned[start..end].Trim(), bestScore);
    }

    /// <summary>在 cues 裡找跟 text 最像的組合：對每個起點試 1～3 段連續 cue（以空白串接），
    /// 取相似度最高者；低於 threshold 或 Normalize(text) 少於 4 個字元時回傳 null。</summary>
    public static CaptionMatch? FindBest(IReadOnlyList<CaptionCue> cues, string text, double threshold = 0.35)
    {
        if (Normalize(text).Length < 4)
        {
            return null;
        }

        CaptionMatch? best = null;
        var bestScore = double.MinValue;
        for (var i = 0; i < cues.Count; i++)
        {
            var combined = cues[i].Text;
            var maxCount = Math.Min(MaxCueCount, cues.Count - i);
            for (var count = 1; count <= maxCount; count++)
            {
                if (count > 1)
                {
                    combined = string.Concat(combined, " ", cues[i + count - 1].Text);
                }
                var score = Similarity(combined, text);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new CaptionMatch(cues[i].StartMs, combined, score);
                }
            }
        }

        return bestScore >= threshold ? best : null;
    }

    /// <summary>把文字拆成字元 bigram 的多重集合計數。</summary>
    private static Dictionary<(char First, char Second), int> BuildBigrams(string text)
    {
        var bigrams = new Dictionary<(char, char), int>(text.Length - 1);
        for (var i = 0; i + 1 < text.Length; i++)
        {
            var pair = (text[i], text[i + 1]);
            bigrams.TryGetValue(pair, out var count);
            bigrams[pair] = count + 1;
        }
        return bigrams;
    }

    /// <summary>兩邊都已正規化的字元 bigram Dice 係數；任一邊少於 2 個字元時回傳 0。</summary>
    private static double DiceOfNormalized(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2)
        {
            return 0;
        }

        var bigramsA = BuildBigrams(a);
        var bigramsB = BuildBigrams(b);

        var intersection = 0;
        foreach (var (pair, count) in bigramsA)
        {
            if (bigramsB.TryGetValue(pair, out var other))
            {
                intersection += Math.Min(count, other);
            }
        }

        var total = a.Length - 1 + b.Length - 1;
        return 2.0 * intersection / total;
    }

    /// <summary>FindBest 的結果：組合起始的 StartMs、串接後的文字與相似度。</summary>
    public sealed record CaptionMatch(long StartMs, string Text, double Score);
}
