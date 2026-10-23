using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class YouTubeVideoIdTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL123&t=30s", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    public void TryParse_YouTubeUrls_ReturnsVideoId(string text, string expected)
    {
        Assert.Equal(expected, YouTubeVideoId.TryParse(text));
    }

    [Theory]
    [InlineData("http://127.0.0.1:12345/")]
    [InlineData("http://127.0.0.1:12345/live/stream")]
    [InlineData("https://rr3---sn-abcd.googlevideo.com/videoplayback?id=videofile&mime=video")]
    [InlineData("C:\\video.mp4")]
    [InlineData("/home/user/video.mp4")]
    [InlineData("")]
    public void TryParse_NonYouTubeText_ReturnsNull(string? text)
    {
        Assert.Null(YouTubeVideoId.TryParse(text));
    }

    [Fact]
    public void TryParse_Null_ReturnsNull()
    {
        Assert.Null(YouTubeVideoId.TryParse(null));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc12345")]       // id 太短
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQAB")]  // id 太長
    [InlineData("https://youtu.be/dQw4w9WgXc!")]                   // 含 videoId 不允許的字元（11 碼）
    [InlineData("https://www.youtube.com/live/short")]             // live 路徑的 id 太短
    public void TryParse_InvalidVideoIdLengthOrChars_ReturnsNull(string text)
    {
        Assert.Null(YouTubeVideoId.TryParse(text));
    }
}
