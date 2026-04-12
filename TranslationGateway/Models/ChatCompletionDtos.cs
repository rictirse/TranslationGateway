using System.Text.Json.Serialization;

namespace TranslationGateway.Models;

public class PotplayerRequest
{
    [JsonPropertyName("current")] public string Current { get; set; } = "";
    [JsonPropertyName("context")] public string Context { get; set; } = "";
}

public class ChatCompletionRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<Message> Messages { get; set; } = new();
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0;
}

public class Message
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

public class ChatCompletionResponse
{
    [JsonPropertyName("choices")] public List<Choice> Choices { get; set; } = new();
    [JsonPropertyName("usage")] public Usage? Usage { get; set; }
}

public class Choice
{
    [JsonPropertyName("message")] public Message? Message { get; set; }
}

public class Usage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
}