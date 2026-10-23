using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>遊戲用語探勘：假的 LLM 依序回傳腳本化的 JSON，搭配暫存 SQLite（比照 MishearingMiningServiceTests 的寫法）。</summary>
public class GameTermDiscoveryServiceTests
{
    private const int GameCardId = 1;

    /// <summary>遊戲卡 1（League of Legends）：附屬用語卡的 ParentId 指向它。</summary>
    private static GlossaryCard Game => new()
    {
        Id = GameCardId,
        Kind = GlossaryKind.Game,
        Name = "League of Legends",
        Description = "Riot Games 的 MOBA",
        Status = "Approved",
    };

    private static TermDiscoveryCandidate Candidate(string word, int count = 3, params string[] examples) =>
        new(word, count, examples.Length > 0 ? examples : [$"{word}だ"]);

    private sealed class Harness : IDisposable
    {
        public TempDatabase Db { get; } = new();

        public ScriptedLorebookLlm Llm { get; } = new();

        public GameTermDiscoveryService Sut { get; }

        public string DbPath => Db.Service.Description["SQLite：".Length..];

        public Harness()
        {
            SeedGameCard();
            Sut = new GameTermDiscoveryService(Llm, Db.Service, NullLogger<GameTermDiscoveryService>.Instance);
        }

