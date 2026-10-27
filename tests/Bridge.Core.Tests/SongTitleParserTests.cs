using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>SongTitleParser：從章節標題拆曲名／歌手——去名次／編號、最後一個分隔符切開、去曲名括號。</summary>
public class SongTitleParserTests
{
    public static TheoryData<string, string, string?> ParseCases => new()
    {
        // 名次（位）＋全形斜線（前後有空白）
        { "10位 星座になれたら ／ 結束バンド", "星座になれたら", "結束バンド" },
        // 名次＋前後空白的半形橫線
        { "10位 回る空うさぎ - Orangestar", "回る空うさぎ", "Orangestar" },
        // 沒有名次
        { "蒼の音階 ／ 涼海ネモ", "蒼の音階", "涼海ネモ" },
        // 非歌曲章節：沒有分隔符，整串當曲名、沒有歌手
        { "配信開始", "配信開始", null },
        // 歌手的連字號前後沒有空白：不是分隔符
        { "01位 夜明けと蛍 ／ n-buna", "夜明けと蛍", "n-buna" },
        // 「第」開頭的名次＋曲名帶括號
        { "第3位「怪獣の花唄」 - Vaundy", "怪獣の花唄", "Vaundy" },
        // 開頭的編號（數字＋點）
        { "1. 曲名", "曲名", null },
    };

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void Parse_ChapterTitle_SplitsTitleAndArtist(string chapterTitle, string expectedTitle, string? expectedArtist)
    {
        var query = SongTitleParser.Parse(chapterTitle);

        Assert.Equal(new SongQuery(expectedTitle, expectedArtist), query);
    }

    // 拆不出曲名（空白）：回 null。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　")]
    public void Parse_BlankTitle_ReturnsNull(string chapterTitle)
    {
        Assert.Null(SongTitleParser.Parse(chapterTitle));
    }

    // 半形斜線前後沒有空白也算分隔（取最後一個出現的位置）。
    [Fact]
    public void Parse_HalfWidthSlashWithoutSpaces_SplitsAtLastOccurrence()
    {
        Assert.Equal(new SongQuery("前半", "後半"), SongTitleParser.Parse("前半/後半"));
        // 取最後一個：曲名本身帶斜線時，最後一段才是歌手
        Assert.Equal(new SongQuery("A/B", "C"), SongTitleParser.Parse("A/B/C"));
    }

    // 歌手為空白（分隔符在結尾）：Artist = null。
    [Fact]
    public void Parse_EmptyArtist_ArtistIsNull()
    {
        Assert.Equal(new SongQuery("曲名", null), SongTitleParser.Parse("曲名 ／ "));
    }

    // 曲名前後多層括號都去掉。
    [Fact]
    public void Parse_NestedBrackets_Stripped()
    {
        Assert.Equal(new SongQuery("怪獣の花唄", "Vaundy"), SongTitleParser.Parse("第1位『「怪獣の花唄」』 - Vaundy"));
    }
}
