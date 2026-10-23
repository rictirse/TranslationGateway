using Translation.Core.Models;

namespace Translation.Bridge.Core.Interface;

public interface ITextProcessingService
{
    string PreProcess(string rawContent);
    /// <summary>
    /// 執行文本後處理（如繁簡轉換、標點修復等）
    /// </summary>
    string PostProcess(string translatedText, string originalText);
    TranslationValidationResult ValidateTranslation(string translatedText, string originalText);
    /// <summary>是否為不需翻譯的無意義輸入（空白、無任何文字字元、或單一字元大量重複）。</summary>
    bool ShouldSkipTranslation(string text);
}