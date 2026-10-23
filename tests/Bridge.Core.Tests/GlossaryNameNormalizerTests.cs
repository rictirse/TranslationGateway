using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class GlossaryNameNormalizerTests
{
    [Theory]
    [InlineData("ホロライブ", "ほろらいぶ")]          // 平假名 ↔ 片假名
    [InlineData("花芽 すみれ", "花芽すみれ")]          // 空白
    [InlineData("ぶいすぽっ！", "ぶいすぽっ")]          // 全形標點（NFKC 後去除）
    [InlineData("ぶいすぽっ!", "ぶいすぽっ")]           // 半形標點
    [InlineData("VALORANT", "valorant")]              // 大小寫
    [InlineData("ＶＡＬＯＲＡＮＴ", "valorant")]       // 全形英數（NFKC）
    [InlineData("カースター", "カースタ")]              // 長音
    [InlineData("ホロライブ・プロダクション", "ホロライブプロダクション")]  // 中黑點
    [InlineData("ホロライブ･プロダクション", "ホロライブプロダクション")]   // 半形中黑點
    [InlineData("A/B", "AB")]                          // 斜線
    public void SameName_WritesDifferentlyNormalized_Same(string a, string b)
    {
        Assert.True(GlossaryNameNormalizer.SameName(a, b));
        Assert.Equal(GlossaryNameNormalizer.Normalize(a), GlossaryNameNormalizer.Normalize(b));
    }

    [Fact]
    public void Normalize_RemovesLongSoundMark()
    {
        // 長音符號會被忽略：ラーメン 與 ラメン 相同
        Assert.Equal("ラメン", GlossaryNameNormalizer.Normalize("ラーメン"));
        Assert.True(GlossaryNameNormalizer.SameName("ラーメン", "ラメン"));
    }

    [Fact]
    public void Normalize_TrimsSurroundingWhitespace()
    {
        Assert.Equal("ホロライブ", GlossaryNameNormalizer.Normalize("  ほろらいぶ\t"));
    }

    [Theory]
    [InlineData("", "")]            // 空字串不算相同
    [InlineData("   ", "ー")]       // 正規化後沒有內容不算相同
    [InlineData("ー！", "、。")]     // 只剩被去除的字元不算相同
    [InlineData("ホロライブ", "にじさんじ")]
    [InlineData("花芽すみれ", "花芽すみ")]
    public void SameName_DifferentOrEmpty_False(string a, string b)
    {
        Assert.False(GlossaryNameNormalizer.SameName(a, b));
    }

    [Fact]
    public void Normalize_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, GlossaryNameNormalizer.Normalize(null));
        Assert.Equal(string.Empty, GlossaryNameNormalizer.Normalize("  "));
    }

    // ===== StripHonorific =====

    [Theory]
    [InlineData("うるは先輩", "うるは")]          // 先輩
    [InlineData("せんぱい", "せんぱい")]          // 去掉後是空的 → 原字串
    [InlineData("もかちゃん", "もか")]            // ちゃん
    [InlineData("もかチャン", "もか")]            // 片假名寫法
    [InlineData("つるぎさん", "つるぎ")]          // さん
    [InlineData("ツルギサン", "ツルギ")]
    [InlineData("ゆうたくん", "ゆうた")]          // くん
    [InlineData("神木クン", "神木")]
    [InlineData("田中君", "田中")]                // 君
    [InlineData("お姉さま", "お姉")]              // さま
    [InlineData("会長様", "会長")]                // 樣
    [InlineData("お嬢たん", "お嬢")]              // たん
    [InlineData("田中氏", "田中")]                // 氏
    [InlineData("山田先生", "山田")]              // 先生
    [InlineData("殿", "殿")]                      // 去掉後太短 → 原字串
    [InlineData("もか醬", "もか")]                // 繁體「醬」
    [InlineData("もか酱", "もか")]                // 簡體「酱」
    [InlineData("お姉さん", "お姉")]              // 去掉後剩 2 個字，可以接受
    [InlineData("うるは", "うるは")]              // 沒有稱謂 → 原字串
    [InlineData(" うるは先輩 ", "うるは")]        // 先 Trim
    public void StripHonorific_RemovesOneTrailingHonorific(string input, string expected)
    {
        Assert.Equal(expected, GlossaryNameNormalizer.StripHonorific(input));
    }

    [Fact]
    public void StripHonorific_RemovesOnlyOneHonorific()
    {
        // 只去掉一個結尾稱謂：「さん」去掉後剩下「様」開頭的名稱不再剝
        Assert.Equal("会長様", GlossaryNameNormalizer.StripHonorific("会長様さん"));
    }

    [Fact]
    public void StripHonorific_NullOrEmpty_ReturnsTrimmedEmpty()
    {
        Assert.Equal(string.Empty, GlossaryNameNormalizer.StripHonorific(null));
        Assert.Equal(string.Empty, GlossaryNameNormalizer.StripHonorific("   "));
    }
}
