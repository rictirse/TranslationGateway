using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class AuditHashTests
{
    [Theory]
    [InlineData("hello", "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("翻譯", "af45f50c7f4ab6a2cd4f2df4cf2d992c067bf64e766db6414a28284b5738ec5b")]
    public void Of_KnownContent_ReturnsSha256Hex(string content, string expected)
    {
        Assert.Equal(expected, AuditHash.Of(content));
    }

    [Fact]
    public void Of_SameContent_ReturnsSameHash()
    {
        Assert.Equal(AuditHash.Of("同じ內容"), AuditHash.Of("同じ內容"));
    }

    [Fact]
    public void Of_DifferentContent_ReturnsDifferentHash()
    {
        Assert.NotEqual(AuditHash.Of("甲"), AuditHash.Of("乙"));
    }

    [Fact]
    public void Of_Always_ReturnsLowercase64Chars()
    {
        var hash = AuditHash.Of("長度與大小寫的檢查");

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(c is (>= '0' and <= '9') or (>= 'a' and <= 'f'), $"不是小寫 hex：{hash}"));
    }
}
