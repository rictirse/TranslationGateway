using System.Globalization;

namespace Translation.Bridge.Core.Services;

/// <summary>PotPlayer Whisper 語言下拉選單（WhisperDllLang）的索引與 ISO 代碼對照；0 是 Auto。</summary>
public static class WhisperLanguages
{
    public const int Auto = 0;

    public const int Japanese = 3;

    /// <summary>索引對齊 PotPlayer 下拉選單的代碼陣列（0 auto、1 ko、2 en、3 ja …）。</summary>
    private static readonly string[] Codes =
    [
        "auto", "ko", "en", "ja", "zh", "de", "es", "ru", "fr", "pt", "tr", "pl", "ca", "nl", "ar", "sv",
        "it", "id", "hi", "fi", "vi", "he", "uk", "el", "ms", "cs", "ro", "da", "hu", "ta", "no",
        "th", "ur", "hr", "bg", "yue", "lt", "la", "mi", "ml", "cy", "sk", "te", "fa", "lv", "bn",
        "sr", "az", "sl", "kn", "et", "mk", "br", "eu", "is", "hy", "ne", "mn", "bs", "kk", "sq",
        "sw", "gl", "mr", "pa", "si", "km", "sn", "yo", "so", "af", "oc", "ka", "be", "tg", "sd",
        "gu", "am", "yi", "lo", "uz", "fo", "ht", "ps", "tk", "nn", "mt", "sa", "lb", "my", "bo",
        "tl", "mg", "as", "tt", "haw", "ln", "ha", "ba", "jw", "su",
    ];

    /// <summary>把語言代碼（如 ja-JP）換成下拉選單索引；找不到或輸入是空的時回傳 null（不回傳 0）。</summary>
    public static int? IndexOf(string? languageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
        {
            return null;
        }

        var code = languageCode.ToLowerInvariant().Trim();
        if (code.Length == 0)
        {
            return null;
        }

        var separator = code.IndexOf('-');
        var primary = separator < 0 ? code : code[..separator];
        var normalized = code switch
        {
            "yue" or "zh-yue" => "yue",
            "iw" => "he",
            "jv" => "jw",
            "nb" => "no",
            _ => code.StartsWith("zh-", StringComparison.Ordinal) ? "zh" : primary,
        };

        var index = Array.IndexOf(Codes, normalized);
        return index >= 0 ? index : null;
    }

    /// <summary>回傳索引對應的代碼（例如 3 → ja）；超出範圍時回傳索引的數字字串。</summary>
    public static string NameOf(int index) =>
        index >= 0 && index < Codes.Length ? Codes[index] : index.ToString(CultureInfo.InvariantCulture);
}
