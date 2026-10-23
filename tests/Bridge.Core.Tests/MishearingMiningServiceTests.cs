using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>聽錯字探勘：假的 LLM 依序回傳腳本化的 JSON，搭配暫存 SQLite（比照 MiningServiceTests 的寫法）。</summary>
public class MishearingMiningServiceTests
{
    /// <summary>名詞 Id 12：ツクリ（有兩個別名）。</summary>
    private static readonly MishearingTermInput Tsukuri = new(12, 1, "眠雲ツクリ", "ツクリ", "Tsukuri", "つくり\nTsukuri");

    /// <summary>名詞 Id 13：ナミ（同卡另一個名詞，驗證「等於其他名詞」用）。</summary>
    private static readonly MishearingTermInput Nami = new(13, 1, "眠雲ツクリ", "ナミ", "Nami", null);

    private static string FixesJson(params string[] fixes) =>
        "{\"fixes\":[" + string.Join(",", fixes) + "]}";

    private static string FixJson(long? termId, string wrong, string evidence) =>
        $"{{\"termId\":{(termId is null ? "null" : termId.ToString())},\"wrong\":\"{wrong}\",\"evidence\":\"{evidence}\"}}";

    private sealed class Harness : IDisposable
    {
        public TempDatabase Db { get; } = new();

        public ScriptedLorebookLlm Llm { get; } = new();

        public MishearingMiningService Sut { get; }

        public string DbPath => Db.Service.Description["SQLite：".Length..];

        public Harness()
        {
            SeedCard();
            Sut = new MishearingMiningService(Llm, Db.Service, NullLogger<MishearingMiningService>.Instance);
        }

