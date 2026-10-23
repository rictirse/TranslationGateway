using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>YouTube 說明欄過濾：雜訊行丟掉、分隔標記截斷、裝飾行判斷、獨立 PR／パパ 的邊界判斷。</summary>
public class StreamDescriptionFilterTests
{
    [Fact]
    public void Filter_DropsNoiseLines_AndKeepsCollabLine()
    {
        var description =
            """
            今日はAPEXのコラボ配信！
            コラボ：橘ひなの、英リサ
            https://example.com/pr
            イラスト：○○ママ
            Live2D：△△さん
            BGM：△△の曲
            #PR 提供：株式会社テスト
            """;

        var result = StreamDescriptionFilter.Filter(description);

        Assert.Equal("今日はAPEXのコラボ配信！\nコラボ：橘ひなの、英リサ", result);
    }

    [Fact]
    public void Filter_TruncateMarker_DropsRestOfDescription()
    {
        var description = "コラボ：橘ひなの\n配信のお約束\n今後の予定";

        Assert.Equal("コラボ：橘ひなの", StreamDescriptionFilter.Filter(description));
    }

    [Fact]
    public void Filter_MarkerOnFirstLine_DropsOnlyThatLine()
    {
        var description = "配信のお約束\nコラボ：橘ひなの";

        Assert.Equal("コラボ：橘ひなの", StreamDescriptionFilter.Filter(description));
    }

    [Fact]
    public void Filter_MarkerOnFirstLine_SkipsRulesUntilSeparator()
    {
        // 實際案例：說明欄開頭是「配信のお約束」規則區，規則後面接裝飾分隔線，再來才是自我介紹
        var description = "˗ˏˋ  配信のお約束　ˎˊ˗ \n\n✦誹謗中傷はしないこと\n✦伝書鳩行為をしないこと！\n\n.｡:+* ﾟ ゜ﾟ *+:｡.\nぶいすぽっ！所属の猫汰つなです！";

        Assert.Equal("ぶいすぽっ！所属の猫汰つなです！", StreamDescriptionFilter.Filter(description));
    }

    [Fact]
    public void Filter_DropsDecorationOnlyLines()
    {
        var description = ".｡:+* ﾟ ゜ﾟ *+:｡.\n˗ˏˋ ˎˊ˗\n本日の配信内容";

        Assert.Equal("本日の配信内容", StreamDescriptionFilter.Filter(description));
    }

    [Fact]
    public void Filter_StandalonePrAndPapa_AreDropped()
    {
        Assert.Equal(string.Empty, StreamDescriptionFilter.Filter("PR：株式会社テスト"));
        Assert.Equal(string.Empty, StreamDescriptionFilter.Filter("パパ：△△"));
    }

    [Fact]
    public void Filter_PrInsideWordOrPapaInsideKatakana_AreKept()
    {
        var description = "PROJECT rgb\nAPRIL FOOLS\nパパラッチ";

        Assert.Equal("PROJECT rgb\nAPRIL FOOLS\nパパラッチ", StreamDescriptionFilter.Filter(description));
    }

    [Fact]
    public void Filter_TruncatesToMaxLength()
    {
        var description = string.Join("\n", Enumerable.Repeat("あいうえおかきくけこ", 20));

        Assert.Equal(50, StreamDescriptionFilter.Filter(description, 50).Length);
    }

    [Fact]
    public void Filter_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, StreamDescriptionFilter.Filter(null));
        Assert.Equal(string.Empty, StreamDescriptionFilter.Filter(" 　\n"));
    }

    [Fact]
    public void Filter_OutputHasNoCarriageReturn()
    {
        var result = StreamDescriptionFilter.Filter("一行目\r\n二行目");

        Assert.Equal("一行目\n二行目", result);
        Assert.DoesNotContain('\r', result);
    }
}
