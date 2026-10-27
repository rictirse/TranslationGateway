using System.Text;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>記住最近一次推送給 PotPlayer 的字幕行，依播放位置找出目前那一句。</summary>
public sealed class PlaybackLineTracker : IDisposable
{
    private readonly PotPlayerSubtitlePusher _pusher;
    private readonly object _lock = new();

    /// <summary>最近一次推送的影片 Id（目前查詢只用最新的推送內容，不必對影片）。</summary>
    private string? _videoId;

    /// <summary>最近一次推送的字幕行（依 StartMs 遞增排序）；還沒推送過時為 null。以 <see cref="_lock"/> 保護。</summary>
    private IReadOnlyList<SubtitleLine>? _lines;

    /// <summary>與 <see cref="_lines"/> 對齊的行文字（經 <see cref="CaptionSubtitleBuilder.CueText"/>）。以 <see cref="_lock"/> 保護。</summary>
    private IReadOnlyList<string>? _cueTexts;

    public PlaybackLineTracker(PotPlayerSubtitlePusher pusher)
    {
        _pusher = pusher;
        _pusher.Pushed += OnPushed;
    }

    /// <summary>推送成功（背景執行緒觸發）：存下 (videoId, lines)，行依 StartMs 排序（穩定排序）。</summary>
    private void OnPushed(string videoId, IReadOnlyList<SubtitleLine> lines)
    {
        var sorted = lines.OrderBy(line => line.StartMs).ToList();
        var cueTexts = sorted.Select(line => CaptionSubtitleBuilder.CueText(line.Text)).ToList();
        lock (_lock)
        {
            _videoId = videoId;
            _lines = sorted;
            _cueTexts = cueTexts;
        }
    }

    /// <summary>位置所在那一行（StartMs ≤ positionMs &lt; EndMs）的原文（經 <see cref="CaptionSubtitleBuilder.CueText"/>）；
    /// 沒有推送過或不在任何一行內回 null。多行重疊時取 StartMs 最大的那一行。</summary>
    public string? FindTextAt(long positionMs)
    {
        IReadOnlyList<SubtitleLine>? lines;
        IReadOnlyList<string>? cueTexts;
        lock (_lock)
        {
            lines = _lines;
            cueTexts = _cueTexts;
        }

        if (lines is null || lines.Count == 0 || cueTexts is null)
        {
            return null;
        }

        // 二分搜尋最後一個 StartMs ≤ positionMs 的索引（字幕可能上千行，每 0.5 秒查一次）
        var lo = 0;
        var hi = lines.Count - 1;
        var index = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (lines[mid].StartMs <= positionMs)
            {
                index = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (index < 0)
        {
            return null;
        }

        // 重疊的行中 StartMs 最大的是最靠近 index 的：從 index 往回找第一個 EndMs > positionMs 的行
        for (var i = index; i >= 0; i--)
        {
            if (lines[i].EndMs > positionMs)
            {
                return cueTexts[i];
            }
        }

        return null;
    }

    /// <summary>正規化比對用：去頭尾空白、連續空白（含全形空白、換行）合併成一個半形空白。</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>取消訂閱 <see cref="PotPlayerSubtitlePusher.Pushed"/>。</summary>
    public void Dispose()
    {
        _pusher.Pushed -= OnPushed;
    }
}
