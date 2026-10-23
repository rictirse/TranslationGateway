using System.Globalization;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// YouTube 說明欄過濾：說明欄常有工商（贊助、PR）、原繪師、Live2D、BGM、規則、商品連結等內容，
/// 這些對開台資料探勘是雜訊。交給 GLM 前先去掉明顯的雜訊行、在分隔標記處截斷、限制長度，
/// 找出連動、合作對象的名字。
/// </summary>
public static class StreamDescriptionFilter
{
    /// <summary>遇到含這些標記的行起，後面全部丟掉（說明欄後半固定的版權、規則、連結區）。</summary>
    private static readonly string[] TruncateMarkers = ["ガイドライン", "配信のお約束", "注意事項", "お願い"];

    /// <summary>含這些關鍵字的行丟掉（不分大小寫）；PR 與パパ另以獨立出現判斷。</summary>
    private static readonly string[] DropKeywords =
    [
        "提供", "協賛", "スポンサー", "#PR", "案件", "タイアップ", "Sponsored", "sponsor",
        "ママ", "イラスト", "illust", "Live2D", "モデリング", "立ち絵", "デザイン",
        "BGM", "素材", "効果音", "サムネ", "thumbnail",
        "グッズ", "booth", "メンバーシップ", "メンバー限定", "membership", "スパチャ", "Super Chat",
        "Twitter", "ツイッター", "X(", "TikTok", "Instagram", "ファンアート", "切り抜き", "ハッシュタグ",
    ];

    /// <summary>過濾說明欄：依換行切行、修剪空白，遇到分隔標記整段截斷，再逐行丟掉雜訊，
    /// 剩下的行以 \n 連接並在 maxLength 截斷。輸入空白時回傳空字串。</summary>
    public static string Filter(string? description, int maxLength = 800)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var lines = description.Replace("\r\n", "\n").Split('\n').Select(raw => raw.Trim()).ToArray();
        var kept = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (IsTruncateMarker(line))
            {
                if (kept.Count > 0)
                {
                    break;
                }
                // 說明欄開頭就是規則區：跳到下一條裝飾分隔線（非空、但沒有文字內容的行）之後；
                // 找不到分隔線時只丟這一行，避免整段都沒了
                var separator = Array.FindIndex(lines, i + 1, l => l.Length > 0 && !HasTextContent(l));
                if (separator > 0)
                {
                    i = separator;
                }
                continue;
            }
            if (ShouldDropLine(line))
            {
                continue;
            }
            kept.Add(line);
        }

        var joined = string.Join("\n", kept);
        return joined.Length <= maxLength ? joined : joined[..maxLength];
    }

    /// <summary>這一行是否為分隔標記（版權、規則、連結區的開頭）。</summary>
    private static bool IsTruncateMarker(string line) => TruncateMarkers.Any(line.Contains);

    /// <summary>逐行丟掉：空行、含網址、只有符號／裝飾字元、含工商等關鍵字。</summary>
    private static bool ShouldDropLine(string line)
    {
        if (line.Length == 0)
        {
            return true;
        }
        if (line.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("www.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!HasTextContent(line))
        {
            return true;
        }
        return DropKeywords.Any(keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
            ContainsStandalone(line, "PR", char.IsAsciiLetter) ||
            ContainsStandalone(line, "パパ", IsKatakana);
    }

    /// <summary>行內是否還有字母、數字、假名、漢字等實際內容；修飾字母（「ﾟ」「゜」等裝飾用的結合字元）不算。</summary>
    private static bool HasTextContent(string line)
    {
        foreach (var c in line)
        {
            if (char.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>keyword（不分大小寫）是否在 line 中獨立出現：前後的字元都不是 isNeighbor 指定的種類
    /// （PR 前後不能是英文字母、パパ前後不能是片假名），避免誤殺 PROJECT、パパラッチ等一般詞。</summary>
    private static bool ContainsStandalone(string line, string keyword, Func<char, bool> isNeighbor)
    {
        var index = 0;
        while (index + keyword.Length <= line.Length)
        {
            var found = line.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }
            var beforeOk = found == 0 || !isNeighbor(line[found - 1]);
            var afterOk = found + keyword.Length == line.Length || !isNeighbor(line[found + keyword.Length]);
            if (beforeOk && afterOk)
            {
                return true;
            }
            index = found + keyword.Length;
        }
        return false;
    }

    /// <summary>全形片假名（U+30A0～U+30FF）與半形片假名（U+FF66～U+FF9F）。</summary>
    private static bool IsKatakana(char c) =>
        c is >= '\u30A0' and <= '\u30FF' or >= '\uFF66' and <= '\uFF9F';
}
