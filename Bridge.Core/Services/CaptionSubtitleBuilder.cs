using System.Text;
using System.Text.RegularExpressions;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>Gateway 字幕軌的一行：一個 cue 就是一整句（前處理句子或程式合併的整句）。</summary>
/// <param name="StartMs">開始毫秒。</param>
/// <param name="EndMs">結束毫秒（已裁到下一行的 StartMs，且至少 StartMs + 500）。</param>
/// <param name="Text">句子文字。</param>
/// <param name="FromSentence">true 表示來自前處理句子（caption_sentences）；false 表示由程式合併原始 cue。</param>
public sealed record SubtitleLine(long StartMs, long EndMs, string Text, bool FromSentence);

/// <summary>疊加在一般字幕上的 cue（章節名稱等）：內容原樣寫入 SRT、不經 CueText 清理；換行用 \n 表示。</summary>
/// <param name="StartMs">開始毫秒。</param>
/// <param name="EndMs">結束毫秒。</param>
/// <param name="Text">cue 內容（含 {\an7\fs12} 這類標記）。</param>
public sealed record SubtitleOverlay(long StartMs, long EndMs, string Text);

/// <summary>組「一個 cue 一整句」的字幕行並輸出成 WebVTT：前處理過的時段直接用前處理句子，
/// 沒涵蓋的時段由程式把原始滾動 cue 合併成整句（原始 cue 文字彼此不重疊，接起來就是完整文字）。</summary>
public static class CaptionSubtitleBuilder
{
    /// <summary>句尾符號：文字裡遇到時判斷是否在符號之後斷句。</summary>
    private const string SentenceEndSymbols = "。？！?!";

    /// <summary>含符號不到這個長度不在句尾符號後斷句，避免「うん。」「何?」單獨一行。</summary>
    private const int MinSentenceLength = 8;

    /// <summary>這一句已經達到這個長度時，在 cue 邊界斷句。</summary>
    private const int MaxSentenceLength = 40;

    /// <summary>前後兩段 cue 的 StartMs 相差達到這個值時，在 cue 邊界斷句。</summary>
    private const long CueGapMs = 3000;

    /// <summary>每一行最少顯示的毫秒數。</summary>
    private const long MinLineDurationMs = 500;

    private static readonly Regex BracketTags = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    /// <summary>把原始字幕與前處理句子組成字幕行：前處理句子優先，沒被涵蓋的原始 cue 依序合併成整句。</summary>
    public static IReadOnlyList<SubtitleLine> Build(IReadOnlyList<CaptionCue> cues, IReadOnlyList<CaptionSentence> sentences)
    {
        var lines = new List<SubtitleLine>();

        // 1. 前處理句子：一句一行（修剪空白後是空字串的略過）
        foreach (var sentence in sentences)
        {
            var text = sentence.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            lines.Add(new SubtitleLine(sentence.StartMs, sentence.EndMs, text, FromSentence: true));
        }

        // 2. cue 的 StartMs 落在任何一句的 [StartMs, EndMs]（含兩端）內就算已涵蓋；
        //    cue 依 StartMs 排序後，連續沒被涵蓋的分成一組各自合併
        List<CaptionCue>? group = null;
        foreach (var cue in cues.OrderBy(c => c.StartMs))
        {
            if (sentences.Any(s => cue.StartMs >= s.StartMs && cue.StartMs <= s.EndMs))
            {
                if (group is not null)
                {
                    MergeGroup(group, lines);
                    group = null;
                }
            }
            else
            {
                (group ??= []).Add(cue);
            }
        }

        if (group is not null)
        {
            MergeGroup(group, lines);
        }

        // 4. 兩種行合在一起依 StartMs 排序；相同時前處理句子排在前面（OrderBy 是穩定排序）
        var sorted = lines.OrderBy(l => l.StartMs).ThenBy(l => l.FromSentence ? 0 : 1).ToList();

        // 5. 結束時間：裁到下一行的 StartMs，但至少 StartMs + 500
        var result = new List<SubtitleLine>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            var endMs = i + 1 < sorted.Count ? Math.Min(sorted[i].EndMs, sorted[i + 1].StartMs) : sorted[i].EndMs;
            result.Add(sorted[i] with { EndMs = Math.Max(endMs, sorted[i].StartMs + MinLineDurationMs) });
        }

