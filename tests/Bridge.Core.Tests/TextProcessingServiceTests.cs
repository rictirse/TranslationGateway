using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class TextProcessingServiceTests
{
    private readonly TextProcessingService _sut = new();

    [Fact]
    public void PreProcess_ReturnsInputUnchanged()
    {
        Assert.Equal("こんにちは", _sut.PreProcess("こんにちは"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PostProcess_BlankInput_ReturnedAsIs(string input)
    {
        Assert.Equal(input, _sut.PostProcess(input, "原文"));
    }

    [Fact]
    public void PostProcess_TrimsSurroundingWhitespace()
    {
        Assert.Equal("你好", _sut.PostProcess("  你好\n", "こんにちは"));
    }

    [Fact]
    public void PostProcess_NoChineseCharacters_OnlyTrims()
    {
        Assert.Equal("Hello", _sut.PostProcess(" Hello ", "こんにちは"));
    }

    [Fact]
    public void PostProcess_ConvertsSimplifiedToTraditional()
    {
        var result = _sut.PostProcess("这是一个测试", "これはテスト");

        Assert.Contains("這", result);
        Assert.Contains("個", result);
        Assert.Contains("測試", result);
    }

    [Theory]
    [InlineData("不到這個程度是不行的。", "不到這個程度是不行的")]
    [InlineData("沒有。啊。", "沒有。啊")]
    [InlineData("好。。", "好")]
    [InlineData("好．", "好")]
    [InlineData("真的嗎？", "真的嗎？")]
    [InlineData("好厲害！", "好厲害！")]
    [InlineData("然後……", "然後……")]
    [InlineData("他說「好。」", "他說「好。」")]
    [InlineData("v1.2.", "v1.2.")]
    [InlineData("。", "。")]
    [InlineData("这是测试。", "這是測試")]
    public void PostProcess_TrailingFullWidthPeriod_RemovedAtEndOnly(string input, string expected)
    {
        Assert.Equal(expected, _sut.PostProcess(input, "これはテスト"));
    }

    [Theory]
    [InlineData("", "原文")]
    [InlineData("  ", "原文")]
    public void Validate_EmptyTranslation_IsInvalid(string translated, string original)
    {
        var result = _sut.ValidateTranslation(translated, original);

        Assert.False(result.IsValid);
        Assert.Equal(0, result.Ratio);
    }

    [Fact]
    public void Validate_EmptyOriginal_IsInvalid()
    {
        var result = _sut.ValidateTranslation("翻譯", "");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_NormalLength_IsValid()
    {
        var result = _sut.ValidateTranslation("今天天氣很好", "今日はいい天気です");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_TranslationTooShort_IsInvalid()
    {
        var result = _sut.ValidateTranslation("好", new string('あ', 20));

        Assert.False(result.IsValid);
        Assert.True(result.Ratio < 0.3);
    }

    [Fact]
    public void Validate_TranslationTooLong_IsInvalid()
    {
        var result = _sut.ValidateTranslation(new string('好', 40), "あいう");

        Assert.False(result.IsValid);
        Assert.True(result.Ratio > 3.0);
    }

    [Fact]
    public void Validate_ManyLinesMostlyMissing_IsInvalid()
    {
        var original = string.Join("\n", Enumerable.Repeat("あいうえお", 10));
        var translated = "好好好好好好好好好好\n好好好好好好好好好好\n好好好好好好好好好好";

        var result = _sut.ValidateTranslation(translated, original);

        Assert.False(result.IsValid);
        Assert.Contains("行數", result.Message);
    }

    [Fact]
    public void Validate_ManyLinesAllPresent_IsValid()
    {
        var original = string.Join("\n", Enumerable.Repeat("あいうえお", 8));
        var translated = string.Join("\n", Enumerable.Repeat("好好好好好", 8));

        var result = _sut.ValidateTranslation(translated, original);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_FewLines_SkipsLineCountCheck()
    {
        var original = "あ\nい\nう";
        var translated = "好好好好好好";

        var result = _sut.ValidateTranslation(translated, original);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("♪")]
    [InlineData("...")]
    [InlineData("！？")]
    [InlineData("123")]
    [InlineData("ああああああああ")]
    [InlineData("ーーーーーーーー")]
    public void ShouldSkip_MeaninglessInput_ReturnsTrue(string? input)
    {
        Assert.True(_sut.ShouldSkipTranslation(input!));
    }

    [Theory]
    [InlineData("ああああ")]
    [InlineData("はい")]
    [InlineData("うん")]
    [InlineData("こんにちは")]
    [InlineData("Hello")]
    [InlineData("えええ")]
    [InlineData("ご視聴ありがとうございました")]
    public void ShouldSkip_NormalText_ReturnsFalse(string input)
    {
        Assert.False(_sut.ShouldSkipTranslation(input));
    }
}
