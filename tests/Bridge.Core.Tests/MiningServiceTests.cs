using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>資料探勘流程：假的 LLM 依序回傳腳本化的 JSON、假的知識來源回傳固定資料，搭配暫存 SQLite。</summary>
public class MiningServiceTests
{
    private const string WikidataUrl = "https://www.wikidata.org/wiki/";

    private static WikidataEntity Entity(string id, string? jaWikiTitle = null) => new(
        id,
        new Dictionary<string, string> { ["ja"] = "ラベル", ["en"] = "Label" },
        new Dictionary<string, string> { ["ja"] = "說明" },
        new Dictionary<string, string> { ["ja"] = "エイリアス" },
        jaWikiTitle, null, null);

    private static string ExtractJson(params string[] nouns) =>
        "{\"nouns\":[" + string.Join(",", nouns) + "]}";

    private static string NounJson(string surface, string kind, int importance) =>
        $"{{\"surface\":\"{surface}\",\"kind\":\"{kind}\",\"importance\":{importance}}}";

    private static string NewCardJson(string kind, string name, string summary, string? parentGroup, params string[] terms) =>
        "{\"decision\":\"new\",\"kind\":\"" + kind + "\",\"name\":\"" + name + "\",\"summary\":\"" + summary +
        "\",\"parentGroup\":" + (parentGroup is null ? "null" : $"\"{parentGroup}\"") +
        ",\"terms\":[" + string.Join(",", terms) + "]}";

    private static string NewCardJsonWithAliases(string kind, string name, string summary, string? parentGroup,
        string aliasesJson, params string[] terms) =>
        "{\"decision\":\"new\",\"kind\":\"" + kind + "\",\"name\":\"" + name + "\",\"summary\":\"" + summary +
        "\",\"aliases\":" + aliasesJson +
        ",\"parentGroup\":" + (parentGroup is null ? "null" : $"\"{parentGroup}\"") +
        ",\"terms\":[" + string.Join(",", terms) + "]}";

    private static string TermJson(string source, string target, string category = "遊戲") =>
        $"{{\"source\":\"{source}\",\"target\":\"{target}\",\"category\":\"{category}\"}}";

    /// <summary>直接查 TermCandidates 的列，供斷言欄位值。</summary>
    private sealed class CandidateRow
    {
        public string Surface { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public long Importance { get; set; }
        public string Origin { get; set; } = string.Empty;
        public long? StreamId { get; set; }
        public string? Context { get; set; }
        public long Occurrences { get; set; }
    }

    private static List<CandidateRow> QueryCandidates(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.Query<CandidateRow>("SELECT * FROM TermCandidates ORDER BY Id").ToList();
    }

    private sealed class Harness : IDisposable
    {
        public TempDatabase Db { get; } = new();

        public ScriptedLorebookLlm Llm { get; } = new();

        public StubKnowledgeSources Knowledge { get; } = new();

        public GlossaryState State { get; } = new();

        public MiningService Sut { get; }

        public string DbPath => Db.Service.Description["SQLite：".Length..];

        public Harness()
        {
            Sut = new MiningService(Llm, Knowledge, Db.Service, State, NullLogger<MiningService>.Instance);
        }

        public void ReloadCatalog() =>
            State.SetCatalog(new GlossaryCatalog(Db.Service.GetGlossaryCards(), Db.Service.GetGlossaryTerms()));

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task MineSituation_ProcessesOnlyTopFive_OthersBecomeCandidates()
    {
        using var harness = new Harness();
        var situation = "第一跟第二跟第三跟第四跟第五跟第六名詞的情境";
        SeedStream(harness, 7);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(ExtractJson(
            NounJson("第一", "person", 6),
            NounJson("第二", "person", 5),
            NounJson("第三", "person", 4),
            NounJson("第四", "person", 3),
            NounJson("第五", "person", 2),
            NounJson("第六", "person", 1)), 10, 2, 5, "glm"));

        var result = await harness.Sut.MineSituationAsync(situation, [], 7, null, TestContext.Current.CancellationToken);

        // 前 5 個查不到任何資料（NotFound 也存候選）；第 6 個完全不查，直接候選
        Assert.Null(result.Error);
        Assert.Equal(6, result.Items.Count);
        Assert.Equal(5, result.Items.Count(i => i.Outcome == MiningOutcome.NotFound));
        var overflow = Assert.Single(result.Items, i => i.Outcome == MiningOutcome.Candidate);
        Assert.Equal("第六", overflow.Surface);
        Assert.Equal(5, harness.Knowledge.WikidataSearchCount);

        // 候選表：6 列（5 個查不到 + 1 個溢位），Origin=Situation、StreamId=7、Context 是情境文字
        var candidates = QueryCandidates(harness.DbPath);
        Assert.Equal(6, candidates.Count);
        Assert.All(candidates, c =>
        {
            Assert.Equal("Situation", c.Origin);
            Assert.Equal(7, c.StreamId);
            Assert.Equal(situation, c.Context);
        });
        Assert.Contains(candidates, c => c.Surface == "第六");
    }

