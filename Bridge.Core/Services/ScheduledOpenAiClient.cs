using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>翻譯模型的排程包裝：所有請求經過 LlmLane 排隊（一次只送一個、依優先度），
/// 優先度取自呼叫端的 LlmPriorityScope（沒有時 SendAsync／SendChatAsync 為 Realtime、WarmUpAsync 為 Background）。</summary>
public sealed class ScheduledOpenAiClient(IOpenAiClient inner, LlmLane lane) : IOpenAiClient
{
    public Task<ChatResult> SendAsync(string userText, string systemText, CancellationToken cancellationToken, int? maxOutputTokens = null)
    {
        var call = LlmPriorityScope.Current ?? new LlmCallInfo(LlmPriority.Realtime);
        return lane.RunAsync(call.Priority,
            token => inner.SendAsync(userText, systemText, token, maxOutputTokens), cancellationToken, call);
    }

    public Task<ChatResult> SendChatAsync(string systemText, IReadOnlyList<ChatTurn> history, string userText, CancellationToken cancellationToken, int? maxOutputTokens = null)
    {
        var call = LlmPriorityScope.Current ?? new LlmCallInfo(LlmPriority.Realtime);
        return lane.RunAsync(call.Priority,
            token => inner.SendChatAsync(systemText, history, userText, token, maxOutputTokens), cancellationToken, call);
    }

    public Task WarmUpAsync(string systemText, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken)
    {
        var call = LlmPriorityScope.Current ?? new LlmCallInfo(LlmPriority.Background);
        return lane.RunAsync<object?>(call.Priority, async token =>
        {
            await inner.WarmUpAsync(systemText, history, token).ConfigureAwait(false);
            return null;
        }, cancellationToken, call);
    }
}
