using System.Text.Json;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Tests;

public class PotplayerRequestTests
{
    // ASP.NET Core minimal API 的 ReadFromJsonAsync 使用 JsonSerializerDefaults.Web
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Deserialize_ArrayContext_ReadsAllFields()
    {
        var json = """{"current":"今の文","context":["前1","前2"]}""";

        var req = JsonSerializer.Deserialize<PotplayerRequest>(json, WebOptions);

        Assert.NotNull(req);
        Assert.Equal("今の文", req.Current);
        Assert.Equal(["前1", "前2"], req.Context);
    }

    [Fact]
    public void Deserialize_MissingContext_DefaultsToEmptyArray()
    {
        var req = JsonSerializer.Deserialize<PotplayerRequest>("""{"current":"x"}""", WebOptions);

        Assert.NotNull(req);
        Assert.Empty(req.Context);
    }

    [Fact]
    public void Deserialize_JapaneseAndEscapedCharacters_RoundTrips()
    {
        var json = "{\"current\":\"「こんにちは」\\n\\\"引用\\\"\",\"context\":[]}";

        var req = JsonSerializer.Deserialize<PotplayerRequest>(json, WebOptions);

        Assert.Equal("「こんにちは」\n\"引用\"", req!.Current);
    }

    [Fact]
    public void Deserialize_ProbeTrue_ReadsProbe()
    {
        var req = JsonSerializer.Deserialize<PotplayerRequest>("""{"current":"x","probe":true}""", WebOptions);

        Assert.NotNull(req);
        Assert.True(req.Probe);
    }

    [Fact]
    public void Deserialize_MissingProbe_DefaultsToFalse()
    {
        var req = JsonSerializer.Deserialize<PotplayerRequest>("""{"current":"x"}""", WebOptions);

        Assert.NotNull(req);
        Assert.False(req.Probe);
    }

    [Fact]
    public void Deserialize_ContextAsString_CurrentlyThrows()
    {
        // 記錄現況：context 必須是字串陣列；舊版 AngelScript 若送字串會解析失敗
        var json = """{"current":"x","context":"a\nb"}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PotplayerRequest>(json, WebOptions));
    }
}
