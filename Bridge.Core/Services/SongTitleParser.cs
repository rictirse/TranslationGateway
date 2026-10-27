using System.Text.RegularExpressions;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>從章節標題（セトリ的一行）拆出曲名與歌手：去掉開頭的名次／編號，
/// 在最後一個分隔符切開曲名／歌手，再去掉曲名前後的括號。</summary>
public static class SongTitleParser
{
    /// <summary>開頭的名次：第 10 位、10位、3名（數字前後允許空白）。</summary>
    private static readonly Regex RankPrefix = new(@"^\s*(?:第\s*)?\d+\s*(?:位|名)\s*", RegexOptions.Compiled);

    /// <summary>開頭的編號：1.、2)、3：、4、（數字後允許空白）。</summary>
    private static readonly Regex NumberPrefix = new(@"^\s*\d+\s*[.)、:：]\s*", RegexOptions.Compiled);

    /// <summary>開頭的編號（No.10、NO 3），不分大小寫。</summary>
    private static readonly Regex NoPrefix = new(@"^\s*No\.?\s*\d+\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>曲名／歌手的分隔符；半形斜線與全形橫線前後不要求空白，其餘（半形橫線類）前後要有空白
    /// （`n-buna` 這種緊貼的半形連字號不是分隔）。</summary>
    private static readonly string[] Separators = ["／", " / ", "/", " - ", " – ", " — ", "－"];

    /// <summary>曲名前要去掉的開括號（與 <see cref="CloseBrackets"/> 成對使用）。</summary>
    private const string OpenBrackets = "「『【“\"";

    /// <summary>曲名後要去掉的閉括號。</summary>
    private const string CloseBrackets = "」』】”\"";

    /// <summary>從章節標題拆曲名／歌手；拆不出曲名（空白）回 null。</summary>
    public static SongQuery? Parse(string chapterTitle)
    {
        var text = RankPrefix.Replace(chapterTitle, string.Empty);
        text = NumberPrefix.Replace(text, string.Empty);
        text = NoPrefix.Replace(text, string.Empty);

        // 取最後一個出現的分隔符切開：之後的章節（談話）不會再出現「曲名／歌手」
        var separatorIndex = -1;
        var separatorLength = 0;
        foreach (var separator in Separators)
        {
            var index = text.LastIndexOf(separator, StringComparison.Ordinal);
            if (index > separatorIndex)
            {
                separatorIndex = index;
                separatorLength = separator.Length;
            }
        }

        var title = text;
        string? artist = null;
        if (separatorIndex >= 0)
        {
            title = text[..separatorIndex];
            artist = text[(separatorIndex + separatorLength)..];
        }

        title = StripBrackets(title.Trim());
        if (title.Length == 0)
        {
            return null;
        }

        var artistText = artist?.Trim();
        return new SongQuery(title, artistText is { Length: > 0 } ? artistText : null);
    }

    /// <summary>去掉曲名前後成對的括號（「」『』【】“”）。</summary>
    private static string StripBrackets(string title)
    {
        while (title.Length >= 2 &&
               OpenBrackets.Contains(title[0]) &&
               CloseBrackets.Contains(title[^1]))
        {
            title = title[1..^1];
        }

        return title;
    }
}
