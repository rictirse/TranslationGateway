using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

public interface IOpenAiClient
{
    Task<ChatResult> SendAsync(string userText, string systemText, CancellationToken cancellationToken);
}