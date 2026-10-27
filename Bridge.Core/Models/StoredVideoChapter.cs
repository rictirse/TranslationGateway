namespace Translation.Bridge.Core.Models;

/// <summary>章節來源（video_chapters 的 Source 欄位值）：人工 &gt; 說明欄 &gt; 留言。</summary>
public static class VideoChapterSources
{
    public const string Manual = "Manual";
    public const string Description = "Description";
    public const string Comment = "Comment";
}

/// <summary>video_chapters 的一列。</summary>
public sealed record StoredVideoChapter(long StartMs, string Title, string? Translation, long? DurationMs, string Source);
