using System.Text;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>聽錯字探勘的 GLM prompt：從最近字幕找出 Whisper 把專有名詞聽錯的寫法。</summary>
public static class MishearingPrompts
{
    public const string System = """
        你在校對日文直播的 Whisper 語音辨識字幕。
        下面列出這場直播會出現的專有名詞（名詞 Id、正確寫法、中文譯名、所屬詞彙卡）。

        任務：找出字幕中其實是在說這些名詞、卻被辨識成別的字的寫法，例如：
        - 同音的漢字
        - 平假名或片假名寫法不同
        - 長音或促音漏掉
        - 被切成兩段

        規則：
        - wrong 必須是字幕中原樣出現的字串。
        - evidence 必須原樣複製那一句字幕。
        - 如果那個字在句子裡是一般的日文用法（例如真的在說「作り」），不要提出。
        - 沒把握就不要提出；沒有發現時回 {"fixes":[]}。

        只輸出 JSON：
        {"fixes":[{"termId":數字,"wrong":"…","evidence":"…"}]}
        """;

    /// <summary>組探勘的 user 訊息：情境（空白時省略整段）、名詞清單與最近字幕（舊到新）。</summary>
    public static string BuildUser(string? situation, IReadOnlyList<MishearingTermInput> terms, IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        var situationText = (situation ?? string.Empty).Trim();
        if (situationText.Length > 0)
        {
            builder.AppendLine("## 情境");
            builder.AppendLine(situationText);
            builder.AppendLine();
        }

        builder.AppendLine("## 名詞");
        foreach (var term in terms)
        {
            builder.Append($"[{term.TermId}] {term.Source}（{term.Target}）— {term.CardName}");
            var aliases = AliasList(term.Aliases);
            if (aliases.Count > 0)
            {
                builder.Append($"；別名：{string.Join("、", aliases)}");
            }
            builder.AppendLine();
        }
        builder.AppendLine();

        builder.AppendLine("## 最近字幕（舊到新）");
        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }
        return builder.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>Aliases 每行一個，去頭尾空白後略過空行。</summary>
    internal static List<string> AliasList(string? aliases)
    {
        if (string.IsNullOrEmpty(aliases))
        {
            return [];
        }
        return aliases
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }
}
