using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>以 AsyncLocal 保存目前這個知識庫模型請求的用途（llm_calls.purpose），寫法比照 <see cref="LlmPriorityScope"/>。
/// 各知識庫工作（資料探勘、聽錯字探勘等）在呼叫 CompleteJsonAsync 前進入 scope，
/// RecordingLorebookLlm 記錄時從這裡取 purpose。</summary>
public static class LlmCallPurposeScope
{
    private static readonly AsyncLocal<string?> CurrentPurpose = new();

    /// <summary>目前請求的用途；沒有設定時為 <see cref="LlmCallPurposes.Other"/>。</summary>
    public static string Current => CurrentPurpose.Value ?? LlmCallPurposes.Other;

    /// <summary>以指定用途進入 scope；Dispose 時還原成進入前的值。</summary>
    public static IDisposable Enter(string purpose)
    {
        var previous = CurrentPurpose.Value;
        CurrentPurpose.Value = purpose;
        return new RestoreOnDispose(previous);
    }

    private sealed class RestoreOnDispose(string? previous) : IDisposable
    {
        public void Dispose() => CurrentPurpose.Value = previous;
    }
}
