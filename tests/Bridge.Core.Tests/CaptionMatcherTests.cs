using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class CaptionMatcherTests
{
    [Fact]
    public void Normalize_ConvertsKatakanaToHiragana()
    {
        Assert.Equal("かたかな", CaptionMatcher.Normalize("カタカナ"));
        Assert.Equal("ゔぁんだる", CaptionMatcher.Normalize("ヴァンダル"));
    }

    [Fact]
    public void Normalize_ConvertsFullWidthToHalfWidth()
    {
        Assert.Equal("ABC123", CaptionMatcher.Normalize("ＡＢＣ１２３"));
    }

    [Fact]
    public void Normalize_RemovesPunctuationSpacesAndSymbols()
    {
        Assert.Equal("こんにちは世界", CaptionMatcher.Normalize("こんにちは、世界！ "));
        Assert.Equal("他者まである", CaptionMatcher.Normalize("「他者まである!」"));
        Assert.Equal(string.Empty, CaptionMatcher.Normalize("♪!？ ……"));
    }

    [Fact]
    public void Similarity_IdenticalTexts_ReturnOne()
    {
        Assert.Equal(1.0, CaptionMatcher.Similarity("これで部屋着じゃなかったら", "これで部屋着じゃなかったら"));
        // 只差標點與片假名／平假名：正規化後相同
        Assert.Equal(1.0, CaptionMatcher.Similarity("カタカナ、ですね！", "かたかなですね"));
    }

    [Fact]
    public void Similarity_CompletelyDifferentTexts_ReturnZero()
    {
        Assert.Equal(0.0, CaptionMatcher.Similarity("こんにちは", "さようなら"));
    }

    [Fact]
    public void Similarity_EitherSideShorterThanTwoChars_ReturnZero()
    {
        Assert.Equal(0.0, CaptionMatcher.Similarity("あ", "こんにちは"));
        Assert.Equal(0.0, CaptionMatcher.Similarity("こんにちは", "あ"));
        Assert.Equal(0.0, CaptionMatcher.Similarity(string.Empty, "こんにちは"));
        Assert.Equal(0.0, CaptionMatcher.Similarity("！！！", "こんにちは"));
    }

    [Fact]
    public void Similarity_RealExample_PartialOverlapScore()
    {
        // 架構師實測的例子：Whisper「これで部屋着じゃなかったら」對 YouTube「どうしよう。これで部屋着じゃなかったら」約 0.83
        var score = CaptionMatcher.Similarity("これで部屋着じゃなかったら", "どうしよう。これで部屋着じゃなかったら");
        Assert.True(score >= 0.8, $"分數 {score} 低於 0.8");
        Assert.True(score < 1.0, $"分數 {score} 應低於 1");
    }

    [Fact]
    public void FindBest_RealExample_MatchesWithHighScore()
    {
        // 架構師實測的例子：Whisper「これで部屋着じゃなかったら」對 YouTube「どうしよう。これで部屋着じゃなかったら」約 0.83
        var cues = new[] { new CaptionCue(3000, 2000, "どうしよう。これで部屋着じゃなかったら") };

        var match = CaptionMatcher.FindBest(cues, "これで部屋着じゃなかったら");

        Assert.NotNull(match);
        Assert.True(match!.Score >= 0.8, $"分數 {match.Score} 低於 0.8");
        Assert.True(match.Score < 1.0, $"分數 {match.Score} 應低於 1");
        Assert.Equal(3000, match.StartMs);
        Assert.Equal("どうしよう。これで部屋着じゃなかったら", match.Text);
    }

    [Fact]
    public void FindBest_SentenceAcrossTwoCues_PicksTwoCueCombination()
    {
        var cues = new[]
        {
            new CaptionCue(10_000, 2000, "今日はいい天気で"),
            new CaptionCue(12_000, 2000, "とても気持ちいいです"),
        };

        var match = CaptionMatcher.FindBest(cues, "今日はいい天気でとても気持ちいいです");

        Assert.NotNull(match);
        // 只有串接兩段才會完全一樣：應選到 2 段的組合（以空白連接），起始位置是第一段
        Assert.Equal(10_000, match!.StartMs);
        Assert.Equal("今日はいい天気で とても気持ちいいです", match.Text);
        Assert.Equal(1.0, match.Score);
    }

    [Fact]
    public void FindBest_ShortText_ReturnsNull()
    {
        var cues = new[] { new CaptionCue(1000, 2000, "うん") };

        // 「うん」「またね」這類短句不參考
        Assert.Null(CaptionMatcher.FindBest(cues, "うん"));
        Assert.Null(CaptionMatcher.FindBest(cues, "またね"));
        Assert.Null(CaptionMatcher.FindBest(cues, "どう"));
    }

    [Fact]
    public void FindBest_BelowThreshold_ReturnsNull()
    {
        var cues = new[] { new CaptionCue(1000, 2000, "こんにちは") };

        Assert.Null(CaptionMatcher.FindBest(cues, "全然違う話題ですね"));
    }

    [Fact]
    public void FindBest_EmptyCues_ReturnsNull()
    {
        Assert.Null(CaptionMatcher.FindBest([], "これで部屋着じゃなかったら"));
    }

    [Fact]
    public void FindBest_ScoreExactlyAtThreshold_IsReturned()
    {
        // 門檻 0.35：分數等於門檻不算「低於」，應回傳
        var cues = new[] { new CaptionCue(1000, 2000, "abcd") };

        var match = CaptionMatcher.FindBest(cues, "abcd", threshold: 1.0);

        Assert.NotNull(match);
        Assert.Equal(1.0, match!.Score);
    }

    [Fact]
    public void TrimToMatch_RealExamples_TrimsToMatchedPart()
    {
        // 架構師用直播 23 真實資料（Python 原型）驗證過的四個例子：cue 只留跟本句對到的那一小段
        var cases = new[]
        {
            ("ですね。道路を忘れるなんてねえ。もう橋 接設計者の風にも受けないねえ。そんな", "私設計者の風上に設けない", "設計者の風にも受けない"),
            ("使ってくださいと言わんばかりの土台が ありますけど。無視、ム視、ムシ。よし。", "ばかりの土台がありますけど", "ばかりの土台が ありますけど。"),
            ("、えっと、分割点を作る。で、ソピアは 賢いのでここに木があると木がバッカン", "ここに木が", "ここに木が"),
            ("油圧系を使って羽橋を作ろう。羽橋っての", "油圧計を使って", "油圧系を使って"),
        };

        foreach (var (caption, text, expected) in cases)
        {
            var (trimmed, score) = CaptionMatcher.TrimToMatch(caption, text);
            Assert.Equal(expected, trimmed);
            Assert.True(score >= 0.35, $"「{text}」裁切後分數 {score} 低於 0.35");
        }
    }

    [Fact]
    public void TrimToMatch_RemovesBracketTags()
    {
        var (trimmed, _) = CaptionMatcher.TrimToMatch("[音楽]ん、でも真ん中に道路のあれが", "でも真ん中に道路のあれが");

        Assert.DoesNotContain("[", trimmed);
        Assert.DoesNotContain("音楽", trimmed);
        Assert.StartsWith("ん、でも真ん中に道路のあれが", trimmed);
    }

    [Fact]
    public void TrimToMatch_CaptionNotMuchLonger_DoesNotTrim()
    {
        var (trimmed, score) = CaptionMatcher.TrimToMatch("なんか尺やし。", "なんかシャクやし");

        // caption 沒比本句長多少（正規化後 ≤ 1.3 倍）：原樣回傳（修剪空白後）
        Assert.Equal("なんか尺やし。", trimmed);
        Assert.True(score >= 0.35, $"分數 {score} 低於 0.35");
    }
}
