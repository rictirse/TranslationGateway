using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class TextDiffTests
{
    [Theory]
    [InlineData("カーレスターどこ?", "カレスタどこ?", "カーレスター", "カレスタ")]
    [InlineData("ペコラかわいい", "ぺこらかわいい", "ペコラ", "ぺこら")]
    [InlineData("今日はマリン船長と", "今日はマリン船長と", null, null)]
    [InlineData("あいう", "あXいう", null, null)]
    [InlineData("えーとね", "ね", "えーと", "")]
    [InlineData("abc", "xyz", "abc", "xyz")]
    [InlineData("カレスタどこ", "カーレスタどこ", "カレスタ", "カーレスタ")]
    [InlineData("宝鐘マリン線長です", "宝鐘マリン船長です", "線長", "船長")]
    [InlineData("hololiveのみこち", "ホロライブのみこち", "hololive", "ホロライブ")]
    public void ChangedSegment_ReturnsReplacedPart(string before, string after, string? from, string? to)
    {
        var result = TextDiff.ChangedSegment(before, after);

        if (from is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Equal((from, to!), result);
        }
    }

    [Fact]
    public void ChangedSegment_RepeatedCharacters_DoNotOverlapPrefixAndSuffix()
    {
        // 共同前後綴重疊時（ああ → あああ）不可切出負長度
        Assert.Null(TextDiff.ChangedSegment("ああ", "あああ"));
        Assert.Equal(("あ", ""), TextDiff.ChangedSegment("あああ", "ああ"));
    }
}
