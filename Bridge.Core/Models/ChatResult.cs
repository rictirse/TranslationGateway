namespace Translation.Bridge.Core.Models;

public record ChatResult(string Content, int? PromptTokens, int? CompletionTokens, int? TotalTokens, string? Model);