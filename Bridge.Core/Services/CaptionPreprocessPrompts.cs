namespace Translation.Bridge.Core.Services;

/// <summary>前處理要修正的名詞：字幕裡會出現的寫法、正確寫法與所屬詞彙卡名稱。</summary>
public sealed record PreprocessTerm(string Source, string Target, string CardName);

/// <summary>生成式字幕前處理的知識庫模型 prompt（架構師實測調校過的版本）。</summary>
public static class CaptionPreprocessPrompts
{
    public const string System = """
        你在整理日文直播存檔的 YouTube 自動字幕。自動字幕是照時間切的片段，常把一句話切成兩半，也有聽錯的詞。

        任務：
        1. 把「本批字幕」重新切成字幕用的短句：一句一個意思，一句最多 25 字；超過時在「。」「、」「ね」「よ」等自然停頓處切開。重複的語氣詞（そう。そう。）可以各自成句或併進相鄰的句子。
        2. 修正明顯聽錯的詞，特別是「專有名詞」列出的名詞：字幕裡發音相近的錯誤寫法要改成正確寫法。
        3. 刪掉 [音楽]、[拍手]、[笑い] 這類方括號標記；只有標記、沒有說話的片段不要輸出。
        4. 其他內容照原樣保留：不要改寫口語、不要摘要、不要翻譯、不要補上沒說的話。沒把握的詞維持原樣。

        每一句用 from、to 標出它由哪幾段組成（段落編號）：
        - from 必須是「本批字幕」的編號；句子依序排列。一段裡有兩句時，兩句可以共用同一個編號（後一句的 from 等於前一句的 to）。
        - 本批最後一句沒說完時，可以用「後文」的段落補完整（to 可以是後文的編號）。
        - 「前文」只給你參考，不要輸出。

        只輸出 JSON：
        {"sentences":[{"from":編號,"to":編號,"text":"整理後的日文"}]}
        """;

    /// <summary>組 user 訊息：專有名詞（空清單時整段省略）、前文（只供參考，空時省略）、本批字幕、後文（空時省略）。
    /// 編號是 cue 在整條字幕裡的索引（從 0 開始）；行與行之間用 \n 連接。</summary>
    public static string BuildUser(IReadOnlyList<PreprocessTerm> terms,
        IReadOnlyList<(int Index, string Text)> before,
        IReadOnlyList<(int Index, string Text)> main,
        IReadOnlyList<(int Index, string Text)> after)
    {
        var lines = new List<string>();

        if (terms.Count > 0)
        {
            lines.Add("## 專有名詞");
            foreach (var term in terms)
            {
                lines.Add($"{term.Source}（{term.Target}）— {term.CardName}");
            }
        }

        if (before.Count > 0)
        {
            lines.Add("## 前文（只供參考）");
            foreach (var cue in before)
            {
                lines.Add($"[{cue.Index}] {cue.Text}");
            }
        }

        lines.Add("## 本批字幕");
        foreach (var cue in main)
        {
            lines.Add($"[{cue.Index}] {cue.Text}");
        }

        if (after.Count > 0)
        {
            lines.Add("## 後文（只供補完最後一句）");
            foreach (var cue in after)
            {
                lines.Add($"[{cue.Index}] {cue.Text}");
            }
        }

        return string.Join("\n", lines);
    }
}
