using OpenCCNET;
using System.Text.RegularExpressions;
using Translation.Bridge.Core.Interface;
using Translation.Core.Models;

namespace Translation.Bridge.Core.Services;

public class TextProcessingService : ITextProcessingService
{
    /// <summary>註解標記（括號內開頭、後接冒號即視為註解）：繁簡與英文大小寫的常見寫法。</summary>
    private const string NoteMarkers = @"(?:譯者註|譯註|译注|註|注|備註|备注|說明|说明|NOTE|Note|note|TN)";

    /// <summary>說明關鍵字：括號內容含任一個就視為模型自己加的說明（唱歌段落常見，繁簡都認得）。</summary>
    private static readonly string[] ExplanationKeywords =
    [
        "辨識", "辨认", "識別", "识别", "不清", "疑似", "原文", "斷句", "断句",
        "指代", "意指", "可能是", "可能指", "聽不清", "听不清", "不明"
    ];

    /// <summary>第一層括號（整段移除時含括號前的空白）：全形（…）或半形 (...)，
    /// 內容不含同類括號（巢狀的「」『』不影響判斷）；括號未閉合而延伸到字串結尾（\z）也算。</summary>
    private static readonly Regex ParenthesesRegex = new(
        @"\s*[（(][^（）()]*[）)]|\s*[（(][^（）()]*\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>括號內容開頭是註解標記加冒號（T-2102 的「（註：…）」）。</summary>
    private static readonly Regex NoteMarkerWithColonRegex = new(
        @"^\s*" + NoteMarkers + @"\s*[:：]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>詞義註解用 / 或 ／ 分隔後的每一段：只由中日文字（CJK 漢字、平假名、片假名）組成。</summary>
    private static readonly Regex CjkWordRegex = new(
        @"^[\u3400-\u4dbf\u4e00-\u9fff\u3040-\u309f\u30a0-\u30ff]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>括號內容的分類：Normal 保留，其他四種整段移除；
    /// MarksOnly／Explanation 在整句移除後變空時改回省略號（不把說明秀在字幕上）。</summary>
    private enum ParenthesisKind
    {
        Normal,
        Note,
        MarksOnly,
        Explanation,
        Gloss
    }

    /// <summary>移除模型自己加的說明括號（（註：…）、（？）、（歌詞辨識不清）、（搬運/舉起）等）；
    /// 移除後連續空白合併成一個、去頭尾空白。整句移除後變空時保留移除前的文字（不能讓字幕消失），
    /// 但整句只有（？）這類符號或說明文字時回傳「…」。不是說明的括號一律保留。</summary>
    private static string RemoveNoteParentheses(string text)
    {
        if (!ParenthesesRegex.IsMatch(text))
        {
            return text;
        }

        var removedMarksOrExplanations = false;
        var removed = ParenthesesRegex.Replace(text, match =>
        {
            var kind = ClassifyParenthesis(ExtractContent(match.Value));
            if (kind == ParenthesisKind.Normal)
            {
                return match.Value;
            }

            if (kind is ParenthesisKind.MarksOnly or ParenthesisKind.Explanation)
            {
                removedMarksOrExplanations = true;
            }

            return string.Empty;
        });

        removed = Regex.Replace(removed, @"\s+", " ").Trim();
        if (removed.Length > 0)
        {
            return removed;
        }

        return removedMarksOrExplanations ? "…" : text;
    }

    /// <summary>取出括號配對的內容（去掉前導空白、括號本身；未閉合時取到字串結尾）。</summary>
    private static string ExtractContent(string parenthesis)
    {
        var start = parenthesis.IndexOfAny(['（', '(']) + 1;
        var end = parenthesis.LastIndexOfAny(['）', ')']);
        return end >= start ? parenthesis[start..end] : parenthesis[start..];
    }

    /// <summary>判斷括號內容屬於哪一種：註解標記加冒號、只有問號／省略號、含說明關鍵字、
    /// 或中日文詞語用 / 隔開的詞義註解（有英數字的不算，例如（AC/DC））。</summary>
    private static ParenthesisKind ClassifyParenthesis(string content)
    {
        if (content.Length == 0)
        {
            return ParenthesisKind.Normal;
        }

        if (NoteMarkerWithColonRegex.IsMatch(content))
        {
            return ParenthesisKind.Note;
        }

        if (IsOnlyUnclearMarks(content))
        {
            return ParenthesisKind.MarksOnly;
        }

        if (ExplanationKeywords.Any(content.Contains))
        {
            return ParenthesisKind.Explanation;
        }

        return IsSlashGloss(content) ? ParenthesisKind.Gloss : ParenthesisKind.Normal;
    }

    /// <summary>內容去空白後只有 ?、？、…、... 的組合（例：（？）、(?)、（…？））。</summary>
    private static bool IsOnlyUnclearMarks(string content)
    {
        var compact = content.Where(c => !char.IsWhiteSpace(c)).ToList();
        return compact.Count > 0 && compact.All(c => c is '?' or '？' or '…' or '.');
    }

    /// <summary>詞義註解：以 / 或 ／ 分隔，每段去空白後都只由中日文字組成且 1～6 字
    /// （例：（搬運/舉起）、（視角/觀點））；段裡有英文字母或數字的不算（例：（AC/DC））。</summary>
    private static bool IsSlashGloss(string content)
    {
        if (!content.Contains('/') && !content.Contains('／'))
        {
            return false;
        }

        return content.Split('/', '／').All(segment =>
        {
            var trimmed = segment.Trim();
            return trimmed.Length is >= 1 and <= 6 && CjkWordRegex.IsMatch(trimmed);
        });
    }

    public string PreProcess(string rawContent)
    {
        return rawContent;
    }

    public string PostProcess(string translatedText, string originalText)
    {
        if (string.IsNullOrWhiteSpace(translatedText)) return translatedText;

        var result = translatedText.Trim();

        // 模型自己加的註解（（註：…））不能出現在字幕上：在繁簡轉換之前移除（簡體標記也要認得）
        result = RemoveNoteParentheses(result);

        if (Regex.IsMatch(translatedText, @"[\u4e00-\u9fa5]"))
        {
            result = result.ToTWFromHans(true);
        }

        result = result.Trim();

        // 字幕句尾不加句號，模型常照抄原文的「。」，所以在這裡統一去掉結尾連續的全形句號；只動結尾，半形「.」、「…」、「？」「！」與收尾括號前的「。」保持原樣。
        var withoutTrailingPeriod = result.TrimEnd('。', '．').Trim();
        if (withoutTrailingPeriod.Length > 0)
        {
            result = withoutTrailingPeriod;
        }

        return result;
    }
    public TranslationValidationResult ValidateTranslation(string translatedText, string originalText)
    {
        if (string.IsNullOrWhiteSpace(translatedText))
            return new TranslationValidationResult(false, "翻譯內容為空", 0);

        if (string.IsNullOrWhiteSpace(originalText))
            return new TranslationValidationResult(false, "原文內容為空", 0);

        // 1. 字數比例檢查
        double ratio = (double)translatedText.Length / originalText.Length;
        bool isLengthValid = ratio > 0.3 && ratio < 3.0;

        // 2. 行數匹配率檢查 (進階檢查)
        // 檢查原文有內容的行數 vs 翻譯後有內容的行數
        var originalLineCount = originalText.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l));
        var translatedLineCount = translatedText.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l));

        // 如果原文有很多行但翻出來只有一兩行，通常是有漏譯
        bool isLineCountValid = true;
        if (originalLineCount > 5) // 小段落不檢查行數比
        {
            double lineRatio = (double)translatedLineCount / originalLineCount;
            isLineCountValid = lineRatio > 0.6; // 至少要翻出 60% 的行數
        }

        if (!isLengthValid)
            return new TranslationValidationResult(false, $"字數比例異常 ({ratio:P2})", ratio);

        if (!isLineCountValid)
            return new TranslationValidationResult(false, $"行數嚴重缺失 (原文 {originalLineCount} 行 / 翻譯 {translatedLineCount} 行)", ratio);

        return new TranslationValidationResult(true, "驗證通過", ratio);
    }

    public bool ShouldSkipTranslation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        var trimmed = text.Trim();

        // 只有符號、標點、音符、數字、空白，沒有任何文字字元
        if (!trimmed.Any(char.IsLetter)) return true;

        // 單一字元連續重複 8 次（含）以上，視為雜訊
        return trimmed.Length >= 8 && trimmed.All(c => c == trimmed[0]);
    }
}