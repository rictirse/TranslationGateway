using TranslationGateway.ViewModels;

namespace TranslationGateway.Models;

public class AppSettings
{
    public string ApiHost { get; set; } = "http://127.0.0.1:8000";
    public string Model { get; set; } = "gpt-oss";
    public string ChatCompletionsPath { get; set; } = "/v1/chat/completions";
    public string? ApiKey { get; set; } = null;
    /// <summary>
    /// 是否使用本地模型
    /// </summary>
    public bool UseLocalModel { get; set; } = false;
    public string LastSelectedControlName { get; set; } = string.Empty;
    public string SystemInput { get; set; } = "";
    public bool IsFakeMode = false;
    public TranslationSettings TranslationSettings { get; set; } = new();
}