namespace Translation.Bridge.Core.Models;

/// <summary>一鍵開台成功時新增的直播紀錄。</summary>
public sealed record StreamRecord(string Platform, string Url, string? ChannelName, string? Title, string? Category, long? CharacterCardId,
    bool IsVod = false, DateTimeOffset? VideoStartedAt = null, DateTimeOffset? VideoEndedAt = null);

/// <summary>要寫入 SubtitleLog 的一句字幕（含 Whisper 的中間版本）。</summary>
public sealed record SubtitleLogEntry(
    string JobId,
    string? RevisionOfJobId,
    long? StreamId,
    string RawSource,
    string Source,
    string Translation,
    string TranslationSource,
    long ModelLatencyMs,
    DateTimeOffset CreatedAt)
{
    /// <summary>命中翻譯快取時，該筆 translation_cache 的 Id；其他情況為 null。</summary>
    public long? CacheId { get; set; }
}
