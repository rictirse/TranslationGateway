using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class ApiHostComparerTests
{
    [Theory]
    [InlineData("http://192.168.5.4:8000/v1/", " HTTP://192.168.5.4:8000/v1", true)]
    [InlineData("http://192.168.5.4:8000/v1", "http://192.168.5.4:8000/v1", true)]
    [InlineData(" http://localhost:8000/ ", "HTTP://LOCALHOST:8000", true)]
    [InlineData("http://192.168.5.4:8000/v1//", "http://192.168.5.4:8000/v1/", true)]
    [InlineData(null, null, false)]
    [InlineData(null, "http://192.168.5.4:8000/v1", false)]
    [InlineData("http://192.168.5.4:8000/v1", null, false)]
    [InlineData("   ", "http://192.168.5.4:8000/v1", false)]
    [InlineData("http://192.168.5.4:8000/v1", "  ", false)]
    public void SameHost_NormalizesTrimSlashAndCase(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, ApiHostComparer.SameHost(a, b));
    }

    [Theory]
    // 只差 port
    [InlineData("http://192.168.5.4:8000/v1", "http://192.168.5.4:8001/v1")]
    // 只差路徑
    [InlineData("http://192.168.5.4:8000/v1", "http://192.168.5.4:8000/v2")]
    // 只差 host
    [InlineData("http://192.168.5.4:8000/v1", "http://192.168.5.5:8000/v1")]
    public void SameHost_DifferentPortPathOrHost_ReturnsFalse(string a, string b)
    {
        Assert.False(ApiHostComparer.SameHost(a, b));
    }
}
