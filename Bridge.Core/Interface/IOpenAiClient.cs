using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

public interface IOpenAiClient
{
    Task<ChatResult> SendAsync(string userText, string systemText, CancellationToken cancellationToken, int? maxOutputTokens = null);

    Task<ChatResult> SendChatAsync(string systemText, IReadOnlyList<ChatTurn> history, string userText, CancellationToken cancellationToken, int? maxOutputTokens = null);

    /// <summary>預熱：送出 system + history（不含新的 user 訊息），只生成 1 token；任何錯誤都不得往外拋。</summary>
    Task WarmUpAsync(string systemText, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken);
}
