using OpenCCNET;
using System.Text.RegularExpressions;
using Translation.Bridge.Core.Interface;
using Translation.Core.Models;

namespace Translation.Bridge.Core.Services;

public class TextProcessingService : ITextProcessingService
{
    public string PreProcess(string rawContent)
    {
        return rawContent;
    }

    public string PostProcess(string translatedText, string originalText)
    {
        if (string.IsNullOrWhiteSpace(translatedText)) return translatedText;

        var result = translatedText.Trim();

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