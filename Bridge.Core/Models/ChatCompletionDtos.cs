using System.Text.Json.Serialization;

namespace Translation.Bridge.Core.Models;

public class PotplayerRequest
{
    [JsonPropertyName("current")] public string Current { get; set; } = "";
    [JsonPropertyName("context")] public string[] Context { get; set; } = Array.Empty<string>();
}