using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class StreamUrlTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", StreamPlatform.YouTube)]
    [InlineData("https://youtu.be/abc", StreamPlatform.YouTube)]
    [InlineData("https://m.youtube.com/live/abc", StreamPlatform.YouTube)]
    [InlineData("https://www.twitch.tv/someone", StreamPlatform.Twitch)]
    [InlineData("https://twitch.tv/someone", StreamPlatform.Twitch)]
    [InlineData("HTTPS://WWW.YOUTUBE.COM/watch?v=abc", StreamPlatform.YouTube)]
    public void TryParse_KnownHosts_AcceptsAndReturnsTrimmedOriginal(string text, StreamPlatform expected)
    {
        var result = StreamUrl.TryParse(text, out var url, out var platform);

        Assert.True(result);
        Assert.Equal(expected, platform);
        Assert.Equal(text.Trim(), url);
    }

    [Fact]
    public void TryParse_WithSurroundingText_UsesFirstNonBlankLine()
    {
        var result = StreamUrl.TryParse("\n\n  https://youtu.be/abc  \nhttps://example.com/other\n文字\n", out var url, out var platform);

        Assert.True(result);
        Assert.Equal(StreamPlatform.YouTube, platform);
        Assert.Equal("https://youtu.be/abc", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("ftp://youtube.com/x")]
    [InlineData("https://example.com/youtube.com")]
    [InlineData("https://notyoutube.com/x")]
    public void TryParse_InvalidInput_Rejects(string? text)
    {
        var result = StreamUrl.TryParse(text, out var url, out _);

        Assert.False(result);
        Assert.Null(url);
    }
}
