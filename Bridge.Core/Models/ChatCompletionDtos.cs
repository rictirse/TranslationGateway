using System.Text.Json.Serialization;

namespace Translation.Bridge.Core.Models;

public class PotplayerRequest
{
    [JsonPropertyName("current")] public string Current { get; set; } = "";
    [JsonPropertyName("context")] public string[] Context { get; set; } = Array.Empty<string>();
    [JsonPropertyName("probe")] public bool Probe { get; set; }

    /// <summary>PotPlayer 正在播放的檔名或網址（腳本 2.6 起隨每個翻譯請求送上來；舊版腳本沒有這個欄位）。</summary>
    [JsonPropertyName("playing")] public string? Playing { get; set; }
}