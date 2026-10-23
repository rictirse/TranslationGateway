namespace Translation.Bridge.Core.Services;

/// <summary>找出兩段文字「被改掉的那一段」，用來把使用者在 Dashboard 修正的原文轉成原文修正規則。</summary>
public static class TextDiff
{
    /// <summary>
    /// 去掉共同的開頭與結尾，回傳 before 中被替換的片段（From）與 after 中對應的新片段（To）。
    /// 片段會往外擴展到完整的「詞」：片假名、漢字、英數字各自連續的一串（例如只改了長音也會取整個人名），
    /// 平假名沒有詞界，不擴展。兩者相同、或 before 沒有可被替換的片段時回傳 null（無法寫成「聽錯=正確」的規則）。
    /// </summary>
    public static (string From, string To)? ChangedSegment(string before, string after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal)) return null;

        var prefix = 0;
        var max = Math.Min(before.Length, after.Length);
        while (prefix < max && before[prefix] == after[prefix]) prefix++;

        var suffix = 0;
        while (suffix < max - prefix &&
               before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        // 擴展到詞界：共同前後綴裡與變動處相鄰、且同一種文字的字元一併納入
        var segmentEmpty = before.Length - prefix - suffix == 0;

        var leftClass = !segmentEmpty ? Classify(before[prefix])
            : prefix > 0 ? Classify(before[prefix - 1]) : Script.None;
        while (prefix > 0 && leftClass != Script.None && Classify(before[prefix - 1]) == leftClass)
        {
            prefix--;
        }

        var rightClass = !segmentEmpty ? Classify(before[before.Length - 1 - suffix])
            : suffix > 0 ? Classify(before[before.Length - suffix]) : Script.None;
        while (suffix > 0 && rightClass != Script.None && Classify(before[before.Length - suffix]) == rightClass)
        {
            suffix--;
        }

        var from = before.Substring(prefix, before.Length - prefix - suffix).Trim();
        var to = after.Substring(prefix, after.Length - prefix - suffix).Trim();

        return from.Length == 0 ? null : (from, to);
    }

    private enum Script { None, Katakana, Kanji, Latin }

    private static Script Classify(char c)
    {
        // 片假名（含長音ー、半形片假名）
        if (c is >= '゠' and <= 'ヿ' or >= 'ｦ' and <= 'ﾟ') return Script.Katakana;
        // 中日韓統一表意文字（含擴充 A）與「々」
        if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or '々') return Script.Kanji;
        // 半形與全形英數字
        if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
            or >= '０' and <= '９' or >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ')
        {
            return Script.Latin;
        }

        return Script.None;
    }
}
