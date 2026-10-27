using System.Text.RegularExpressions;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>組 system prompt 與單輪 user prompt 的靜態輔助元件。</summary>
public static class PromptBuilder
{
    /// <summary>PotPlayer 腳本連線測試送出的固定句子。</summary>
    public const string ProbeText = "Subtitle translation test. PotPlayer can support online translation.";

    /// <summary>預設 system prompt 模板；設定裡的模板是 null 或空白時改用這份。</summary>
    public const string DefaultSystemTemplate =
        "你是日文直播字幕翻譯器。把「Current」翻成適合台灣觀眾的繁體中文字幕。「Context」（或之前的對話）是前面幾句，只用來理解主詞、語氣和話題，不要翻譯。\n" +
        "原文是語音辨識的結果，常有同音錯字和不完整的斷句。\n" +
        "規則：\n" +
        "1. 只輸出 Current 的譯文，不要解釋、不要附原文、不要加註。\n" +
        "2. 口語、自然、符合台灣用法，只用繁體中文字。句尾不加句號（原文有「。」也一樣）；一句裡有好幾段時用空格或逗號隔開，不用句號。問號和驚嘆號用全形。\n" +
        "3. 無意義的口頭禪（あの、ええと、草、w）可以省略；整句只有反應詞（うん、へぇ、ね）時，翻成對應的短反應（嗯、喔、對吧）。\n" +
        "4. 人名、團名，以及遊戲的地圖、角色、道具、造型名稱：詞彙表有的一律照詞彙表；沒有的用台灣常見的中文譯名；都不確定就保留原文，不要自己拼成英文。\n" +
        "5. 疑似辨識錯字：只有詞彙表或情境能明確對上時，才當成正確的詞來翻（例如詞彙表有「黒波」，「黒並み」就當成「黒波」）；對不上就照字面翻。\n" +
        "6. 原文殘缺或看不懂時，只翻能確定的部分，寧可短，不要補內容。";

    /// <summary>批次（錄影預先翻譯）用的 system prompt 模板：每一行編號輸入對應一行編號輸出。</summary>
    public const string BatchSystemTemplate =
        "你是日文直播字幕翻譯器。把每一行編號的日文字幕翻成適合台灣觀眾的繁體中文字幕。\n" +
        "原文是語音辨識或自動字幕的結果，常有同音錯字和不完整的斷句。\n" +
        "規則：\n" +
        "1. 每一行輸入對應一行輸出，格式是「編號. 譯文」，編號和輸入相同，不可合併、拆分或跳過。\n" +
        "2. 只輸出譯文，不要解釋、不要附原文。\n" +
        "3. 口語、自然、符合台灣用法，只用繁體中文字。句尾不加句號（原文有「。」也一樣）；一行裡有好幾段時用空格或逗號隔開，不用句號。問號和驚嘆號用全形。\n" +
        "4. 無意義的口頭禪（あの、ええと、草、w）可以省略；整行只有反應詞（うん、へぇ、ね）時，翻成對應的短反應（嗯、喔、對吧）。\n" +
        "5. 人名、團名，以及遊戲的地圖、角色、道具、造型名稱：詞彙表有的一律照詞彙表；沒有的用台灣常見的中文譯名；都不確定就保留原文，不要自己拼成英文。\n" +
        "6. 疑似辨識錯字：只有詞彙表或情境能明確對上時，才當成正確的詞來翻；對不上就照字面翻。\n" +
        "7. 原文殘缺或看不懂時，只翻能確定的部分，寧可短，不要補內容。\n" +
        "8. 前後行是連續的對話，可以參考上下文理解語意，同一個詞前後要用同一種翻法。\n" +
        "9. 只輸出譯文，不要加任何括號說明、註解或「辨識不清」之類的備註；原文看不懂時照字面盡量翻，不要解釋。";

    /// <summary>批次回覆每一行的格式：「編號. 譯文」，編號後的分隔符號容許全形．、頓號、括號等變化。</summary>
    private static readonly Regex BatchReplyLine = new(@"^\s*(\d+)\s*[.．、)]\s*(.*)$", RegexOptions.Compiled);

