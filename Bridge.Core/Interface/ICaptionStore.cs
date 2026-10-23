using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

/// <summary>YouTube 生成式字幕快取（youtube_captions）的讀寫；同步方法，只會在背景執行緒呼叫。</summary>
public interface ICaptionStore
{
    /// <summary>依影片與語言讀回快取的字幕；找不到或資料庫不可用時回傳 null。</summary>
    StoredCaption? GetCaption(string videoId, string language);

    /// <summary>新增或覆寫（同 video_id + language）；CreatedAt、LastUsedAt 都設為現在。回傳 Id，失敗回傳 null。</summary>
    long? SaveCaption(string videoId, string language, bool isAuto, string? clientName, IReadOnlyList<CaptionCue> cues);

    /// <summary>LastUsedAt 設為現在；失敗時不做事。</summary>
    void TouchCaption(long id);

    /// <summary>該字幕前處理過的句子（caption_sentences），依 StartMs 排序；失敗時回傳空清單。</summary>
    IReadOnlyList<CaptionSentence> GetSentences(long captionId);

    /// <summary>同一個交易內寫入句子；(caption_id, start_ms) 已存在時覆寫。失敗只寫 Warning log，不擲例外。</summary>
    void SaveSentences(long captionId, IReadOnlyList<CaptionSentence> sentences);
}
