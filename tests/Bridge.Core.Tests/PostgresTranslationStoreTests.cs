using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class PostgresTranslationStoreTests
{
    private static DatabaseSettings Settings(
        string schema = "public",
        string host = "127.0.0.1",
        int port = 1,
        string password = "secret-pw") => new()
        {
            UsePostgres = true,
            Host = host,
            Port = port,
            Database = "testdb",
            UserName = "tester",
            Password = password,
            Schema = schema,
        };

    [Theory]
    [InlineData("public", true)]
    [InlineData("youtube_history", true)]
    [InlineData("_a1", true)]
    [InlineData("", false)]
    [InlineData("1abc", false)]
    [InlineData("a-b", false)]
    [InlineData("a;drop", false)]
    [InlineData("\"x\"", false)]
    public void IsValidSchema_ReturnsExpected(string schema, bool expected)
    {
        Assert.Equal(expected, PostgresTranslationStore.IsValidSchema(schema));
    }

    [Fact]
    public void Constructor_InvalidSchema_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new PostgresTranslationStore(Settings(schema: "a-b"), NullLogger<PostgresTranslationStore>.Instance));
    }

    [Fact]
    public void Constructor_ValidSchema_DoesNotConnect()
    {
        // 指到必定拒絕連線的 127.0.0.1:1：若建構子企圖連線就會丟例外
        var sut = new PostgresTranslationStore(Settings(), NullLogger<PostgresTranslationStore>.Instance);

        Assert.True(sut.IsAvailable);
    }

    [Fact]
    public void BuildConnectionString_ContainsExpectedValues()
    {
        var settings = Settings(schema: "youtube_history", host: "192.168.5.4", port: 5433);

        var builder = new NpgsqlConnectionStringBuilder(
            PostgresTranslationStore.BuildConnectionString(settings, timeoutSeconds: 7));

        Assert.Equal("192.168.5.4", builder.Host);
        Assert.Equal(5433, builder.Port);
        Assert.Equal("testdb", builder.Database);
        Assert.Equal("youtube_history", builder.SearchPath);
        Assert.Equal(7, builder.Timeout);
        Assert.Equal(5, builder.CommandTimeout);
        Assert.True(builder.Pooling);
    }

    [Fact]
    public void BuildConnectionString_EmptySchema_DefaultsToPublic()
    {
        var settings = Settings(schema: "  ");

        var builder = new NpgsqlConnectionStringBuilder(
            PostgresTranslationStore.BuildConnectionString(settings));

        Assert.Equal("public", builder.SearchPath);
    }

    [Fact]
    public void Methods_UnreachableServer_ReturnFallbacksAndEnterUnavailableState()
    {
        var time = new ManualTimeProvider();
        var sut = new PostgresTranslationStore(Settings(), NullLogger<PostgresTranslationStore>.Instance, time);

        // 127.0.0.1:1 會被立即拒絕：四個方法都不得丟例外
        Assert.Null(sut.GetCache("x"));
        sut.SaveCache("x", "y");
        Assert.False(sut.DeleteCache("x"));
        Assert.Empty(sut.GetAllCache());

        // 失敗後進入斷線狀態
        Assert.False(sut.IsAvailable);

        // 斷線期間不嘗試連線，直接回傳替代值
        Assert.Null(sut.GetCache("x"));

        // 30 秒過後恢復可嘗試狀態
        time.UtcNow += TimeSpan.FromSeconds(31);
        Assert.True(sut.IsAvailable);
    }

    [Fact]
    public void Description_DoesNotContainPassword()
    {
        var sut = new PostgresTranslationStore(
            Settings(schema: "youtube_history", host: "192.168.5.4", port: 5432),
            NullLogger<PostgresTranslationStore>.Instance);

        Assert.Equal("PostgreSQL：192.168.5.4:5432/testdb（youtube_history）", sut.Description);
        Assert.DoesNotContain("secret-pw", sut.Description);
    }

    [Fact]
    public void GlossaryReads_UnreachableServer_ReturnEmptyListsWithoutThrowing()
    {
        var sut = new PostgresTranslationStore(Settings(), NullLogger<PostgresTranslationStore>.Instance);

        // 127.0.0.1:1 會被立即拒絕：讀取不丟例外、回傳空清單
        Assert.Empty(sut.GetGlossaryCards());
        Assert.False(sut.IsAvailable);

        // 斷線期間直接回傳替代值
        Assert.Empty(sut.GetGlossaryTerms());

        // 審核頁的 GetAll 也一樣（含 Rejected 的讀取）
        Assert.Empty(sut.GetAllGlossaryCards());
        Assert.Empty(sut.GetAllGlossaryTerms());
    }

    [Fact]
    public void SaveGlossaryChanges_UnreachableServer_Throws()
    {
        var sut = new PostgresTranslationStore(Settings(), NullLogger<PostgresTranslationStore>.Instance);

        // 儲存失敗時使用者必須知道：不走 fallback，直接擲出例外
        Assert.ThrowsAny<Exception>(() => sut.SaveGlossaryChanges(
            [new GlossaryCard { Id = 1, Kind = "Group", Name = "G", Status = "Approved" }],
            []));
    }

    [Fact]
    public void SaveGlossaryTermEdits_UnreachableServer_Throws()
    {
        var sut = new PostgresTranslationStore(Settings(), NullLogger<PostgresTranslationStore>.Instance);

        // 儲存失敗時使用者必須知道：不走 fallback，直接擲出例外
        Assert.ThrowsAny<Exception>(() => sut.SaveGlossaryTermEdits(
            [new GlossaryTerm { Id = 1, CardId = 1, Source = "a", Target = "甲", Status = "Approved" }],
            [new GlossaryTerm { Id = 0, CardId = 1, Source = "b", Target = "乙", Status = "Pending" }]));
    }

    [Fact]
    public void SubtitleLogMethods_UnreachableServer_CreateStreamReturnsNullOthersDoNotThrow()
    {
        var sut = new PostgresTranslationStore(Settings(), NullLogger<PostgresTranslationStore>.Instance);

        // 127.0.0.1:1 會被立即拒絕：CreateStream 回 null，其他三個方法不丟例外
        Assert.Null(sut.CreateStream(new StreamRecord("YouTube", "https://youtube.com/watch?v=1", null, null, null, null)));
        Assert.False(sut.IsAvailable);

        sut.InsertSubtitle(new SubtitleLogEntry("job1", null, null, "raw", "source", "譯文", "Model", 1, DateTimeOffset.UtcNow));
        sut.MarkSubtitleEdited("job1", null, "手動譯");
        sut.MarkSubtitleDeleted("job1");
    }

    [Fact]
    public void StreamCardMethods_UnreachableServer_DoNotThrow()
    {
        var sut = new PostgresTranslationStore(Settings(), NullLogger<PostgresTranslationStore>.Instance);

        // 127.0.0.1:1 會被立即拒絕：兩個方法都走 fallback，不丟例外
        sut.AddStreamCard(1, 2, "Host");
        Assert.False(sut.IsAvailable);

        // 斷線期間直接回傳替代值
        sut.EndStreamCard(1, 2);
    }

    [Fact]
    public void CreateGlossaryCard_UnreachableServer_Throws()
    {
        var sut = new PostgresTranslationStore(Settings(), NullLogger<PostgresTranslationStore>.Instance);

        // 寫入失敗時使用者必須知道：不走 fallback，直接擲出例外
        Assert.ThrowsAny<Exception>(() => sut.CreateGlossaryCard(
            new GlossaryCard { Kind = "Game", Name = "G", Status = "Pending" },
            [new GlossaryTerm { Source = "a", Target = "甲", Status = "Pending" }]));
    }

    [Fact]
    public void UpsertTermCandidates_UnreachableServer_Throws()
    {
        var sut = new PostgresTranslationStore(Settings(), NullLogger<PostgresTranslationStore>.Instance);

        Assert.ThrowsAny<Exception>(() => sut.UpsertTermCandidates(
            [new TermCandidate("花火大會", "event", 4, "Situation", null, null)]));
    }

    [Fact]
    public void MishearingMethods_UnreachableServer_GetReturnsEmptyOthersThrow()
    {
        var sut = new PostgresTranslationStore(Settings(), NullLogger<PostgresTranslationStore>.Instance);

        // 讀取比照 glossary 讀取方法：不丟例外、回傳空清單，並進入斷線狀態
        Assert.Empty(sut.GetMishearingFixes());
        Assert.False(sut.IsAvailable);

        // 寫入比照 glossary 寫入方法（SaveGlossaryChanges 等）：不走 fallback，直接擲出例外
        Assert.ThrowsAny<Exception>(() => sut.UpsertMishearingFixes(
            [new MishearingProposal(1, 11, MishearingKind.Pre, "作り", "ヴァンダル", "證據")]));
        Assert.ThrowsAny<Exception>(() => sut.ApproveMishearingFixes([1]));
        Assert.ThrowsAny<Exception>(() => sut.DeleteMishearingFixes([1]));
    }

    [Fact]
    public void CaptionMethods_UnreachableServer_GetAndSaveReturnNullTouchDoesNotThrow()
    {
        var sut = new PostgresTranslationStore(Settings(), NullLogger<PostgresTranslationStore>.Instance);

        // 127.0.0.1:1 會被立即拒絕：比照 CreateStream，Get 回 null、Save 回 null、Touch 不丟例外
        Assert.Null(sut.GetCaption("dQw4w9WgXcQ", "ja"));
        Assert.False(sut.IsAvailable);

        sut.TouchCaption(1);
        Assert.Null(sut.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: true, "IOS",
            [new CaptionCue(0, 100, "こんにちは")]));
    }
}

