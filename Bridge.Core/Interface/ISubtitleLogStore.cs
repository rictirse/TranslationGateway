using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

/// <summary>字幕紀錄（SubtitleLog）與直播紀錄（Streams）的讀寫；同步方法，只會在背景執行緒呼叫。</summary>
public interface ISubtitleLogStore
{
    /// <summary>新增直播，回傳 Id；失敗回傳 null。</summary>
    long? CreateStream(StreamRecord stream);

    /// <summary>新增一句字幕；RevisionOfJobId 不為 null 時，RevisionOfId 填該 JobId 最新一列的 Id（找不到為 NULL）。</summary>
    void InsertSubtitle(SubtitleLogEntry entry);

    /// <summary>更新該 JobId 最新一列：source 不為 null 才改 Source；Translation 改為 translation；IsEdited = 1；UpdatedAt = 現在。</summary>
    void MarkSubtitleEdited(string jobId, string? source, string translation);

    /// <summary>該 JobId 最新一列 IsDeleted = 1、UpdatedAt = 現在。</summary>
    void MarkSubtitleDeleted(string jobId);

    /// <summary>新增一筆直播生效的詞彙卡（ActivatedAt = 現在）。</summary>
    void AddStreamCard(long streamId, long cardId, string reason);

    /// <summary>該直播、該卡最新一筆 DeactivatedAt 為空的紀錄，設 DeactivatedAt = 現在；找不到時不做事。</summary>
    void EndStreamCard(long streamId, long cardId);

    /// <summary>最近 maxStreams 場直播（依 Id 由大到小，排除 excludeStreamId）的字幕原文，IsDeleted = 0 的列；
    /// GameCardId = 該直播 StreamCards 中、卡別是 Game 的最小 CardId（沒有為 null）。失敗擲例外。</summary>
    /// <remarks>預設實作直接擲例外：只有遊戲用語探勘會用到，不支援的實作（例如單元測試的假 store）不必實作。</remarks>
    IReadOnlyList<TopicSubtitleSource> GetTopicSubtitleSources(long? excludeStreamId, int maxStreams)
        => throw new NotSupportedException("此字幕紀錄後端不支援 GetTopicSubtitleSources。");
}
