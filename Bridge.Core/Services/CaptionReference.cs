using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 翻譯時的 YouTube 字幕參考（DI singleton）：以播放位置在時間範圍內對照最像的 YouTube 字幕，
/// 夠像時組成 user prompt 的參考段落，讓模型在 Whisper 明顯聽錯時採用 YouTube 的寫法。
/// 沒有播放位置時沿用上次對到的位置附近找；從來沒對到過則提高門檻在全片找。
/// </summary>
public sealed class CaptionReference
{
    /// <summary>有播放位置時的搜尋範圍：往回 15 秒。</summary>
    private const long PositionBeforeMs = 15_000;

    /// <summary>有播放位置時的搜尋範圍：往後 3 秒。</summary>
    private const long PositionAfterMs = 3_000;

    /// <summary>沒有播放位置、沿用上次對到位置時的搜尋範圍：往回 30 秒。</summary>
    private const long LastMatchBeforeMs = 30_000;

    /// <summary>沒有播放位置、沿用上次對到位置時的搜尋範圍：往後 60 秒。</summary>
    private const long LastMatchAfterMs = 60_000;

    /// <summary>一般比對門檻。</summary>
    private const double Threshold = 0.35;

    /// <summary>從來沒對到過、在全片找時的提高門檻（避免短句在全片誤配）。</summary>
    private const double GlobalThreshold = 0.6;

    /// <summary>裁切後分數達此值以上視為和本句幾乎相同：沒有要修正的地方，不放參考。</summary>
    private const double NearIdenticalThreshold = 0.9;

    /// <summary>全片找時 Normalize(current) 至少要有的字元數。</summary>
    private const int GlobalMinLength = 8;

    private readonly CaptionTrackState _captions;
    private readonly IPlaybackClock? _clock;
    private readonly ILogger<CaptionReference>? _logger;
    private readonly object _lock = new();
    private long? _lastMatchMs;

    public CaptionReference(CaptionTrackState captions, IPlaybackClock? clock, ILogger<CaptionReference>? logger = null)
    {
        _captions = captions;
        _clock = clock;
        _logger = logger;
        // 換影片或清空字幕時，上一次對到的位置一併重置
        captions.Changed += Reset;
    }

    /// <summary>組本句的 YouTube 字幕參考段落；沒載入字幕或對不到時回傳空字串。</summary>
    public string BuildUserPromptSection(string current)
    {
        if (!_captions.IsLoaded)
        {
            return string.Empty;
        }

        CaptionMatcher.CaptionMatch? match;
        double threshold;
        var position = _clock?.GetPositionMs();
        long? lastMatch;
        lock (_lock)
        {
            lastMatch = _lastMatchMs;
        }

        if (position is { } now)
        {
            threshold = Threshold;
            match = CaptionMatcher.FindBest(
                _captions.GetRange(now - PositionBeforeMs, now + PositionAfterMs), current, Threshold);
        }
        else if (lastMatch is { } last)
        {
            threshold = Threshold;
            match = CaptionMatcher.FindBest(
                _captions.GetRange(last - LastMatchBeforeMs, last + LastMatchAfterMs), current, Threshold);
        }
        else
        {
            // 從來沒對到過：在全部 cues 裡找，但門檻提高、原文要夠長，避免短句在全片誤配
            if (CaptionMatcher.Normalize(current).Length < GlobalMinLength)
            {
                return string.Empty;
            }
            threshold = GlobalThreshold;
            match = CaptionMatcher.FindBest(_captions.Cues, current, GlobalThreshold);
        }

        if (match is null)
        {
            return string.Empty;
        }

        // 裁切到只跟本句對到的那一小段（cue 是捲動片段，常含前一句或後一句的內容）
        var (trimmed, score) = CaptionMatcher.TrimToMatch(match.Text, current);
        if (score < threshold)
        {
            // 裁切後不夠像：視為沒對到（不更新上次對到的位置）
            return string.Empty;
        }

        lock (_lock)
        {
            _lastMatchMs = match.StartMs;
        }

        if (score >= NearIdenticalThreshold)
        {
            _logger?.LogDebug("YouTube 字幕參考：略過，和本句幾乎相同（{Score:0.00}）", score);
            return string.Empty;
        }

        _logger?.LogInformation("YouTube 字幕參考：{Score:0.00}「{Text}」", score, trimmed);
        return $"# 參考：同一時段的 YouTube 自動字幕（只用來修正本句聽錯的詞；不要翻譯、不要輸出這段）\n{trimmed}";
    }

    /// <summary>清掉上次對到的位置（換影片或重配字幕時由 CaptionTrackState.Changed 呼叫）。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _lastMatchMs = null;
        }
    }
}