        return result;
    }

    /// <summary>cue 單行文字的輸出格式：換行換成空白、避免出現 WebVTT／SRT 的時間分隔符「-->」。</summary>
    public static string CueText(string text) =>
        text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace("-->", "->");

    /// <summary>把字幕行輸出成 WebVTT 文字（第一行 WEBVTT、cue 之間空一行、換行用 \n）。</summary>
    public static string ToWebVtt(IReadOnlyList<SubtitleLine> lines)
    {
        var builder = new StringBuilder("WEBVTT\n");
        foreach (var line in lines)
        {
            builder.Append('\n');
            builder.Append(FormatTimestamp(line.StartMs)).Append(" --> ").Append(FormatTimestamp(line.EndMs)).Append('\n');
            builder.Append(CueText(line.Text));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>把章節清單組成疊加 cue：每章一條，結束時間是 min(下一章的開始（最後一章用 <paramref name="videoEndMs"/>）,
    /// 開始＋原曲長度＋5 秒收尾)；查不到原曲長度（<see cref="ChapterOverlayItem.DurationMs"/> 是 null）的章節顯示
    /// <paramref name="fallbackDurationMs"/>（≤ 0 視為 5 分鐘，不加收尾秒數）。
    /// 內容以 {\an7\fs&lt;fontSize&gt;} 開頭（PotPlayer 會固定顯示在左上角）只放日文原文——
    /// PotPlayer 把這條 cue 送來翻譯時，TranslationManager 會直接回傳 ChapterOverlayState 已翻好的中文，
    /// 由 PotPlayer 照平常的方式顯示「中文在上、日文在下」。</summary>
    public static IReadOnlyList<SubtitleOverlay> BuildChapterOverlays(
        IReadOnlyList<ChapterOverlayItem> items, long videoEndMs, int fontSize, long fallbackDurationMs)
    {
        if (fontSize <= 0)
        {
            fontSize = 12;
        }

        if (fallbackDurationMs <= 0)
        {
            fallbackDurationMs = 300000;
        }

        var overlays = new List<SubtitleOverlay>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            // 唱完後的聊天不掛歌名：查到原曲長度就多留 5 秒收尾，查不到用 fallback
            var displayMs = items[i].DurationMs is { } durationMs ? durationMs + 5000 : fallbackDurationMs;
            var endMs = i + 1 < items.Count ? items[i + 1].StartMs : videoEndMs;
            endMs = Math.Min(endMs, items[i].StartMs + displayMs);
            if (endMs <= items[i].StartMs)
            {
                endMs = items[i].StartMs + 60000;
            }

            overlays.Add(new SubtitleOverlay(items[i].StartMs, endMs, $"{{\\an7\\fs{fontSize}}}{items[i].Title}"));
        }

        return overlays;
    }

    /// <summary>把字幕行輸出成 SRT 文字（編號從 1 開始、cue 之間空一行、換行用 \r\n、時間用逗號）。</summary>
    public static string ToSrt(IReadOnlyList<SubtitleLine> lines) => ToSrt(lines, []);

    /// <summary>把字幕行與疊加 cue 輸出成 SRT 文字：依開始時間合併排序（同時間一般字幕在前）、重新編號；
    /// 一般字幕照舊經 <see cref="CueText"/>，疊加 cue 原樣輸出（換行的 \n 轉成 \r\n）。</summary>
    public static string ToSrt(IReadOnlyList<SubtitleLine> lines, IReadOnlyList<SubtitleOverlay> overlays)
    {
        var entries = new List<(long StartMs, bool IsOverlay, SubtitleLine? Line, SubtitleOverlay? Overlay)>(lines.Count + overlays.Count);
        foreach (var line in lines)
        {
            entries.Add((line.StartMs, IsOverlay: false, line, null));
        }

        foreach (var overlay in overlays)
        {
            entries.Add((overlay.StartMs, IsOverlay: true, null, overlay));
        }

        var builder = new StringBuilder();
        var index = 0;
        foreach (var entry in entries.OrderBy(e => e.StartMs).ThenBy(e => e.IsOverlay))
        {
            index++;
            if (index > 1)
            {
                builder.Append("\r\n");
            }

            var (startMs, endMs, text) = entry.IsOverlay
                ? (entry.Overlay!.StartMs, entry.Overlay.EndMs, entry.Overlay.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"))
                : (entry.Line!.StartMs, entry.Line.EndMs, CueText(entry.Line.Text));
            builder.Append(index).Append("\r\n");
            builder.Append(FormatSrtTimestamp(startMs)).Append(" --> ").Append(FormatSrtTimestamp(endMs)).Append("\r\n");
            builder.Append(text);
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>把一組連續沒被前處理句子涵蓋的 cue 合併成整句。</summary>
    private static void MergeGroup(List<CaptionCue> group, List<SubtitleLine> lines)
    {
        var builder = new StringBuilder();
        long startMs = 0;
        long endMs = 0;
        long previousStartMs = 0;
        var hasPreviousCue = false;

        // 這一句是否由句尾符號斷句後的殘段開始：殘句要接下一個句尾符號才收，
        // 不適用前後 cue 相差的時間距離（滾動字幕前後 cue 本來就差好幾秒，會把殘句切碎）
        var startedBySplit = false;

        foreach (var cue in group)
        {
            var text = BracketTags.Replace(cue.Text, string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            // cue 邊界斷句：這一句已經達到長度上限，或前後兩段 cue 相差太大
            if (builder.Length >= MaxSentenceLength ||
                (!startedBySplit && hasPreviousCue && cue.StartMs - previousStartMs >= CueGapMs))
            {
                CloseCurrentSentence();
            }

            if (builder.Length == 0)
            {
                startMs = cue.StartMs;
                startedBySplit = false;
            }

            // 這一句的結尾：最後一段 cue 的 StartMs + DurationMs
            endMs = cue.StartMs + cue.DurationMs;
            foreach (var ch in text)
            {
                builder.Append(ch);
                if (SentenceEndSymbols.Contains(ch) && builder.Length >= MinSentenceLength)
                {
                    CloseCurrentSentence();

                    // 符號後面剩下的文字開始下一句，StartMs 是這一段 cue 的 StartMs
                    startMs = cue.StartMs;
                    startedBySplit = true;
                }
            }

            previousStartMs = cue.StartMs;
            hasPreviousCue = true;
        }

        CloseCurrentSentence();
        return;

        // 收掉目前這一句：修剪空白後是空字串、或只剩標點的句子丟掉
        void CloseCurrentSentence()
        {
            if (builder.Length == 0)
            {
                return;
            }

            var text = builder.ToString().Trim();
            builder.Clear();
            if (text.Length == 0 || text.All(c => char.IsPunctuation(c) || char.IsSymbol(c)))
            {
                return;
            }

            lines.Add(new SubtitleLine(startMs, endMs, text, FromSentence: false));
        }
    }

    private static string FormatTimestamp(long ms)
    {
        var hours = ms / 3_600_000;
        var remaining = ms % 3_600_000;
        var minutes = remaining / 60_000;
        remaining %= 60_000;
        var seconds = remaining / 1_000;
        var millis = remaining % 1_000;
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}.{millis:D3}";
    }

    /// <summary>SRT 的時間格式：與 WebVTT 相同但毫秒用逗號（HH:mm:ss,fff）。</summary>
    private static string FormatSrtTimestamp(long ms) => FormatTimestamp(ms).Replace('.', ',');
}
