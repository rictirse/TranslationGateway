using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>字幕段落的 JSON 序列化（存進 youtube_captions.CuesJson）：
/// <c>[{"s":開始毫秒,"d":長度毫秒,"t":"文字"}]</c>，不縮排、日文不跳脫成 \uXXXX。</summary>
public static class CaptionCueJson
{
    /// <summary>序列化選項：UnsafeRelaxedJsonEscaping（保留日文原字）、不縮排。</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>序列化成 JSON 陣列（依傳入順序，不另行排序）。</summary>
    public static string Serialize(IReadOnlyList<CaptionCue> cues) =>
        JsonSerializer.Serialize(cues.Select(c => new CueDto(c.StartMs, c.DurationMs, c.Text)).ToArray(), Options);

    /// <summary>反序列化；結果照 StartMs 排序。格式壞掉時擲 <see cref="JsonException"/>（呼叫端處理）。</summary>
    public static IReadOnlyList<CaptionCue> Deserialize(string json)
    {
        var dtos = JsonSerializer.Deserialize<CueDto[]>(json) ?? [];
        return [.. dtos.OrderBy(d => d.S).Select(d => new CaptionCue(d.S, d.D, d.T ?? string.Empty))];
    }

    /// <summary>CuesJson 的 DTO：s = 開始毫秒、d = 長度毫秒、t = 文字。</summary>
    private sealed record CueDto(
        [property: JsonPropertyName("s")] long S,
        [property: JsonPropertyName("d")] long D,
        [property: JsonPropertyName("t")] string? T);
}