    [Fact]
    public async Task MineSituation_EventKind_BecomesCandidateWithoutQuery()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("夏の花火大会", "event", 5)), 10, 2, 5, "glm"));

        var result = await harness.Sut.MineSituationAsync("夏の花火大会を見る", [], null, null, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.Candidate, item.Outcome);
        Assert.Equal(0, harness.Knowledge.WikidataSearchCount);
        var candidate = Assert.Single(QueryCandidates(harness.DbPath));
        Assert.Equal("event", candidate.Kind);
        Assert.Equal(5, candidate.Importance);
    }

    [Fact]
    public async Task MineSituation_Gate2_NormalizedNameMatch_DoesNotQueryWiki()
    {
        using var harness = new Harness();
        SeedCard(harness, "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Group', 'ぶいすぽっ!', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Status) VALUES (1, 'ぶいすぽっ!', 'VSPO!', 'Approved')");
        harness.ReloadCatalog();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("ぶいすぽっ！", "group", 5)), 10, 2, 5, "glm"));

        var result = await harness.Sut.MineSituationAsync("ぶいすぽっ！のコラボ", [], null, null, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.Matched, item.Outcome);
        Assert.Equal(1, item.CardId);
        Assert.Equal("ぶいすぽっ!", item.CardName);
        Assert.Equal(0, harness.Knowledge.WikidataSearchCount);
        Assert.Equal([1L], result.CardIdsToActivate);
        Assert.Empty(QueryCandidates(harness.DbPath));
    }

    [Fact]
    public async Task MineSituation_Gate2_HonorificStrippedMatch_DoesNotQueryWiki()
    {
        using var harness = new Harness();
        // 既有卡的卡片別名有「うるは」，探勘拆出「うるは先輩」：去掉稱謂後比對到，不建新卡
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, Aliases, Status) VALUES (1, 'Character', '一ノ瀬Uruha', 'うるは', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, '一ノ瀬Uruha', '一瀨Uruha', '人名', 'Approved')");
        harness.ReloadCatalog();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("うるは先輩", "person", 5)), 10, 2, 5, "glm"));

        var result = await harness.Sut.MineSituationAsync("うるは先輩の配信", [], null, null, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.Matched, item.Outcome);
        Assert.Equal(1, item.CardId);
        Assert.Equal("一ノ瀬Uruha", item.CardName);
        Assert.Equal(0, harness.Knowledge.WikidataSearchCount);
        Assert.Single(harness.Db.Service.GetGlossaryCards());   // 沒有建新卡
        Assert.Single(harness.Db.Service.GetGlossaryTerms());   // 也沒有新增名詞（稱謂由模型自然翻譯）
        Assert.Empty(QueryCandidates(harness.DbPath));
    }

    [Fact]
    public async Task MineSituation_RejectedCardMatch_SkipsWithoutQuery()
    {
        using var harness = new Harness();
        // 被拒絕的卡不在型錄裡，但探勘開始時會讀出來當黑名單：比對到就略過
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', 'うるは先輩', 'Rejected')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, 'うるは先輩', '黑名單', '人名', 'Rejected')");
        harness.ReloadCatalog();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("うるは先輩", "person", 5)), 10, 2, 5, "glm"));

        var result = await harness.Sut.MineSituationAsync("うるは先輩の配信", [], null, null, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.Rejected, item.Outcome);
        Assert.Equal("已拒絕的卡：うるは先輩", item.Detail);
        Assert.Equal(0, harness.Knowledge.WikidataSearchCount);
        Assert.Empty(QueryCandidates(harness.DbPath));
    }

    [Fact]
    public async Task MineSituation_RejectedCardMatchedByStrippedAlias_SkipsWithoutQuery()
    {
        using var harness = new Harness();
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, Aliases, Status) VALUES (1, 'Character', '一ノ瀬Uruha', 'うるは', 'Rejected')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, '一ノ瀬Uruha', '黑名單', '人名', 'Rejected')");
        harness.ReloadCatalog();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("うるは先輩", "person", 5)), 10, 2, 5, "glm"));

        var result = await harness.Sut.MineSituationAsync("うるは先輩の配信", [], null, null, TestContext.Current.CancellationToken);

        // surface 去掉稱謂後與被拒絕卡的別名「うるは」相同：同樣略過
        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.Rejected, item.Outcome);
        Assert.Equal("已拒絕的卡：一ノ瀬Uruha", item.Detail);
        Assert.Equal(0, harness.Knowledge.WikidataSearchCount);
    }

    [Fact]
    public async Task MineSituation_GlmNewNameMatchesRejectedCard_ReturnsRejected()
    {
        using var harness = new Harness();
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', '一ノ瀬うるは', 'Rejected')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, '一ノ瀬うるは', '黑名單', '人名', 'Rejected')");
        harness.ReloadCatalog();
        harness.Knowledge.WikidataSearch = _ => [new WikidataHit("Q1", "うるは", "VTuber")];
        harness.Knowledge.WikidataEntity = id => Entity(id);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("うるは", "person", 5)), 10, 2, 5, "glm"));
        // GLM 決定 new，但 Name 與被拒絕卡的卡名相同
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJson("Character", "一ノ瀬うるは", "VTuber。", null, TermJson("一ノ瀬うるは", "一瀨Uruha", "人名")),
            20, 3, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("うるはの配信", [], null, null, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.Rejected, item.Outcome);
        Assert.Equal("已拒絕的卡：一ノ瀬うるは", item.Detail);
        Assert.Empty(harness.Db.Service.GetGlossaryCards());    // 沒有建卡（被拒絕的卡不會出現在這裡）
        Assert.Empty(QueryCandidates(harness.DbPath));
    }

    [Fact]
    public async Task MineSituation_GlmNewNameMatchesExistingCard_AddsAliasInstead()
    {
        using var harness = new Harness();
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', '一ノ瀬Uruha', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, '一ノ瀬Uruha', '一瀨Uruha', '人名', 'Approved')");
        harness.ReloadCatalog();
        harness.Knowledge.WikidataSearch = _ => [new WikidataHit("Q1", "うるは", "VTuber")];
        harness.Knowledge.WikidataEntity = id => Entity(id);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("うるは", "person", 5)), 10, 2, 5, "glm"));
        // GLM 決定 new，但 Name 與既有卡卡名相同：不重建，改成併入
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJson("Character", "一ノ瀬Uruha", "VTuber。", null, TermJson("一ノ瀬Uruha", "一瀨Uruha", "人名")),
            20, 3, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("うるはの配信", [], null, null, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.AddedAlias, item.Outcome);
        Assert.Equal(1, item.CardId);
        Assert.All(harness.Db.Service.GetGlossaryCards(), c => Assert.Equal(1, c.Id));   // 沒有新建卡
        var alias = Assert.Single(harness.Db.Service.GetGlossaryTerms(), t => t.Source == "うるは");
        Assert.Equal("一瀨Uruha", alias.Target);
        Assert.Equal("資料探勘：別名", alias.Note);
    }

    [Fact]
    public async Task MineSituation_PersonMatchingOnlyGroupTerm_QueriesAndCreatesCharacterCard()
    {
        using var harness = new Harness();
        // 團體卡「ぶいすぽっ!」的名詞裡有成員名「猫汰つな」，但沒有她的角色卡
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Group', 'ぶいすぽっ!', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, '猫汰つな', '貓汰Tsuna', '人名', 'Approved')");
        harness.ReloadCatalog();
        harness.Knowledge.FandomSearch = _ => ["猫汰つな"];
        harness.Knowledge.FandomPage = _ => new WikiPage(
            "virtualyoutuber.fandom.com", "猫汰つな", "https://virtualyoutuber.fandom.com/wiki/猫汰つな", "ぶいすぽっ!所属のVTuber", null, null);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("猫汰つな", "person", 5)), 10, 2, 5, "glm"));
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJson("Character", "猫汰つな", "ぶいすぽっ!所屬的VTuber。", null,
                TermJson("猫汰つな", "貓汰Tsuna", "人名")),
            20, 3, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("猫汰つなの配信", [], null, null, TestContext.Current.CancellationToken);

        // 第 2 關只比對到團體卡的名詞：照樣查知識來源、建立角色卡，上層設為該團體卡
        Assert.Equal(MiningOutcome.Created, Assert.Single(result.Items).Outcome);
        Assert.Equal(1, harness.Knowledge.WikidataSearchCount);
        var card = Assert.Single(harness.Db.Service.GetGlossaryCards(), c => c.Name == "猫汰つな");
        Assert.Equal(GlossaryKind.Character, card.Kind);
        Assert.Equal(1, card.ParentId);
    }

    [Fact]
    public async Task MineSituation_PersonInGroupTermAndCharacterAlias_MatchesCharacterCard()
    {
        using var harness = new Harness();
        // 團體卡（Id 較小）的名詞和角色卡的卡片別名都有「橘ひなの」：算比對到角色卡，不重複建卡
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Group', 'ぶいすぽっ!', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, '橘ひなの', '橘Hinano', '人名', 'Approved')");
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, ParentId, Aliases, Status) VALUES (2, 'Character', '橘Hinano', 1, '橘ひなの', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (2, 'Hinano', '橘Hinano', '人名', 'Approved')");
        harness.ReloadCatalog();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("橘ひなの", "person", 5)), 10, 2, 5, "glm"));

        var result = await harness.Sut.MineSituationAsync("橘ひなのの配信", [], null, null, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.Matched, item.Outcome);
        Assert.Equal(2, item.CardId);
        Assert.Equal(0, harness.Knowledge.WikidataSearchCount);
    }

    [Fact]
    public async Task MineSituation_PersonMatchingOnlyGroupTerm_GlmExistingOnGroupCard_TreatedAsNew()
    {
        using var harness = new Harness();
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Group', 'ぶいすぽっ!', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, '猫汰つな', '貓汰Tsuna', '人名', 'Approved')");
        harness.ReloadCatalog();
        harness.Knowledge.FandomSearch = _ => ["猫汰つな"];
        harness.Knowledge.FandomPage = _ => new WikiPage(
            "virtualyoutuber.fandom.com", "猫汰つな", "https://virtualyoutuber.fandom.com/wiki/猫汰つな", "ぶいすぽっ!所属のVTuber", null, null);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("猫汰つな", "person", 5)), 10, 2, 5, "glm"));
        // GLM 回 existing 加上團體卡 Id：視為 new，照樣建角色卡（同一份 JSON 帶完整建卡欄位）
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            "{\"decision\":\"existing\",\"cardId\":1,\"kind\":\"Character\",\"name\":\"猫汰つな\"," +
            "\"summary\":\"ぶいすぽっ!所屬的VTuber。\",\"parentGroup\":null," +
            "\"terms\":[{\"source\":\"猫汰つな\",\"target\":\"貓汰Tsuna\",\"category\":\"人名\"}]}",
            20, 3, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("猫汰つなの配信", [], null, null, TestContext.Current.CancellationToken);

        Assert.Equal(MiningOutcome.Created, Assert.Single(result.Items).Outcome);
        var card = Assert.Single(harness.Db.Service.GetGlossaryCards(), c => c.Name == "猫汰つな");
        Assert.Equal(GlossaryKind.Character, card.Kind);
        Assert.Equal(1, card.ParentId);
    }

    [Fact]
    public async Task MineSituation_PersonSameAsGroupCardName_StillMatched()
    {
        using var harness = new Harness();
        SeedCard(harness, "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Group', 'ぶいすぽっ!', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Status) VALUES (1, 'ぶいすぽっ!', 'VSPO!', 'Approved')");
        harness.ReloadCatalog();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("ぶいすぽっ!", "person", 5)), 10, 2, 5, "glm"));

        var result = await harness.Sut.MineSituationAsync("ぶいすぽっ!のコラボ", [], null, null, TestContext.Current.CancellationToken);

        // surface 與團體卡名相同：維持 Matched，不查 Wiki、不建卡
        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.Matched, item.Outcome);
        Assert.Equal(1, item.CardId);
        Assert.Equal(0, harness.Knowledge.WikidataSearchCount);
        Assert.Single(harness.Db.Service.GetGlossaryCards());
    }

    [Fact]
    public async Task MineSituation_GroupKindNounMatchingGroupTerm_StillMatched()
    {
        using var harness = new Harness();
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Group', 'ぶいすぽっ!', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, '猫汰つな', '貓汰Tsuna', '人名', 'Approved')");
        harness.ReloadCatalog();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("猫汰つな", "group", 5)), 10, 2, 5, "glm"));

        var result = await harness.Sut.MineSituationAsync("猫汰つなのコラボ", [], null, null, TestContext.Current.CancellationToken);

        // 種類不是 person：即使只比對到團體卡的名詞，維持 Matched
        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.Matched, item.Outcome);
        Assert.Equal(1, item.CardId);
        Assert.Equal("ぶいすぽっ!", item.CardName);
        Assert.Equal(0, harness.Knowledge.WikidataSearchCount);
        Assert.Single(harness.Db.Service.GetGlossaryCards());
    }

    [Fact]
    public async Task MineSituation_PersonMatchingOnlyGroupTerm_NoSourcesFound_StoresCandidate()
    {
        using var harness = new Harness();
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Group', 'ぶいすぽっ!', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, '猫汰つな', '貓汰Tsuna', '人名', 'Approved')");
        harness.ReloadCatalog();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("猫汰つな", "person", 5)), 10, 2, 5, "glm"));

        var result = await harness.Sut.MineSituationAsync("猫汰つなの配信", [], null, null, TestContext.Current.CancellationToken);

        // 照現有行為：三個知識來源都查不到時存成名詞候選、NotFound，不建卡
        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.NotFound, item.Outcome);
        Assert.Equal(1, harness.Knowledge.WikidataSearchCount);
        Assert.Contains(QueryCandidates(harness.DbPath), c => c.Surface == "猫汰つな");
        Assert.Single(harness.Db.Service.GetGlossaryCards());
    }

    [Fact]
    public async Task MineSituation_Gate1_WikidataIdMatchesCard_AddsAlias()
    {
        using var harness = new Harness();
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, SourceUrl, Status) VALUES (1, 'Character', '英リサ', 'https://www.wikidata.org/wiki/Q11252393', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, '英リサ', '英莉莎', '人名', 'Approved')");
        harness.ReloadCatalog();
        harness.Knowledge.WikidataSearch = _ => [new WikidataHit("Q11252393", "英リサ", "VTuber")];
        harness.Knowledge.WikidataEntity = id => Entity(id);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("はなぶさりさ", "person", 5)), 10, 2, 5, "glm"));

        var result = await harness.Sut.MineSituationAsync("はなぶさりさ登場", [], null, null, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.AddedAlias, item.Outcome);
        Assert.Equal(1, item.CardId);
        Assert.Equal("英リサ", item.CardName);

        // 新名詞：Source 是 surface、Target／Category 沿用「卡名同名名詞」、Note 與 Status 如規格
        var terms = harness.Db.Service.GetGlossaryTerms();
        var alias = Assert.Single(terms, t => t.Source == "はなぶさりさ");
        Assert.Equal("英莉莎", alias.Target);
        Assert.Equal("人名", alias.Category);
        Assert.Equal("資料探勘：別名", alias.Note);
        Assert.Equal("Pending", alias.Status);
        Assert.Empty(QueryCandidates(harness.DbPath));
    }

    [Fact]
    public async Task MineSituation_Gate2_MatchesCardCreatedThisRun_DoesNotQueryWiki()
    {
        using var harness = new Harness();
        // 兩個名詞都查不到 Wikidata；第一個經 Wikipedia 搜尋建卡，卡的名詞裡已有另一個寫法
        harness.Knowledge.WikipediaSearch = _ => ["メメント・ヴァニタス"];
        harness.Knowledge.WikipediaPage = _ => new WikiPage(
            "ja.wikipedia.org", "メメント・ヴァニタス", "https://ja.wikipedia.org/wiki/メメント・ヴァニタス", "日本の三人組VTuberグループ", null, null);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(ExtractJson(
            NounJson("Memento Vanitas", "person", 5),
            NounJson("メーメントヴァニタス", "person", 4)), 10, 2, 5, "glm"));
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJson("Character", "Memento Vanitas", "三人組VTuber團體。", null,
                TermJson("Memento Vanitas", "紀念虛無", "人名"), TermJson("メーメントヴァニタス", "紀念虛無", "人名")),
            20, 3, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("Memento Vanitas（メーメントヴァニタス）の配信", [], null, null, TestContext.Current.CancellationToken);

        // 第一個建卡；第二個比對到本次剛建立的卡（第 2 關），不查 Wiki、不再叫 GLM 建卡
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(MiningOutcome.Created, result.Items[0].Outcome);
        Assert.Equal(MiningOutcome.Matched, result.Items[1].Outcome);
        Assert.Equal(result.Items[0].CardId, result.Items[1].CardId);
        Assert.Equal("Memento Vanitas", result.Items[1].CardName);
        Assert.Single(harness.Db.Service.GetGlossaryCards());
        Assert.Equal(1, harness.Knowledge.WikidataSearchCount);
        Assert.Equal(2, harness.Llm.UserPrompts.Count); // 拆名詞＋第一次建卡，第二個名詞沒有 compose
    }

    [Fact]
    public async Task MineSituation_Gate1_SameFandomUrlAsCardCreatedThisRun_AddsAlias()
    {
        using var harness = new Harness();
        // 兩個名詞都查不到 Wikidata 與 Wikipedia，但 Fandom 搜尋都落到同一頁
        const string fandomUrl = "https://virtualyoutuber.fandom.com/wiki/Memento_Vanitas";
        harness.Knowledge.FandomSearch = _ => ["Memento Vanitas"];
        harness.Knowledge.FandomPage = _ => new WikiPage(
            "virtualyoutuber.fandom.com", "Memento Vanitas", fandomUrl, "VTuber group", null, null);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(ExtractJson(
            NounJson("Memento Vanitas", "person", 5),
            NounJson("メメントヴァニタス", "person", 4)), 10, 2, 5, "glm"));
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJson("Character", "Memento Vanitas", "三人組VTuber團體。", null,
                TermJson("Memento Vanitas", "紀念虛無", "人名")),
            20, 3, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("Memento Vanitas（メメントヴァニタス）の配信", [], null, null, TestContext.Current.CancellationToken);

        // 第二個名詞的 Fandom 網址與本次剛建立的卡相同（第 1 關）：併入別名，只建 1 張卡
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(MiningOutcome.Created, result.Items[0].Outcome);
        Assert.Equal(MiningOutcome.AddedAlias, result.Items[1].Outcome);
        Assert.Equal(result.Items[0].CardId, result.Items[1].CardId);
        Assert.Single(harness.Db.Service.GetGlossaryCards());

        // 別名名詞的 Target 沿用本次建立的卡裡「卡名同名名詞」的 Target
        var alias = Assert.Single(harness.Db.Service.GetGlossaryTerms(), t => t.Source == "メメントヴァニタス");
        Assert.Equal("紀念虛無", alias.Target);
        Assert.Equal("人名", alias.Category);
        Assert.Equal("資料探勘：別名", alias.Note);
    }

    [Fact]
    public async Task MineSituation_Gate3_ExistingDecisionOnCardCreatedThisRun_AddsAlias()
    {
        using var harness = new Harness();
        harness.Knowledge.WikidataSearch = surface =>
        [
            new WikidataHit(surface == "花芽すみれ" ? "Q1" : "Q2", surface, "VTuber"),
        ];
        harness.Knowledge.WikidataEntity = id => Entity(id);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(ExtractJson(
            NounJson("花芽すみれ", "person", 5),
            NounJson("スミレちゃん", "person", 4)), 10, 2, 5, "glm"));
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJson("Character", "花芽すみれ", "VSPO所屬的VTuber。", null,
                TermJson("花芽すみれ", "花芽澄伶", "人名")),
            20, 3, 8, "glm"));
        // 第二個名詞的建卡判斷：GLM 認定與本次剛建立的卡（Id 1）相同
        // （surface 用「スミレちゃん」而不是「花芽すみれちゃん」：後者去掉稱謂後會在第 2 關直接比對到剛建立的卡）
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            "{\"decision\":\"existing\",\"cardId\":1}", 20, 3, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("花芽すみれ（スミレちゃん）の配信", [], null, null, TestContext.Current.CancellationToken);

        // 第 3 關：similarCards 含本次剛建立的卡（compose user 訊息裡看得到），GLM 回 existing 時併入那張卡
        Assert.Equal(3, harness.Llm.UserPrompts.Count);
        Assert.Contains("- id 1：花芽すみれ（Character）", harness.Llm.UserPrompts[2]);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(MiningOutcome.Created, result.Items[0].Outcome);
        Assert.Equal(MiningOutcome.AddedAlias, result.Items[1].Outcome);
        Assert.Equal(1, result.Items[1].CardId);
        Assert.Equal("花芽すみれ", result.Items[1].CardName);
        Assert.Single(harness.Db.Service.GetGlossaryCards());
        var alias = Assert.Single(harness.Db.Service.GetGlossaryTerms(), t => t.Source == "スミレちゃん");
        Assert.Equal("花芽澄伶", alias.Target);
    }

    [Fact]
    public async Task MineSituation_NewGameCard_OneTermDescriptionIsSummarySourceUrlFromWikidata()
    {
        using var harness = new Harness();
        harness.Knowledge.WikidataSearch = _ => [new WikidataHit("Q1", "テストゲーム", "任天堂のゲーム")];
        harness.Knowledge.WikidataEntity = id => Entity(id);
        var longSummary = new string('簡', 150);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("テストゲーム", "game", 5)), 10, 2, 5, "glm"));
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJson("Game", "テストゲーム（官方）", longSummary, null,
                TermJson("テストゲーム", "測試遊戲"), TermJson("多余名詞一", "多一"), TermJson("多余名詞二", "多二")),
            20, 3, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("テストゲームを実況", [], null, null, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.Created, item.Outcome);
        Assert.Equal("テストゲーム（官方）", item.CardName);

        var card = Assert.Single(harness.Db.Service.GetGlossaryCards());
        Assert.Equal(item.CardId, card.Id);
        Assert.Equal(GlossaryKind.Game, card.Kind);
        Assert.Equal(new string('簡', 100), card.Description); // Game：Description 就是 summary，最多 100 字
        Assert.Equal(WikidataUrl + "Q1", card.SourceUrl);
        Assert.Equal("Pending", card.Status);

        // 遊戲卡只放 1 個名詞（遊戲名稱），超過的截掉
        var terms = harness.Db.Service.GetGlossaryTerms();
        var term = Assert.Single(terms);
        Assert.Equal("テストゲーム", term.Source);
        Assert.Equal("測試遊戲", term.Target);
        Assert.Equal("資料探勘", term.Note);
        Assert.Equal("Pending", term.Status);
        Assert.Equal([card.Id], result.CardIdsToActivate);
    }

    [Fact]
    public async Task MineSituation_NewGameCard_ComposeAliasesNormalizedIntoCard()
    {
        using var harness = new Harness();
        harness.Knowledge.WikidataSearch = _ => [new WikidataHit("Q1", "エペ", "ゲーム")];
        harness.Knowledge.WikidataEntity = id => Entity(id);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("エペ", "game", 5)), 10, 2, 5, "glm"));
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJsonWithAliases("Game", "Apex Legends", "射擊遊戲。", null,
                "[\"APEX\",\"エペ\",\"Apex Legends\",\"apex\"]",
                TermJson("エペ", "Apex Legends")),
            20, 3, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("エペをやる", [], null, null, TestContext.Current.CancellationToken);

        Assert.Equal(MiningOutcome.Created, Assert.Single(result.Items).Outcome);
        var card = Assert.Single(harness.Db.Service.GetGlossaryCards());
        Assert.Equal("Apex Legends", card.Name);
        // 與卡名相同的行丟掉；apex 與 APEX 不分大小寫重複也丟掉
        Assert.Equal("APEX\nエペ", card.Aliases);
    }

    [Fact]
    public async Task MineSituation_NewCharacterCard_ParentGroupMatchesExistingGroup_SetsParentId()
    {
        using var harness = new Harness();
        SeedCard(harness, "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Group', 'ぶいすぽっ!', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, 'ぶいすぽっ!', 'VSPO!', '團體', 'Approved')");
        harness.ReloadCatalog();
        harness.Knowledge.WikidataSearch = _ => [new WikidataHit("Q2", "橘ひなの", "VTuber")];
        harness.Knowledge.WikidataEntity = id => Entity(id);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("橘ひなの", "person", 5)), 10, 2, 5, "glm"));
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJson("Character", "橘ひなの", "VSPO所屬的VTuber。", "ぶいすぽっ！",
                TermJson("橘ひなの", "橘雛乃", "人名"), TermJson("ひなちゃん", "雛乃醬", "暱稱")),
            20, 3, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("橘ひなののコラボ", [], null, null, TestContext.Current.CancellationToken);

        Assert.Equal(MiningOutcome.Created, Assert.Single(result.Items).Outcome);
        var card = Assert.Single(harness.Db.Service.GetGlossaryCards(), c => c.Name == "橘ひなの");
        Assert.Equal(GlossaryKind.Character, card.Kind);
        Assert.Equal(1, card.ParentId);
        Assert.Equal(2, harness.Db.Service.GetGlossaryTerms().Count(t => t.CardId == card.Id));
        // 非遊戲卡：Description 是 summary 加來源行（entity 查了 Wikidata，但沒有 wiki 頁，就只有 summary）
        Assert.Equal("VSPO所屬的VTuber。", card.Description);
    }

    [Fact]
    public async Task MineSituation_NewCardWithSimilarCards_MarksSuspectedDuplicate()
    {
        using var harness = new Harness();
        SeedCard(harness, "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'マインクラフト', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Status) VALUES (1, 'マインクラフト', '當個創世神', 'Approved')");
        harness.ReloadCatalog();
        harness.Knowledge.WikidataSearch = _ => [new WikidataHit("Q3", "マインクラフトswitch", "ゲーム")];
        harness.Knowledge.WikidataEntity = id => Entity(id);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("マインクラフトswitch", "game", 5)), 10, 2, 5, "glm"));
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJson("Game", "マイクラswitch", "沙盒遊戲。", null, TermJson("マインクラフトswitch", "當個創世神")),
            20, 3, 8, "glm"));

        await harness.Sut.MineSituationAsync("マインクラフトswitchをやる", [], null, null, TestContext.Current.CancellationToken);

        var card = Assert.Single(harness.Db.Service.GetGlossaryCards(), c => c.Name == "マイクラswitch");
        Assert.StartsWith("疑似重複：マイクラswitch ↔ マインクラフト\n", card.Description);
        Assert.Equal("疑似重複：マイクラswitch ↔ マインクラフト\n沙盒遊戲。", card.Description);
    }

    [Fact]
    public async Task MineSituation_ExistingDecisionWithUnknownCardId_TreatedAsNew()
    {
        using var harness = new Harness();
        SeedCard(harness, "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'マイクラ', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Status) VALUES (1, 'マイクラ', '當個創世神', 'Approved')");
        harness.ReloadCatalog();
        harness.Knowledge.WikidataSearch = _ => [new WikidataHit("Q3", "マインクラフト", "ゲーム")];
        harness.Knowledge.WikidataEntity = id => Entity(id);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("マインクラフト", "game", 5)), 10, 2, 5, "glm"));
        // cardId 999 不在 similarCards 中：視為 new（同一份 JSON 帶著名卡欄位）
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            "{\"decision\":\"existing\",\"cardId\":999,\"kind\":\"Game\",\"name\":\"マインクラフト\"," +
            "\"summary\":\"沙盒遊戲。\",\"parentGroup\":null,\"terms\":[{\"source\":\"マインクラフト\",\"target\":\"當個創世神\",\"category\":\"遊戲\"}]}",
            20, 3, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("マインクラフトをやる", [], null, null, TestContext.Current.CancellationToken);

        Assert.Equal(MiningOutcome.Created, Assert.Single(result.Items).Outcome);
        Assert.NotNull(harness.Db.Service.GetGlossaryCards().SingleOrDefault(c => c.Name == "マインクラフト" && c.Id != 1));
    }

    [Fact]
    public async Task MineSituation_UnparseableComposeReply_FailsAndContinues()
    {
        using var harness = new Harness();
        harness.Knowledge.WikidataSearch = _ => [new WikidataHit("Q9", "ラベル", "說明")];
        harness.Knowledge.WikidataEntity = id => Entity(id);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(ExtractJson(
            NounJson("橘ひなの", "person", 5),
            NounJson("テストゲーム", "game", 4)), 10, 2, 5, "glm"));
        harness.Llm.Replies.Enqueue(new LlmJsonResult("這不是 JSON", 20, 3, 8, "glm"));
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJson("Game", "テストゲーム", "簡介。", null, TermJson("テストゲーム", "測試遊戲")),
            20, 3, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("橘ひなのとテストゲーム", [], null, null, TestContext.Current.CancellationToken);

        // 第一個名詞失敗（存候選），第二個照常建立
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(MiningOutcome.Failed, result.Items[0].Outcome);
        Assert.Equal("橘ひなの", result.Items[0].Surface);
        Assert.Equal("AI 回覆無法解析", result.Items[0].Detail);
        Assert.Equal(MiningOutcome.Created, result.Items[1].Outcome);

        Assert.NotNull(harness.Db.Service.GetGlossaryCards().SingleOrDefault(c => c.Name == "テストゲーム"));
        Assert.Contains(QueryCandidates(harness.DbPath), c => c.Surface == "橘ひなの");
    }

    [Fact]
    public async Task MineSituation_TotalTokens_SumsAllLlmCalls()
    {
        using var harness = new Harness();
        harness.Knowledge.WikidataSearch = _ => [new WikidataHit("Q1", "テストゲーム", "ゲーム")];
        harness.Knowledge.WikidataEntity = id => Entity(id);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("テストゲーム", "game", 5)), 11, 3, 5, "glm"));
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJson("Game", "テストゲーム", "簡介。", null, TermJson("テストゲーム", "測試遊戲")),
            22, 5, 8, "glm"));

        var result = await harness.Sut.MineSituationAsync("テストゲーム", [], null, null, TestContext.Current.CancellationToken);

        Assert.Equal((11 + 3) + (22 + 5), result.TotalTokens);
    }

    [Fact]
    public async Task MineSituation_Cancelled_ThrowsOperationCanceled()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("テストゲーム", "game", 5)), 10, 2, 5, "glm"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Sut.MineSituationAsync("テストゲーム", [], null, null, cts.Token));
    }

    [Fact]
    public async Task MineSituation_UnparseableExtractReply_ReturnsError()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult("亂七八糟", 5, 1, 1, "glm"));

        var result = await harness.Sut.MineSituationAsync("情境", [], null, null, TestContext.Current.CancellationToken);

        Assert.Equal("AI 回覆無法解析", result.Error);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task MineStream_ExtractUserMessage_HasStreamInfoNotSituation()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("花芽すみれ", "person", 5)), 10, 2, 5, "glm"));

        await harness.Sut.MineStreamAsync(
            "Sumire Ch. 花芽すみれ", "【APEX】w/ 花芽すみれ・橘ひなの", null, [], null, null, TestContext.Current.CancellationToken);

        var user = Assert.Single(harness.Llm.UserPrompts);
        Assert.Contains("頻道名稱：Sumire Ch. 花芽すみれ", user);
        Assert.Contains("直播標題：【APEX】w/ 花芽すみれ・橘ひなの", user);
        Assert.DoesNotContain("## 情境", user);
    }

    [Fact]
    public async Task MineStream_CandidatesUseStreamInfoOriginAndChannelContext()
    {
        using var harness = new Harness();
        SeedStream(harness, 7);
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("夏の花火大会", "event", 5)), 10, 2, 5, "glm"));

        var result = await harness.Sut.MineStreamAsync(
            "花芽すみれ", "夏の花火大会を見る", null, [], 7, null, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        var candidate = Assert.Single(QueryCandidates(harness.DbPath));
        Assert.Equal("StreamInfo", candidate.Origin);
        Assert.Equal(7, candidate.StreamId);
        Assert.StartsWith("頻道：花芽すみれ／標題：夏の花火大会を見る", candidate.Context);
        Assert.Equal(0, harness.Knowledge.WikidataSearchCount);
    }

    [Fact]
    public async Task MineStream_BlankChannelAndTitle_ReturnsEmptyWithoutCallingLlm()
    {
        using var harness = new Harness();

        var result = await harness.Sut.MineStreamAsync(" ", "", null, [], null, null, TestContext.Current.CancellationToken);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalTokens);
        Assert.Null(result.Error);
        Assert.Empty(harness.Llm.UserPrompts);
        Assert.Equal(0, harness.Knowledge.WikidataSearchCount);
        Assert.Empty(QueryCandidates(harness.DbPath));
    }

    [Fact]
    public async Task MineStream_DescriptionAddedToUserMessage()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("英リサ", "person", 2)), 10, 2, 5, "glm"));

        await harness.Sut.MineStreamAsync("花芽すみれ", "【APEX】コラボ配信",
            "コラボ：橘ひなの、英リサ\n#PR 提供：株式会社テスト", [], null, null, TestContext.Current.CancellationToken);

        var user = Assert.Single(harness.Llm.UserPrompts);
        Assert.Contains("## 說明欄", user);
        Assert.Contains("コラボ：橘ひなの、英リサ", user);
        Assert.Contains("importance 最多給 2", user);
        Assert.DoesNotContain("株式会社テスト", user);
    }

    [Fact]
    public async Task MineStream_DescriptionFilteredEmpty_OmitsDescriptionSection()
    {
        using var harness = new Harness();
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            ExtractJson(NounJson("花芽すみれ", "person", 5)), 10, 2, 5, "glm"));

        await harness.Sut.MineStreamAsync("花芽すみれ", "雑談", "https://example.com\n#PR",
            [], null, null, TestContext.Current.CancellationToken);

        var user = Assert.Single(harness.Llm.UserPrompts);
        Assert.DoesNotContain("## 說明欄", user);
        Assert.DoesNotContain("importance 最多給 2", user);
    }

    // ===== MineNameAsync（成員探勘：不拆名詞） =====

    [Fact]
    public async Task MineName_MatchesExistingCard_WithoutCallingAnyLlm()
    {
        using var harness = new Harness();
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', '英リサ', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, '英リサ', '英莉莎', '人名', 'Approved')");
        harness.ReloadCatalog();

        var result = await harness.Sut.MineNameAsync("英リサ", null, null, TestContext.Current.CancellationToken);

        // 不拆名詞：名字直接比對到既有卡，GLM 一次都沒叫（沒有拆名詞的 user 訊息，也沒有建卡）
        var item = Assert.Single(result.Items);
        Assert.Equal(MiningOutcome.Matched, item.Outcome);
        Assert.Equal(1, item.CardId);
        Assert.Empty(harness.Llm.UserPrompts);
        Assert.Equal(0, harness.Knowledge.WikidataSearchCount);
        Assert.Equal([1L], result.CardIdsToActivate);
    }

    [Fact]
    public async Task MineName_GroupMemberName_CreatesCharacterCardWithGroupParent()
    {
        using var harness = new Harness();
        SeedCard(harness,
            "INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Group', 'ぶいすぽっ!', 'Approved')",
            "INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (1, '猫汰つな', '貓汰Tsuna', '人名', 'Approved')");
        harness.ReloadCatalog();
        harness.Knowledge.FandomSearch = _ => ["猫汰つな"];
        harness.Knowledge.FandomPage = _ => new WikiPage(
            "virtualyoutuber.fandom.com", "猫汰つな", "https://virtualyoutuber.fandom.com/wiki/猫汰つな", "ぶいすぽっ!所属のVTuber", null, null);
        // 只排建卡回覆（不拆名詞，沒有拆名詞回覆）
        harness.Llm.Replies.Enqueue(new LlmJsonResult(
            NewCardJson("Character", "猫汰つな", "ぶいすぽっ!所屬的VTuber。", null,
                TermJson("猫汰つな", "貓汰Tsuna", "人名")),
            20, 3, 8, "glm"));

        var result = await harness.Sut.MineNameAsync("猫汰つな", null, null, TestContext.Current.CancellationToken);

        // 團體卡的成員名：第 2 關記下團體卡，建出的角色卡上層是那個團體
        Assert.Equal(MiningOutcome.Created, Assert.Single(result.Items).Outcome);
        var card = Assert.Single(harness.Db.Service.GetGlossaryCards(), c => c.Name == "猫汰つな");
        Assert.Equal(GlossaryKind.Character, card.Kind);
        Assert.Equal(1, card.ParentId);
        // 只叫了一次 GLM（建卡），沒有拆名詞
        Assert.Single(harness.Llm.UserPrompts);
    }

    [Fact]
    public async Task MineName_ShortName_ReturnsEmptyWithoutCallingLlm()
    {
        using var harness = new Harness();

        var result = await harness.Sut.MineNameAsync(" あ ", null, null, TestContext.Current.CancellationToken);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalTokens);
        Assert.Null(result.Error);
        Assert.Empty(harness.Llm.UserPrompts);
        Assert.Equal(0, harness.Knowledge.WikidataSearchCount);
        Assert.Empty(QueryCandidates(harness.DbPath));
    }

    private static void SeedCard(Harness harness, string cardSql, string termSql)
    {
        using var conn = new SqliteConnection($"Data Source={harness.DbPath}");
        conn.Execute(cardSql);
        conn.Execute(termSql);
    }

    /// <summary>TermCandidates.StreamId 有外部鍵限制：先建指定 Id 的直播。</summary>
    private static void SeedStream(Harness harness, long id)
    {
        using var conn = new SqliteConnection($"Data Source={harness.DbPath}");
        conn.Execute("INSERT INTO Streams (Id, Platform, Url) VALUES (@id, 'YouTube', 'https://example.com/live')", new { id });
    }
}

