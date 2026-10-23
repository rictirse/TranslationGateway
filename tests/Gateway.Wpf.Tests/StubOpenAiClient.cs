using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Gateway.Wpf.Tests;

/// <summary>不連網的模型替身：一律回傳固定譯文。</summary>
internal sealed class StubOpenAiClient : IOpenAiClient
{
    public const string Reply = "重新翻譯的結果";

    public Task<ChatResult> SendAsync(string userText, string systemText, CancellationToken cancellationToken, int? maxOutputTokens = null)
        => Task.FromResult(new ChatResult(Reply, 1, 1, 2, "stub"));

    public Task<ChatResult> SendChatAsync(string systemText, IReadOnlyList<ChatTurn> history, string userText, CancellationToken cancellationToken, int? maxOutputTokens = null)
        => Task.FromResult(new ChatResult(Reply, 1, 1, 2, "stub"));

    public Task WarmUpAsync(string systemText, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
