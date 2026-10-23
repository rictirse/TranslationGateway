using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class WhisperLanguagesTests
{
    [Fact]
    public void Constants_MatchPotPlayerDropdown()
    {
        Assert.Equal(0, WhisperLanguages.Auto);
        Assert.Equal(3, WhisperLanguages.Japanese);
    }

    [Theory]
    [InlineData("ja", 3)]
    [InlineData("ja-JP", 3)]
    [InlineData("JA", 3)]
    [InlineData("ko", 1)]
    [InlineData("en-US", 2)]
    [InlineData("zh-Hant", 4)]
    [InlineData("zh-TW", 4)]
    [InlineData("zh", 4)]
    [InlineData("yue", 35)]
    [InlineData("zh-yue", 35)]
    [InlineData("su", 100)]
    [InlineData("iw", 21)]
    [InlineData("jv", 99)]
    [InlineData("nb", 30)]
    [InlineData(" jA ", 3)]
    public void IndexOf_KnownCodes_ReturnsDropdownIndex(string languageCode, int expected)
    {
        Assert.Equal(expected, WhisperLanguages.IndexOf(languageCode));
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("")]
    [InlineData("   ")]
    public void IndexOf_UnknownOrEmpty_ReturnsNull(string? languageCode)
    {
        Assert.Null(WhisperLanguages.IndexOf(languageCode));
    }

    [Fact]
    public void IndexOf_Null_ReturnsNull()
    {
        Assert.Null(WhisperLanguages.IndexOf(null));
    }

    [Fact]
    public void NameOf_ValidIndex_ReturnsCode()
    {
        Assert.Equal("ja", WhisperLanguages.NameOf(3));
        Assert.Equal("auto", WhisperLanguages.NameOf(0));
    }

    [Fact]
    public void NameOf_OutOfRange_ReturnsIndexAsString()
    {
        Assert.Equal("999", WhisperLanguages.NameOf(999));
        Assert.Equal("-1", WhisperLanguages.NameOf(-1));
    }
}
