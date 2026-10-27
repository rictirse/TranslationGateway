using System.Text;
using System.Text.RegularExpressions;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>從留言挑出最像セトリ（曲目時間軸）的一則，轉成章節清單。</summary>
public static class ChapterExtractor
{
    private const int MaxChapters = 200;
    private const string UntitledTitle = "(無標題)";
    private const string StartTitle = "開始";

    /// <summary>頭尾要去掉的分隔符號（含全形的 ｜；セトリ常用「8:35｜曲名」這類寫法）。</summary>
    private static readonly char[] Separators = ['-', '–', '—', '|', '｜', ':', '：', '・'];

    /// <summary>セトリ關鍵字（不分大小寫）：內文有提到者優先當セトリ。</summary>
    private static readonly string[] SetlistKeywords = ["セトリ", "セットリスト", "setlist", "set list"];

    /// <summary>說明欄時間戳：可選的時數、分、秒；前後不得緊鄰數字或冒號（分秒由呼叫端驗證 &lt; 60）。</summary>
    private static readonly Regex TimestampPattern = new(@"(?<![\d:])(?:(\d{1,2}):)?(\d{1,2}):(\d{2})(?![\d:])", RegexOptions.Compiled);

    /// <summary>從說明欄文字解析章節（YouTube 說明欄章節的寫法：每行一個時間戳＋標題）；不合格回空清單。
    /// <paramref name="description"/> 為 null 表示不知道影片長度。</summary>
    public static IReadOnlyList<VideoChapter> FromDescription(string? description, long? durationMs) =>
        FromText(description, durationMs);

    /// <summary>解析使用者貼上的純文字セトリ（規則同 <see cref="FromDescription"/>：每行一個時間戳＋標題）；不合格回空清單。
    /// <paramref name="durationMs"/> 為 null 表示不知道影片長度。</summary>
    public static IReadOnlyList<VideoChapter> FromText(string? text, long? durationMs)
    {
        // 以 \n 分行（\r\n 視為 \n：行尾殘留的 \r 會在標題清理時被當空白）
        List<TimelineLine> timeline = [];
        if (!string.IsNullOrEmpty(text))
        {
            var lineStart = 0;
            for (var i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n')
                {
                    continue;
                }

                var parsed = ParseDescriptionLine(text[lineStart..i]);
                if (parsed is not null)
                {
                    timeline.Add(parsed);
                }

                lineStart = i + 1;
            }
        }

        // 合格條件與 FromComments 對單一則留言相同：≥ 3 行、時間嚴格遞增、（知道的話）全部在影片長度內
        if (!IsQualified(timeline, durationMs))
        {
            return [];
        }

        var chapters = timeline
            .Select(static line => new VideoChapter(line.Seconds * 1000L, line.Title))
            .OrderBy(static chapter => chapter.StartMs)
            .ToList();

        // 第一章不是 0 時補「開始」；超過 200 章截斷
        if (chapters.Count > 0 && chapters[0].StartMs != 0)
        {
            chapters.Insert(0, new VideoChapter(0, StartTitle));
        }

        return chapters.Count > MaxChapters ? [.. chapters.Take(MaxChapters)] : chapters;
    }

    /// <summary>說明欄的一行：第一個時間戳（分秒 &lt; 60 才算）的秒數與移除所有時間戳後清理出的標題；沒有時間戳回 null。</summary>
    private static TimelineLine? ParseDescriptionLine(string line)
    {
        var seconds = -1;
        var title = new StringBuilder(line.Length);
        var position = 0;
        foreach (Match match in TimestampPattern.Matches(line))
        {
            var minutes = int.Parse(match.Groups[2].Value);
            var secs = int.Parse(match.Groups[3].Value);
            if (minutes >= 60 || secs >= 60)
            {
                continue;
            }

            if (seconds < 0)
            {
                var hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
                seconds = hours * 3600 + minutes * 60 + secs;
            }

            // 標題 = 行文字移除「所有」時間戳文字後再清理
            title.Append(line, position, match.Index - position);
            position = match.Index + match.Length;
        }

        if (seconds < 0)
        {
            return null;
        }

        if (position < line.Length)
        {
            title.Append(line, position, line.Length - position);
        }

        return new TimelineLine(seconds, CleanTitle(title.ToString()));
    }

