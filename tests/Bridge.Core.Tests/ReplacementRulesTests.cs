using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class ReplacementRulesTests
{
    // ---------- Parse ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrNull_ReturnsEmptyList(string? text)
    {
        Assert.Empty(ReplacementRules.Parse(text));
    }

    [Fact]
    public void Parse_Null_ReturnsEmptyList()
    {
        Assert.Empty(ReplacementRules.Parse(null));
    }

    [Fact]
    public void Parse_SkipsCommentBlankAndNoEqualsLines()
    {
        var rules = ReplacementRules.Parse("# 註解\n\n佩可=佩可拉\n沒有等號\n   \n#peko=1\n你妳=");

        Assert.Equal(2, rules.Count);
        Assert.Equal(("佩可", "佩可拉"), (rules[0].From, rules[0].To));
        Assert.Equal(("你妳", string.Empty), (rules[1].From, rules[1].To));
    }

    [Fact]
    public void Parse_TrimsBothSides()
    {
        var rules = ReplacementRules.Parse("  a = b  ");

        var rule = Assert.Single(rules);
        Assert.Equal(("a", "b"), (rule.From, rule.To));
    }

    [Fact]
    public void Parse_SplitsAtFirstEquals()
    {
        var rules = ReplacementRules.Parse("a=b=c");

        var rule = Assert.Single(rules);
        Assert.Equal(("a", "b=c"), (rule.From, rule.To));
    }

    [Fact]
    public void Parse_EmptyFrom_Skipped()
    {
        Assert.Empty(ReplacementRules.Parse("=b"));
        Assert.Empty(ReplacementRules.Parse("   =b"));
    }

    [Fact]
    public void Parse_DuplicateFrom_LastOneWins()
    {
        var rules = ReplacementRules.Parse("a=1\nb=2\na=3");

        Assert.Equal(2, rules.Count);
        Assert.Equal(("a", "3"), (rules[0].From, rules[0].To));
        Assert.Equal(("b", "2"), (rules[1].From, rules[1].To));
    }

    [Fact]
    public void Parse_CrlfLineEndings_Accepted()
    {
        var rules = ReplacementRules.Parse("a=1\r\nb=2\r\n");

        Assert.Equal(2, rules.Count);
        Assert.Equal(("a", "1"), (rules[0].From, rules[0].To));
        Assert.Equal(("b", "2"), (rules[1].From, rules[1].To));
    }

    // ---------- Apply ----------

    [Fact]
    public void Apply_BasicReplacement()
    {
        Assert.Equal("佩可拉說你好", ReplacementRules.Apply("佩可說你好", "佩可=佩可拉"));
    }

    [Fact]
    public void Apply_MultipleOccurrencesInSameText()
    {
        Assert.Equal("佩可拉與佩可拉", ReplacementRules.Apply("佩可與佩可", "佩可=佩可拉"));
    }

    [Fact]
    public void Apply_LongerRuleHasPriority()
    {
        Assert.Equal("Y和X", ReplacementRules.Apply("佩可拉和佩可", "佩可=X\n佩可拉=Y"));
    }

    [Fact]
    public void Apply_NoChainedReplacement()
    {
        Assert.Equal("B", ReplacementRules.Apply("A", "A=B\nB=C"));
    }

    [Fact]
    public void Apply_RegexSpecialCharactersTreatedLiterally()
    {
        Assert.Equal("X", ReplacementRules.Apply("a.b", "a.b=X"));
        Assert.Equal("acb", ReplacementRules.Apply("acb", "a.b=X"));

        Assert.Equal("Y", ReplacementRules.Apply("a*b(c)", "a*b(c)=Y"));
        Assert.Equal("Z", ReplacementRules.Apply("[te(st)]", "\\d+=Z\n[te(st)]=Z"));
    }

    [Fact]
    public void Apply_EmptyRightSide_DeletesTerm()
    {
        Assert.Equal("說你好", ReplacementRules.Apply("佩可說你好", "佩可="));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# 只有註解\n")]
    public void Apply_EmptyOrNoValidRules_ReturnsOriginalText(string rulesText)
    {
        Assert.Equal("原文", ReplacementRules.Apply("原文", rulesText));
    }

    [Fact]
    public void Apply_NullRulesText_ReturnsOriginalText()
    {
        Assert.Equal("原文", ReplacementRules.Apply("原文", null));
    }

    [Fact]
    public void Apply_CaseSensitive()
    {
        Assert.Equal("ABC", ReplacementRules.Apply("ABC", "abc=x"));
    }

    [Fact]
    public void Apply_SameRulesText_ParsesAndCompilesOnlyOnce()
    {
        // 同一份 rulesText 連續套用多筆文字，結果必須一致（單一快取快取命中）
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal($"佩可拉{i}", ReplacementRules.Apply($"佩可{i}", "佩可=佩可拉"));
        }
    }

    [Fact]
    public void Apply_TwoRuleSetsAlternately_BothApplyCorrectly()
    {
        // 原文修正與譯名修正會交替呼叫，快取必須同時保有多份規則
        const string sourceRules = "ペコラ=ぺこら";
        const string targetRules = "佩可=佩可拉";

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal($"ぺこら{i}", ReplacementRules.Apply($"ペコラ{i}", sourceRules));
            Assert.Equal($"佩可拉{i}", ReplacementRules.Apply($"佩可{i}", targetRules));
        }
    }
}
