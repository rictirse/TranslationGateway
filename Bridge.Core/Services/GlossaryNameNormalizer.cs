using System.Text;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 詞彙卡名稱的正規化（純函式）：抹平全形半形、大小寫、平假名片假名與空白、長音、標點等寫法差異，
/// 供資料探勘比對「不同寫法其實是同一個名稱」使用。
/// </summary>
public static class GlossaryNameNormalizer
{
    /// <summary>要去除的字元：空白、中黑點、長音符號、波狀線與常見標點、度數符號。</summary>
    private static readonly HashSet<char> RemovedChars =
    [
        ' ', '\t', '\r', '\n', '\u3000',
        '・', '･', 'ー', '〜', '~',
        '!', '！', '?', '？', '.', ',', '、', '。', '\'', '"', '「', '」', '『', '』', '(', ')', '（', '）', '-', '_', '/', '°',
    ];

    /// <summary>正規化：修剪 → NFKC（全形半形統一）→ 轉小寫 → 平假名轉片假名 → 去除空白與標點。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (RemovedChars.Contains(c))
            {
                continue;
            }
            builder.Append(ToKatakana(c));
        }
        return builder.ToString();
    }

    /// <summary>兩個名稱正規化後是否相等；空字串（正規化後沒有內容）不算相同。</summary>
    public static bool SameName(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        return left.Length > 0 && string.Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>結尾稱謂清單，由長到短排列（比對時只去掉一個最長的結尾稱謂）。</summary>
    private static readonly string[] HonorificSuffixes =
    [
        "せんぱい", "センパイ",
        "ちゃん", "チャン",
        "先輩", "さん", "サン", "くん", "クン", "さま", "たん", "先生",
        "君", "様", "氏", "殿", "醬", "酱",
    ];

    /// <summary>去掉結尾的稱謂（先輩、ちゃん、さん等）；去掉後少於 2 個字時回傳原字串（修剪後）。</summary>
    public static string StripHonorific(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        foreach (var suffix in HonorificSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal) && trimmed.Length - suffix.Length >= 2)
            {
                return trimmed[..^suffix.Length];
            }
        }
        return trimmed;
    }

    /// <summary>平假名（ぁ～ゖ）轉片假名；其他字元原樣回傳。</summary>
    private static char ToKatakana(char c) =>
        c is >= '\u3041' and <= '\u3096' ? (char)(c + 0x60) : c;
}