    /// <summary>從留言挑出最像セトリ的一則轉成章節；沒有合格留言回空清單。<paramref name="durationMs"/> 為 null 表示不知道影片長度。</summary>
    public static IReadOnlyList<VideoChapter> FromComments(IReadOnlyList<YouTubeComment> comments, long? durationMs)
    {
        // 合格的セトリ候選：時間軸行 ≥ 3、時間嚴格遞增、（知道的話）全部在影片長度內
        List<(YouTubeComment Comment, List<TimelineLine> Lines, bool HasKeyword)> candidates = [];
        foreach (var comment in comments)
        {
            var lines = ExtractTimelineLines(comment);
            if (!IsQualified(lines, durationMs))
            {
                continue;
            }

            candidates.Add((comment, lines, HasSetlistKeyword(comment)));
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        // 取第一名：關鍵字 → 時間軸行數 → 讚數 → 留言清單順序
        var best = candidates[0];
        for (var i = 1; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate.HasKeyword != best.HasKeyword)
            {
                if (candidate.HasKeyword) best = candidate;
                continue;
            }

            if (candidate.Lines.Count != best.Lines.Count)
            {
                if (candidate.Lines.Count > best.Lines.Count) best = candidate;
                continue;
            }

            if (candidate.Comment.LikeCount > best.Comment.LikeCount)
            {
                best = candidate;
            }
        }

        var chapters = best.Lines
            .Select(static line => new VideoChapter(line.Seconds * 1000L, line.Title))
            .OrderBy(static chapter => chapter.StartMs)
            .ToList();

        // 第一章不是 0 時補「開始」；超過 200 章截斷
        if (chapters.Count > 0 && chapters[0].StartMs != 0)
        {
            chapters.Insert(0, new VideoChapter(0, StartTitle));
        }

        return chapters.Count > MaxChapters ? [.. chapters.Take(MaxChapters)] : chapters;
    }

    /// <summary>一個時間軸行：第一個時間戳連結的秒數與清理後的標題。</summary>
    private sealed record TimelineLine(int Seconds, string Title);

    /// <summary>留言的時間軸行：含至少一個時間戳連結的行，時間取行內第一個連結。</summary>
    private static List<TimelineLine> ExtractTimelineLines(YouTubeComment comment)
    {
        var text = comment.Text;
        List<(string Line, int Start)> lines = [];

        // 以 \n 分行（\r\n 視為 \n：行尾殘留的 \r 會在標題清理時被當空白）
        var lineStart = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add((text[lineStart..i], lineStart));
                lineStart = i + 1;
            }
        }

        lines.Add((text[lineStart..], lineStart));

        List<TimelineLine> timeline = [];
        foreach (var (line, start) in lines)
        {
            // StartIndex 落在行內（[start, start+length)）的連結；取最早出現（StartIndex 最小）的當這行的時間
            CommentTimestamp? first = null;
            foreach (var timestamp in comment.Timestamps)
            {
                if (timestamp.StartIndex < start || timestamp.StartIndex >= start + line.Length)
                {
                    continue;
                }

                if (first is null || timestamp.StartIndex < first.StartIndex)
                {
                    first = timestamp;
                }
            }

            if (first is not null)
            {
                timeline.Add(new TimelineLine(first.Seconds, BuildTitle(line, start, comment.Timestamps)));
            }
        }

        return timeline;
    }

    /// <summary>標題 = 行文字移除所有時間戳連結的文字後，合併空白、去頭尾空白與分隔符號；空標題用 <see cref="UntitledTitle"/>。</summary>
    private static string BuildTitle(string line, int lineStart, IReadOnlyList<CommentTimestamp> timestamps)
    {
        var links = timestamps
            .Where(t => t.StartIndex >= lineStart && t.StartIndex < lineStart + line.Length)
            .Select(t => (Start: t.StartIndex - lineStart, t.Length))
            .OrderBy(static link => link.Start)
            .ToList();

        var title = new StringBuilder(line.Length);
        var position = 0;
        foreach (var (linkStart, linkLength) in links)
        {
            if (linkStart < position)
            {
                continue;
            }

            title.Append(line, position, linkStart - position);
            position = Math.Min(linkStart + linkLength, line.Length);
        }

        if (position < line.Length)
        {
            title.Append(line, position, line.Length - position);
        }

        return CleanTitle(title.ToString());
    }

    /// <summary>全形空白（U+3000）當一般空白、連續空白合併成一個、去頭尾空白，再去掉頭尾的分隔符號與其後（前）的空白。
    /// 說明欄章節（<see cref="FromDescription"/>）與留言セトリ（<see cref="FromComments"/>）共用這個清理。</summary>
    private static string CleanTitle(string raw)
    {
        var cleaned = new StringBuilder(raw.Length);
        var lastWasSpace = false;
        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) cleaned.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                cleaned.Append(c);
                lastWasSpace = false;
            }
        }

        var title = cleaned.ToString().Trim();
        while (title.Length > 0)
        {
            // 頭尾其中一端是分隔符號就拿掉，並去掉多出來的空白；兩端都不是才停手
            if (Separators.Contains(title[0]))
            {
                title = title[1..].TrimStart();
            }
            else if (Separators.Contains(title[^1]))
            {
                title = title[..^1].TrimEnd();
            }
            else
            {
                break;
            }
        }

        return title.Length == 0 ? UntitledTitle : title;
    }

    private static bool IsQualified(List<TimelineLine> lines, long? durationMs)
    {
        if (lines.Count < 3)
        {
            return false;
        }

        // 時間照行的順序嚴格遞增（精華片段留言常出現 52:32 後面接 50:00）
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Seconds <= lines[i - 1].Seconds)
            {
                return false;
            }
        }

        if (durationMs is { } duration)
        {
            foreach (var line in lines)
            {
                if (line.Seconds * 1000L >= duration)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool HasSetlistKeyword(YouTubeComment comment) =>
        SetlistKeywords.Any(keyword => comment.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}
