using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>遊戲用語探勘的計數器：拆詞、句數累計、跨主題普遍度與候選詞篩選。</summary>
public class TermDiscoveryCounterTests
{
    private static void AddLines(TermDiscoveryCounter counter, params string[] lines)
    {
        foreach (var line in lines)
        {
            counter.Add(line);
        }
    }

    [Fact]
    public void ExtractWords_SplitsKatakanaRuns_InOrderWithoutDuplicates()
    {
        Assert.Equal(["トップ", "ガリオ"], TermDiscoveryCounter.ExtractWords("トップ行ってガリオとガリオ"));
    }

    [Fact]
    public void ExtractWords_ShortKatakanaNotCounted()
    {
        // 2 個字的片假名不算（3 個字的アイウ算）
        Assert.Equal(["アイウ"], TermDiscoveryCounter.ExtractWords("アとイウとアイウ"));
    }

    [Fact]
    public void ExtractWords_DropsWordStartingWithLongVowelMark()
    {
        // 以ー開頭的連續片假名丟掉（ーラー）；中間的長音符號照算（ガリオン）
        Assert.Equal(["ガリオン"], TermDiscoveryCounter.ExtractWords("ガリオンとーラー"));
    }

    [Fact]
    public void TakeDue_ThreeSentences_Returned_TwoNotReturned()
    {
        var counter = new TermDiscoveryCounter();
        AddLines(counter, "ガリオだ", "ガリオとナイス", "またガリオ");

        var due = counter.TakeDue([]);

        Assert.Equal("ガリオ", Assert.Single(due).Word);
        // ナイス只在 1 句出現，不到 3 句
    }

    [Fact]
    public void Add_SameWordTwiceInOneSentence_CountsOnce()
    {
        var counter = new TermDiscoveryCounter();
        // 同一句裡ガリオ出現 2 次只算 1 句：加起來只有 2 句，不到 3 句不會被 TakeDue 回傳
        AddLines(counter, "ガリオとガリオ", "ガリオだ");

        Assert.Empty(counter.TakeDue([]));
    }

    [Fact]
    public void TakeDue_TopicFrequencyAtLeastThree_NotReturned_TwoReturned()
    {
        var counter = new TermDiscoveryCounter();
        AddLines(counter, "ガリオだ", "ガリオと", "またガリオ", "ナイスだ", "ナイスと", "またナイス");

        counter.SetTopicFrequency(new Dictionary<string, int> { ["ガリオ"] = 3, ["ナイス"] = 2 });

        Assert.Equal(["ナイス"], counter.TakeDue([]).Select(c => c.Word).ToList());
    }

    [Fact]
    public void TakeDue_ContainingKnownName_NotReturned()
    {
        var counter = new TermDiscoveryCounter();
        AddLines(counter, "ガリオだ", "ガリオと", "またガリオ");

        // 名稱等於詞：不回傳
        Assert.Empty(counter.TakeDue(["ガリオ"]));

        // 名稱包含詞（ガリオス ⊃ ガリオ）：不回傳
        counter.Reset();
        AddLines(counter, "ガリオだ", "ガリオと", "またガリオ");
        Assert.Empty(counter.TakeDue(["ガリオス"]));

        // 名稱少於 3 個字：不影響
        counter.Reset();
        AddLines(counter, "ガリオだ", "ガリオと", "またガリオ");
        Assert.Equal(["ガリオ"], counter.TakeDue(["ガリ", "ポ"]).Select(c => c.Word).ToList());
    }

    [Fact]
    public void TakeDue_AlreadyJudged_NotReturnedAgain_ResetClears()
    {
        var counter = new TermDiscoveryCounter();
        AddLines(counter, "ガリオだ", "ガリオと", "またガリオ");
        Assert.Equal(["ガリオ"], counter.TakeDue([]).Select(c => c.Word).ToList());

        // 已回傳過的詞，再 Add 幾次也不會再回傳
        AddLines(counter, "ガリオ行く", "ガリオ来た", "ガリオだ");
        Assert.Empty(counter.TakeDue([]));

        // Reset 之後會再回傳
        counter.Reset();
        AddLines(counter, "ガリオだ", "ガリオと", "またガリオ");
        Assert.Equal(["ガリオ"], counter.TakeDue([]).Select(c => c.Word).ToList());
    }

    [Fact]
    public void TakeDue_MoreThanThirtyCandidates_ReturnsThirtyByCountDesc()
    {
        var counter = new TermDiscoveryCounter();
        var first = "カキクケコ";
        var second = "サシスセソ";
        var third = "タチツテト";
        for (var i = 0; i < 35; i++)
        {
            // 35 個相異的 3 字片假名（カサタ、カサチ…），每個出現 3～5 句（都達下限）
            var word = $"{first[i / 25]}{second[(i / 5) % 5]}{third[i % 5]}";
            for (var count = 0; count < 3 + i % 3; count++)
            {
                counter.Add($"{word}だ{count}");
            }
        }

        var due = counter.TakeDue([]);

        Assert.Equal(TermDiscoveryCounter.MaxCandidatesPerRun, due.Count);
        // 依 Count 遞減
        Assert.True(due.Zip(due.Skip(1), (a, b) => a.Count >= b.Count).All(ok => ok));
        // 句數最多的詞（出現 5 句）在前面，而且 5 句的都在 30 個之內
        Assert.Equal(5, due[0].Count);
        Assert.Contains(due, c => c.Count == 5);
    }

    [Fact]
    public void TakeDue_ExamplesAtMostTwo_TruncatedTo100Chars()
    {
        var counter = new TermDiscoveryCounter();
        var longLine = $"ガリオ {new string('あ', 150)}";
        AddLines(counter, longLine, "ガリオと", "またガリオ", "最後のガリオ");

        var candidate = Assert.Single(counter.TakeDue([]));

        Assert.Equal(2, candidate.Examples.Count);
        // 例句去頭尾空白後超過 100 字截斷；最早的 2 句
        Assert.Equal(longLine[..100], candidate.Examples[0]);
        Assert.Equal("ガリオと", candidate.Examples[1]);
    }

    [Fact]
    public void BuildTopicFrequency_SameGameCardIsOneTopic_NullGameCardEachItsOwn()
    {
        var rows = new[]
        {
            new TopicSubtitleSource(1, 7, "ガリオだ"),
            new TopicSubtitleSource(2, 7, "ガリオと"),
            new TopicSubtitleSource(3, null, "ガリオだ"),
            new TopicSubtitleSource(4, null, "ガリオと"),
        };

        var frequency = TermDiscoveryCounter.BuildTopicFrequency(rows);

        // 同一個 GameCardId 的兩場直播算 1 個主題，加上兩個沒有遊戲卡的直播 → 3 個主題
        Assert.Equal(3, frequency["ガリオ"]);
    }

    [Fact]
    public void BuildTopicFrequency_TwoStreamTopicsWithoutGameCard_CountTwo()
    {
        var rows = new[]
        {
            new TopicSubtitleSource(1, null, "トップだ"),
            new TopicSubtitleSource(2, null, "トップと"),
        };

        var frequency = TermDiscoveryCounter.BuildTopicFrequency(rows);

        // GameCardId 是 null 的兩場直播算 2 個主題
        Assert.Equal(2, frequency["トップ"]);
    }
}