        private void SeedGameCard()
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Description, Status) VALUES (1, 'Game', 'League of Legends', 'Riot Games 的 MOBA', 'Approved')");
        }

        /// <summary>建一張遊戲卡 1 的附屬用語卡，回傳卡 Id。</summary>
        public long SeedSupplementCard(string name = "League of Legends 用語", string status = "Pending")
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            return conn.ExecuteScalar<long>(
                "INSERT INTO GlossaryCards (Kind, Name, ParentId, Status, Description) VALUES ('Common', @name, 1, @status, '手動建立'); SELECT last_insert_rowid();",
                new { name, status });
        }

        public void SeedSupplementTerm(long cardId, string source, string status = "Pending") =>
            Execute($"INSERT INTO GlossaryTerms (CardId, Source, Target, Status) VALUES ({cardId}, '{source}', '既有的', '{status}')");

        public long Scalar(string sql)
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            return conn.ExecuteScalar<long>(sql);
        }

        public void Execute(string sql)
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Execute(sql);
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task DiscoverAsync_NoSupplementCard_CreatesCardWithPendingTerm()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            "{\"terms\":[{\"word\":\"ガリオ\",\"isTerm\":true,\"target\":\"加里歐\"},{\"word\":\"ナイス\",\"isTerm\":false,\"target\":\"\"}]}",
            30, 8, 25, "glm"));

        var result = await harness.Sut.DiscoverAsync(Game, [Candidate("ガリオ"), Candidate("ナイス")],
            TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Judged);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(["ガリオ"], result.InsertedWords);
        Assert.Equal(38, result.TotalTokens);

        // 建了一張附屬用語卡：Common、ParentId 指向遊戲卡、Pending、說明與名稱如規格
        var supplement = harness.Db.Service.GetAllGlossaryCards().Single(card => card.Kind == GlossaryKind.Common);
        Assert.NotNull(result.SupplementCardId);
        Assert.Equal(supplement.Id, result.SupplementCardId);
        Assert.Equal("League of Legends 用語", supplement.Name);
        Assert.Equal(GameCardId, supplement.ParentId);
        Assert.Equal("Pending", supplement.Status);
        Assert.Equal("遊戲用語探勘自動建立", supplement.Description);

        // 只有 ガリオ=加里歐 一個名詞，欄位如規格
        var term = Assert.Single(harness.Db.Service.GetAllGlossaryTerms());
        Assert.Equal(supplement.Id, term.CardId);
        Assert.Equal("ガリオ", term.Source);
        Assert.Equal("加里歐", term.Target);
        Assert.Equal("遊戲用語", term.Category);
        Assert.Equal("遊戲用語探勘：ガリオだ", term.Note);
        Assert.Equal("Pending", term.Status);
    }

    [Fact]
    public async Task DiscoverAsync_SupplementCardExists_AddsTerms_SkipsExistingSources()
    {
        using var harness = new Harness();
        var cardId = harness.SeedSupplementCard();
        harness.SeedSupplementTerm(cardId, "ジャンナ");
        harness.SeedSupplementTerm(cardId, "レーン", status: "Rejected");
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            "{\"terms\":[" +
            "{\"word\":\"ガリオ\",\"isTerm\":true,\"target\":\"加里歐\"}," +
            "{\"word\":\"ジャンナ\",\"isTerm\":true,\"target\":\"珍娜\"}," +
            "{\"word\":\"レーン\",\"isTerm\":true,\"target\":\"路線\"}]}",
            30, 8, 25, "glm"));

        var result = await harness.Sut.DiscoverAsync(
            Game, [Candidate("ガリオ"), Candidate("ジャンナ"), Candidate("レーン")], TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Accepted);
        // 已有相同 Source（含 Rejected）的詞跳過，只新增 ガリオ
        Assert.Equal(["ガリオ"], result.InsertedWords);
        Assert.Equal(cardId, result.SupplementCardId);
        // 沒有另建新卡
        Assert.Single(harness.Db.Service.GetAllGlossaryCards(), card => card.Kind == GlossaryKind.Common);
        Assert.Equal(3, harness.Scalar("SELECT COUNT(*) FROM GlossaryTerms"));
        Assert.Equal("加里歐", harness.Db.Service.GetAllGlossaryTerms().Single(t => t.Source == "ガリオ").Target);
    }

    [Fact]
    public async Task DiscoverAsync_SupplementCardRejected_ReturnsErrorWithoutWriting()
    {
        using var harness = new Harness();
        var cardId = harness.SeedSupplementCard(status: "Rejected");
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            "{\"terms\":[{\"word\":\"ガリオ\",\"isTerm\":true,\"target\":\"加里歐\"}]}", 30, 8, 25, "glm"));

        var result = await harness.Sut.DiscoverAsync(Game, [Candidate("ガリオ")], TestContext.Current.CancellationToken);

        Assert.Equal("附屬用語卡已被拒絕", result.Error);
        Assert.Equal(cardId, result.SupplementCardId);
        Assert.Empty(result.InsertedWords);
        Assert.Equal(0, harness.Scalar("SELECT COUNT(*) FROM GlossaryTerms"));
    }

    [Fact]
    public async Task DiscoverAsync_InvalidEntries_Dropped()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            "{\"terms\":[" +
            "{\"word\":\"ミッド\",\"isTerm\":true,\"target\":\"中路\"}," +      // 不在候選清單
            "{\"word\":\"ジャンナ\",\"isTerm\":true,\"target\":\"\"}," +        // 譯名空白
            "{\"word\":\"ガリオ\",\"isTerm\":true,\"target\":\"加里歐\"}," +    // 接受
            "{\"word\":\"ガリオ\",\"isTerm\":true,\"target\":\"凱里歐\"}," +    // 同一個詞重複：丟棄
            "{\"word\":\"ガリオ\",\"isTerm\":false,\"target\":\"路人\"}]}",    // isTerm 否：丟棄
            30, 8, 25, "glm"));

        var result = await harness.Sut.DiscoverAsync(
            Game, [Candidate("ガリオ"), Candidate("ジャンナ")], TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Judged);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(["ガリオ"], result.InsertedWords);
        var term = Assert.Single(harness.Db.Service.GetAllGlossaryTerms());
        Assert.Equal("加里歐", term.Target);
    }

    [Fact]
    public async Task DiscoverAsync_UnparseableReply_ReturnsErrorWithoutWriting()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult("這不是 JSON", 30, 8, 25, "glm"));

        var result = await harness.Sut.DiscoverAsync(Game, [Candidate("ガリオ")], TestContext.Current.CancellationToken);

        Assert.Equal("AI 回覆無法解析", result.Error);
        Assert.Equal(38, result.TotalTokens);
        Assert.Equal(0, harness.Scalar("SELECT COUNT(*) FROM GlossaryCards WHERE Kind = 'Common'"));
        Assert.Equal(0, harness.Scalar("SELECT COUNT(*) FROM GlossaryTerms"));
    }

    [Fact]
    public async Task DiscoverAsync_MarkdownFencedReply_Parses()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            "好的，以下是判斷結果：\n```json\n{\"terms\":[{\"word\":\"ガリオ\",\"isTerm\":\"true\",\"target\":\"加里歐\"}]}\n```",
            30, 8, 25, "glm"));

        var result = await harness.Sut.DiscoverAsync(Game, [Candidate("ガリオ")], TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(["ガリオ"], result.InsertedWords);
        // isTerm 也接受字串 "true"
        Assert.Equal("加里歐", Assert.Single(harness.Db.Service.GetAllGlossaryTerms()).Target);
    }

    [Fact]
    public async Task DiscoverAsync_AllRejected_NoCardCreated()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            "{\"terms\":[{\"word\":\"ナイス\",\"isTerm\":false,\"target\":\"\"}]}", 30, 8, 25, "glm"));

        var result = await harness.Sut.DiscoverAsync(Game, [Candidate("ナイス")], TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(0, result.Accepted);
        Assert.Empty(result.InsertedWords);
        Assert.Null(result.SupplementCardId);
        Assert.Equal(0, harness.Scalar("SELECT COUNT(*) FROM GlossaryCards WHERE Kind = 'Common'"));
    }

    [Fact]
    public async Task DiscoverAsync_EmptyCandidates_DoesNotCallLlm()
    {
        using var harness = new Harness();

        var result = await harness.Sut.DiscoverAsync(Game, [], TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(0, result.Judged);
        Assert.Empty(harness.Llm.UserPrompts);
    }

    [Fact]
    public async Task DiscoverAsync_LlmThrows_ReturnsErrorWithoutThrowing()
    {
        using var harness = new Harness();
        var sut = new GameTermDiscoveryService(
            new ThrowingLorebookLlm(), harness.Db.Service, NullLogger<GameTermDiscoveryService>.Instance);

        var result = await sut.DiscoverAsync(Game, [Candidate("ガリオ")], TestContext.Current.CancellationToken);

        Assert.Equal("模型還沒設定", result.Error);
        Assert.Equal(1, result.Judged);
        Assert.Empty(result.InsertedWords);
        Assert.Equal(0, harness.Scalar("SELECT COUNT(*) FROM GlossaryTerms"));
    }

    [Fact]
    public async Task DiscoverAsync_UserMessage_MatchesSpec()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult("{\"terms\":[]}", 30, 8, 25, "glm"));

        await harness.Sut.DiscoverAsync(Game,
            [Candidate("ガリオ", 3, "ガリオがトップだ", "ガリオとガリオ"), Candidate("ナイス", 2, "ナイス")],
            TestContext.Current.CancellationToken);

        var user = Assert.Single(harness.Llm.UserPrompts).Replace("\r", "");
        var expected =
            "遊戲：League of Legends\n" +
            "簡介：Riot Games 的 MOBA\n" +
            "\n" +
            "# 候選詞\n" +
            "## ガリオ（出現 3 次）\n" +
            "- ガリオがトップだ\n" +
            "- ガリオとガリオ\n" +
            "\n" +
            "## ナイス（出現 2 次）\n" +
            "- ナイス\n" +
            "\n" +
            "# Output（JSON）";
        Assert.Equal(expected, user);

        // System prompt 如規格（開頭與結尾）
        Assert.StartsWith("你是直播字幕的名詞審查員。", TermDiscoveryPrompts.System);
        Assert.EndsWith("判「否」時 isTerm 是 false、target 是空字串。", TermDiscoveryPrompts.System);
    }

    [Fact]
    public void BuildUser_BlankDescription_OmitsDescriptionLine()
    {
        var game = new GlossaryCard
        {
            Id = GameCardId,
            Kind = GlossaryKind.Game,
            Name = "League of Legends",
            Description = "   ",
        };

        var user = TermDiscoveryPrompts.BuildUser(game, [Candidate("ガリオ", 3, "ガリオだ")]);

        Assert.StartsWith("遊戲：League of Legends\n", user.Replace("\r", ""));
        Assert.DoesNotContain("簡介：", user);
        var lines = user.Replace("\r", "").Split('\n');
        Assert.Equal(string.Empty, lines[1]);
        Assert.Equal("# 候選詞", lines[2]);
    }

    /// <summary>固定丟 LorebookLlmException 的假模型。</summary>
    private sealed class ThrowingLorebookLlm : ILorebookLlm
    {
        public Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
            throw new LorebookLlmException("模型還沒設定");

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }
}
