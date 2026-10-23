using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class RevisionDetectorTests
{
    [Theory]
    [InlineData("ギャルみたいになっちゃったけど。", "ギャルみたいになっちゃったけど")]
    [InlineData("何?マビノギ?", "何マビノギ?")]
    [InlineData("もう一回またかけてみるの?", "もう一回 またかけてみるの?")]
    public void Classify_PunctuationOnlyDiff_Identical(string previous, string current)
    {
        Assert.Equal(RevisionKind.Identical, RevisionDetector.Classify(previous, current));
    }

    [Theory]
    [InlineData("損はない絶対 クエルなぁ", "損はない絶対")]
    [InlineData("ありがとうございました! えーすごい!", "ありがとうございました!")]
    public void Classify_ShortenedVersion_Shrink(string previous, string current)
    {
        Assert.Equal(RevisionKind.Shrink, RevisionDetector.Classify(previous, current));
    }

    [Theory]
    [InlineData("怒るとめちゃくちゃ怖かったけどおじいちゃんも", "おじいちゃん")]
    [InlineData("お肉やっぱ英語ランクとか言われるともう本当にたまらないよね", "そこからトリガーが外れてしまって")]
    [InlineData("何?", "何")]
    [InlineData("楽しかった そういえばマリナーズマスマジで楽屋弁当がとっても豪華だったと聞いたのですが", "テンディを食べました はい先ほど言った通り")]
    public void Classify_UnrelatedSentences_None(string previous, string current)
    {
        Assert.Equal(RevisionKind.None, RevisionDetector.Classify(previous, current));
    }

    [Theory]
    [InlineData("普段、引きこもりなんで全然", "普段、引きこもりなんで全然ご飯行かないし")]
    [InlineData("テンディを食べました はい先ほど言った通り", "天理を食べました はい先ほど言った通り")]
    [InlineData("ありがとうございます ローストビーフと悩んだんですよ", "ありがとうございます ローストビーフと悩んだんですけどね")]
    [InlineData("なんかちょっとしか映ってない", "なんかちょっとしか写ってない")]
    [InlineData("天理を食べました はい先ほど言った通り英語ランクの焼肉弁当を食べさせていただき 即完食させていただき", "はい、先ほど言った通り、英語ランクの焼肉弁当を食べさせていただき、即完食させていただきました")]
    [InlineData("始まりの街の雑貨屋さんの隣にある", "はじまりの町の雑貨屋さんの隣にある女の子に話をかけると")]
    public void Classify_ExtendedOrEditedVersion_Revise(string previous, string current)
    {
        Assert.Equal(RevisionKind.Revise, RevisionDetector.Classify(previous, current));
    }

    [Fact]
    public void Normalize_FullWidthAndPunctuation_KeepsLettersAndDigitsOnly()
    {
        Assert.Equal("abc123", RevisionDetector.Normalize("ａｂｃ１、２ ３!"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Normalize_NullOrEmpty_ReturnsEmpty(string? text)
    {
        Assert.Equal(string.Empty, RevisionDetector.Normalize(text!));
    }
}
