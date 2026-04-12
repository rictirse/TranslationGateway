using TranslationGateway.Models;

namespace TranslationGateway.Interface;

public interface IOpenAiClient
{
    Task<ChatResult> SendAsync(string userText, string systemText, CancellationToken cancellationToken);
}
