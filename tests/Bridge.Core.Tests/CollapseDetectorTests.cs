using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class CollapseDetectorTests
{
    [Theory]
    [InlineData("カーレスターどこ?", "卡", true)]
    [InlineData("まあ、ありえるな。上からだとちょっとどうしようもないんで。もう少しちゃんと考えたほうがいいと思うよ。", "這", true)]
    [InlineData("ありがとうございます", "謝謝", false)]
    [InlineData("うん", "嗯", false)]
    [InlineData("草", "笑死", false)]
    [InlineData("まあ、ありえるな。 上からだとちょっとどうしようもないんで。", "嗯，確實有可能。從上面來就沒辦法了。", false)]
    public void IsCollapsed_MatchesExpected(string source, string translation, bool expected)
    {
        Assert.Equal(expected, CollapseDetector.IsCollapsed(source, translation));
    }
}
