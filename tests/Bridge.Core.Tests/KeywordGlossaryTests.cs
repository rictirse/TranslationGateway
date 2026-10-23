using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>關鍵字詞彙提示：驗證本句／前文掃描、生效卡排除、別名與一個字名詞的比對規則、排序與上限。</summary>
public class KeywordGlossaryTests
{
    private static GlossaryCard Card(long id, string kind = GlossaryKind.Game, string name = "卡") => new()
    {
        Id = id,
        Kind = kind,
        Name = name,
    };

    private static GlossaryTerm Term(long id, long cardId, string source, string target, string? aliases = null) => new()
    {
        Id = id,
        CardId = cardId,
        Source = source,
        Target = target,
        Aliases = aliases,
    };

    private static KeywordGlossary Create(
        GlossaryCard[]? cards = null,
        GlossaryTerm[]? terms = null,
        long[]? activeIds = null)
    {
        var state = new GlossaryState();
        state.SetCatalog(new GlossaryCatalog(cards ?? [], terms ?? []));
        if (activeIds is not null)
        {
            state.SetActiveIds(activeIds);
        }
        return new KeywordGlossary(state);
    }

    [Fact]
    public void TermInCurrent_Listed_ActiveCardTermNotListed()
    {
        var sut = Create(
            cards: [Card(1, name: "VALORANT"), Card(2, name: "生效卡")],
            terms:
            [
                Term(11, 1, "ヴァンダル", "暴徒"),
                Term(21, 2, "あかりん", "小燈"),
            ],
            activeIds: [2]);

        var section = sut.BuildUserPromptSection("ヴァンダル買った", []);

        Assert.StartsWith("# 參考詞彙（本句或前文提到，原文=譯名，譯名請照用）", section);
        Assert.Contains("ヴァンダル=暴徒", section);
        Assert.DoesNotContain("あかりん", section);
    }

    [Fact]
    public void CommonCard_TermInCurrent_Listed()
    {
        var sut = Create(
            cards: [Card(1, kind: GlossaryKind.Common, name: "VTuber用語")],
            terms: [Term(11, 1, "ガチ恋", "真愛粉")]);

        Assert.Contains("ガチ恋=真愛粉", sut.BuildUserPromptSection("ガチ恋だね", []));
    }

    [Fact]
    public void TermOnlyInContext_Listed_CurrentTermsListedFirst()
    {
        var sut = Create(
            cards: [Card(1, name: "VALORANT")],
            terms:
            [
                Term(11, 1, "ヴァンダル", "暴徒"),
                Term(12, 1, "ジェット", "捷風"),
            ]);

        var section = sut.BuildUserPromptSection("ジェットで飛ぶ", ["ヴァンダル買った"]);
        var lines = section.Split('\n');

        Assert.Contains("ヴァンダル=暴徒", lines);
        Assert.Contains("ジェット=捷風", lines);
        // 本句出現的（ジェット）排在前文出現的（ヴァンダル）前面
        Assert.True(Array.IndexOf(lines, "ジェット=捷風") < Array.IndexOf(lines, "ヴァンダル=暴徒"));
    }

    [Fact]
    public void EmptyContext_UsesLastCurrentAsPreceding_ResetClearsIt()
    {
        var sut = Create(
            cards: [Card(1, name: "VALORANT")],
            terms: [Term(11, 1, "ヴァンダル", "暴徒")]);

        // 第一次：current 記為下一次的前文
        sut.BuildUserPromptSection("ヴァンダル買った", []);

        // 第二次（context 空）：上一次的 current 當前文 → 列出
        Assert.Contains("ヴァンダル=暴徒", sut.BuildUserPromptSection("全然関係ない話", []));

        // Reset 後前文清空 → 沒有命中
        sut.Reset();
        Assert.Equal(string.Empty, sut.BuildUserPromptSection("全然関係ない話", []));
    }

    [Fact]
    public void AliasHit_ListsSourceAndTarget()
    {
        var sut = Create(
            cards: [Card(1, name: "卡")],
            terms: [Term(11, 1, "ガチ恋", "真愛粉", aliases: "リアコ")]);

        var section = sut.BuildUserPromptSection("リアコすぎる", []);

        Assert.Contains("ガチ恋=真愛粉", section);
        Assert.DoesNotContain("リアコ=", section);
    }

    [Fact]
    public void AsciiName_RequiresWholeWord()
    {
        var sut = Create(
            cards: [Card(1, name: "卡")],
            terms: [Term(11, 1, "Ash", "小灰")]);

        Assert.Equal(string.Empty, sut.BuildUserPromptSection("Ashley参上", []));
        Assert.Contains("Ash=小灰", sut.BuildUserPromptSection("Ashで飛んだ", []));
    }

    [Fact]
    public void SingleCharacterName_StrictBoundaryMatch()
    {
        var sut = Create(
            cards: [Card(1, name: "卡")],
            terms: [Term(11, 1, "草", "草（笑）")]);

        // 每個子案例之間 Reset：避免上一次的 current 被當成前文而互相干擾
        Assert.Contains("草=草（笑）", sut.BuildUserPromptSection("いや、草。", []));
        sut.Reset();
        Assert.Equal(string.Empty, sut.BuildUserPromptSection("草原だ", []));
        sut.Reset();
        Assert.Equal(string.Empty, sut.BuildUserPromptSection("それは草", []));
        sut.Reset();
        Assert.Contains("草=草（笑）", sut.BuildUserPromptSection("草", []));
    }

    [Fact]
    public void HiraganaKatakana_MatchEachOther()
    {
        var sut = Create(
            cards: [Card(1, name: "卡")],
            terms: [Term(11, 1, "てぇてぇ", "尊")]);

        Assert.Contains("てぇてぇ=尊", sut.BuildUserPromptSection("テェテェだね", []));
    }

