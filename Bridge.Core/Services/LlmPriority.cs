namespace Translation.Bridge.Core.Services;

/// <summary>LLM 請求的優先度：數字越小越優先。</summary>
public enum LlmPriority
{
    /// <summary>即時翻譯（翻譯模型，之後的卡使用）。</summary>
    Realtime = 0,

    /// <summary>資料探勘、詞彙匯入、測試連線（使用者在等）。</summary>
    Mining = 1,

    /// <summary>預先翻譯（翻譯模型，之後的卡使用）。</summary>
    Prefetch = 2,

    /// <summary>播放位置之後（含）的前處理。</summary>
    PreprocessNear = 3,

    /// <summary>播放位置之前、往回補的前處理。</summary>
    PreprocessBackfill = 4,

    /// <summary>聽錯字探勘、其他沒指定的工作。</summary>
    Background = 5,
}

/// <summary>一次 LLM 請求的資訊：優先度與（由 LlmLane 維護的）開始執行時間。</summary>
public sealed class LlmCallInfo(LlmPriority priority)
{
    /// <summary>這個請求的優先度。</summary>
    public LlmPriority Priority { get; } = priority;

    /// <summary>LlmLane 開始執行這個請求的時間；被插隊取消後設回 null。</summary>
    public DateTimeOffset? StartedAt { get; internal set; }
}

/// <summary>以 AsyncLocal 保存目前這個 LLM 請求的資訊，讓底層的排程器（LlmLane）能取用呼叫端的優先度。</summary>
public static class LlmPriorityScope
{
    private static readonly AsyncLocal<LlmCallInfo?> CurrentCall = new();

    /// <summary>目前的請求資訊；沒有時為 null。</summary>
    public static LlmCallInfo? Current => CurrentCall.Value;

    /// <summary>以指定優先度進入 scope；Dispose 時還原成進入前的值。</summary>
    public static IDisposable Enter(LlmPriority priority) => Enter(new LlmCallInfo(priority));

    /// <summary>以指定的請求資訊進入 scope；Dispose 時還原成進入前的值。</summary>
    public static IDisposable Enter(LlmCallInfo call)
    {
        var previous = CurrentCall.Value;
        CurrentCall.Value = call;
        return new RestoreOnDispose(previous);
    }

    private sealed class RestoreOnDispose(LlmCallInfo? previous) : IDisposable
    {
        public void Dispose() => CurrentCall.Value = previous;
    }
}