/// <summary>依序回傳排入的腳本回覆；沒有排入時回傳空 JSON。取消時拋 OperationCanceledException。</summary>
internal sealed class ScriptedLorebookLlm : ILorebookLlm
{
    public Queue<LlmJsonResult> Replies { get; } = new();

    public List<string> UserPrompts { get; } = [];

    public Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        UserPrompts.Add(userPrompt);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Replies.Count > 0 ? Replies.Dequeue() : new LlmJsonResult("{}", 0, 0, 0, "fake"));
    }

    public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult((true, "連線成功"));
}

/// <summary>以委派提供固定資料的知識來源查詢替身（不連網）；預設全部查不到。</summary>
internal sealed class StubKnowledgeSources : IKnowledgeSources
{
    public Func<string, IReadOnlyList<WikidataHit>> WikidataSearch { get; set; } = _ => [];

    public Func<string, WikidataEntity?> WikidataEntity { get; set; } = _ => null;

    public Func<string, IReadOnlyList<string>> WikipediaSearch { get; set; } = _ => [];

    public Func<string, WikiPage?> WikipediaPage { get; set; } = _ => null;

    public Func<string, IReadOnlyList<string>> FandomSearch { get; set; } = _ => [];

    public Func<string, WikiPage?> FandomPage { get; set; } = _ => null;

    public int WikidataSearchCount { get; private set; }

    public Task<IReadOnlyList<WikidataHit>> SearchWikidataAsync(string text, string language = "ja", int limit = 3, CancellationToken ct = default)
    {
        WikidataSearchCount++;
        return Task.FromResult(WikidataSearch(text));
    }

    public Task<WikidataEntity?> GetWikidataEntityAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(WikidataEntity(id));

    public Task<IReadOnlyList<string>> SearchWikipediaAsync(string lang, string text, int limit = 3, CancellationToken ct = default) =>
        Task.FromResult(WikipediaSearch(text));

    public Task<WikiPage?> GetWikipediaPageAsync(string lang, string title, CancellationToken ct = default) =>
        Task.FromResult(WikipediaPage(title));

    public Task<bool> FandomExistsAsync(string subdomain, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<string>> SearchFandomAsync(string subdomain, string text, int limit = 3, CancellationToken ct = default) =>
        Task.FromResult(FandomSearch(text));

    public Task<WikiPage?> GetFandomPageAsync(string subdomain, string title, CancellationToken ct = default) =>
        Task.FromResult(FandomPage(title));
}
