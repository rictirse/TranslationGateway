namespace Translation.Core.Models;

public class PromptHistory
{
    public string? ModelName { get; set; }
    public string? SystemPrompt { get; set; }
    public DateTime SaveTime { get; set; }
}