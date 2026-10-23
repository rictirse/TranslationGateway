namespace Translation.Bridge.Core.Services;

/// <summary>目前直播的狀態（DI singleton）：一鍵開台成功時寫入 Streams.Id，之後的字幕都帶這個 StreamId。</summary>
public sealed class StreamSession
{
    /// <summary>目前直播的 Streams.Id；尚未一鍵開台或建立失敗時為 null。</summary>
    public long? CurrentStreamId { get; set; }

    /// <summary>目前這場是直播（不是錄影）；一鍵開台成功時設定。直播中知識庫模型故障時不借用翻譯模型。</summary>
    public bool IsLive { get; set; }
}
