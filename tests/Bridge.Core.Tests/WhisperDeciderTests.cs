using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class WhisperDeciderTests
{
    /// <summary>組一個只填 ForVod 會用到的欄位的影片頁面資訊。</summary>
    private static YouTubePageInfo Page(
        IReadOnlyList<string> captionLanguages,
        string? autoCaptionLanguage = null,
        string title = "ページのタイトル",
        string description = "説明欄") =>
        new("v1", title, "Author", "UC1", null, null, description, [],
            false, false, false, null, null, null, captionLanguages, AutoCaptionLanguage: autoCaptionLanguage);

    // ===== DetectLanguage =====

    [Theory]
    [InlineData("【マイクラ】建築するぞ", 3)]
    [InlineData("오늘 방송", 1)]
    [InlineData("Minecraft stream", 3)]
    public void DetectLanguage_SingleText_ReturnsExpectedLanguage(string text, int expected)
    {
        Assert.Equal(expected, WhisperDecider.DetectLanguage(text));
    }

    [Fact]
    public void DetectLanguage_TitleWithoutKana_DescriptionWithKana_ReturnsJapanese()
    {
        Assert.Equal(3, WhisperDecider.DetectLanguage("Minecraft stream", "説明欄にかながあります"));
    }

    [Fact]
    public void DetectLanguage_KoreanAfterKanaLessTexts_ReturnsKorean()
    {
        Assert.Equal(1, WhisperDecider.DetectLanguage("Minecraft", "게임 방송"));
    }

    // ===== ForLive =====

    [Fact]
    public void ForLive_EnablesWhisperWithDetectedLanguage()
    {
        var plan = WhisperDecider.ForLive("오늘 방송", "설명");

        Assert.True(plan.Enabled);
        Assert.Equal(1, plan.LanguageIndex);
        Assert.Equal("直播中", plan.Reason);
    }

    // ===== ForVod =====

    [Fact]
    public void ForVod_JapaneseCaptions_DisablesWhisperWithJapanese()
    {
        var plan = WhisperDecider.ForVod(Page(["ja"], "ja"));

        Assert.False(plan.Enabled);
        Assert.Equal(3, plan.LanguageIndex);
        Assert.Equal("有日文字幕，改用 YouTube 字幕", plan.Reason);
    }

    [Fact]
    public void ForVod_JapaneseCaptionVariant_AlsoDisablesWhisper()
    {
        var plan = WhisperDecider.ForVod(Page(["en", "ja-Hira"]));

        Assert.False(plan.Enabled);
    }

    [Fact]
    public void ForVod_NoCaptions_KoreanTitle_EnablesWhisperWithKorean()
    {
        var plan = WhisperDecider.ForVod(Page([], title: "오늘 방송"));

        Assert.True(plan.Enabled);
        Assert.Equal(1, plan.LanguageIndex);
        Assert.Equal("錄影沒有日文字幕", plan.Reason);
    }

    [Fact]
    public void ForVod_NoJapaneseCaptions_AutoCaptionLanguageWins()
    {
        var plan = WhisperDecider.ForVod(Page(["en"], "en"));

        Assert.True(plan.Enabled);
        Assert.Equal(2, plan.LanguageIndex);
    }
}