    [Fact]
    public void SameSourceAsActiveCardTerm_Skipped_AndSectionLimitedTo30Lines()
    {
        var terms = Enumerable.Range(1, 35)
            .Select(i => Term(i, 1, $"用語{i:00}", $"譯{i:00}"))
            .ToArray();
        var sut = Create(
            cards: [Card(1, name: "大量"), Card(2, name: "生效卡")],
            terms: terms.Concat([Term(90, 2, "用語01", "生效譯")]).ToArray(),
            activeIds: [2]);

        var sentence = string.Concat(terms.Select(t => t.Source));
        var lines = sut.BuildUserPromptSection(sentence, []).Split('\n');

        // 和生效卡同 Source 的名詞（用語01）不列；超過 30 行截斷（用語32 以後不列）
        Assert.Equal(31, lines.Length); // 標題 + 30 行
        Assert.DoesNotContain("用語01=譯01", lines);
        Assert.Contains("用語02=譯02", lines);
        Assert.Contains("用語31=譯31", lines);
        Assert.DoesNotContain("用語32=譯32", lines);
    }

    [Fact]
    public void SameSource_SameCard_TermDedupedInOneLine()
    {
        var sut = Create(
            cards: [Card(1, name: "卡")],
            terms:
            [
                Term(11, 1, "ジェット", "捷風"),
                Term(12, 1, "ジェット", "另譯"),
            ]);

        var lines = sut.BuildUserPromptSection("ジェット速い", []).Split('\n');

        Assert.Equal(1, lines.Count(line => line.StartsWith("ジェット=", StringComparison.Ordinal)));
    }

    [Fact]
    public void NoHit_ReturnsEmpty()
    {
        var sut = Create(
            cards: [Card(1, name: "VALORANT")],
            terms: [Term(11, 1, "ヴァンダル", "暴徒")]);

        Assert.Equal(string.Empty, sut.BuildUserPromptSection("全然関係ない話", ["これも関係ない"]));
    }

    [Fact]
    public void BlankSourceOrTarget_Skipped()
    {
        var sut = Create(
            cards: [Card(1, name: "卡")],
            terms:
            [
                Term(11, 1, "  ", "譯名"),
                Term(12, 1, "原文", ""),
                Term(13, 1, "正常", "名詞"),
            ]);

        var section = sut.BuildUserPromptSection("原文と正常の話", []);

        var lines = section.Split('\n');
        Assert.Equal("# 參考詞彙（本句或前文提到，原文=譯名，譯名請照用）", lines[0]);
        Assert.Contains("正常=名詞", lines);
        Assert.DoesNotContain("原文=譯名", lines);
    }

    [Fact]
    public void Reset_ClearsRecordedLastCurrent()
    {
        var sut = Create(
            cards: [Card(1, name: "VALORANT")],
            terms: [Term(11, 1, "ヴァンダル", "暴徒")]);

        sut.BuildUserPromptSection("ヴァンダル買った", []);
        sut.Reset();

        // 前文已被清掉：Reset 後的第一次呼叫不會看到上一句
        Assert.Equal(string.Empty, sut.BuildUserPromptSection("全然関係ない話", []));
    }

    // ===== 回溯紀錄：matchedTermIds 收集實際輸出到段落的名詞 Id =====

    [Fact]
    public void MatchedTermIds_CollectedForListedTerms_CurrentFirstThenContext()
    {
        var sut = Create(
            cards: [Card(1, name: "VALORANT"), Card(2, name: "生效卡")],
            terms:
            [
                Term(11, 1, "ヴァンダル", "暴徒"),
                Term(12, 1, "ジェット", "捷風"),
                Term(21, 2, "あかりん", "小燈"),
            ],
            activeIds: [2]);

        var ids = new List<long>();
        var withIds = sut.BuildUserPromptSection("ジェットで飛ぶ", ["ヴァンダル買った"], ids);

        // 只收集有輸出到段落的名詞：本句的（ジェット）在前、前文的（ヴァンダル）在後；生效卡不算
        Assert.Equal([12L, 11L], ids);
        Assert.Contains("ジェット=捷風", withIds);
        Assert.Contains("ヴァンダル=暴徒", withIds);

        // 不傳 collector：段落輸出完全相同
        sut.Reset();
        var withoutIds = sut.BuildUserPromptSection("ジェットで飛ぶ", ["ヴァンダル買った"]);
        Assert.Equal(withIds, withoutIds);
    }

    [Fact]
    public void MatchedTermIds_DuplicateSource_OnlyListedTermCollected()
    {
        var sut = Create(
            cards: [Card(1, name: "卡")],
            terms:
            [
                Term(11, 1, "ジェット", "捷風"),
                Term(12, 1, "ジェット", "另譯"),
            ]);

        var ids = new List<long>();
        var section = sut.BuildUserPromptSection("ジェット速い", [], ids);

        // 同一個 Source 只輸出一次：只收集實際列出的那一筆
        Assert.Equal(1, section.Split('\n').Count(line => line.StartsWith("ジェット=", StringComparison.Ordinal)));
        Assert.Equal([11L], ids);
    }

    [Fact]
    public void MatchedTermIds_NoHit_CollectsNothing()
    {
        var sut = Create(
            cards: [Card(1, name: "VALORANT")],
            terms: [Term(11, 1, "ヴァンダル", "暴徒")]);

        var ids = new List<long>();

        Assert.Equal(string.Empty, sut.BuildUserPromptSection("全然関係ない話", [], ids));
        Assert.Empty(ids);
    }
}
