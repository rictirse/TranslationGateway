namespace TranslationGateway.Models;

public record GptChatResult(string Content, int? PromptTokens, int? CompletionTokens, int? TotalTokens, string? Model);