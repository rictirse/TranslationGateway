using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class ChapterExtractorTests
{
    /// <summary>把（行文字、時間戳連結文字、秒數）組成留言：連結的 StartIndex 由文字位置算出；Link 是 null 表示該行沒有連結。</summary>
    private static YouTubeComment BuildComment(string id, int likes, params (string Line, string? Link, int Seconds)[] lines)
    {
        var text = string.Join("\n", lines.Select(static line => line.Line));
        List<CommentTimestamp> timestamps = [];
        var offset = 0;
        foreach (var (line, link, seconds) in lines)
        {
            if (link is not null)
            {
                var index = line.IndexOf(link, StringComparison.Ordinal);
                Assert.True(index >= 0, $"行「{line}」裡找不到連結文字「{link}」");
                timestamps.Add(new CommentTimestamp(offset + index, link.Length, seconds));
            }

            offset += line.Length + 1;
        }

        return new YouTubeComment(id, text, likes, timestamps);
    }

    /// <summary>卡片範例的セトリ留言：前 4 行曲目（全形空白排版），開頭是標題行。</summary>
    private static YouTubeComment SetlistComment() => BuildComment("setlist", 3,
        ("第4回リスナー投票ランキング歌枠 のセトリ↓", null, 0),
        ("", null, 0),
        ("10位　8:35　星座になれたら ／ 結束バンド", "8:35", 8 * 60 + 35),
        ("09位　17:33　Booo! ／ TOKOTOKO", "17:33", 17 * 60 + 33),
        ("08位　26:10　アイドル ／ 新井圭一", "26:10", 26 * 60 + 10),
        ("07位　34:05　夜に駆ける ／ Ayase", "34:05", 34 * 60 + 5));

    [Fact]
    public void FromComments_SetlistComment_BuildsChapters()
    {
        var chapters = ChapterExtractor.FromComments([SetlistComment()], null);

        Assert.Equal(
        [
            new VideoChapter(0, "開始"),
            new VideoChapter(515000, "10位 星座になれたら ／ 結束バンド"),
            new VideoChapter(1053000, "09位 Booo! ／ TOKOTOKO"),
            new VideoChapter(1570000, "08位 アイドル ／ 新井圭一"),
            new VideoChapter(2045000, "07位 夜に駆ける ／ Ayase")
        ], chapters);
    }

    [Fact]
    public void FromComments_NonAscendingComment_Rejected()
    {
        // 精華片段留言：52:32 後面接 50:00，時間沒有遞增 → 不合格；選到另一則セトリ
        var highlights = BuildComment("highlights", 100,
            ("6:33 シーン1", "6:33", 393),
            ("15:26 シーン2", "15:26", 926),
            ("52:32 シーン3", "52:32", 3152),
            ("50:00 シーン4", "50:00", 3000));

        var chapters = ChapterExtractor.FromComments([highlights, SetlistComment()], 3600_000);

        Assert.Equal(5, chapters.Count);
        Assert.Equal(new VideoChapter(0, "開始"), chapters[0]);
        Assert.Equal("10位 星座になれたら ／ 結束バンド", chapters[1].Title);
        Assert.DoesNotContain(chapters, static chapter => chapter.Title.StartsWith("シーン", StringComparison.Ordinal));
    }

    [Fact]
    public void FromComments_SetlistKeywordBeatsMoreLines()
    {
        // 5 行沒有關鍵字 vs 3 行有「セトリ」→ 選後者
        var noKeyword = BuildComment("plain", 999,
            ("1:00 曲1", "1:00", 60),
            ("2:00 曲2", "2:00", 120),
            ("3:00 曲3", "3:00", 180),
            ("4:00 曲4", "4:00", 240),
            ("5:00 曲5", "5:00", 300));
        var withKeyword = BuildComment("setlist", 1,
            ("セトリ↓", null, 0),
            ("0:10 曲A", "0:10", 10),
            ("0:20 曲B", "0:20", 20),
            ("0:30 曲C", "0:30", 30));

        var chapters = ChapterExtractor.FromComments([noKeyword, withKeyword], null);

        Assert.Equal(4, chapters.Count);
        Assert.Equal(new VideoChapter(0, "開始"), chapters[0]);
        Assert.Equal(new VideoChapter(10000, "曲A"), chapters[1]);
        Assert.Equal("曲C", chapters[^1].Title);
    }

    [Fact]
    public void FromComments_TieBreakByLikesThenOrder()
    {
        // 三則同行數（3 行）、都沒有關鍵字：讚數 99 > 10；b 與 c 同讚數 → 清單中較前的 b 勝
        var first = BuildComment("a", 10,
            ("1:00 A1", "1:00", 60), ("2:00 A2", "2:00", 120), ("3:00 A3", "3:00", 180));
        var second = BuildComment("b", 99,
            ("1:00 B1", "1:00", 60), ("2:00 B2", "2:00", 120), ("3:00 B3", "3:00", 180));
        var third = BuildComment("c", 99,
            ("1:00 C1", "1:00", 60), ("2:00 C2", "2:00", 120), ("3:00 C3", "3:00", 180));

        var chapters = ChapterExtractor.FromComments([first, second, third], null);

        Assert.Equal(4, chapters.Count);
        Assert.Equal("B1", chapters[1].Title);
        Assert.Equal("B3", chapters[^1].Title);
    }

    [Fact]
    public void FromComments_FewerThanThreeLines_ReturnsEmpty()
    {
        var comment = BuildComment("short", 50,
            ("セトリ", null, 0),
            ("1:00 曲A", "1:00", 60),
            ("2:00 曲B", "2:00", 120));

        Assert.Empty(ChapterExtractor.FromComments([comment], null));
    }

    [Fact]
    public void FromComments_TimestampBeyondDuration_Rejected()
    {
        // 影片長度 10 分鐘，但最後一行在 11:40 → 整則不合格
        var comment = BuildComment("over", 5,
            ("1:00 曲A", "1:00", 60),
            ("2:00 曲B", "2:00", 120),
            ("11:40 曲C", "11:40", 700));

        Assert.Empty(ChapterExtractor.FromComments([comment], 600_000));
    }

    [Fact]
    public void FromComments_TitleCleanup()
    {
        var comment = BuildComment("titles", 0,
            ("0:34 - タイトル", "0:34", 34),
            ("1:23", "1:23", 83),
            ("2:45　・　エンディング", "2:45", 165),
            ("3:33 ギリギリで生きてる", "3:33", 213),
            ("00:06:33 ｜ 曲名", "00:06:33", 393));

        var chapters = ChapterExtractor.FromComments([comment], null);

        Assert.Equal(
        [
            new VideoChapter(0, "開始"),
            new VideoChapter(34000, "タイトル"),
            new VideoChapter(83000, "(無標題)"),
            new VideoChapter(165000, "エンディング"),
            new VideoChapter(213000, "ギリギリで生きてる"),
            new VideoChapter(393000, "曲名")
        ], chapters);
    }

    [Fact]
    public void FromComments_FirstChapterAtZero_NoExtraStart()
    {
        var comment = BuildComment("zero", 0,
            ("0:00 オープニング", "0:00", 0),
            ("1:00 曲A", "1:00", 60),
            ("2:00 曲B", "2:00", 120));

        var chapters = ChapterExtractor.FromComments([comment], null);

        Assert.Equal(
        [
            new VideoChapter(0, "オープニング"),
            new VideoChapter(60000, "曲A"),
            new VideoChapter(120000, "曲B")
        ], chapters);
    }

    /// <summary>典型的 YouTube 說明欄章節：m:ss 與 h:mm:ss 混用、第一章在 0:00。</summary>
    private const string TypicalDescription =
        "0:00 配信開始\n" +
        "13:12 10位 回る空うさぎ - Orangestar\n" +
        "17:33 9位 Booo! ／ TOKOTOKO\n" +
        "1:02:56 3位 夜に駆ける ／ Ayase";

    [Fact]
    public void FromDescription_ParsesTypicalDescription()
    {
        var chapters = ChapterExtractor.FromDescription(TypicalDescription, null);

        // 第一章在 0:00：不補「開始」；m:ss 與 h:mm:ss 都要能解析
        Assert.Equal(
        [
            new VideoChapter(0, "配信開始"),
            new VideoChapter(792000, "10位 回る空うさぎ - Orangestar"),
            new VideoChapter(1053000, "9位 Booo! ／ TOKOTOKO"),
            new VideoChapter(3776000, "3位 夜に駆ける ／ Ayase")
        ], chapters);
    }

    [Fact]
    public void FromDescription_IgnoresNonAscending()
    {
        // 時間沒有嚴格遞增（5:00 → 3:00）→ 不合格
        var chapters = ChapterExtractor.FromDescription("5:00 曲A\n3:00 曲B\n6:00 曲C", null);

        Assert.Empty(chapters);
    }

    [Fact]
    public void FromDescription_NullOrEmpty()
    {
        Assert.Empty(ChapterExtractor.FromDescription(null, null));
        Assert.Empty(ChapterExtractor.FromDescription(string.Empty, null));
        Assert.Empty(ChapterExtractor.FromDescription("   \n  \n", null));
    }

    [Fact]
    public void FromDescription_SharesTitleCleanupWithComments()
    {
        // 與 FromComments_TitleCleanup 同一組行（時間戳放在文字裡，而不是當連結）：清理結果必須相同
        var description = "0:34 - タイトル\n1:23\n2:45　・　エンディング\n3:33 ギリギリで生きてる\n00:06:33 ｜ 曲名";
        var comment = BuildComment("titles", 0,
            ("0:34 - タイトル", "0:34", 34),
            ("1:23", "1:23", 83),
            ("2:45　・　エンディング", "2:45", 165),
            ("3:33 ギリギリで生きてる", "3:33", 213),
            ("00:06:33 ｜ 曲名", "00:06:33", 393));

        var fromDescription = ChapterExtractor.FromDescription(description, null);
        var fromComments = ChapterExtractor.FromComments([comment], null);

        Assert.Equal(
        [
            new VideoChapter(0, "開始"),
            new VideoChapter(34000, "タイトル"),
            new VideoChapter(83000, "(無標題)"),
            new VideoChapter(165000, "エンディング"),
            new VideoChapter(213000, "ギリギリで生きてる"),
            new VideoChapter(393000, "曲名")
        ], fromDescription);
        Assert.Equal(fromComments, fromDescription);
    }

    [Fact]
    public void FromDescription_FewerThanThreeTimestamps_ReturnsEmpty()
    {
        Assert.Empty(ChapterExtractor.FromDescription("1:00 曲A\n2:00 曲B", null));
    }

    [Fact]
    public void FromDescription_TimestampBeyondDuration_ReturnsEmpty()
    {
        // 影片長度 10 分鐘，但有一行在 11:40 → 不合格
        Assert.Empty(ChapterExtractor.FromDescription("1:00 A\n2:00 B\n11:40 C", 600_000));
    }

    [Fact]
    public void FromDescription_TakesFirstTimestamp_RemovesAllTimestamps()
    {
        // 一行有兩個時間戳：第一個當這行的時間，標題兩個都移除
        var chapters = ChapterExtractor.FromDescription("0:10 1:20 曲A\n2:00 曲B\n3:00 曲C", null);

        Assert.Equal(new VideoChapter(10000, "曲A"), chapters[1]);
    }

    [Fact]
    public void FromDescription_InvalidMinutesOrSeconds_NotTimestamp()
    {
        // 99:99 分秒都 ≥ 60：不是時間戳，那行不算時間軸行
        Assert.Empty(ChapterExtractor.FromDescription("99:99 曲A\n2:00 曲B\n3:00 曲C", null));
    }

    // ===== FromText（人工貼上的セトリ；規則同 FromDescription）=====

    /// <summary>使用者貼上的セトリ：全形空白排版（卡片範例同款）、第一行不在 0:00（會補「開始」）。</summary>
    private const string PastedSetlist =
        "第4回リスナー投票ランキング歌枠 のセトリ↓\n" +
        "\n" +
        "10位　8:35　星座になれたら ／ 結束バンド\n" +
        "09位　17:33　Booo! ／ TOKOTOKO\n" +
        "08位　26:10　アイドル ／ 新井圭一\n" +
        "07位　34:05　夜に駆ける ／ Ayase";

    [Fact]
    public void FromText_PastedSetlistWithFullWidthSpaces_BuildsChapters()
    {
        var chapters = ChapterExtractor.FromText(PastedSetlist, null);

        Assert.Equal(
        [
            new VideoChapter(0, "開始"),
            new VideoChapter(515000, "10位 星座になれたら ／ 結束バンド"),
            new VideoChapter(1053000, "09位 Booo! ／ TOKOTOKO"),
            new VideoChapter(1570000, "08位 アイドル ／ 新井圭一"),
            new VideoChapter(2045000, "07位 夜に駆ける ／ Ayase")
        ], chapters);
    }

    [Fact]
    public void FromText_FirstLineAtZero_NoExtraStart()
    {
        var chapters = ChapterExtractor.FromText("0:00 配信開始\n1:00 曲A\n2:00 曲B", null);

        Assert.Equal(
        [
            new VideoChapter(0, "配信開始"),
            new VideoChapter(60000, "曲A"),
            new VideoChapter(120000, "曲B")
        ], chapters);
    }

    [Fact]
    public void FromText_FewerThanThreeTimestamps_ReturnsEmpty()
    {
        Assert.Empty(ChapterExtractor.FromText("1:00 曲A\n2:00 曲B", null));
        Assert.Empty(ChapterExtractor.FromText(null, null));
        Assert.Empty(ChapterExtractor.FromText("   \n  \n", null));
    }

    [Fact]
    public void FromText_NonAscending_ReturnsEmpty()
    {
        Assert.Empty(ChapterExtractor.FromText("5:00 曲A\n3:00 曲B\n6:00 曲C", null));
    }

    [Fact]
    public void FromText_TimestampBeyondDuration_ReturnsEmpty()
    {
        Assert.Empty(ChapterExtractor.FromText("1:00 A\n2:00 B\n11:40 C", 600_000));
    }

    [Fact]
    public void FromText_SharesRulesWithFromDescription()
    {
        // 同一段文字：FromDescription（說明欄）與 FromText（人工貼上）解析結果必須相同
        Assert.Equal(
            ChapterExtractor.FromDescription(PastedSetlist, null),
            ChapterExtractor.FromText(PastedSetlist, null));
    }
}
