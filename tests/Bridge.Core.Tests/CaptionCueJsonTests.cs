using System.Text.Json;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>CaptionCueJson：CuesJson 欄位的序列化／反序列化（日文原字、排序、壞格式例外）。</summary>
public class CaptionCueJsonTests
{
    [Fact]
    public void SerializeThenDeserialize_RoundTripsCues()
    {
        var cues = new CaptionCue[]
        {
            new(267040, 5520, "うん。なんか"),
            new(272560, 3000, "今日は"),
            new(275560, 2000, "マイクラやります"),
        };

        var json = CaptionCueJson.Serialize(cues);
        var restored = CaptionCueJson.Deserialize(json);

        Assert.Equal(cues, restored);
    }

    [Fact]
    public void Serialize_KeepsJapaneseUnescapedAndCompact()
    {
        var json = CaptionCueJson.Serialize([new CaptionCue(267040, 5520, "うん。なんか「花火」")]);

        // 日文不要跳脫成 \uXXXX；不縮排（沒有多餘空白與換行）
        Assert.Contains("うん。なんか「花火」", json);
        Assert.DoesNotContain("\\u", json);
        Assert.DoesNotContain("\n", json);
        Assert.DoesNotContain(" ", json);
        Assert.Equal("""[{"s":267040,"d":5520,"t":"うん。なんか「花火」"}]""", json);
    }

    [Fact]
    public void Deserialize_SortsByStartMs()
    {
        var json = """[{"s":9000,"d":1000,"t":"遅い"},{"s":1000,"d":500,"t":"早い"},{"s":5000,"d":700,"t":"途中"}]""";

        var cues = CaptionCueJson.Deserialize(json);

        Assert.Equal([1000L, 5000L, 9000L], cues.Select(c => c.StartMs).ToList());
        Assert.Equal("早い", cues[0].Text);
        Assert.Equal("途中", cues[1].Text);
        Assert.Equal("遅い", cues[2].Text);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"s\":1}")]
    [InlineData("[{\"s\":\"x\",\"d\":1,\"t\":\"a\"}]")]
    [InlineData("")]
    public void Deserialize_BrokenJson_ThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => CaptionCueJson.Deserialize(json));
    }
}
