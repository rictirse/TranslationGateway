using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

public interface IOpenAiClient
{
    Task<ChatResult> SendAsync(string userText, string systemText, CancellationToken cancellationToken, int? maxOutputTokens = null);

    Task<ChatResult> SendChatAsync(string systemText, IReadOnlyList<ChatTurn> history, string userText, CancellationToken cancellationToken, int? maxOutputTokens = null);

    /// <summary>預熱：只生成 1 token，讓伺服器留下下一句可以接續的檢查點；有歷史時送到最後一輪的 user 為止（不含它的 assistant），
    /// 沒有歷史時只送 system。任何錯誤都不得往外拋。</summary>
    Task WarmUpAsync(string systemText, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken);
}
