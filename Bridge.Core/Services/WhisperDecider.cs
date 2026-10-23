using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>依影片類型決定 PotPlayer Whisper 的開關與語言：直播開（沒有字幕可用）、有日文字幕的錄影關（由 Gateway 的字幕接手）。</summary>
public static class WhisperDecider
{
    /// <summary>Whisper 的決策結果：Enabled 是開關、LanguageIndex 是 WhisperLanguages 的索引、Reason 是原因（記錄與訊息用）。</summary>
    public sealed record WhisperPlan(bool Enabled, int LanguageIndex, string Reason);

    /// <summary>從文字偵測 Whisper 語言：有假名（平假名／片假名）→ 日文，否則有韓文字 → 韓文，都沒有 → 日文。</summary>
    public static int DetectLanguage(params string?[] texts)
    {
        var hasHangul = false;
        foreach (var text in texts)
        {
            if (text is null)
            {
                continue;
            }

            foreach (var c in text)
            {
                if (c is >= '\u3040' and <= '\u30FF')
                {
                    return WhisperLanguages.Japanese;
                }

                if ((c >= '\uAC00' && c <= '\uD7AF') || (c >= '\u1100' && c <= '\u11FF'))
                {
                    hasHangul = true;
                }
            }
        }

        return hasHangul ? 1 : WhisperLanguages.Japanese;
    }

    /// <summary>直播：沒有字幕可用，開 Whisper，語言從標題與說明欄偵測。</summary>
    public static WhisperPlan ForLive(string? title, string? description) =>
        new(true, DetectLanguage(title, description), "直播中");

    /// <summary>YouTube 錄影：有日文字幕軌就關 Whisper（由 Gateway 的字幕、前處理和推送接手），否則照開；
    /// 語言優先用生成式字幕軌的語言，沒有時從標題與說明欄偵測。</summary>
    public static WhisperPlan ForVod(YouTubePageInfo page)
    {
        var hasJapaneseCaptions = page.CaptionLanguages.Any(code =>
            code == "ja" || code.StartsWith("ja-", StringComparison.Ordinal));
        return new WhisperPlan(
            !hasJapaneseCaptions,
            WhisperLanguages.IndexOf(page.AutoCaptionLanguage) ?? DetectLanguage(page.Title, page.Description),
            hasJapaneseCaptions ? "有日文字幕，改用 YouTube 字幕" : "錄影沒有日文字幕");
    }
}