    /// <summary>
    /// 組 system prompt：以 <see cref="TranslationSettings.SystemPromptTemplate"/> 為基礎
    /// （null 或空白時改用 <see cref="DefaultSystemTemplate"/>），依設定附加直播情境、
    /// 角色表參考資料與詞彙表段落；皆無時原樣回傳模板。
    /// </summary>
    /// <param name="glossarySection">生效中詞彙卡組出的段落；null 或空白時結果與不帶段落完全相同。</param>
    public static string BuildSystemPrompt(AppSettings settings, string? glossarySection = null) =>
        AppendReferenceSections(
            string.IsNullOrWhiteSpace(settings.Translation.SystemPromptTemplate)
                ? DefaultSystemTemplate
                : settings.Translation.SystemPromptTemplate,
            settings,
            glossarySection);

    /// <summary>
    /// 組批次（錄影預先翻譯）的 system prompt：與 <see cref="BuildSystemPrompt"/> 相同依序附加
    /// 直播情境、角色表與詞彙表段落，唯一的差別是基底改用 <see cref="BatchSystemTemplate"/>。
    /// </summary>
    public static string BuildBatchSystemPrompt(AppSettings settings, string? glossarySection = null) =>
        AppendReferenceSections(BatchSystemTemplate, settings, glossarySection);

    /// <summary>在模板之後依序附加直播情境、角色表與詞彙表段落（皆無時原樣回傳模板）。</summary>
    private static string AppendReferenceSections(string template, AppSettings settings, string? glossarySection)
    {
        var prompt = template;

        if (!string.IsNullOrWhiteSpace(settings.StreamSituation))
        {
            prompt += "\n\n# 直播情境（參考資料）\n" + settings.StreamSituation.Trim();
        }

        var characterLines = (settings.CharacterTable ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#"))
            .ToList();
        if (characterLines.Count > 0)
        {
            prompt += "\n\n# 角色表（參考資料）\n" + string.Join("\n", characterLines);
        }

        if (!string.IsNullOrWhiteSpace(glossarySection))
        {
            prompt += "\n\n" + glossarySection.Trim();
        }

        return prompt;
    }

    /// <summary>單輪模式：以 Context（只用來理解）加 Current 組出 user prompt；Context 沒有任何行時省略整個 Context 段落。</summary>
    /// <param name="contextTranslations">與 Context 等長的譯文清單；有譯文的行寫成「原文 → 譯文」，null 的行只寫原文。</param>
    public static string BuildSingleTurnUserPrompt(PotplayerRequest request, IReadOnlyList<string?>? contextTranslations = null)
    {
        var contextSection = string.Empty;
        if (request.Context.Length > 0)
        {
            var lines = request.Context
                .Select((line, i) =>
                    contextTranslations is not null && i < contextTranslations.Count && contextTranslations[i] is not null
                        ? $"{line} → {contextTranslations[i]}"
                        : line);
            contextSection =
                $"# Context（前面幾句，只用來理解，不要翻譯）\n" +
                $"{string.Join("\n", lines)}\n";
        }

        return
            contextSection +
            $"# Current（只翻譯這句）\n" +
            $"{request.Current}\n" +
            $"# Output（直接輸出繁體中文譯文）";
    }

    /// <summary>批次（錄影預先翻譯）的 user prompt：每行「編號. 原文」，用 \n 連接（編號從 1 開始）。</summary>
    public static string BuildBatchUserPrompt(IReadOnlyList<string> lines) =>
        string.Join("\n", lines.Select((line, i) => $"{i + 1}. {line}"));

    /// <summary>解析批次回覆：每一行「編號. 譯文」；譯文修剪後是空的不收，同一編號出現兩次只收第一次，
    /// 不符合格式的行略過。</summary>
    public static IReadOnlyDictionary<int, string> ParseBatchReply(string reply)
    {
        var result = new Dictionary<int, string>();
        foreach (var line in reply.Split('\n'))
        {
            var match = BatchReplyLine.Match(line);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var number))
            {
                continue;
            }

            var text = match.Groups[2].Value.Trim();
            if (text.Length == 0 || result.ContainsKey(number))
            {
                continue;
            }

            result[number] = text;
        }

        return result;
    }
}