/// <summary>opt-in 整合測試：設定環境變數 TG_TEST_POSTGRES（Npgsql 連線字串，不含 Search Path）才會對真實伺服器測試。</summary>
public class PostgresTranslationStoreIntegrationTests : IDisposable
{
    private readonly string? _baseConnectionString = Environment.GetEnvironmentVariable("TG_TEST_POSTGRES");
    /// <summary>本測試建立的隨機 schema；Dispose 時 DROP。</summary>
    private string? _schema;

    [Fact]
    public async Task FullRoundtrip_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        // 第一個 store：建表 + 快取讀寫完整流程
        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);
        store.SaveCache("こんにちは", "你好");
        Assert.Equal("你好", store.GetCache("こんにちは"));

        // 更新（手動項目覆寫同一筆）
        store.SaveCache("こんにちは", "哈囉", isManual: true);
        Assert.Equal("哈囉", store.GetCache("こんにちは"));

        // 刪除：預設不含手動項目；includeManual 才刪得掉
        Assert.False(store.DeleteCache("こんにちは"));
        Assert.Equal("哈囉", store.GetCache("こんにちは"));
        Assert.True(store.DeleteCache("こんにちは", includeManual: true));
        Assert.Null(store.GetCache("こんにちは"));

        store.SaveCache("a", "甲");
        store.SaveCache("b", "乙");
        var all = store.GetAllCache().ToList();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, x => x.SourceText == "a" && x.TargetText == "甲");
        Assert.Contains(all, x => x.SourceText == "b" && x.TargetText == "乙");

        // 三張表都已建立
        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            var tables = (await conn.QueryAsync<string>(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema",
                new { schema })).ToList();
            Assert.Contains("translation_cache", tables);
            Assert.Contains("glossary_cards", tables);
            Assert.Contains("glossary_terms", tables);

            // 詞彙卡讀取：寫入 2 張卡（1 張 Rejected）與 2 個名詞（1 個 Rejected），Rejected 不讀出
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_cards (id, kind, name, parent_id, channel_handle, status) VALUES
                    (1, 'Group',     'ぶいすぽっ!', NULL, NULL, 'Approved'),
                    (2, 'Character', '英リサ', 1, '@lisahanabusa', 'Approved'),
                    (3, 'Game',      '已刪除的卡', NULL, NULL, 'Rejected')");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_terms (id, card_id, source, target, aliases, status) VALUES
                    (11, 1, 'あかりん', '小燈', NULL, 'Approved'),
                    (12, 2, '英リサ', '英莉莎', 'Hanabusa', 'Approved'),
                    (13, 2, '已刪除', '名詞', NULL, 'Rejected')");
        }

        // 同一個 schema 建第二個 store 再讀取（欄位以別名對應屬性）
        var second = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);
        var cards = second.GetGlossaryCards();
        Assert.Equal(2, cards.Count);
        var character = Assert.Single(cards, c => c.Name == "英リサ");
        Assert.Equal(1, character.ParentId);
        Assert.Equal("@lisahanabusa", character.ChannelHandle);

        var terms = second.GetGlossaryTerms();
        Assert.Equal(2, terms.Count);
        Assert.DoesNotContain(terms, t => t.Source == "已刪除");
        var term = Assert.Single(terms, t => t.CardId == 2);
        Assert.Equal("Hanabusa", term.Aliases);

        second.SaveCache("c", "丙");
        Assert.Equal("丙", second.GetCache("c"));

        // ===== Streams 與 SubtitleLog：字幕紀錄完整流程 =====
        var streamId = second.CreateStream(new StreamRecord("YouTube", "https://youtube.com/watch?v=abc", "頻道A", "直播標題", null, 2));
        Assert.NotNull(streamId);

        second.InsertSubtitle(new SubtitleLogEntry("job1", null, streamId, "ペコラ", "ぺこら", "佩克拉", "Model", 123, DateTimeOffset.UtcNow));
        second.InsertSubtitle(new SubtitleLogEntry("job2", "job1", streamId, "ぺこら", "ぺこら", "佩克拉！", "Reused", 0, DateTimeOffset.UtcNow));

        // 修正版取代中間版本：編輯只改最新一列，刪除只做標記
        second.MarkSubtitleEdited("job2", null, "手動譯");
        second.MarkSubtitleDeleted("job1");

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);

            // 欄位註解來自嵌入資源 postgres-schema.sql 的 COMMENT ON
            var rawSourceComment = await conn.QuerySingleAsync<string?>(
                "SELECT col_description(@regclass::regclass, ordinal_position) FROM information_schema.columns " +
                "WHERE table_schema = @schema AND table_name = 'subtitle_log' AND column_name = 'raw_source'",
                new { regclass = $"{schema}.subtitle_log", schema });
            Assert.False(string.IsNullOrWhiteSpace(rawSourceComment));

            var rows = (await conn.QueryAsync<dynamic>(
                $"SELECT * FROM {schema}.subtitle_log ORDER BY id")).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal((long)streamId, (long)rows[0].stream_id);
            Assert.Equal("job1", (string)rows[0].job_id);
            Assert.Equal("ペコラ", (string)rows[0].raw_source);
            Assert.True((bool)rows[0].is_deleted);
            Assert.False((bool)rows[0].is_edited);
            Assert.Equal("job2", (string)rows[1].job_id);
            Assert.Equal((long)rows[0].id, (long)rows[1].revision_of_id);
            Assert.False((bool)rows[1].is_deleted);
            Assert.True((bool)rows[1].is_edited);
            Assert.Equal("手動譯", (string)rows[1].translation);
        }
    }

    /// <summary>opt-in：設定 TG_TEST_POSTGRES 時才對真實伺服器測 SaveGlossaryChanges 的往返與交易回復。</summary>
    [Fact]
    public async Task GlossarySave_RoundtripAndRollback_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);

        // 先讓 store 完成建 schema 與資料表（第一次連線時執行 DDL），之後才能用原始連線塞資料
        Assert.Empty(store.GetAllGlossaryCards());

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_cards (id, kind, name, parent_id, channel_handle, description, source_url, status) VALUES
                    (1, 'Group', '舊團體', NULL, '@old', '舊說明', 'https://old', 'Pending'),
                    (2, 'Group', '不動的卡', NULL, NULL, NULL, NULL, 'Approved')");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_terms (id, card_id, source, target, aliases, category, note, status) VALUES
                    (11, 1, '舊詞', '舊譯', '舊別名', '舊分類', '舊備註', 'Pending'),
                    (12, 1, '乙', '乙譯', NULL, NULL, NULL, 'Pending'),
                    (13, 2, '不動', '不動譯', NULL, NULL, NULL, 'Approved')");
        }

        // GetAll 含 Rejected：先驗證讀得到整批資料
        Assert.Equal(2, store.GetAllGlossaryCards().Count);
        Assert.Equal(3, store.GetAllGlossaryTerms().Count);

        store.SaveGlossaryChanges(
            [new GlossaryCard
            {
                Id = 1,
                Kind = "Group",
                Name = "新團體",
                ParentId = null,
                ChannelId = null,
                ChannelHandle = "@new",
                Description = "新說明",
                SourceUrl = "https://new",
                Status = "Approved",
            }],
            [new GlossaryTerm
            {
                Id = 11,
                CardId = 1,
                Source = "新詞",
                Target = "新譯",
                Aliases = "新別名",
                Category = "新分類",
                Note = "新備註",
                Status = "Rejected",
            }]);

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);

            var card = await conn.QuerySingleAsync<dynamic>(
                $"SELECT name, channel_handle, description, source_url, status, updated_at FROM {schema}.glossary_cards WHERE id = 1");
            Assert.Equal("新團體", (string)card.name);
            Assert.Equal("@new", (string)card.channel_handle);
            Assert.Equal("新說明", (string)card.description);
            Assert.Equal("https://new", (string)card.source_url);
            Assert.Equal("Approved", (string)card.status);
            Assert.NotNull(card.updated_at);

            var term = await conn.QuerySingleAsync<dynamic>(
                $"SELECT source, target, aliases, category, note, status, updated_at FROM {schema}.glossary_terms WHERE id = 11");
            Assert.Equal("新詞", (string)term.source);
            Assert.Equal("新譯", (string)term.target);
            Assert.Equal("新別名", (string)term.aliases);
            Assert.Equal("新分類", (string)term.category);
            Assert.Equal("新備註", (string)term.note);
            Assert.Equal("Rejected", (string)term.status);
            Assert.NotNull(term.updated_at);

            // 同一批沒列出的列不更新
            var untouched = await conn.QuerySingleAsync<dynamic>(
                $"SELECT updated_at FROM {schema}.glossary_terms WHERE id = 13");
            Assert.Null(untouched.updated_at);

            // 回到本測試的第二段：交易回復（名詞 12 改成與同卡的名詞 11「新詞」同名，違反 UNIQUE(card_id, source)）
            Assert.ThrowsAny<Exception>(() => store.SaveGlossaryChanges(
                [new GlossaryCard { Id = 2, Kind = "Group", Name = "也會回復", Status = "Rejected" }],
                [new GlossaryTerm { Id = 12, CardId = 1, Source = "新詞", Target = "乙譯", Status = "Approved" }]));

            var rolledBackCard = await conn.QuerySingleAsync<dynamic>(
                $"SELECT name, status, updated_at FROM {schema}.glossary_cards WHERE id = 2");
            Assert.Equal("不動的卡", (string)rolledBackCard.name);
            Assert.Equal("Approved", (string)rolledBackCard.status);
            Assert.Null(rolledBackCard.updated_at);

            var term12 = await conn.QuerySingleAsync<dynamic>(
                $"SELECT source, updated_at FROM {schema}.glossary_terms WHERE id = 12");
            Assert.Equal("乙", (string)term12.source);
            Assert.Null(term12.updated_at);
        }
    }

    /// <summary>opt-in：設定 TG_TEST_POSTGRES 時才對真實伺服器測詞彙卡 Aliases 的存讀（含正規化），以及 schema 重複初始化不出錯。</summary>
    [Fact]
    public void GlossaryCardAliases_RoundtripAndDoubleSchemaInit_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);

        // ===== 建卡：Aliases 正規化後寫入 =====
        var cardId = store.CreateGlossaryCard(
            new GlossaryCard
            {
                Kind = "Game",
                Name = "Apex Legends",
                Aliases = " APEX \nApex Legends\nエペ\napex",
                Status = "Pending",
            },
            [new GlossaryTerm { Source = "エペ", Target = "エ佩", Status = "Pending" }]);

        var card = Assert.Single(store.GetAllGlossaryCards());
        Assert.Equal(cardId, card.Id);
        Assert.Equal("APEX\nエペ", card.Aliases);

        // ===== 更新：SaveGlossaryChanges 也走正規化 =====
        store.SaveGlossaryChanges(
            [new GlossaryCard { Id = cardId, Kind = "Game", Name = "Apex Legends", Aliases = " エペ ", Status = "Approved" }],
            []);
        Assert.Equal("エペ", Assert.Single(store.GetAllGlossaryCards()).Aliases);

        // ===== 同一個 schema 建第二個 store：DDL（含 ALTER TABLE ... IF NOT EXISTS）再跑一次不出錯 =====
        var second = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);
        var reread = Assert.Single(second.GetAllGlossaryCards());
        Assert.Equal("エペ", reread.Aliases);
    }

    /// <summary>opt-in：設定 TG_TEST_POSTGRES 時才對真實伺服器測 SaveGlossaryTermEdits 的往返與交易回復。</summary>
    [Fact]
    public async Task GlossaryTermEdits_RoundtripAndRollback_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);

        // 先讓 store 完成建 schema 與資料表（第一次連線時執行 DDL），之後才能用原始連線塞資料
        Assert.Empty(store.GetAllGlossaryCards());

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_cards (id, kind, name, status) VALUES
                    (1, 'Game', 'VALORANT', 'Approved')");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_terms (id, card_id, source, target, status) VALUES
                    (11, 1, 'ヴァンダル', '暴徒', 'Approved'),
                    (12, 1, 'オペ', '玩家', 'Approved')");
        }

        store.SaveGlossaryTermEdits(
            [new GlossaryTerm
            {
                Id = 11,
                CardId = 1,
                Source = "ヴァンダル",
                Target = "暴徒步槍",
                Aliases = "Vandal",
                Category = "武器",
                Status = "Approved",
            }],
            [new GlossaryTerm
            {
                Id = 0,
                CardId = 1,
                Source = "スペクター",
                Target = "幽靈",
                Status = "Pending",
            }]);

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);

            var updated = await conn.QuerySingleAsync<dynamic>(
                $"SELECT source, target, aliases, category, updated_at FROM {schema}.glossary_terms WHERE id = 11");
            Assert.Equal("ヴァンダル", (string)updated.source);
            Assert.Equal("暴徒步槍", (string)updated.target);
            Assert.Equal("Vandal", (string)updated.aliases);
            Assert.Equal("武器", (string)updated.category);
            Assert.NotNull(updated.updated_at);

            // 新增的名詞：created_at 用預設值、updated_at 留空
            var inserted = await conn.QuerySingleAsync<dynamic>(
                $"SELECT source, target, status, created_at, updated_at FROM {schema}.glossary_terms WHERE source = 'スペクター'");
            Assert.Equal("幽靈", (string)inserted.target);
            Assert.Equal("Pending", (string)inserted.status);
            Assert.NotNull(inserted.created_at);
            Assert.Null(inserted.updated_at);

            // 同一批沒列出的列不更新
            var untouched = await conn.QuerySingleAsync<dynamic>(
                $"SELECT updated_at FROM {schema}.glossary_terms WHERE id = 12");
            Assert.Null(untouched.updated_at);

            // 交易回復：新增一個與名詞 12 同 Source 的名詞（違反 UNIQUE(card_id, source)），同批的更新也要回復
            Assert.ThrowsAny<Exception>(() => store.SaveGlossaryTermEdits(
                [new GlossaryTerm { Id = 11, CardId = 1, Source = "ヴァンダル", Target = "也會回復", Status = "Approved" }],
                [new GlossaryTerm { Id = 0, CardId = 1, Source = "オペ", Target = "重複", Status = "Pending" }]));

            var rolledBack = await conn.QuerySingleAsync<dynamic>(
                $"SELECT target, updated_at FROM {schema}.glossary_terms WHERE id = 11");
            Assert.Equal("暴徒步槍", (string)rolledBack.target);
            Assert.Equal(updated.updated_at, rolledBack.updated_at);

            var duplicateCount = await conn.QuerySingleAsync<long>(
                $"SELECT count(*) FROM {schema}.glossary_terms WHERE source = 'オペ'");
            Assert.Equal(1L, duplicateCount);
        }
    }

    /// <summary>opt-in：設定 TG_TEST_POSTGRES 時才對真實伺服器測 stream_cards 的建表、欄位註解與往返。</summary>
    [Fact]
    public async Task StreamCards_TableCommentsAndRoundtrip_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);

        // 先讓 store 完成建 schema 與資料表，之後才能塞 FK 需要的資料
        Assert.Empty(store.GetAllGlossaryCards());

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_cards (id, kind, name, status) VALUES
                    (1, 'Group',     'ぶいすぽっ!', 'Approved'),
                    (2, 'Character', '英リサ', 'Approved'),
                    (3, 'Game',      'VALORANT', 'Approved')");
        }

        var streamId = store.CreateStream(new StreamRecord("YouTube", "https://youtube.com/watch?v=abc", "頻道", "標題", null, 2));
        Assert.NotNull(streamId);

        store.AddStreamCard(streamId!.Value, 1, "Parent");
        store.AddStreamCard(streamId.Value, 2, "Host");
        store.AddStreamCard(streamId.Value, 3, "Carried");
        store.EndStreamCard(streamId.Value, 3);

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);

            // 資料表與兩個索引都要存在
            var tables = (await conn.QueryAsync<string>(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema",
                new { schema })).ToList();
            Assert.Contains("stream_cards", tables);
            var indexes = (await conn.QueryAsync<string>(
                "SELECT indexname FROM pg_indexes WHERE schemaname = @schema AND tablename = 'stream_cards'",
                new { schema })).ToList();
            Assert.Contains("ix_stream_cards_stream_id", indexes);
            Assert.Contains("ix_stream_cards_card_id", indexes);

            // 欄位註解來自嵌入資源 postgres-schema.sql 的 COMMENT ON
            var reasonComment = await conn.QuerySingleAsync<string?>(
                "SELECT col_description(@regclass::regclass, ordinal_position) FROM information_schema.columns " +
                "WHERE table_schema = @schema AND table_name = 'stream_cards' AND column_name = 'reason'",
                new { regclass = $"{schema}.stream_cards", schema });
            Assert.False(string.IsNullOrWhiteSpace(reasonComment));

            var rows = (await conn.QueryAsync<dynamic>(
                $"SELECT stream_id, card_id, reason, activated_at, deactivated_at FROM {schema}.stream_cards ORDER BY id")).ToList();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.NotNull(r.activated_at));
            Assert.Null((DateTimeOffset?)rows[0].deactivated_at);
            Assert.Null((DateTimeOffset?)rows[1].deactivated_at);
            // EndStreamCard 只結束最新一筆未結束的紀錄
            Assert.NotNull((DateTimeOffset?)rows[2].deactivated_at);
            Assert.Equal("Parent", (string)rows[0].reason);
            Assert.Equal("Host", (string)rows[1].reason);
            Assert.Equal("Carried", (string)rows[2].reason);

            // 再結束一次：已沒有未結束的紀錄，不做事
            store.EndStreamCard(streamId.Value, 3);
            var recount = await conn.QuerySingleAsync<int>($"SELECT count(*) FROM {schema}.stream_cards WHERE deactivated_at IS NULL");
            Assert.Equal(2, recount);
        }
    }

    /// <summary>opt-in：設定 TG_TEST_POSTGRES 時才對真實伺服器測 term_candidates 的建表、欄位註解與新增／累加、CreateGlossaryCard 的往返。</summary>
    [Fact]
    public async Task TermCandidatesAndCreateGlossaryCard_RoundtripAndComments_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);

        // 先讓 store 完成建 schema 與資料表，之後才能塞 FK 需要的資料
        Assert.Empty(store.GetAllGlossaryCards());

        // ===== CreateGlossaryCard 往返：新卡與名詞同一個交易寫入 =====
        var cardId = store.CreateGlossaryCard(
            new GlossaryCard
            {
                Kind = "Game",
                Name = "マイクラ",
                Description = "簡介",
                SourceUrl = "https://www.wikidata.org/wiki/Q1",
                Status = "Pending",
            },
            [new GlossaryTerm { Source = "マイクラ", Target = "當個創世神", Category = "遊戲", Note = "資料探勘", Status = "Pending" }]);

        var card = Assert.Single(store.GetAllGlossaryCards());
        Assert.Equal(cardId, card.Id);
        Assert.Equal("Game", card.Kind);
        Assert.Equal("マイクラ", card.Name);
        Assert.Equal("簡介", card.Description);
        Assert.Equal("https://www.wikidata.org/wiki/Q1", card.SourceUrl);
        var term = Assert.Single(store.GetAllGlossaryTerms());
        Assert.Equal(cardId, term.CardId);
        Assert.Equal("當個創世神", term.Target);
        Assert.Equal("資料探勘", term.Note);

        // ===== term_candidates：新增與累加 =====
        var streamId = store.CreateStream(new StreamRecord("YouTube", "https://youtube.com/watch?v=abc", null, null, null, null));
        store.UpsertTermCandidates([new TermCandidate("花火大會", "event", 4, "Situation", streamId, "夏天的事件")]);
        store.UpsertTermCandidates([new TermCandidate("花火大會", "event", 2, "Situation", null, "晚上的事件")]);

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);

            // 資料表與索引都要存在
            var tables = (await conn.QueryAsync<string>(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema",
                new { schema })).ToList();
            Assert.Contains("term_candidates", tables);
            var indexes = (await conn.QueryAsync<string>(
                "SELECT indexname FROM pg_indexes WHERE schemaname = @schema AND tablename = 'term_candidates'",
                new { schema })).ToList();
            Assert.Contains("ix_term_candidates_status", indexes);

            // 每個欄位都有註解（來自嵌入資源 postgres-schema.sql 的 COMMENT ON）
            foreach (var column in new[] { "id", "surface", "kind", "importance", "origin", "stream_id", "context", "occurrences", "status", "created_at", "last_seen_at" })
            {
                var comment = await conn.QuerySingleAsync<string?>(
                    "SELECT col_description(@regclass::regclass, ordinal_position) FROM information_schema.columns " +
                    "WHERE table_schema = @schema AND table_name = 'term_candidates' AND column_name = @column",
                    new { regclass = $"{schema}.term_candidates", schema, column });
                Assert.False(string.IsNullOrWhiteSpace(comment), $"欄位 {column} 沒有註解");
            }

            var candidate = await conn.QuerySingleAsync<dynamic>(
                $"SELECT surface, kind, importance, origin, stream_id, context, occurrences, status FROM {schema}.term_candidates");
            Assert.Equal("花火大會", (string)candidate.surface);
            Assert.Equal("event", (string)candidate.kind);
            Assert.Equal(4, (int)candidate.importance);          // 取較大值
            Assert.Equal("Situation", (string)candidate.origin);
            Assert.Null(candidate.stream_id);                     // 更新為 null
            Assert.Equal("晚上的事件", (string)candidate.context); // 更新為較新的
            Assert.Equal(2, (int)candidate.occurrences);          // 累加
            Assert.Equal("New", (string)candidate.status);
        }
    }

    /// <summary>opt-in：設定 TG_TEST_POSTGRES 時才對真實伺服器測 mishearing_fixes 的建表、欄位註解、往返與交易回復。</summary>
    [Fact]
    public async Task MishearingFixes_RoundtripAndComments_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);

        // 先讓 store 完成建 schema 與資料表（第一次連線時執行 DDL），之後才能塞 FK 需要的資料
        Assert.Empty(store.GetMishearingFixes());

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_cards (id, kind, name, status) VALUES
                    (1, 'Game', 'VALORANT', 'Approved')");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_terms (id, card_id, source, target, status) VALUES
                    (11, 1, 'ヴァンダル', '暴徒', 'Approved')");
        }

        // 新增（略過條件只回傳實際新增的筆數）
        var inserted = store.UpsertMishearingFixes(
        [
            new MishearingProposal(1, 11, MishearingKind.Pre, "ツクリー", "ヴァンダル", "ツクーーリ買った"),
            new MishearingProposal(1, 11, MishearingKind.Pre, "作り", "ヴァンダル", "今日は作りを買った"),
            new MishearingProposal(1, 11, MishearingKind.Pre, "字", "詞", "太短"),
            new MishearingProposal(1, 11, MishearingKind.Pre, "ヴァンダル", "ヴァンダル", "錯字等於正確"),
            new MishearingProposal(1, 11, MishearingKind.Pre, "作り", "ヴァンダル", "   "),
        ]);
        Assert.Equal(2, inserted);

        // 讀取：欄位以別名對應（wrong_text→Wrong、right_text→Right），依 card_id、term_id、wrong_text 排序
        var fixes = store.GetMishearingFixes();
        Assert.Equal(2, fixes.Count);
        var first = fixes[0];
        Assert.Equal(1, first.CardId);
        Assert.Equal(11, first.TermId);
        Assert.Equal(MishearingKind.Pre, first.Kind);
        Assert.Equal("ツクリー", first.Wrong);
        Assert.Equal("ヴァンダル", first.Right);
        Assert.Equal(MishearingStatus.Pending, first.Status);
        Assert.Equal("ツクーーリ買った", first.Evidence);
        Assert.Equal(1, first.Occurrences);
        Assert.NotEqual(default, first.CreatedAt);
        Assert.Null(first.UpdatedAt);

        // 核准後再 Upsert 同一個鍵：Occurrences + 1、Evidence 換新、Status 仍是 Approved
        store.ApproveMishearingFixes([fixes.Single(f => f.Wrong == "作り").Id]);
        Assert.Equal(0, store.UpsertMishearingFixes(
            [new MishearingProposal(1, 11, MishearingKind.Pre, "作り", "ヴァンダル", "新的證據句")]));

        fixes = store.GetMishearingFixes();
        var updated = fixes.Single(f => f.Wrong == "作り");
        Assert.Equal(2, updated.Occurrences);
        Assert.Equal("新的證據句", updated.Evidence);
        Assert.Equal(MishearingStatus.Approved, updated.Status);
        Assert.NotNull(updated.UpdatedAt);

        // 交易回復：第二批第二筆 Right 違反 NOT NULL，第一批的新增也要回復
        Assert.ThrowsAny<Exception>(() => store.UpsertMishearingFixes(
            new List<MishearingProposal>
            {
                new(1, 11, MishearingKind.Pre, "バンダル", "ヴァンダル", "證據"),
                new(1, 11, MishearingKind.Pre, "ヴァンダール", null!, "證據"),
            }));
        Assert.Equal(2, store.GetMishearingFixes().Count);

        // 駁回＝刪除
        store.DeleteMishearingFixes([fixes.Single(f => f.Wrong == "ツクリー").Id]);
        var remaining = store.GetMishearingFixes();
        var row = Assert.Single(remaining);
        Assert.Equal("作り", row.Wrong);

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);

            // 資料表與索引都要存在
            var tables = (await conn.QueryAsync<string>(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema",
                new { schema })).ToList();
            Assert.Contains("mishearing_fixes", tables);
            var indexes = (await conn.QueryAsync<string>(
                "SELECT indexname FROM pg_indexes WHERE schemaname = @schema AND tablename = 'mishearing_fixes'",
                new { schema })).ToList();
            Assert.Contains("ix_mishearing_fixes_status", indexes);

            // 每個欄位都有註解（來自嵌入資源 postgres-schema.sql 的 COMMENT ON）
            foreach (var column in new[] { "id", "card_id", "term_id", "kind", "wrong_text", "right_text", "status", "evidence", "occurrences", "created_at", "updated_at" })
            {
                var comment = await conn.QuerySingleAsync<string?>(
                    "SELECT col_description(@regclass::regclass, ordinal_position) FROM information_schema.columns " +
                    "WHERE table_schema = @schema AND table_name = 'mishearing_fixes' AND column_name = @column",
                    new { regclass = $"{schema}.mishearing_fixes", schema, column });
                Assert.False(string.IsNullOrWhiteSpace(comment), $"欄位 {column} 沒有註解");
            }

            // 資料確實刪除
            var count = await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.mishearing_fixes");
            Assert.Equal(1L, count);
        }
    }

    /// <summary>opt-in：設定 TG_TEST_POSTGRES 時才對真實伺服器測 youtube_captions 的建表、快取往返與 streams 新欄位。</summary>
    [Fact]
    public async Task YoutubeCaptionsAndStreamColumns_Roundtrip_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);

        // 查不到回 null
        Assert.Null(store.GetCaption("dQw4w9WgXcQ", "ja"));

        // 新增後讀回：欄位與 cues 一致
        var cues = new CaptionCue[]
        {
            new(267040, 5520, "うん。なんか"),
            new(272560, 3000, "今日は"),
        };
        var id = store.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: true, "VISIONOS", cues);
        Assert.NotNull(id);
        var caption = store.GetCaption("dQw4w9WgXcQ", "ja");
        Assert.NotNull(caption);
        Assert.Equal(id, caption.Id);
        Assert.Equal("dQw4w9WgXcQ", caption.VideoId);
        Assert.Equal("ja", caption.Language);
        Assert.True(caption.IsAuto);
        Assert.Equal("VISIONOS", caption.ClientName);
        Assert.Equal(cues, caption.Cues);
        Assert.NotEqual(default, caption.CreatedAt);
        Assert.NotEqual(default, caption.LastUsedAt);

        // 同 videoId + language 再存一次是覆寫：仍只有一筆，內容是新的
        var secondId = store.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: false, "IOS",
            [new CaptionCue(1000, 500, "新しい字幕")]);
        caption = store.GetCaption("dQw4w9WgXcQ", "ja");
        Assert.NotNull(caption);
        Assert.Equal(secondId, caption.Id);
        Assert.False(caption.IsAuto);
        Assert.Equal("IOS", caption.ClientName);
        Assert.Equal("新しい字幕", Assert.Single(caption.Cues).Text);

        // 不同語言分開存
        store.SaveCaption("dQw4w9WgXcQ", "en", isAuto: true, "IOS", [new CaptionCue(0, 100, "hello")]);
        Assert.Equal("hello", Assert.Single(store.GetCaption("dQw4w9WgXcQ", "en")!.Cues).Text);

        // Touch：last_used_at 變新（先回填到過去才能斷言）
        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            var count = await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.youtube_captions");
            Assert.Equal(2L, count);
            await conn.ExecuteAsync(
                $"UPDATE {schema}.youtube_captions SET last_used_at = '2020-01-01 00:00:00+00' WHERE id = @id", new { id = secondId });
        }

        store.TouchCaption(secondId!.Value);
        var touched = store.GetCaption("dQw4w9WgXcQ", "ja");
        Assert.NotNull(touched);
        Assert.True(touched.LastUsedAt > new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(touched.LastUsedAt <= DateTimeOffset.UtcNow);

        // streams 新欄位：CreateStream 帶 IsVod 與影片起訖時間，讀回欄位正確
        var started = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(9));
        var streamId = store.CreateStream(new StreamRecord("YouTube", "https://youtube.com/watch?v=abc", null, null, null, null,
            IsVod: true, VideoStartedAt: started, VideoEndedAt: started.AddHours(3)));

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);

            // 資料表（含 caption_sentences）與索引都要存在
            var tables = (await conn.QueryAsync<string>(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema",
                new { schema })).ToList();
            Assert.Contains("youtube_captions", tables);
            Assert.Contains("caption_sentences", tables);
            var indexes = (await conn.QueryAsync<string>(
                "SELECT indexname FROM pg_indexes WHERE schemaname = @schema AND tablename = 'youtube_captions'",
                new { schema })).ToList();
            Assert.Contains("ix_youtube_captions_last_used_at", indexes);

            // 每個欄位都有註解（來自嵌入資源 postgres-schema.sql 的 COMMENT ON）
            foreach (var column in new[] { "video_id", "language", "is_auto", "client_name", "cue_count", "cues_json", "created_at", "last_used_at" })
            {
                var comment = await conn.QuerySingleAsync<string?>(
                    "SELECT col_description(@regclass::regclass, ordinal_position) FROM information_schema.columns " +
                    "WHERE table_schema = @schema AND table_name = 'youtube_captions' AND column_name = @column",
                    new { regclass = $"{schema}.youtube_captions", schema, column });
                Assert.False(string.IsNullOrWhiteSpace(comment), $"欄位 {column} 沒有註解");
            }

            var stream = await conn.QuerySingleAsync<dynamic>(
                $"SELECT is_vod, video_started_at, video_ended_at FROM {schema}.streams WHERE id = @streamId", new { streamId });
            Assert.True((bool)stream.is_vod);
            Assert.NotNull(stream.video_started_at);
            Assert.NotNull(stream.video_ended_at);
            // timestamptz 讀回 UTC：換算後與寫入的一致
            Assert.Equal(started.UtcDateTime, ((DateTimeOffset)stream.video_started_at).UtcDateTime);
            Assert.Equal(started.AddHours(3).UtcDateTime, ((DateTimeOffset)stream.video_ended_at).UtcDateTime);
        }
    }

    /// <summary>opt-in：設定 TG_TEST_POSTGRES 時才對真實伺服器測 caption_sentences 的往返、覆寫與刪除 caption 的 CASCADE。</summary>
    [Fact]
    public async Task CaptionSentences_Roundtrip_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);

        var cues = new CaptionCue[]
        {
            new(267040, 5520, "うん。なんか"),
            new(272560, 3000, "今日は"),
        };
        var captionId = store.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: true, "IOS", cues)!.Value;

        // 查不到回空清單
        Assert.Empty(store.GetSentences(captionId));

        // 亂序寫入：讀回依 start_ms 排序，欄位一致
        store.SaveSentences(captionId,
        [
            new CaptionSentence(272560, 275560, "今日は", "今日は跳ね橋を作ろう", "glm"),
            new CaptionSentence(267040, 272560, "うん。なんか", "うん。なんか", "glm"),
        ]);
        var sentences = store.GetSentences(captionId);
        Assert.Equal([267040L, 272560L], sentences.Select(s => s.StartMs).ToList());
        Assert.Equal("うん。なんか", sentences[0].Text);
        Assert.Equal("今日は跳ね橋を作ろう", sentences[1].Text);
        Assert.Equal("今日は", sentences[1].RawText);
        Assert.Equal("glm", sentences[1].Model);

        // 同 (caption_id, start_ms) 再存一次是覆寫：那一句換成新內容，其他句子不動
        store.SaveSentences(captionId, [new CaptionSentence(272560, 273000, "今日は", "修正後", "glm2")]);
        sentences = store.GetSentences(captionId);
        Assert.Equal([267040L, 272560L], sentences.Select(s => s.StartMs).ToList());
        Assert.Equal("修正後", sentences[1].Text);
        Assert.Equal(273000, sentences[1].EndMs);
        Assert.Equal("glm2", sentences[1].Model);
        Assert.Equal("うん。なんか", sentences[0].Text);
        Assert.Equal(272560, sentences[0].EndMs);
        Assert.Equal("glm", sentences[0].Model);

        // 刪除 caption：句子跟著刪除（ON DELETE CASCADE）
        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.ExecuteAsync($"DELETE FROM {schema}.youtube_captions WHERE id = @captionId", new { captionId });
        }
        Assert.Empty(store.GetSentences(captionId));
    }

    /// <summary>opt-in：設定 TG_TEST_POSTGRES 時才對真實伺服器測 MergeGlossaryCards 的搬移、衝突與改指向（含例外條件）。</summary>
    [Fact]
    public async Task GlossaryMerge_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);

        // 先讓 store 完成建 schema 與資料表，之後才能塞 FK 需要的資料
        Assert.Empty(store.GetAllGlossaryCards());

        // target 卡 1、source 卡 2（子卡 3）、無關卡 4，各帶名詞與聽錯字修正、直播 7／8
        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_cards (id, kind, name, parent_id, channel_id, channel_handle, aliases, status) VALUES
                    (1, 'Character', '英リサ',    NULL, 'UC123', '@lisahanabusa', 'Hanabusa', 'Approved'),
                    (2, 'Character', 'うるは先輩', NULL, NULL, NULL, NULL, 'Pending'),
                    (3, 'Group',     '子卡',      2,    NULL, NULL, NULL, 'Approved'),
                    (4, 'Game',      '其他卡',    NULL, NULL, NULL, NULL, 'Approved')");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_terms (id, card_id, source, target, status) VALUES
                    (11, 1, 'ウルハ',     'ウ露哈',   'Approved'),
                    (12, 1, 'エペ',       '艾佩',     'Approved'),
                    (21, 2, 'うるは',     '潤羽',     'Approved'),
                    (22, 2, 'ヴァンダル', '暴徒',     'Approved'),
                    (23, 2, 'すぺくたー', '幽靈',     'Rejected'),
                    (31, 3, '子卡名詞',   '子卡名詞', 'Approved'),
                    (41, 4, '其他名詞',   '其他名詞', 'Approved')");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.mishearing_fixes (card_id, term_id, kind, wrong_text, right_text, status, evidence) VALUES
                    (2, 21, 'Pre', 'ウルハー',     'うるは',     'Approved', 'ウルハーだ'),
                    (2, 21, 'Pre', 'バンダル',     'うるは',     'Pending',  'バンダルだ'),
                    (1, 11, 'Pre', 'バンダル',     'ウルハ',     'Approved', '既有的バンダル'),
                    (2, 22, 'Pre', 'ヴァンーダル', 'ヴァンダル', 'Pending',  'ヴァンーダルだ'),
                    (4, 41, 'Pre', '其他錯字',     '其他名詞',   'Pending',  '其他證據')");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.streams (id, platform, url, character_card_id) VALUES
                    (7, 'YouTube', 'https://example.com/7', 2),
                    (8, 'YouTube', 'https://example.com/8', 4)");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.stream_cards (stream_id, card_id, reason) VALUES
                    (7, 2, 'Host'),
                    (7, 4, 'Carried')");
        }

        // 搬 2 個（ヴァンダル、すぺくたー）、丟 1 個（うるは 對到 ウルハ）、加 1 行別名（source 卡名）
        Assert.Equal(new GlossaryMergeResult(2, 1, 1), store.MergeGlossaryCards(2, 1));

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);

            // source 卡不見了；target 卡補上別名、有 updated_at、原本的欄位沒被清掉
            var target = await conn.QuerySingleAsync<dynamic>(
                $"SELECT channel_handle, aliases, updated_at FROM {schema}.glossary_cards WHERE id = 1");
            Assert.Equal("@lisahanabusa", (string)target.channel_handle);
            Assert.Equal("Hanabusa\nうるは先輩", (string)target.aliases);
            Assert.NotNull(target.updated_at);
            var sourceCount = await conn.QuerySingleAsync<long>(
                $"SELECT count(*) FROM {schema}.glossary_cards WHERE id = 2");
            Assert.Equal(0L, sourceCount);

            // 子卡的上層指到 target；無關卡不動
            Assert.Equal(1L, await conn.QuerySingleAsync<long>($"SELECT parent_id FROM {schema}.glossary_cards WHERE id = 3"));
            Assert.Null(await conn.QuerySingleAsync<long?>($"SELECT parent_id FROM {schema}.glossary_cards WHERE id = 4"));

            // 搬過去的名詞在 target 卡而且是 Pending（Rejected 的也照搬）；衝突的名詞被刪；target 原本的名詞不變
            var moved = (await conn.QueryAsync<dynamic>(
                $"SELECT card_id, status FROM {schema}.glossary_terms WHERE id IN (22, 23) ORDER BY id")).ToList();
            Assert.All(moved, t => { Assert.Equal(1L, (long)t.card_id); Assert.Equal("Pending", (string)t.status); });
            Assert.Equal(2, moved.Count);
            var droppedCount = await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.glossary_terms WHERE id = 21");
            Assert.Equal(0L, droppedCount);
            Assert.Equal("Approved", await conn.QuerySingleAsync<string>($"SELECT status FROM {schema}.glossary_terms WHERE id = 11"));

            // 丟掉的名詞的聽錯字修正改掛到 kept 名詞，重複的（バンダル）被刪；搬過去的名詞的修正跟著換卡
            var fixes = (await conn.QueryAsync<dynamic>(
                $"SELECT card_id, term_id, wrong_text FROM {schema}.mishearing_fixes ORDER BY id")).ToList();
            Assert.Equal(4, fixes.Count);
            var movedFix = fixes.Single(f => (string)f.wrong_text == "ウルハー");
            Assert.Equal(1L, (long)movedFix.card_id);
            Assert.Equal(11L, (long)movedFix.term_id);
            var keptDuplicate = fixes.Single(f => (string)f.wrong_text == "バンダル");
            Assert.Equal(11L, (long)keptDuplicate.term_id);
            Assert.Equal("Approved", await conn.QuerySingleAsync<string>(
                $"SELECT status FROM {schema}.mishearing_fixes WHERE wrong_text = 'バンダル'"));
            var movedTermFix = fixes.Single(f => (string)f.wrong_text == "ヴァンーダル");
            Assert.Equal(1L, (long)movedTermFix.card_id);
            Assert.Equal(22L, (long)movedTermFix.term_id);
            Assert.Equal(4L, (long)fixes.Single(f => (string)f.wrong_text == "其他錯字").card_id);

            // 開台生效紀錄與直播的主播卡都指到 target；無關的不動
            var streamCards = (await conn.QueryAsync<dynamic>(
                $"SELECT stream_id, card_id FROM {schema}.stream_cards ORDER BY id")).ToList();
            Assert.Equal(2, streamCards.Count);
            Assert.Equal(1L, (long)streamCards[0].card_id);
            Assert.Equal(4L, (long)streamCards[1].card_id);
            Assert.Equal(1L, await conn.QuerySingleAsync<long>($"SELECT character_card_id FROM {schema}.streams WHERE id = 7"));
            Assert.Equal(4L, await conn.QuerySingleAsync<long>($"SELECT character_card_id FROM {schema}.streams WHERE id = 8"));

            // 兩個 Id 相同、卡不存在：ArgumentException，資料不變（仍三張卡、target 的 updated_at 沒再被動）
            Assert.Throws<ArgumentException>(() => store.MergeGlossaryCards(1, 1));
            Assert.Throws<ArgumentException>(() => store.MergeGlossaryCards(1, 99));
            Assert.Throws<ArgumentException>(() => store.MergeGlossaryCards(99, 1));
            Assert.Equal(3L, await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.glossary_cards"));
            Assert.Equal(6L, await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.glossary_terms"));
        }
    }

    /// <summary>opt-in：設定 TG_TEST_POSTGRES 時才對真實伺服器測 DeleteGlossaryCard 的刪除與清空參照。</summary>
    [Fact]
    public async Task GlossaryDelete_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);

        // 先讓 store 完成建 schema 與資料表，之後才能塞 FK 需要的資料
        Assert.Empty(store.GetAllGlossaryCards());

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_cards (id, kind, name, parent_id, status) VALUES
                    (1, 'Character', '英リサ',    NULL, 'Approved'),
                    (2, 'Character', 'うるは先輩', NULL, 'Pending'),
                    (3, 'Group',     '子卡',      2,    'Approved'),
                    (4, 'Game',      '其他卡',    NULL, 'Approved')");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_terms (id, card_id, source, target, status) VALUES
                    (21, 2, 'うるは', '潤羽', 'Approved'),
                    (41, 4, '其他名詞', '其他名詞', 'Approved')");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.mishearing_fixes (card_id, term_id, kind, wrong_text, right_text, status, evidence) VALUES
                    (2, 21, 'Pre', 'ウルハー', 'うるは', 'Pending', 'ウルハーだ'),
                    (4, 41, 'Pre', '其他錯字', '其他名詞', 'Pending', '其他證據')");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.streams (id, platform, url, character_card_id) VALUES
                    (7, 'YouTube', 'https://example.com/7', 2),
                    (8, 'YouTube', 'https://example.com/8', 4)");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.stream_cards (stream_id, card_id, reason) VALUES
                    (7, 2, 'Host'),
                    (7, 4, 'Carried')");
        }

        store.DeleteGlossaryCard(2);

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);

            // 卡、名詞、聽錯字修正、開台生效紀錄都刪了
            Assert.Equal(0L, await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.glossary_cards WHERE id = 2"));
            Assert.Equal(0L, await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.glossary_terms WHERE card_id = 2"));
            Assert.Equal(0L, await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.mishearing_fixes WHERE card_id = 2"));
            Assert.Equal(0L, await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.stream_cards WHERE card_id = 2"));

            // 直播的主播卡與子卡的上層改成空；無關的不動
            Assert.Null(await conn.QuerySingleAsync<long?>($"SELECT character_card_id FROM {schema}.streams WHERE id = 7"));
            Assert.Equal(4L, await conn.QuerySingleAsync<long>($"SELECT character_card_id FROM {schema}.streams WHERE id = 8"));
            Assert.Null(await conn.QuerySingleAsync<long?>($"SELECT parent_id FROM {schema}.glossary_cards WHERE id = 3"));
            Assert.Equal("其他名詞", await conn.QuerySingleAsync<string>(
                $"SELECT source FROM {schema}.glossary_terms WHERE id = 41"));
            Assert.Equal(1L, await conn.QuerySingleAsync<long>(
                $"SELECT count(*) FROM {schema}.mishearing_fixes WHERE card_id = 4"));
            Assert.Equal(1L, await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.stream_cards WHERE card_id = 4"));

            // 卡不存在時不動作也不丟例外
            store.DeleteGlossaryCard(999);
            Assert.Equal(3L, await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.glossary_cards"));
        }
    }

    /// <summary>opt-in：設定 TG_TEST_POSTGRES 時才對真實伺服器測 DeleteGlossaryTerms 的刪除名詞與聽錯字修正。</summary>
    [Fact]
    public async Task GlossaryTermDelete_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);

        // 先讓 store 完成建 schema 與資料表，之後才能塞 FK 需要的資料
        Assert.Empty(store.GetAllGlossaryCards());

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_cards (id, kind, name, aliases, status) VALUES
                    (1, 'Character', '英リサ', 'Hanabusa', 'Approved'),
                    (2, 'Game',      '其他卡', NULL,        'Approved')");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.glossary_terms (id, card_id, source, target, status) VALUES
                    (11, 1, 'ウルハ',     'ウ露哈', 'Approved'),
                    (12, 1, 'エペ',       '艾佩',   'Approved'),
                    (21, 2, 'うるは',     '潤羽',   'Approved'),
                    (22, 2, 'ヴァンダル', '暴徒',   'Approved')");
            await conn.ExecuteAsync($@"
                INSERT INTO {schema}.mishearing_fixes (card_id, term_id, kind, wrong_text, right_text, status, evidence) VALUES
                    (2, 21, 'Pre', 'ウルハー',     'うるは',     'Pending', 'ウルハーだ'),
                    (2, 21, 'Pre', 'バンダル',     'うるは',     'Pending', 'バンダルだ'),
                    (2, 22, 'Pre', 'ヴァンーダル', 'ヴァンダル', 'Pending', 'ヴァンーダルだ'),
                    (1, 11, 'Pre', 'バンダル',     'ウルハ',     'Approved', '既有的バンダル')");
        }

        // 21 有兩個聽錯字修正、22 有一個；999 不存在（略過）
        store.DeleteGlossaryTerms([21, 22, 999]);

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);

            // 只刪指定的名詞；其他卡的名詞不動
            var remainingIds = (await conn.QueryAsync<long>(
                $"SELECT id FROM {schema}.glossary_terms ORDER BY id")).ToList();
            Assert.Equal([11L, 12L], remainingIds);

            // 被刪名詞的聽錯字修正一起刪掉；還在的名詞（11）的修正保留
            var remainingFixTerms = (await conn.QueryAsync<long>(
                $"SELECT term_id FROM {schema}.mishearing_fixes ORDER BY id")).ToList();
            Assert.Equal([11L], remainingFixTerms);
            Assert.Equal("Approved", await conn.QuerySingleAsync<string>(
                $"SELECT status FROM {schema}.mishearing_fixes WHERE term_id = 11"));

            // 卡片與別名不受影響
            Assert.Equal("Hanabusa", await conn.QuerySingleAsync<string>(
                $"SELECT aliases FROM {schema}.glossary_cards WHERE id = 1"));
            Assert.Equal(2L, await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.glossary_cards"));

            // 空清單不動作
            store.DeleteGlossaryTerms([]);
            Assert.Equal(2L, await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.glossary_terms"));
        }
    }

    /// <summary>opt-in：設定 TG_TEST_POSTGRES 時才對真實伺服器測回溯紀錄：prompt 版本與快照的去重、模型呼叫紀錄與過期清除。</summary>
    [Fact]
    public async Task AuditStores_RoundtripAndPurge_AgainstRealServer()
    {
        if (string.IsNullOrEmpty(_baseConnectionString))
        {
            Assert.Skip("未設定 TG_TEST_POSTGRES，略過 PostgreSQL 整合測試");
        }

        var schema = $"tg_test_{Guid.NewGuid().ToString("N")[..12]}";
        _schema = schema;
        var baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = baseBuilder.Host ?? string.Empty,
            Port = baseBuilder.Port,
            Database = baseBuilder.Database ?? string.Empty,
            UserName = baseBuilder.Username ?? string.Empty,
            Password = baseBuilder.Password ?? string.Empty,
            Schema = schema,
        };

        var store = new PostgresTranslationStore(settings, NullLogger<PostgresTranslationStore>.Instance);

        // ===== 第 1 項：EnsurePromptTemplate 的去重與讀取 =====
        // 先讓 store 完成建 schema 與資料表（第一次連線時執行 DDL）
        var first = store.EnsurePromptTemplate("LiveSystem", "你是翻譯員", "Default", "1.0.0");
        Assert.NotNull(first);

        // 同 kind 同內容：同一筆，Origin 與 AppVersion 不改
        var second = store.EnsurePromptTemplate("LiveSystem", "你是翻譯員", "User", "2.0.0");
        Assert.Equal(first, second);
        var template = store.GetPromptTemplate(first!.Value);
        Assert.NotNull(template);
        Assert.Equal("你是翻譯員", template!.Content);
        Assert.Equal(AuditHash.Of("你是翻譯員"), template.ContentHash);
        Assert.Equal("Default", template.Origin);
        Assert.Equal("1.0.0", template.AppVersion);

        // 不同 kind 但內容相同：各自一筆
        var vod = store.EnsurePromptTemplate("VodBatch", "你是翻譯員", "Default", null);
        Assert.NotNull(vod);
        Assert.NotEqual(first, vod);

        // v2 之後新增，GetPromptTemplates 依 Id 由大到小；不存在的 Id 回 null
        store.EnsurePromptTemplate("LiveSystem", "你是翻譯員（改）", "User", null);
        var templates = store.GetPromptTemplates("LiveSystem");
        Assert.Equal([templates[0].Id, first!.Value], templates.Select(t => t.Id).ToList());
        Assert.Null(store.GetPromptTemplate(999));

        // ===== 第 2 項：快照去重與 last_used_at 更新 =====
        var snapshotId = store.EnsurePromptSnapshot(first, "組好的 system prompt");
        Assert.NotNull(snapshotId);
        var settingsSnapshotId = store.EnsureSettingsSnapshot("""{"timeoutMs":1500}""");
        Assert.NotNull(settingsSnapshotId);

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.ExecuteAsync(
                $"UPDATE {schema}.prompt_snapshots SET last_used_at = '2020-01-01 00:00:00+00' WHERE id = @id",
                new { id = snapshotId });
            await conn.ExecuteAsync(
                $"UPDATE {schema}.settings_snapshots SET last_used_at = '2020-01-01 00:00:00+00' WHERE id = @id",
                new { id = settingsSnapshotId });
        }

        // 同內容再 Ensure：同一筆、last_used_at 更新（template_id 不參與去重）
        Assert.Equal(snapshotId, store.EnsurePromptSnapshot(null, "組好的 system prompt"));
        Assert.Equal(settingsSnapshotId, store.EnsureSettingsSnapshot("""{"timeoutMs":1500}"""));

        // ===== 第 4 項：過期清除 =====
        // 未被參照的舊 prompt 快照
        var unreferencedSnapshotId = store.EnsurePromptSnapshot(first, "沒有被參照的快照");
        Assert.NotNull(unreferencedSnapshotId);

        // 新的 llm_call 參照舊快照；舊的 llm_call 參照設定快照（等著被清除）
        var newCallId = store.InsertLlmCall(new LlmCallRecord
        {
            Purpose = LlmCallPurposes.LiveTranslation,
            JobId = "newjob01",
            TemplateId = first,
            SystemSnapshotId = snapshotId,
            Model = "qwen3.8-flash",
            Outcome = LlmCallOutcomes.Ok,
        });
        var oldCallId = store.InsertLlmCall(new LlmCallRecord
        {
            Purpose = LlmCallPurposes.VodBatch,
            JobId = "oldjob001",
            SettingsSnapshotId = settingsSnapshotId,
            Outcome = LlmCallOutcomes.Timeout,
        });
        Assert.NotNull(newCallId);
        Assert.NotNull(oldCallId);

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.ExecuteAsync(
                $"UPDATE {schema}.llm_calls SET created_at = '2020-01-01 00:00:00+00' WHERE id = @id", new { id = oldCallId });
            await conn.ExecuteAsync(
                $"UPDATE {schema}.prompt_snapshots SET last_used_at = '2020-01-01 00:00:00+00' WHERE id = @id",
                new { id = unreferencedSnapshotId });
            // 設定快照也要過期才會被清（只被舊 llm_call 參照，舊 llm_call 刪掉後就沒有參照）
            await conn.ExecuteAsync(
                $"UPDATE {schema}.settings_snapshots SET last_used_at = '2020-01-01 00:00:00+00' WHERE id = @id",
                new { id = settingsSnapshotId });
        }

        var result = store.PurgeAudit(DateTimeOffset.UtcNow.AddDays(-10));

        await using (var conn = new NpgsqlConnection(_baseConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);

            // 舊 llm_call 刪掉、新的保留（欄位都讀得回來）
            Assert.Equal(1, result.LlmCalls);
            var call = await conn.QuerySingleAsync<dynamic>(
                $"SELECT job_id, purpose, template_id, system_snapshot_id, model, outcome FROM {schema}.llm_calls");
            Assert.Equal("newjob01", (string)call.job_id);
            Assert.Equal("LiveTranslation", (string)call.purpose);
            Assert.Equal((long)first!, (long)call.template_id);
            Assert.Equal((long)snapshotId!, (long)call.system_snapshot_id);
            Assert.Equal("qwen3.8-flash", (string)call.model);
            Assert.Equal("Ok", (string)call.outcome);

            // 被新 llm_call 參照的舊快照保留；沒被參照的舊快照刪掉 → 只剩 prompt 快照一筆
            Assert.Equal(1, result.PromptSnapshots);
            var snapshotCount = await conn.QuerySingleAsync<long>(
                $"SELECT count(*) FROM {schema}.prompt_snapshots WHERE id = @id", new { id = snapshotId });
            Assert.Equal(1L, snapshotCount);

            // 沒被（剩餘的）llm_call 參照的舊設定快照刪掉
            Assert.Equal(1, result.SettingsSnapshots);
            Assert.Equal(0L, await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.settings_snapshots"));

            // prompt_templates 永不刪除
            Assert.Equal(3L, await conn.QuerySingleAsync<long>($"SELECT count(*) FROM {schema}.prompt_templates"));
        }
    }

    public void Dispose()
    {
        // 只清理自己建立的隨機 schema
        if (string.IsNullOrEmpty(_baseConnectionString) || _schema is null) return;

        try
        {
            using var conn = new NpgsqlConnection(_baseConnectionString);
            conn.Open();
            conn.Execute($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE");
        }
        catch (NpgsqlException)
        {
            // 清理失敗不讓測試爆掉
        }
    }
}