        /// <summary>卡 1（眠雲ツクリ）與名詞 12（ツクリ）、13（ナミ）：MishearingFixes 有外部鍵限制。</summary>
        private void SeedCard()
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', '眠雲ツクリ', 'Approved')");
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Aliases, Status) VALUES (12, 1, 'ツクリ', 'Tsukuri', @aliases, 'Approved')",
                new { aliases = "つくり\nTsukuri" });
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (13, 1, 'ナミ', 'Nami', 'Approved')");
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task MineAsync_ValidFix_InsertsPendingWithWholeLineEvidence()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            FixesJson(FixJson(12, "ツクリー", "ツクリーだね")), 30, 8, 25, "glm"));

        var result = await harness.Sut.MineAsync(
            "遊戲實況", [Tsukuri], ["ツクリーだね、こんにちは"], TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(1, result.Proposed);
        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Accumulated);
        Assert.Equal(0, result.Dropped);

        // 新增為 Pending：Right = 名詞 Source、CardId 正確、Evidence 是它所在的那一整句字幕
        var fix = Assert.Single(harness.Db.Service.GetMishearingFixes());
        Assert.Equal(1, fix.CardId);
        Assert.Equal(12, fix.TermId);
        Assert.Equal(MishearingKind.Pre, fix.Kind);
        Assert.Equal("ツクリー", fix.Wrong);
        Assert.Equal("ツクリ", fix.Right);
        Assert.Equal(MishearingStatus.Pending, fix.Status);
        Assert.Equal("ツクリーだね、こんにちは", fix.Evidence);
        Assert.Equal(1, fix.Occurrences);
    }

    [Fact]
    public async Task MineAsync_LongEvidence_TruncatedTo200Chars()
    {
        using var harness = new Harness();
        var line = new string('あ', 120) + "ツクリー" + new string('い', 120);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            FixesJson(FixJson(12, "ツクリー", "ツクリー")), 30, 8, 25, "glm"));

        await harness.Sut.MineAsync(null, [Tsukuri], [line], TestContext.Current.CancellationToken);

        var fix = Assert.Single(harness.Db.Service.GetMishearingFixes());
        Assert.Equal(200, fix.Evidence!.Length);
        Assert.StartsWith(new string('あ', 100), fix.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknownTerm")]
    [InlineData("tooShort")]
    [InlineData("ownSource")]
    [InlineData("ownAlias")]
    [InlineData("otherTermSource")]
    [InlineData("evidenceNotInLines")]
    [InlineData("wrongNotInEvidence")]
    public async Task MineAsync_InvalidFix_DroppedWithoutWriting(string kind)
    {
        using var harness = new Harness();
        var reply = kind switch
        {
            "unknownTerm" => FixesJson(FixJson(999, "ツクリー", "ツクリーだね")),
            "tooShort" => FixesJson(FixJson(12, "ツ", "ツだね")),
            "ownSource" => FixesJson(FixJson(12, "ツクリ", "ツクリだね")),
            "ownAlias" => FixesJson(FixJson(12, "つくり", "つくりだね")),
            "otherTermSource" => FixesJson(FixJson(12, "ナミ", "ナミだね")),
            "evidenceNotInLines" => FixesJson(FixJson(12, "ツクリー", "這句不在字幕裡")),
            "wrongNotInEvidence" => FixesJson(FixJson(12, "ツクリー", "今日はいい天気")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        harness.Llm.Replies.Enqueue(new LlmJsonResult(reply, 30, 8, 25, "glm"));

        var result = await harness.Sut.MineAsync(
            null, [Tsukuri, Nami], ["今日はいい天気", "ツクリーだね"], TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(1, result.Proposed);
        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Accumulated);
        Assert.Equal(1, result.Dropped);
        Assert.Empty(harness.Db.Service.GetMishearingFixes());
    }

    [Fact]
    public async Task MineAsync_DuplicateInReply_StoresOnceThenAccumulates()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            FixesJson(FixJson(12, "ツクリー", "ツクリーだね"), FixJson(12, "ツクリー", "ツクリーだね")), 30, 8, 25, "glm"));

        var first = await harness.Sut.MineAsync(null, [Tsukuri], ["ツクリーだね"], TestContext.Current.CancellationToken);

        // 同一次回覆重複提出：只留第一筆（重複的計入丟棄）
        Assert.Equal(2, first.Proposed);
        Assert.Equal(1, first.Inserted);
        Assert.Equal(0, first.Accumulated);
        Assert.Equal(1, first.Dropped);

        // 第二次探勘再提出同一筆：不新增、改累加
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            FixesJson(FixJson(12, "ツクリー", "またツクリーだね")), 30, 8, 25, "glm"));
        var second = await harness.Sut.MineAsync(null, [Tsukuri], ["またツクリーだね"], TestContext.Current.CancellationToken);

        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, second.Accumulated);
        Assert.Equal(0, second.Dropped);

        var fix = Assert.Single(harness.Db.Service.GetMishearingFixes());
        Assert.Equal(2, fix.Occurrences);
        Assert.Equal("またツクリーだね", fix.Evidence);
    }

    [Fact]
    public async Task MineAsync_LinesDeduplicate_KeepFirstOccurrenceOrder()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(FixesJson(), 30, 8, 25, "glm"));

        await harness.Sut.MineAsync(null, [Tsukuri],
            ["重複句", "第一句", "重複句", "第二句", "  第二句  ", ""],
            TestContext.Current.CancellationToken);

        var user = Assert.Single(harness.Llm.UserPrompts);
        // 換行正規化（AppendLine 在 Windows 是 \r\n）
        var normalized = user.Replace("\r", "");
        var subtitleLines = normalized.Split('\n').SkipWhile(l => !l.StartsWith("## 最近字幕", StringComparison.Ordinal)).Skip(1).ToList();
        // 去除重複（保留第一次出現的順序）、Trim、空字串丟掉
        Assert.Equal(["重複句", "第一句", "第二句"], subtitleLines);
    }

    [Fact]
    public async Task MineAsync_MoreThan80Lines_KeepsLast80()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(FixesJson(), 30, 8, 25, "glm"));
        var lines = Enumerable.Range(1, 100).Select(i => $"第{i}句").ToList();

        await harness.Sut.MineAsync(null, [Tsukuri], lines, TestContext.Current.CancellationToken);

        var user = Assert.Single(harness.Llm.UserPrompts);
        Assert.Contains("第100句", user);
        Assert.Contains("第21句", user);
        Assert.DoesNotContain("第20句", user);
        Assert.DoesNotContain("第1句", user);
        // 舊到新排列
        Assert.True(user.IndexOf("第21句", StringComparison.Ordinal) < user.IndexOf("第100句", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MineAsync_TotalLineCharsOver4000_DropsOldestFirst()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(FixesJson(), 30, 8, 25, "glm"));
        var old = new string('古', 2500);
        var mid = new string('中', 2500);
        var newest = new string('新', 2500);

        await harness.Sut.MineAsync(null, [Tsukuri], [old, mid, newest], TestContext.Current.CancellationToken);

        // 總長超過 4000 字：從最舊的開始丟，只留最新的兩句（5000 > 4000，所以只留一句）
        var user = Assert.Single(harness.Llm.UserPrompts);
        Assert.Contains(newest, user);
        Assert.DoesNotContain(old, user);
        Assert.DoesNotContain(mid, user);
    }

    [Fact]
    public async Task MineAsync_EmptyTermsOrLines_DoesNotCallLlm()
    {
        using var harness = new Harness();

        var noTerms = await harness.Sut.MineAsync(null, [], ["ツクリーだね"], TestContext.Current.CancellationToken);
        var noLines = await harness.Sut.MineAsync(null, [Tsukuri], [], TestContext.Current.CancellationToken);

        foreach (var result in new[] { noTerms, noLines })
        {
            Assert.Equal(0, result.Proposed);
            Assert.Equal(0, result.Inserted);
            Assert.Equal(0, result.Accumulated);
            Assert.Equal(0, result.Dropped);
            Assert.Equal(0, result.TotalTokens);
            Assert.Null(result.Error);
        }
        Assert.Empty(harness.Llm.UserPrompts);
        Assert.Empty(harness.Db.Service.GetMishearingFixes());
    }

    [Fact]
    public async Task MineAsync_UnparseableReply_ReturnsErrorWithoutWriting()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult("這不是 JSON", 30, 8, 25, "glm"));

        var result = await harness.Sut.MineAsync(null, [Tsukuri], ["ツクリーだね"], TestContext.Current.CancellationToken);

        Assert.Equal("AI 回覆無法解析", result.Error);
        Assert.Equal(38, result.TotalTokens);
        Assert.Empty(harness.Db.Service.GetMishearingFixes());
    }

    [Fact]
    public async Task MineAsync_UserMessage_ContainsSectionsAndTerms()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(FixesJson(), 30, 8, 25, "glm"));

        await harness.Sut.MineAsync("睡眠系 VTuber 的遊戲台", [Tsukuri], ["ツクリーだね"], TestContext.Current.CancellationToken);

        var user = Assert.Single(harness.Llm.UserPrompts);
        Assert.Contains("## 情境", user);
        Assert.Contains("睡眠系 VTuber 的遊戲台", user);
        Assert.Contains("## 名詞", user);
        Assert.Contains("[12] ツクリ（Tsukuri）— 眠雲ツクリ", user);
        Assert.Contains("別名：つくり、Tsukuri", user);
        Assert.Contains("## 最近字幕（舊到新）", user);
    }

    [Fact]
    public async Task MineAsync_BlankSituation_OmitsSituationSection()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(FixesJson(), 30, 8, 25, "glm"));

        await harness.Sut.MineAsync("   ", [Tsukuri], ["ツクリーだね"], TestContext.Current.CancellationToken);

        var user = Assert.Single(harness.Llm.UserPrompts);
        Assert.DoesNotContain("## 情境", user);
        Assert.StartsWith("## 名詞", user);
    }

    [Fact]
    public async Task MineAsync_TotalTokens_SumsPromptAndCompletion()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(FixesJson(), 11, 3, 12, "glm"));

        var result = await harness.Sut.MineAsync(null, [Tsukuri], ["ツクリーだね"], TestContext.Current.CancellationToken);

        Assert.Equal(14, result.TotalTokens);
    }

    [Fact]
    public async Task MineAsync_TermIdAsString_ParsesNumberFromString()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            "{\"fixes\":[{\"termId\":\"12\",\"wrong\":\"ツクリー\",\"evidence\":\"ツクリーだね\"}]}", 30, 8, 25, "glm"));

        var result = await harness.Sut.MineAsync(null, [Tsukuri], ["ツクリーだね"], TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Inserted);
    }

    [Theory]
    [InlineData("ツクリちゃん", "クリ", false)]   // 錯字只在正確寫法裡面
    [InlineData("ツクリとクリ", "クリ", true)]     // 正確寫法以外也有出現
    [InlineData("ツクリーだね", "ツクリー", true)] // 錯字比正確寫法長，不算被包住
    public void ContainsOutsideNames_IgnoresOccurrencesInsideCorrectSpelling(string text, string wrong, bool expected)
    {
        Assert.Equal(expected, MishearingMiningService.ContainsOutsideNames(text, wrong, ["ツクリ"]));
    }
}
