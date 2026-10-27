using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

/// <summary>影片章節（video_chapters）的讀寫；同步方法，只在背景執行緒呼叫；失敗不擲例外。</summary>
public interface IVideoChapterStore
{
    /// <summary>這支影片存的章節（依 StartMs 排序）；沒有或失敗回空清單。</summary>
    IReadOnlyList<StoredVideoChapter> GetVideoChapters(string videoId);

    /// <summary>在同一個交易內刪掉這支影片的全部章節再寫入 chapters（全部同一個 Source）。失敗只記 Warning。</summary>
    void ReplaceVideoChapters(string videoId, IReadOnlyList<StoredVideoChapter> chapters);

    /// <summary>刪掉這支影片的全部章節。</summary>
    void DeleteVideoChapters(string videoId);
}
