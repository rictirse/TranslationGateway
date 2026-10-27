using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class SqliteTranslationStoreTests
{
    /// <summary>從 Description 取得暫存資料庫的檔案路徑。</summary>
    private static string PathOf(TempDatabase db) => db.Service.Description["SQLite：".Length..];

    private static List<string> QueryNames(string path, string type)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type = @type", new { type }).ToList();
    }

    /// <summary>直接查 SubtitleLog 的列，供斷言欄位值。</summary>
    private sealed class SubtitleRow
    {
        public long Id { get; set; }
        public long? StreamId { get; set; }
        public string JobId { get; set; } = string.Empty;
        public string RawSource { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        public string TranslationSource { get; set; } = string.Empty;
        public long? RevisionOfId { get; set; }
        public long ModelLatencyMs { get; set; }
        public long IsEdited { get; set; }
        public long IsDeleted { get; set; }
        public long? CacheId { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }

    private static List<SubtitleRow> QuerySubtitles(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.Query<SubtitleRow>("SELECT * FROM SubtitleLog ORDER BY Id").ToList();
    }

    private static SubtitleLogEntry Entry(string jobId, string? revisionOf = null, long? streamId = null,
        string rawSource = "raw", string source = "source", string translation = "譯文") => new(
            jobId, revisionOf, streamId, rawSource, source, translation, "Model", 123,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    [Fact]
    public void GetCache_Missing_ReturnsNull()
    {
        using var db = new TempDatabase();

        Assert.Null(db.Service.GetCache("不存在"));
    }

    [Fact]
    public void SaveCache_ThenGetCache_ReturnsStoredValue()
    {
        using var db = new TempDatabase();

        db.Service.SaveCache("こんにちは", "你好");

        Assert.Equal("你好", db.Service.GetCache("こんにちは"));
    }

    // ===== GetCacheId（回溯紀錄：命中快取時記下該筆 TranslationCache 的 Id）=====

    [Fact]
    public void GetCacheId_Missing_ReturnsNull()
    {
        using var db = new TempDatabase();

        Assert.Null(db.Service.GetCacheId("不存在"));
    }

    [Fact]
    public void GetCacheId_Exists_ReturnsRowId()
    {
        using var db = new TempDatabase();
        db.Service.SaveCache("こんにちは", "你好");

        var id = db.Service.GetCacheId("こんにちは");

        using var conn = new SqliteConnection($"Data Source={PathOf(db)}");
        var rowId = conn.QueryFirst<long>("SELECT Id FROM TranslationCache WHERE SourceText = 'こんにちは'");
        Assert.Equal(rowId, id);
    }

    [Fact]
    public void SaveCache_SameSourceTwice_UpdatesInsteadOfDuplicating()
    {
        using var db = new TempDatabase();

        db.Service.SaveCache("src", "舊");
        db.Service.SaveCache("src", "新", isManual: true);

        var all = db.Service.GetAllCache().ToList();
        var item = Assert.Single(all);
        Assert.Equal("新", item.TargetText);
    }

    [Fact]
    public void GetAllCache_ReturnsAllEntries()
    {
        using var db = new TempDatabase();
        db.Service.SaveCache("a", "甲");
        db.Service.SaveCache("b", "乙");

        var all = db.Service.GetAllCache().Select(x => x.SourceText).ToList();

        Assert.Equal(2, all.Count);
        Assert.Contains("a", all);
        Assert.Contains("b", all);
    }

    [Fact]
    public void SaveCache_HandlesSqlSpecialCharacters()
    {
        using var db = new TempDatabase();
        const string source = "it's \"quoted\"; DROP TABLE TranslationCache;--";

        db.Service.SaveCache(source, "安全");

        Assert.Equal("安全", db.Service.GetCache(source));
    }

    // 沒有同原文的快取時：直接新增（非手動項目）。
    [Fact]
    public void SaveCacheUnlessManual_Missing_InsertsNonManualEntry()
    {
        using var db = new TempDatabase();

        db.Service.SaveCacheUnlessManual("src", "預先翻譯");

        Assert.Equal("預先翻譯", db.Service.GetCache("src"));
        // 是非手動項目：預設的刪除刪得掉
        Assert.True(db.Service.DeleteCache("src"));
    }

    // 同原文已存在而且不是手動修正時：覆蓋譯文。
    [Fact]
    public void SaveCacheUnlessManual_ExistingNonManual_Overwrites()
    {
        using var db = new TempDatabase();
        db.Service.SaveCache("src", "自動");

        db.Service.SaveCacheUnlessManual("src", "預先翻譯");

        Assert.Equal("預先翻譯", db.Service.GetCache("src"));
    }

    // 同原文已存在而且是手動修正時：不覆蓋。
    [Fact]
    public void SaveCacheUnlessManual_ExistingManual_DoesNotOverwrite()
    {
        using var db = new TempDatabase();
        db.Service.SaveCache("src", "手動", isManual: true);

        db.Service.SaveCacheUnlessManual("src", "預先翻譯");

        Assert.Equal("手動", db.Service.GetCache("src"));
    }

    [Fact]
    public void DeleteCache_Default_DeletesNonManualEntry()
    {
        using var db = new TempDatabase();
        db.Service.SaveCache("src", "自動");

        Assert.True(db.Service.DeleteCache("src"));
        Assert.Null(db.Service.GetCache("src"));
    }

    [Fact]
    public void DeleteCache_Default_DoesNotDeleteManualEntry()
    {
        using var db = new TempDatabase();
        db.Service.SaveCache("src", "手動", isManual: true);

        Assert.False(db.Service.DeleteCache("src"));
        Assert.Equal("手動", db.Service.GetCache("src"));
    }

    [Fact]
    public void DeleteCache_IncludeManual_DeletesManualEntry()
    {
        using var db = new TempDatabase();
        db.Service.SaveCache("src", "手動", isManual: true);

        Assert.True(db.Service.DeleteCache("src", includeManual: true));
        Assert.Null(db.Service.GetCache("src"));
    }

    [Fact]
    public void Constructor_CreatesGlossaryTablesAndIndex()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        var tables = QueryNames(path, "table");
        Assert.Contains("GlossaryCards", tables);
        Assert.Contains("GlossaryTerms", tables);

        Assert.Contains("IX_GlossaryTerms_CardId", QueryNames(path, "index"));
    }

    [Fact]
    public void Constructor_CreatesAllFiveTablesAndSubtitleLogJobIdIndex()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        // 建表 SQL 改由嵌入資源 Sql/sqlite-schema.sql 提供：五張表與字幕紀錄的索引都要存在
        var tables = QueryNames(path, "table");
        Assert.Contains("TranslationCache", tables);
        Assert.Contains("GlossaryCards", tables);
        Assert.Contains("GlossaryTerms", tables);
        Assert.Contains("Streams", tables);
        Assert.Contains("SubtitleLog", tables);

        Assert.Contains("IX_SubtitleLog_JobId", QueryNames(path, "index"));
    }

    [Fact]
    public void Constructor_CreatesStreamCardsTableAndIndexes()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        Assert.Contains("StreamCards", QueryNames(path, "table"));

        var indexes = QueryNames(path, "index");
        Assert.Contains("IX_StreamCards_StreamId", indexes);
        Assert.Contains("IX_StreamCards_CardId", indexes);
    }

    /// <summary>直接查 StreamCards 的列，供斷言欄位值。</summary>
    private sealed class StreamCardRow
    {
        public long Id { get; set; }
        public long StreamId { get; set; }
        public long CardId { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string? ActivatedAt { get; set; }
        public string? DeactivatedAt { get; set; }
    }

    private static List<StreamCardRow> QueryStreamCards(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.Query<StreamCardRow>("SELECT * FROM StreamCards ORDER BY Id").ToList();
    }

    [Fact]
    public void AddStreamCard_ThenEndStreamCard_EndsOnlyLatestOpenRecord()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Execute(@"
                INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES
                    (7, 'Character', '英リサ', 'Approved'),
                    (8, 'Game', 'VALORANT', 'Approved')");
        }
        var streamId = db.Service.CreateStream(new StreamRecord("YouTube", "https://youtube.com/watch?v=1", "頻道", null, null, 7));

        db.Service.AddStreamCard(streamId!.Value, 7, "Host");
        db.Service.AddStreamCard(streamId.Value, 7, "Manual");
        db.Service.AddStreamCard(streamId.Value, 8, "Carried");
        db.Service.EndStreamCard(streamId.Value, 7);

        var rows = QueryStreamCards(path);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r.ActivatedAt));

        // 只結束「最新一筆未結束」的紀錄（Id = 2）；第一筆與其他卡不受影響
        Assert.Null(rows[0].DeactivatedAt);
        Assert.NotNull(rows[1].DeactivatedAt);
        Assert.Equal("Manual", rows[1].Reason);
        Assert.Null(rows[2].DeactivatedAt);

        // 再結束一次：已沒有未結束的紀錄，不做事也不丟例外
        db.Service.EndStreamCard(streamId.Value, 7);
        Assert.Equal(3, QueryStreamCards(path).Count);

        // 不存在的組合也不丟例外
        db.Service.EndStreamCard(999, 7);
        db.Service.AddStreamCard(streamId.Value, 7, "Title");
        Assert.Equal(4, QueryStreamCards(path).Count);
    }

    [Fact]
    public void CreateStream_ReturnsIncreasingIds()
    {
        using var db = new TempDatabase();

        using (var conn = new SqliteConnection($"Data Source={PathOf(db)}"))
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (7, 'Character', '英リサ', 'Approved')");
        }

        var first = db.Service.CreateStream(new StreamRecord("YouTube", "https://youtube.com/watch?v=1", "頻道A", "標題", null, null));
        var second = db.Service.CreateStream(new StreamRecord("Twitch", "https://twitch.tv/b", null, null, " category ", 7));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(second > first);
    }

    [Fact]
    public void InsertSubtitle_WithRevisionOfJobId_LinksToLatestRowOfThatJobId()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        db.Service.InsertSubtitle(Entry("job1", rawSource: "ペコラ", source: "ぺこら", translation: "佩克拉"));
        db.Service.InsertSubtitle(Entry("job2", revisionOf: "job1"));

        var rows = QuerySubtitles(path);
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].RevisionOfId);
        Assert.Equal(rows[0].Id, rows[1].RevisionOfId);
        Assert.Equal("ペコラ", rows[0].RawSource);
        Assert.Equal("ぺこら", rows[0].Source);
        Assert.Equal("2026-01-02 03:04:05", rows[0].CreatedAt);
    }

    [Fact]
    public void MarkSubtitleEdited_UpdatesOnlyLatestRowAndNullSourceKeepsSource()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        // 同一個 JobId 兩列（修正版取代中間版本）
        db.Service.InsertSubtitle(Entry("job1", source: "中間版", translation: "舊譯"));
        db.Service.InsertSubtitle(Entry("job1", source: "確定版", translation: "新譯"));

        // source 為 null：只改譯文，Source 不變
        db.Service.MarkSubtitleEdited("job1", null, "手動譯");

        var rows = QuerySubtitles(path);
        Assert.Equal(2, rows.Count);
        Assert.Equal("中間版", rows[0].Source);
        Assert.Equal("舊譯", rows[0].Translation);
        Assert.Equal(0, rows[0].IsEdited);
        Assert.Null(rows[0].UpdatedAt);
        Assert.Equal("確定版", rows[1].Source);
        Assert.Equal("手動譯", rows[1].Translation);
        Assert.Equal(1, rows[1].IsEdited);
        Assert.NotNull(rows[1].UpdatedAt);
    }

    [Fact]
    public void MarkSubtitleEdited_WithSource_UpdatesSource()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        db.Service.InsertSubtitle(Entry("job1", source: "聽錯", translation: "譯"));
        db.Service.MarkSubtitleEdited("job1", "正確", "新譯");

        var row = Assert.Single(QuerySubtitles(path));
        Assert.Equal("正確", row.Source);
        Assert.Equal("新譯", row.Translation);
        Assert.Equal(1, row.IsEdited);
    }

    [Fact]
    public void MarkSubtitleDeleted_OnlySetsFlag_RowCountUnchanged()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        db.Service.InsertSubtitle(Entry("job1"));
        db.Service.InsertSubtitle(Entry("job2"));
        db.Service.MarkSubtitleDeleted("job1");

        var rows = QuerySubtitles(path);
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].IsDeleted);
        Assert.NotNull(rows[0].UpdatedAt);
        Assert.Equal(0, rows[1].IsDeleted);
    }

    [Fact]
    public void MarkSubtitleEditedOrDeleted_UnknownJobId_DoesNotThrow()
    {
        using var db = new TempDatabase();

        db.Service.MarkSubtitleEdited("不存在", "原文", "譯文");
        db.Service.MarkSubtitleEdited("不存在", null, "譯文");
        db.Service.MarkSubtitleDeleted("不存在");
    }

    [Fact]
    public void InsertSubtitle_UnknownRevisionOfJobId_StoresNullRevision()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        db.Service.InsertSubtitle(Entry("job1", revisionOf: "不存在的job"));

        var row = Assert.Single(QuerySubtitles(path));
        Assert.Null(row.RevisionOfId);
    }

    [Fact]
    public void Constructor_OldDatabaseWithOnlyTranslationCache_KeepsDataAndAddsNewTables()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tg-test-{Guid.NewGuid():N}.db");
        try
        {
            // 先建出只有 TranslationCache 的舊資料庫並寫入一筆
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                conn.Execute(@"
                    CREATE TABLE IF NOT EXISTS TranslationCache (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        SourceText TEXT UNIQUE,
                        TargetText TEXT,
                        IsManual INTEGER DEFAULT 0,
                        LastUsed DATETIME
                    )");
                conn.Execute("INSERT INTO TranslationCache (SourceText, TargetText) VALUES ('舊句', '舊譯')");
            }

            var store = new SqliteTranslationStore(NullLogger<SqliteTranslationStore>.Instance, path);

            Assert.Equal("舊譯", store.GetCache("舊句"));
            var tables = QueryNames(path, "table");
            Assert.Contains("TranslationCache", tables);
            Assert.Contains("GlossaryCards", tables);
            Assert.Contains("GlossaryTerms", tables);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Constructor_OldDatabaseWithOnlyTranslationCache_AddsStreamsAndSubtitleLog()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tg-test-{Guid.NewGuid():N}.db");
        try
        {
            // 只有 TranslationCache 的舊資料庫：升級後資料保留，並多出 Streams 與 SubtitleLog
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                conn.Execute("CREATE TABLE TranslationCache (Id INTEGER PRIMARY KEY, SourceText TEXT, TargetText TEXT)");
                conn.Execute("INSERT INTO TranslationCache (SourceText, TargetText) VALUES ('舊句', '舊譯')");
            }

            var store = new SqliteTranslationStore(NullLogger<SqliteTranslationStore>.Instance, path);

            Assert.Equal("舊譯", store.GetCache("舊句"));
            var tables = QueryNames(path, "table");
            Assert.Contains("Streams", tables);
            Assert.Contains("SubtitleLog", tables);

            // 新表可以直接寫入
            var streamId = store.CreateStream(new StreamRecord("YouTube", "https://youtube.com/watch?v=1", null, null, null, null));
            store.InsertSubtitle(Entry("job1", streamId: streamId));
            var row = Assert.Single(QuerySubtitles(path));
            Assert.Equal(streamId, row.StreamId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Constructor_TwiceOnSameFile_DoesNotThrow()
    {
        using var db = new TempDatabase();

        var second = new SqliteTranslationStore(NullLogger<SqliteTranslationStore>.Instance, PathOf(db));

        Assert.Equal($"SQLite：{PathOf(db)}", second.Description);
    }

    [Fact]
    public void GetGlossaryCardsAndTerms_ExcludeRejectedAndMapFields()
    {
        using var db = new TempDatabase();
        using (var conn = new SqliteConnection($"Data Source={PathOf(db)}"))
        {
            conn.Execute(@"
                INSERT INTO GlossaryCards (Id, Kind, Name, ParentId, ChannelId, ChannelHandle, Description, SourceUrl, Status) VALUES
                    (1, 'Group',     'ぶいすぽっ!', NULL, NULL, NULL, '團體說明', 'https://example.com/group', 'Approved'),
                    (2, 'Character', '英リサ',      1,    'UC123', '@lisahanabusa', NULL, NULL, 'Approved'),
                    (3, 'Character', '已刪除的卡',  NULL, NULL, NULL, NULL, NULL, 'Rejected')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Aliases, Category, Note, Status) VALUES
                    (11, 1, 'あかりん', '小燈', NULL, '成員', '備註', 'Approved'),
                    (12, 2, '英リサ',   '英莉莎', 'Hanabusa', NULL, NULL, 'Approved'),
                    (13, 2, '已刪除',   '名詞', NULL, NULL, NULL, 'Rejected')");
        }

        var cards = db.Service.GetGlossaryCards();
        var terms = db.Service.GetGlossaryTerms();

        Assert.Equal(2, cards.Count);
        Assert.DoesNotContain(cards, c => c.Name == "已刪除的卡");

        var group = Assert.Single(cards, c => c.Name == "ぶいすぽっ!");
        Assert.Null(group.ParentId);
        Assert.Equal("團體說明", group.Description);
        Assert.Equal("https://example.com/group", group.SourceUrl);
        Assert.Equal("Approved", group.Status);

        var character = Assert.Single(cards, c => c.Name == "英リサ");
        Assert.Equal(1, character.ParentId);
        Assert.Equal("UC123", character.ChannelId);
        Assert.Equal("@lisahanabusa", character.ChannelHandle);

        Assert.Equal(2, terms.Count);
        Assert.DoesNotContain(terms, t => t.Source == "已刪除");
        // 依 CardId、Id 排序
        Assert.Equal([11L, 12L], terms.Select(t => t.Id).ToList());

        var term = Assert.Single(terms, t => t.CardId == 2);
        Assert.Equal("英リサ", term.Source);
        Assert.Equal("英莉莎", term.Target);
        Assert.Equal("Hanabusa", term.Aliases);
    }

    /// <summary>直接查 GlossaryCards／GlossaryTerms 的列，供斷言欄位值（含 UpdatedAt）。</summary>
    private sealed class GlossaryRow
    {
        public long Id { get; set; }
        public long? ParentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? ChannelHandle { get; set; }
        public string? Description { get; set; }
        public string? SourceUrl { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string? Aliases { get; set; }
        public string? Category { get; set; }
        public string? Note { get; set; }
        public string? UpdatedAt { get; set; }
    }

    private static List<GlossaryRow> QueryCards(string path) =>
        QueryGlossary(path, "SELECT Id, ParentId, Name, ChannelHandle, Aliases, Description, SourceUrl, Status, NULL AS Source, NULL AS Target, NULL AS Category, NULL AS Note, UpdatedAt FROM GlossaryCards ORDER BY Id");

    private static List<GlossaryRow> QueryTerms(string path) =>
        QueryGlossary(path, "SELECT Id, NULL AS ParentId, NULL AS Name, NULL AS ChannelHandle, NULL AS Description, NULL AS SourceUrl, Status, Source, Target, Aliases, Category, Note, UpdatedAt FROM GlossaryTerms ORDER BY Id");

    private static List<GlossaryRow> QueryGlossary(string path, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.Query<GlossaryRow>(sql).ToList();
    }

    [Fact]
    public void GetAllGlossaryCardsAndTerms_IncludeRejected()
    {
        using var db = new TempDatabase();
        using (var conn = new SqliteConnection($"Data Source={PathOf(db)}"))
        {
            conn.Execute(@"
                INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES
                    (1, 'Group',     'G團體',     'Approved'),
                    (2, 'Character', 'C角色',     'Pending'),
                    (3, 'Game',      '已拒絕的卡', 'Rejected')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '済み', '甲', 'Approved'),
                    (12, 2, '未1',  '乙', 'Pending'),
                    (13, 3, '却下', '丙', 'Rejected')");
        }

        var cards = db.Service.GetAllGlossaryCards();
        var terms = db.Service.GetAllGlossaryTerms();

        Assert.Equal(3, cards.Count);
        Assert.Contains(cards, c => c.Name == "已拒絕的卡");

        Assert.Equal(3, terms.Count);
        Assert.Contains(terms, t => t.Source == "却下");
        // 依 CardId、Id 排序
        Assert.Equal([11L, 12L, 13L], terms.Select(t => t.Id).ToList());
    }

    [Fact]
    public void SaveGlossaryChanges_UpdatesFieldsAndSetsUpdatedAt()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Execute(@"
                INSERT INTO GlossaryCards (Id, Kind, Name, ParentId, ChannelId, ChannelHandle, Description, SourceUrl, Status) VALUES
                    (1, 'Group', '舊團體', NULL, 'UC123', '@old', '舊說明', 'https://old', 'Pending'),
                    (2, 'Group', '不動的卡', NULL, NULL, NULL, NULL, NULL, 'Approved')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Aliases, Category, Note, Status) VALUES
                    (11, 1, '舊詞', '舊譯', '舊別名', '舊分類', '舊備註', 'Pending'),
                    (12, 2, '不動', '不動譯', NULL, NULL, NULL, 'Approved')");
        }

        db.Service.SaveGlossaryChanges(
            [new GlossaryCard
            {
                Id = 1,
                Kind = "Group",
                Name = "新團體",
                ParentId = null,
                ChannelId = "UC123",
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

        var card = Assert.Single(QueryCards(path), c => c.Id == 1);
        Assert.Equal("新團體", card.Name);
        Assert.Null(card.ParentId);
        Assert.Equal("@new", card.ChannelHandle);
        Assert.Equal("新說明", card.Description);
        Assert.Equal("https://new", card.SourceUrl);
        Assert.Equal("Approved", card.Status);
        Assert.NotNull(card.UpdatedAt);

        var term = Assert.Single(QueryTerms(path), t => t.Id == 11);
        Assert.Equal("新詞", term.Source);
        Assert.Equal("新譯", term.Target);
        Assert.Equal("新別名", term.Aliases);
        Assert.Equal("新分類", term.Category);
        Assert.Equal("新備註", term.Note);
        Assert.Equal("Rejected", term.Status);
        Assert.NotNull(term.UpdatedAt);

        // 同一批沒列出的列不更新（UpdatedAt 仍為 null）
        var untouchedCard = Assert.Single(QueryCards(path), c => c.Id == 2);
        Assert.Null(untouchedCard.UpdatedAt);
        var untouchedTerm = Assert.Single(QueryTerms(path), t => t.Id == 12);
        Assert.Null(untouchedTerm.UpdatedAt);
    }

    [Fact]
    public void SaveGlossaryChanges_UniqueViolation_RollsBackWholeBatch()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Group', 'G團體', 'Pending')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '甲', '甲譯', 'Pending'),
                    (12, 1, '乙', '乙譯', 'Pending')");
        }

        // 同一批：改卡名 + 兩個名詞都改成同一個 Source（違反 UNIQUE(CardId, Source)）
        Assert.ThrowsAny<Exception>(() => db.Service.SaveGlossaryChanges(
            [new GlossaryCard { Id = 1, Kind = "Group", Name = "新團體", Status = "Approved" }],
            [
                new GlossaryTerm { Id = 11, CardId = 1, Source = "同名", Target = "甲譯", Status = "Approved" },
                new GlossaryTerm { Id = 12, CardId = 1, Source = "同名", Target = "乙譯", Status = "Approved" },
            ]));

        // 整批回復：卡名與兩個名詞都沒有被更新，UpdatedAt 仍為 null
        var card = Assert.Single(QueryCards(path));
        Assert.Equal("G團體", card.Name);
        Assert.Equal("Pending", card.Status);
        Assert.Null(card.UpdatedAt);

        var terms = QueryTerms(path);
        Assert.Equal("甲", terms[0].Source);
        Assert.Equal("乙", terms[1].Source);
        Assert.All(terms, t => Assert.Null(t.UpdatedAt));
    }

    [Fact]
    public void SaveGlossaryTermEdits_UpdatesExistingAndInsertsNew()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Aliases, Category, Note, Status) VALUES
                    (11, 1, 'ヴァンダル', '暴徒', NULL, '武器', NULL, 'Approved'),
                    (12, 1, 'オペ', '玩家', NULL, NULL, NULL, 'Approved')");
        }

        db.Service.SaveGlossaryTermEdits(
            [new GlossaryTerm
            {
                Id = 11,
                CardId = 1,
                Source = "ヴァンダル",
                Target = "暴徒步槍",
                Aliases = "Vandal",
                Category = "武器",
                Note = "步槍",
                Status = "Approved",
            }],
            [new GlossaryTerm
            {
                Id = 0,
                CardId = 1,
                Source = "スペクター",
                Target = "幽靈",
                Category = "武器",
                Status = "Pending",
            }]);

        var terms = QueryTerms(path);
        Assert.Equal(3, terms.Count);

        var updated = Assert.Single(terms, t => t.Id == 11);
        Assert.Equal("暴徒步槍", updated.Target);
        Assert.Equal("Vandal", updated.Aliases);
        Assert.Equal("步槍", updated.Note);
        Assert.Equal("Approved", updated.Status);
        Assert.NotNull(updated.UpdatedAt);

        // 新增的名詞：Id 由資料庫給、CreatedAt 用預設值（不是 null）、UpdatedAt 留空
        var inserted = Assert.Single(terms, t => t.Source == "スペクター");
        Assert.NotEqual(0L, inserted.Id);
        Assert.Equal("幽靈", inserted.Target);
        Assert.Equal("Pending", inserted.Status);
        Assert.Null(inserted.UpdatedAt);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            Assert.False(string.IsNullOrEmpty(conn.QuerySingle<string?>(
                "SELECT CreatedAt FROM GlossaryTerms WHERE Source = 'スペクター'")));
        }

        // 同一批沒列出的列不更新（UpdatedAt 仍為 null）
        var untouched = Assert.Single(terms, t => t.Id == 12);
        Assert.Null(untouched.UpdatedAt);
    }

    [Fact]
    public void SaveGlossaryTermEdits_UniqueViolation_RollsBackWholeBatch()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, 'ヴァンダル', '暴徒', 'Approved'),
                    (12, 1, 'オペ', '玩家', 'Approved')");
        }

        // 同一批：更新名詞 11 + 新增一個與名詞 12 同 Source 的名詞（違反 UNIQUE(CardId, Source)）
        Assert.ThrowsAny<Exception>(() => db.Service.SaveGlossaryTermEdits(
            [new GlossaryTerm { Id = 11, CardId = 1, Source = "ヴァンダル", Target = "暴徒步槍", Status = "Approved" }],
            [new GlossaryTerm { Id = 0, CardId = 1, Source = "オペ", Target = "重複", Status = "Pending" }]));

        // 整批回復：更新沒有生效（UpdatedAt null）、新增沒有寫入
        var terms = QueryTerms(path);
        Assert.Equal(2, terms.Count);
        Assert.Equal("暴徒", terms[0].Target);
        Assert.All(terms, t => Assert.Null(t.UpdatedAt));
    }

    // ===== CreateGlossaryCard 與 TermCandidates =====

    /// <summary>直接查 TermCandidates 的列，供斷言欄位值。</summary>
    private sealed class CandidateRow
    {
        public long Id { get; set; }
        public string Surface { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public long Importance { get; set; }
        public string Origin { get; set; } = string.Empty;
        public long? StreamId { get; set; }
        public string? Context { get; set; }
        public long Occurrences { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? CreatedAt { get; set; }
        public string? LastSeenAt { get; set; }
    }

    private static List<CandidateRow> QueryCandidates(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.Query<CandidateRow>("SELECT * FROM TermCandidates ORDER BY Id").ToList();
    }

    [Fact]
    public void Constructor_CreatesTermCandidatesTableAndIndex()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        Assert.Contains("TermCandidates", QueryNames(path, "table"));
        Assert.Contains("IX_TermCandidates_Status", QueryNames(path, "index"));
    }

    [Fact]
    public void CreateGlossaryCard_InsertsCardAndTerms_ReturnsNewId()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        var cardId = db.Service.CreateGlossaryCard(
            new GlossaryCard
            {
                Kind = "Game",
                Name = "マイクラ",
                ParentId = null,
                ChannelId = null,
                ChannelHandle = null,
                Description = "簡介",
                SourceUrl = "https://www.wikidata.org/wiki/Q1",
                Status = "Pending",
            },
            [
                new GlossaryTerm { Source = "マイクラ", Target = "當個創世神", Category = "遊戲", Note = "資料探勘", Status = "Pending" },
                new GlossaryTerm { Source = "鑽石", Target = "鑽石", Status = "Pending" },
            ]);

        Assert.NotEqual(0L, cardId);
        var card = Assert.Single(QueryCards(path));
        Assert.Equal(cardId, card.Id);
        Assert.Equal("マイクラ", card.Name);
        Assert.Equal("簡介", card.Description);
        Assert.Equal("https://www.wikidata.org/wiki/Q1", card.SourceUrl);
        Assert.Equal("Pending", card.Status);

        var terms = QueryTerms(path);
        Assert.Equal(2, terms.Count);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            var cardIds = conn.Query<long>("SELECT CardId FROM GlossaryTerms ORDER BY Id").ToList();
            Assert.All(cardIds, id => Assert.Equal(cardId, id));
            Assert.False(string.IsNullOrEmpty(conn.QuerySingle<string?>(
                "SELECT CreatedAt FROM GlossaryCards WHERE Id = @id", new { id = cardId })));
        }

        // 讀回來的型錄欄位都對得上
        var read = Assert.Single(db.Service.GetGlossaryCards());
        Assert.Equal("マイクラ", read.Name);
        Assert.Equal(2, db.Service.GetGlossaryTerms().Count);
    }

    [Fact]
    public void CreateGlossaryCard_WithAliases_NormalizesPersistsAndReadsBack()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        var cardId = db.Service.CreateGlossaryCard(
            new GlossaryCard
            {
                Kind = "Game",
                Name = "Apex Legends",
                Aliases = " APEX \nApex Legends\nエペ\napex",
                Status = "Pending",
            },
            [new GlossaryTerm { Source = "エペ", Target = "エ佩", Status = "Pending" }]);

        // 存檔前正規化：去空白、去掉與卡名相同的行、不分大小寫去重複
        var card = Assert.Single(QueryCards(path));
        Assert.Equal("APEX\nエペ", card.Aliases);
        Assert.Equal("APEX\nエペ", Assert.Single(db.Service.GetGlossaryCards()).Aliases);
        Assert.Equal("APEX\nエペ", Assert.Single(db.Service.GetAllGlossaryCards()).Aliases);
        Assert.NotEqual(0L, cardId);
    }

    [Fact]
    public void SaveGlossaryChanges_UpdatesAliases()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'League of Legends', 'Pending')");
        }

        db.Service.SaveGlossaryChanges(
            [new GlossaryCard { Id = 1, Kind = "Game", Name = "League of Legends", Aliases = " LoL \nlol", Status = "Approved" }],
            []);

        Assert.Equal("LoL", Assert.Single(QueryCards(path)).Aliases);

        // 清成空白 → 存 null
        db.Service.SaveGlossaryChanges(
            [new GlossaryCard { Id = 1, Kind = "Game", Name = "League of Legends", Aliases = "  ", Status = "Approved" }],
            []);
        Assert.Null(Assert.Single(QueryCards(path)).Aliases);
    }

    [Fact]
    public void Constructor_OldDatabaseWithoutCardAliasesColumn_AddsColumnKeepsOldRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tg-test-{Guid.NewGuid():N}.db");
        try
        {
            // 先手動建沒有 Aliases 欄位的 GlossaryCards（模擬舊資料庫）並插入一筆卡
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                conn.Execute(@"
                    CREATE TABLE GlossaryCards (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Kind TEXT NOT NULL,
                        Name TEXT NOT NULL UNIQUE,
                        ParentId INTEGER REFERENCES GlossaryCards(Id) ON DELETE SET NULL,
                        ChannelId TEXT,
                        ChannelHandle TEXT,
                        Description TEXT,
                        SourceUrl TEXT,
                        Status TEXT NOT NULL DEFAULT 'Pending',
                        CreatedAt DATETIME NOT NULL DEFAULT (datetime('now')),
                        UpdatedAt DATETIME
                    )");
                conn.Execute("INSERT INTO GlossaryCards (Kind, Name, Status) VALUES ('Game', 'VALORANT', 'Approved')");
            }

            var store = new SqliteTranslationStore(NullLogger<SqliteTranslationStore>.Instance, path);

            // 欄位補上：舊卡的 Aliases 是 null，讀取不 include 它也對
            var card = Assert.Single(store.GetAllGlossaryCards());
            Assert.Null(card.Aliases);
            Assert.Equal("VALORANT", card.Name);

            // 而且可以寫入
            store.SaveGlossaryChanges(
                [new GlossaryCard { Id = card.Id, Kind = "Game", Name = "VALORANT", Aliases = "ヴァロ", Status = "Approved" }],
                []);
            var updated = Assert.Single(store.GetAllGlossaryCards());
            Assert.Equal("ヴァロ", updated.Aliases);

            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                var columns = conn.Query<string>(
                    "SELECT name FROM pragma_table_info('GlossaryCards')").ToList();
                Assert.Contains("Aliases", columns);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CreateGlossaryCard_FailingTerm_RollsBackCard()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        // 兩個名詞同 Source（違反 UNIQUE(CardId, Source)）：整批回復，卡也不該寫入
        Assert.ThrowsAny<Exception>(() => db.Service.CreateGlossaryCard(
            new GlossaryCard { Kind = "Game", Name = "マイクラ", Status = "Pending" },
            [
                new GlossaryTerm { Source = "同名", Target = "甲", Status = "Pending" },
                new GlossaryTerm { Source = "同名", Target = "乙", Status = "Pending" },
            ]));

        Assert.Empty(QueryCards(path));
        Assert.Empty(QueryTerms(path));
        Assert.Empty(db.Service.GetGlossaryCards());
    }

    [Fact]
    public void UpsertTermCandidates_InsertsAndAccumulates()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            // StreamId 有外部鍵限制：先建 Id 7 的直播
            conn.Execute("INSERT INTO Streams (Id, Platform, Url) VALUES (7, 'YouTube', 'https://example.com/live')");
            conn.Execute("INSERT INTO Streams (Id, Platform, Url) VALUES (9, 'YouTube', 'https://example.com/live2')");
        }

        db.Service.UpsertTermCandidates(
        [
            new TermCandidate("花火大會", "event", 4, "Situation", 7, "夏天的事件"),
            new TermCandidate("マイクラ", "game", 2, "Situation", null, null),
        ]);

        var rows = QueryCandidates(path);
        Assert.Equal(2, rows.Count);
        var first = Assert.Single(rows, r => r.Surface == "花火大會");
        Assert.Equal("event", first.Kind);
        Assert.Equal(4, first.Importance);
        Assert.Equal("Situation", first.Origin);
        Assert.Equal(7, first.StreamId);
        Assert.Equal("夏天的事件", first.Context);
        Assert.Equal(1, first.Occurrences);
        Assert.Equal("New", first.Status);
        Assert.NotNull(first.CreatedAt);
        Assert.NotNull(first.LastSeenAt);

        // 同 (Surface, Kind) 再探勘一次：Occurrences + 1、Importance 取較大值、Context／StreamId 更新
        db.Service.UpsertTermCandidates(
        [
            new TermCandidate("花火大會", "event", 2, "Situation", 9, "晚上的事件"),
        ]);

        rows = QueryCandidates(path);
        Assert.Equal(2, rows.Count);
        var accumulated = Assert.Single(rows, r => r.Surface == "花火大會");
        Assert.Equal(2, accumulated.Occurrences);
        Assert.Equal(4, accumulated.Importance);
        Assert.Equal(9, accumulated.StreamId);
        Assert.Equal("晚上的事件", accumulated.Context);

        // Kind 不同視為不同候選
        db.Service.UpsertTermCandidates(
        [
            new TermCandidate("花火大會", "place", 1, "Situation", null, null),
        ]);
        Assert.Equal(3, QueryCandidates(path).Count);
    }

    // ===== MishearingFixes =====

    /// <summary>直接查 MishearingFixes 的列，供斷言欄位值。</summary>
    private sealed class MishearingRow
    {
        public long Id { get; set; }
        public long CardId { get; set; }
        public long TermId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Wrong { get; set; } = string.Empty;
        public string Right { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? Evidence { get; set; }
        public long Occurrences { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }

    private static List<MishearingRow> QueryMishearingFixes(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.Query<MishearingRow>("SELECT * FROM MishearingFixes ORDER BY Id").ToList();
    }

    /// <summary>建一張卡（Id=1）與一個名詞（Id=11），供 MishearingFixes 測試使用。</summary>
    private static void SeedCardAndTerm(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
        conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'ヴァンダル', '暴徒', 'Approved')");
    }

    [Fact]
    public void Constructor_CreatesMishearingFixesTableAndIndex()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        Assert.Contains("MishearingFixes", QueryNames(path, "table"));
        Assert.Contains("IX_MishearingFixes_Status", QueryNames(path, "index"));
    }

    [Fact]
    public void GetMishearingFixes_MapsFieldsAndSortsByCardTermWrong()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        SeedCardAndTerm(path);

        Assert.Equal(2, db.Service.UpsertMishearingFixes(
        [
            new MishearingProposal(1, 11, MishearingKind.Pre, "ツクリー", "ヴァンダル", "ツクーーリ買った"),
            new MishearingProposal(1, 11, MishearingKind.Pre, "作り", "ヴァンダル", "今日は作りを買った"),
        ]));

        var fixes = db.Service.GetMishearingFixes();

        // 依 CardId、TermId、Wrong 排序（SQLite BINARY：片假名 ツ U+30C4 在漢字 作 U+4F5C 之前）
        Assert.Equal(["ツクリー", "作り"], fixes.Select(f => f.Wrong).ToList());
        var first = fixes[0];
        Assert.Equal(1, first.CardId);
        Assert.Equal(11, first.TermId);
        Assert.Equal(MishearingKind.Pre, first.Kind);
        Assert.Equal("ヴァンダル", first.Right);
        Assert.Equal(MishearingStatus.Pending, first.Status);
        Assert.Equal("ツクーーリ買った", first.Evidence);
        Assert.Equal(1, first.Occurrences);
        Assert.NotEqual(default, first.CreatedAt);
        Assert.Null(first.UpdatedAt);
    }

    [Fact]
    public void UpsertMishearingFixes_AccumulatesExistingAndKeepsStatus()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        SeedCardAndTerm(path);

        Assert.Equal(1, db.Service.UpsertMishearingFixes(
            [new MishearingProposal(1, 11, MishearingKind.Pre, "作り", "ヴァンダル", "第一句證據")]));

        // 先核准，再對同一個 (TermId, Kind, Wrong) Upsert：Occurrences + 1、Evidence 換新、Status 仍是 Approved
        var id = QueryMishearingFixes(path).Single().Id;
        db.Service.ApproveMishearingFixes([id]);

        Assert.Equal(0, db.Service.UpsertMishearingFixes(
            [new MishearingProposal(1, 11, MishearingKind.Pre, "  作り  ", "ヴァンダル", "第二句證據")]));

        var row = Assert.Single(QueryMishearingFixes(path));
        Assert.Equal(2, row.Occurrences);
        Assert.Equal("第二句證據", row.Evidence);
        Assert.Equal("Approved", row.Status);
        Assert.NotNull(row.UpdatedAt);
    }

    [Fact]
    public void UpsertMishearingFixes_SkipsInvalidProposals_ReturnCountOnlyCountsInserts()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        SeedCardAndTerm(path);

        var inserted = db.Service.UpsertMishearingFixes(
        [
            new MishearingProposal(1, 11, MishearingKind.Pre, "字", "詞", "太短的錯字"),
            new MishearingProposal(1, 11, MishearingKind.Pre, "  ", "詞", "去頭尾後空白"),
            new MishearingProposal(1, 11, MishearingKind.Pre, "ヴァンダル", "ヴァンダル", "錯字等於正確"),
            new MishearingProposal(1, 11, MishearingKind.Pre, "作り", "ヴァンダル", "   "),
            new MishearingProposal(1, 11, MishearingKind.Pre, "ツクリー", "ヴァンダル", "有效的證據"),
        ]);

        Assert.Equal(1, inserted);
        var row = Assert.Single(QueryMishearingFixes(path));
        Assert.Equal("ツクリー", row.Wrong);
    }

    [Fact]
    public void ApproveAndDeleteMishearingFixes_OnlyAffectGivenIds()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        SeedCardAndTerm(path);

        db.Service.UpsertMishearingFixes(
        [
            new MishearingProposal(1, 11, MishearingKind.Pre, "作り", "ヴァンダル", "證據一"),
            new MishearingProposal(1, 11, MishearingKind.Pre, "ツクリー", "ヴァンダル", "證據二"),
        ]);
        var rows = QueryMishearingFixes(path);
        Assert.All(rows, r => Assert.Equal("Pending", r.Status));

        db.Service.ApproveMishearingFixes([rows[0].Id]);

        rows = QueryMishearingFixes(path);
        Assert.Equal("Approved", rows[0].Status);
        Assert.NotNull(rows[0].UpdatedAt);
        Assert.Equal("Pending", rows[1].Status);
        Assert.Null(rows[1].UpdatedAt);

        // 駁回＝直接刪除
        db.Service.DeleteMishearingFixes([rows[1].Id]);

        rows = QueryMishearingFixes(path);
        var remaining = Assert.Single(rows);
        Assert.Equal("作り", remaining.Wrong);
    }

    [Fact]
    public void UpsertMishearingFixes_FailingSecondEntry_RollsBackWholeBatch()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        SeedCardAndTerm(path);

        // 第二筆 Right 為 null：違反 NOT NULL，第一筆（已寫入）也要一起回復
        Assert.ThrowsAny<Exception>(() => db.Service.UpsertMishearingFixes(
            new List<MishearingProposal>
            {
                new(1, 11, MishearingKind.Pre, "作り", "ヴァンダル", "證據一"),
                new(1, 11, MishearingKind.Pre, "ツクリー", null!, "證據二"),
            }));

        Assert.Empty(QueryMishearingFixes(path));
    }

    // ===== MergeGlossaryCards 與 DeleteGlossaryCard =====

    /// <summary>合併／刪除測試共用的資料：target 卡 1、source 卡 2（子卡 3）、無關卡 4，各帶名詞與聽錯字修正、直播 7／8。</summary>
    private static void SeedMergeAndDeleteFixture(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Execute(@"
            INSERT INTO GlossaryCards (Id, Kind, Name, ParentId, ChannelId, ChannelHandle, Aliases, Status) VALUES
                (1, 'Character', '英リサ',    NULL, 'UC123', '@lisahanabusa', 'Hanabusa', 'Approved'),
                (2, 'Character', 'うるは先輩', NULL, NULL,    NULL,            NULL,       'Pending'),
                (3, 'Group',     '子卡',      2,    NULL, NULL, NULL, 'Approved'),
                (4, 'Game',      '其他卡',    NULL, NULL, NULL, NULL, 'Approved')");
        conn.Execute(@"
            INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                (11, 1, 'ウルハ',     'ウ露哈', 'Approved'),
                (12, 1, 'エペ',       '艾佩',   'Approved'),
                (21, 2, 'うるは',     '潤羽',   'Approved'),
                (22, 2, 'ヴァンダル', '暴徒',   'Approved'),
                (23, 2, 'すぺくたー', '幽靈',   'Rejected'),
                (31, 3, '子卡名詞',   '子卡名詞', 'Approved'),
                (41, 4, '其他名詞',   '其他名詞', 'Approved')");
        conn.Execute(@"
            INSERT INTO MishearingFixes (CardId, TermId, Kind, Wrong, Right, Status, Evidence) VALUES
                (2, 21, 'Pre', 'ウルハー',     'うるは',     'Approved', 'ウルハーだ'),
                (2, 21, 'Pre', 'バンダル',     'うるは',     'Pending',  'バンダルだ'),
                (1, 11, 'Pre', 'バンダル',     'ウルハ',     'Approved', '既有的バンダル'),
                (2, 22, 'Pre', 'ヴァンーダル', 'ヴァンダル', 'Pending',  'ヴァンーダルだ'),
                (4, 41, 'Pre', '其他錯字',     '其他名詞',   'Pending',  '其他證據')");
        conn.Execute(@"
            INSERT INTO Streams (Id, Platform, Url, CharacterCardId) VALUES
                (7, 'YouTube', 'https://example.com/7', 2),
                (8, 'YouTube', 'https://example.com/8', 4)");
        conn.Execute(@"
            INSERT INTO StreamCards (StreamId, CardId, Reason) VALUES
                (7, 2, 'Host'),
                (7, 4, 'Carried')");
    }

    /// <summary>直接查名詞的 CardId 與 Status，供合併／刪除測試斷言。</summary>
    private sealed class TermCardRow
    {
        public long Id { get; set; }
        public long CardId { get; set; }
        public string Source { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    private static List<TermCardRow> QueryTermCards(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.Query<TermCardRow>("SELECT Id, CardId, Source, Status FROM GlossaryTerms ORDER BY Id").ToList();
    }

    private static List<long?> QueryStreamCharacterCards(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.Query<long?>("SELECT CharacterCardId FROM Streams ORDER BY Id").ToList();
    }

    [Fact]
    public void MergeGlossaryCards_MovesTermsDropsConflictsAndRepointsReferences()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        SeedMergeAndDeleteFixture(path);

        var result = db.Service.MergeGlossaryCards(2, 1);

        // 搬 2 個（ヴァンダル、すぺくたー）、丟 1 個（うるは 對到 ウルハ）、加 1 行別名（source 卡名）
        Assert.Equal(new GlossaryMergeResult(2, 1, 1), result);

        // source 卡不見了；target 卡補上別名、有 UpdatedAt、原本的欄位沒被清掉
        var cards = QueryCards(path);
        Assert.DoesNotContain(cards, c => c.Id == 2);
        var target = Assert.Single(cards, c => c.Id == 1);
        Assert.Equal("Hanabusa\nうるは先輩", target.Aliases);
        Assert.Equal("@lisahanabusa", target.ChannelHandle);
        Assert.NotNull(target.UpdatedAt);

        // 子卡的上層指到 target；無關卡不動
        Assert.Equal(1L, Assert.Single(cards, c => c.Id == 3).ParentId);
        Assert.Null(Assert.Single(cards, c => c.Id == 4).ParentId);

        // 搬過去的名詞在 target 卡而且是 Pending（Rejected 的也照搬）；衝突的名詞被刪；target 原本的名詞不變
        var terms = QueryTermCards(path);
        Assert.DoesNotContain(terms, t => t.Id == 21);
        Assert.All(terms.Where(t => t.Id is 22 or 23), t =>
        {
            Assert.Equal(1L, t.CardId);
            Assert.Equal("Pending", t.Status);
        });
        Assert.All(terms.Where(t => t.Id is 11 or 12), t =>
        {
            Assert.Equal(1L, t.CardId);
            Assert.Equal("Approved", t.Status);
        });
        Assert.Equal(3L, Assert.Single(terms, t => t.Id == 31).CardId);

        // 丟掉的名詞的聽錯字修正改掛到 kept 名詞，重複的（バンダル）被刪；搬過去的名詞的修正跟著換卡
        var fixes = QueryMishearingFixes(path);
        Assert.DoesNotContain(fixes, f => f.TermId == 21);
        var movedFix = Assert.Single(fixes, f => f.Wrong == "ウルハー");
        Assert.Equal(11L, movedFix.TermId);
        Assert.Equal(1L, movedFix.CardId);
        var keptDuplicate = Assert.Single(fixes, f => f.Wrong == "バンダル");
        Assert.Equal(11L, keptDuplicate.TermId);
        Assert.Equal("Approved", keptDuplicate.Status);
        var movedTermFix = Assert.Single(fixes, f => f.Wrong == "ヴァンーダル");
        Assert.Equal(22L, movedTermFix.TermId);
        Assert.Equal(1L, movedTermFix.CardId);
        Assert.Equal(4L, Assert.Single(fixes, f => f.Wrong == "其他錯字").CardId);

        // 開台生效紀錄與直播的主播卡都指到 target；無關的不動
        Assert.Equal([(7L, 1L), (7L, 4L)], QueryStreamCards(path).Select(r => (r.StreamId, r.CardId)).ToList());
        Assert.Equal([1L, 4L], QueryStreamCharacterCards(path));
    }

    [Fact]
    public void MergeGlossaryCards_SameIdOrMissingCard_ThrowsAndKeepsData()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        SeedMergeAndDeleteFixture(path);

        Assert.Throws<ArgumentException>(() => db.Service.MergeGlossaryCards(1, 1));
        Assert.Throws<ArgumentException>(() => db.Service.MergeGlossaryCards(1, 99));
        Assert.Throws<ArgumentException>(() => db.Service.MergeGlossaryCards(99, 1));

        // 資料完全沒動：四張卡、七個名詞都在，也沒有被更新過（UpdatedAt 仍為 null）
        Assert.Equal(4, QueryCards(path).Count);
        Assert.Equal(7, QueryTerms(path).Count);
        Assert.All(QueryCards(path), c => Assert.Null(c.UpdatedAt));
        Assert.All(QueryTerms(path), t => Assert.Null(t.UpdatedAt));
    }

    [Fact]
    public void DeleteGlossaryCard_RemovesCardAndClearsReferences()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        SeedMergeAndDeleteFixture(path);

        db.Service.DeleteGlossaryCard(2);

        // 卡、名詞、聽錯字修正、開台生效紀錄都刪了
        var cards = QueryCards(path);
        Assert.DoesNotContain(cards, c => c.Id == 2);
        var terms = QueryTermCards(path);
        Assert.DoesNotContain(terms, t => t.CardId == 2);
        Assert.DoesNotContain(QueryMishearingFixes(path), f => f.CardId == 2);
        Assert.DoesNotContain(QueryStreamCards(path), r => r.CardId == 2);

        // 直播的主播卡與子卡的上層改成空；無關的不動
        Assert.Equal(new long?[] { null, 4L }, QueryStreamCharacterCards(path));
        Assert.Null(Assert.Single(cards, c => c.Id == 3).ParentId);
        Assert.Contains(cards, c => c.Id == 4);
        Assert.Contains(terms, t => t.Id == 41);
        Assert.Equal(4L, Assert.Single(QueryMishearingFixes(path), f => f.Wrong == "其他錯字").CardId);
        var remainingStreamCard = Assert.Single(QueryStreamCards(path));
        Assert.Equal(7L, remainingStreamCard.StreamId);
        Assert.Equal(4L, remainingStreamCard.CardId);

        // 卡不存在時不動作也不丟例外
        db.Service.DeleteGlossaryCard(999);
        Assert.Equal(3, QueryCards(path).Count);
    }

    [Fact]
    public void DeleteGlossaryTerms_RemovesTermsAndTheirFixes_KeepsOthers()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        SeedMergeAndDeleteFixture(path);

        // 21 有兩個聽錯字修正、23 沒有、999 不存在（略過）
        db.Service.DeleteGlossaryTerms([21, 23, 999]);

        // 只刪指定的名詞；同卡與其他卡的名詞不動
        var terms = QueryTermCards(path);
        Assert.Equal([11L, 12L, 22L, 31L, 41L], terms.Select(t => t.Id).ToList());

        // 被刪名詞的聽錯字修正一起刪掉；還在的名詞（22）的修正保留
        var fixes = QueryMishearingFixes(path);
        Assert.Equal([11L, 22L, 41L], fixes.Select(f => f.TermId).OrderBy(id => id).ToList());

        // 卡片與別名不受影響
        var cards = QueryCards(path);
        Assert.Equal(4, cards.Count);
        Assert.Equal("Hanabusa", Assert.Single(cards, c => c.Id == 1).Aliases);

        // 空清單不動作
        db.Service.DeleteGlossaryTerms([]);
        Assert.Equal(5, QueryTermCards(path).Count);
        Assert.Equal(3, QueryMishearingFixes(path).Count);
    }

    // ===== ICaptionStore（YoutubeCaptions）=====

    private static readonly CaptionCue[] StoredCues =
    [
        new CaptionCue(267040, 5520, "うん。なんか"),
        new CaptionCue(272560, 3000, "今日は"),
    ];

    private static long CountCaptions(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.QuerySingle<long>("SELECT COUNT(*) FROM YoutubeCaptions");
    }

    /// <summary>直接查 Streams 的列，供斷言新欄位值。</summary>
    private sealed class StreamRow
    {
        public long Id { get; set; }
        public string Platform { get; set; } = string.Empty;
        public long IsVod { get; set; }
        public string? VideoStartedAt { get; set; }
        public string? VideoEndedAt { get; set; }
    }

    private static List<StreamRow> QueryStreams(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.Query<StreamRow>("SELECT Id, Platform, IsVod, VideoStartedAt, VideoEndedAt FROM Streams ORDER BY Id").ToList();
    }

    [Fact]
    public void Constructor_CreatesYoutubeCaptionsAndCaptionSentencesTables()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        var tables = QueryNames(path, "table");
        Assert.Contains("YoutubeCaptions", tables);
        Assert.Contains("CaptionSentences", tables);
        Assert.Contains("IX_YoutubeCaptions_LastUsedAt", QueryNames(path, "index"));
    }

    [Fact]
    public void GetCaption_Missing_ReturnsNull()
    {
        using var db = new TempDatabase();

        Assert.Null(db.Service.GetCaption("dQw4w9WgXcQ", "ja"));
    }

    [Fact]
    public void SaveCaption_ThenGetCaption_ReturnsStoredFieldsAndCues()
    {
        using var db = new TempDatabase();

        var id = db.Service.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: true, "VISIONOS", StoredCues);

        Assert.NotNull(id);
        Assert.NotEqual(0L, id);
        var caption = db.Service.GetCaption("dQw4w9WgXcQ", "ja");
        Assert.NotNull(caption);
        Assert.Equal(id, caption.Id);
        Assert.Equal("dQw4w9WgXcQ", caption.VideoId);
        Assert.Equal("ja", caption.Language);
        Assert.True(caption.IsAuto);
        Assert.Equal("VISIONOS", caption.ClientName);
        Assert.Equal(StoredCues, caption.Cues);
        Assert.True(caption.CreatedAt <= DateTimeOffset.UtcNow);
        Assert.True(caption.LastUsedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void SaveCaption_SameVideoAndLanguageTwice_OverwritesSingleRow()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        db.Service.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: true, "IOS", StoredCues);
        var second = db.Service.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: false, "VISIONOS",
            [new CaptionCue(1000, 500, "新しい字幕")]);

        Assert.Equal(1, CountCaptions(path));
        var caption = db.Service.GetCaption("dQw4w9WgXcQ", "ja");
        Assert.NotNull(caption);
        Assert.Equal(second, caption.Id);
        Assert.False(caption.IsAuto);
        Assert.Equal("VISIONOS", caption.ClientName);
        Assert.Equal("新しい字幕", Assert.Single(caption.Cues).Text);
    }

    [Fact]
    public void SaveCaption_DifferentLanguages_StoredSeparately()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        db.Service.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: true, "IOS", StoredCues);
        db.Service.SaveCaption("dQw4w9WgXcQ", "en", isAuto: true, "IOS", [new CaptionCue(0, 100, "hello")]);

        Assert.Equal(2, CountCaptions(path));
        Assert.Equal(2, db.Service.GetCaption("dQw4w9WgXcQ", "ja")!.Cues.Count);
        Assert.Equal("hello", Assert.Single(db.Service.GetCaption("dQw4w9WgXcQ", "en")!.Cues).Text);
    }

    [Fact]
    public void TouchCaption_UpdatesLastUsedAt()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        var id = db.Service.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: true, "IOS", StoredCues);

        // 直接把兩個時間回填到過去：才能斷言 Touch 之後 LastUsedAt 變新（CreatedAt 不動）
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Execute(
                "UPDATE YoutubeCaptions SET CreatedAt = '2020-01-01 00:00:00', LastUsedAt = '2020-01-01 00:00:00' WHERE Id = @id",
                new { id });
        }

        db.Service.TouchCaption(id!.Value);

        var caption = db.Service.GetCaption("dQw4w9WgXcQ", "ja");
        Assert.NotNull(caption);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), caption.CreatedAt);
        Assert.True(caption.LastUsedAt > caption.CreatedAt);
    }

    // ===== ICaptionStore（CaptionSentences）=====

    private static CaptionSentence Sentence(long startMs, long endMs, string text, string? model = "glm-test") =>
        new(startMs, endMs, $"raw:{text}", text, model);

    [Fact]
    public void GetSentences_NoCaption_ReturnsEmpty()
    {
        using var db = new TempDatabase();

        Assert.Empty(db.Service.GetSentences(999));
    }

    [Fact]
    public void SaveSentences_ThenGetSentences_ReturnsSortedRows()
    {
        using var db = new TempDatabase();
        var captionId = db.Service.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: true, "IOS", StoredCues)!.Value;

        // 故意亂序寫入：讀回要依 StartMs 排序
        db.Service.SaveSentences(captionId,
        [
            Sentence(2000, 3000, "乙"),
            Sentence(1000, 1500, "甲"),
            Sentence(2500, 2600, "丙"),
        ]);

        var sentences = db.Service.GetSentences(captionId);
        Assert.Equal([1000L, 2000L, 2500L], sentences.Select(s => s.StartMs).ToList());
        Assert.Equal([1500L, 3000L, 2600L], sentences.Select(s => s.EndMs).ToList());
        Assert.Equal("甲", sentences[0].Text);
        Assert.Equal("raw:甲", sentences[0].RawText);
        Assert.Equal("glm-test", sentences[0].Model);
    }

    [Fact]
    public void SaveSentences_SameStartMsTwice_OverwritesSingleRow()
    {
        using var db = new TempDatabase();
        var captionId = db.Service.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: true, "IOS", StoredCues)!.Value;

        db.Service.SaveSentences(captionId, [Sentence(1000, 1500, "舊")]);
        db.Service.SaveSentences(captionId, [Sentence(1000, 1800, "新")]);

        var sentence = Assert.Single(db.Service.GetSentences(captionId));
        Assert.Equal("新", sentence.Text);
        Assert.Equal(1800, sentence.EndMs);
    }

    [Fact]
    public void SaveSentences_DifferentCaptions_KeptSeparately()
    {
        using var db = new TempDatabase();
        var first = db.Service.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: true, "IOS", StoredCues)!.Value;
        var second = db.Service.SaveCaption("abcdefghijk", "ja", isAuto: true, "IOS", StoredCues)!.Value;

        db.Service.SaveSentences(first, [Sentence(1000, 1500, "第一支")]);
        db.Service.SaveSentences(second, [Sentence(2000, 2500, "第二支")]);

        Assert.Equal("第一支", Assert.Single(db.Service.GetSentences(first)).Text);
        Assert.Equal("第二支", Assert.Single(db.Service.GetSentences(second)).Text);
    }

    [Fact]
    public void SaveSentences_NullModel_PersistsAndReadsBack()
    {
        using var db = new TempDatabase();
        var captionId = db.Service.SaveCaption("dQw4w9WgXcQ", "ja", isAuto: true, "IOS", StoredCues)!.Value;

        db.Service.SaveSentences(captionId, [Sentence(1000, 1500, "無模型", model: null)]);

        Assert.Null(Assert.Single(db.Service.GetSentences(captionId)).Model);
    }

    [Fact]
    public void CreateStream_WithVodAndVideoTimes_PersistsNewColumns()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        var started = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var ended = started.AddHours(3);
        var vodId = db.Service.CreateStream(new StreamRecord("YouTube", "https://youtube.com/watch?v=1", "頻道", "標題", null, null,
            IsVod: true, VideoStartedAt: started, VideoEndedAt: ended));
        var liveId = db.Service.CreateStream(new StreamRecord("Twitch", "https://twitch.tv/x", null, null, null, null));

        Assert.NotNull(vodId);
        Assert.NotNull(liveId);
        var rows = QueryStreams(path);
        Assert.Equal(2, rows.Count);
        // 時間一律存 UTC（yyyy-MM-dd HH:mm:ss）
        Assert.Equal(1, rows[0].IsVod);
        Assert.Equal("2026-01-02 03:04:05", rows[0].VideoStartedAt);
        Assert.Equal("2026-01-02 06:04:05", rows[0].VideoEndedAt);
        Assert.Equal(0, rows[1].IsVod);
        Assert.Null(rows[1].VideoStartedAt);
        Assert.Null(rows[1].VideoEndedAt);
    }

    [Fact]
    public void Constructor_OldStreamsWithoutVodColumns_AddsColumnsAndKeepsRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tg-test-{Guid.NewGuid():N}.db");
        try
        {
            // 先手動建沒有錄影與影片時間欄位的 Streams（模擬舊資料庫）並插入一筆直播
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                conn.Execute(@"
                    CREATE TABLE Streams (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Platform TEXT NOT NULL,
                        Url TEXT NOT NULL,
                        ChannelName TEXT,
                        Title TEXT,
                        Category TEXT,
                        CharacterCardId INTEGER,
                        CreatedAt DATETIME NOT NULL DEFAULT (datetime('now'))
                    )");
                conn.Execute("INSERT INTO Streams (Platform, Url) VALUES ('YouTube', 'https://example.com/live')");
            }

            var store = new SqliteTranslationStore(NullLogger<SqliteTranslationStore>.Instance, path);

            // 三個欄位自動補上
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                var columns = conn.Query<string>("SELECT name FROM pragma_table_info('Streams')").ToList();
                Assert.Contains("IsVod", columns);
                Assert.Contains("VideoStartedAt", columns);
                Assert.Contains("VideoEndedAt", columns);
            }

            // 舊資料保留（補上的欄位用預設值），新的寫入帶欄位也讀得回來
            var id = store.CreateStream(new StreamRecord("YouTube", "https://youtube.com/watch?v=2", null, null, null, null,
                IsVod: true, VideoStartedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
            Assert.NotNull(id);
            var rows = QueryStreams(path);
            Assert.Equal(2, rows.Count);
            Assert.Equal(0, rows[0].IsVod);
            Assert.Null(rows[0].VideoStartedAt);
            Assert.Equal(1, rows[1].IsVod);
            Assert.Equal("2026-01-02 03:04:05", rows[1].VideoStartedAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ===== GetTopicSubtitleSources（遊戲用語探勘的跨主題普遍度素材） =====

    /// <summary>建兩張遊戲卡（10、11）與一張角色卡（12），並把卡掛到指定直播（StreamCards）。</summary>
    private static void SeedCardsForTopics(string path, params (long? StreamId, long CardId)[] streamCards)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Execute(@"
            INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES
                (10, 'Game', 'LoL', 'Approved'),
                (11, 'Game', 'VALORANT', 'Approved'),
                (12, 'Character', '英リサ', 'Approved')");
        foreach (var (streamId, cardId) in streamCards)
        {
            conn.Execute("INSERT INTO StreamCards (StreamId, CardId, Reason) VALUES (@streamId, @cardId, 'Manual')",
                new { streamId, cardId });
        }
    }

    [Fact]
    public void GetTopicSubtitleSources_ExcludesDeletedRowsAndExcludedStream()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        var s1 = db.Service.CreateStream(new StreamRecord("YouTube", "https://a", null, null, null, null));
        var s2 = db.Service.CreateStream(new StreamRecord("YouTube", "https://b", null, null, null, null));
        SeedCardsForTopics(path, (s1, 10), (s2, 11));
        db.Service.InsertSubtitle(Entry("j1", streamId: s1, source: "ガリオ"));
        db.Service.InsertSubtitle(Entry("j2", streamId: s1, source: "刪除的"));
        db.Service.InsertSubtitle(Entry("j3", streamId: s2, source: "トップ"));
        db.Service.InsertSubtitle(Entry("j4"));
        db.Service.MarkSubtitleDeleted("j2");

        var rows = db.Service.GetTopicSubtitleSources(excludeStreamId: s2, maxStreams: 10);

        // 排除 s2（excludeStreamId）、IsDeleted = 1 的列與 StreamId 是 null 的列
        var row = Assert.Single(rows);
        Assert.Equal(s1, row.StreamId);
        Assert.Equal(10, row.GameCardId);
        Assert.Equal("ガリオ", row.Source);
    }

    [Fact]
    public void GetTopicSubtitleSources_GameCardIdIsSmallestGameCard_CharacterOnlyStreamIsNull()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        var s1 = db.Service.CreateStream(new StreamRecord("YouTube", "https://a", null, null, null, null));
        var s2 = db.Service.CreateStream(new StreamRecord("YouTube", "https://b", null, null, null, null));
        // s1 掛兩張遊戲卡（11、10）加一張角色卡 → 取最小的遊戲卡 10；s2 只掛角色卡 → null
        SeedCardsForTopics(path, (s1, 11), (s1, 12), (s1, 10), (s2, 12));
        db.Service.InsertSubtitle(Entry("j1", streamId: s1, source: "ガリオ"));
        db.Service.InsertSubtitle(Entry("j2", streamId: s2, source: "トップ"));

        var rows = db.Service.GetTopicSubtitleSources(excludeStreamId: null, maxStreams: 10);

        Assert.Equal(10, Assert.Single(rows, r => r.StreamId == s1).GameCardId);
        Assert.Null(Assert.Single(rows, r => r.StreamId == s2).GameCardId);
    }

    [Fact]
    public void GetTopicSubtitleSources_MaxStreams_KeepsNewestStreamsOnly()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);
        var s1 = db.Service.CreateStream(new StreamRecord("YouTube", "https://a", null, null, null, null));
        var s2 = db.Service.CreateStream(new StreamRecord("YouTube", "https://b", null, null, null, null));
        var s3 = db.Service.CreateStream(new StreamRecord("YouTube", "https://c", null, null, null, null));
        SeedCardsForTopics(path, (s1, 10), (s2, 10), (s3, 10));
        db.Service.InsertSubtitle(Entry("j1", streamId: s1, source: "一句"));
        db.Service.InsertSubtitle(Entry("j2", streamId: s2, source: "二句"));
        db.Service.InsertSubtitle(Entry("j3", streamId: s3, source: "三句"));

        // 只取最新的 2 場（s3、s2，依 StreamId 由大到小），最舊的 s1 不在內
        var sources = db.Service.GetTopicSubtitleSources(excludeStreamId: null, maxStreams: 2)
            .Select(r => r.Source).ToList();

        Assert.Equal(["三句", "二句"], sources);
    }

    // ===== IAuditStore（回溯紀錄）=====

    /// <summary>直接查 LlmCalls 的列，供斷言欄位值。</summary>
    private sealed class LlmCallRow
    {
        public long Id { get; set; }
        public long? StreamId { get; set; }
        public string? JobId { get; set; }
        public string Purpose { get; set; } = string.Empty;
        public long? TemplateId { get; set; }
        public long? SystemSnapshotId { get; set; }
        public long? SettingsSnapshotId { get; set; }
        public string? Model { get; set; }
        public string? UserPrompt { get; set; }
        public string? History { get; set; }
        public string? ResponseRaw { get; set; }
        public string? KeywordTermIds { get; set; }
        public string? FixIds { get; set; }
        public long? PromptTokens { get; set; }
        public long? CompletionTokens { get; set; }
        public long? LatencyMs { get; set; }
        public string Outcome { get; set; } = string.Empty;
        public string? CreatedAt { get; set; }
    }

    private static List<LlmCallRow> QueryLlmCalls(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.Query<LlmCallRow>("SELECT * FROM LlmCalls ORDER BY Id").ToList();
    }

    /// <summary>快照表的列（兩種快照表欄位相同）。</summary>
    private sealed class SnapshotRow
    {
        public long Id { get; set; }
        public long? TemplateId { get; set; }
        public string Content { get; set; } = string.Empty;
        public string? CreatedAt { get; set; }
        public string? LastUsedAt { get; set; }
    }

    private static List<SnapshotRow> QuerySnapshots(string path, string table)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        return conn.Query<SnapshotRow>($"SELECT * FROM {table} ORDER BY Id").ToList();
    }

    // 回溯紀錄的測試合併成單一 [Fact]：先手動建「沒有 CacheId 欄位」的舊資料庫，store 初始化會補欄位並建立全部資料表，
    // 之後的斷言都用同一個 store——每多一個 [Fact] 就多一次暫存資料庫與完整 schema，會拉高整套測試的並行負載，讓 CaptionPreprocessorTests 等計時測試變得不穩。
    [Fact]
    public void Audit_Stores_Purge_AndCacheIdColumnOnLegacyDatabase()
    {
        using var legacy = LegacySubtitleDatabase.Create();
        var path = legacy.Path;
        var store = new SqliteTranslationStore(NullLogger<SqliteTranslationStore>.Instance, path);

        // 舊資料保留（補上的 CacheId 是 null）；InsertSubtitle 帶 CacheId 時寫入、沒帶時是 null
        Assert.Null(Assert.Single(QuerySubtitles(path)).CacheId);
        store.InsertSubtitle(Entry("job-c1") with { CacheId = 42 });
        store.InsertSubtitle(Entry("job-c2"));
        var subtitles = QuerySubtitles(path);
        Assert.Equal("舊job", subtitles[0].JobId);
        Assert.Equal(42, subtitles[1].CacheId);
        Assert.Null(subtitles[2].CacheId);

        // 建表：四張表與索引都在
        var tables = QueryNames(path, "table");
        Assert.Contains("PromptTemplates", tables);
        Assert.Contains("PromptSnapshots", tables);
        Assert.Contains("SettingsSnapshots", tables);
        Assert.Contains("LlmCalls", tables);
        var indexes = QueryNames(path, "index");
        Assert.Contains("IX_LlmCalls_CreatedAt", indexes);
        Assert.Contains("IX_LlmCalls_StreamId", indexes);
        Assert.Contains("IX_LlmCalls_JobId", indexes);
        Assert.Contains("IX_PromptSnapshots_LastUsedAt", indexes);
        Assert.Contains("IX_SettingsSnapshots_LastUsedAt", indexes);

        // 同 kind 同內容：同一筆，Origin 與 AppVersion 維持第一次寫入的值
        var first = store.EnsurePromptTemplate("LiveSystem", "你是翻譯員", "Default", "1.0.0");
        var second = store.EnsurePromptTemplate("LiveSystem", "你是翻譯員", "User", "2.0.0");
        Assert.NotNull(first);
        Assert.Equal(first, second);
        var template = store.GetPromptTemplate(first!.Value);
        Assert.NotNull(template);
        Assert.Equal("Default", template!.Origin);
        Assert.Equal("1.0.0", template.AppVersion);

        // 不同 kind 但內容相同：各自一筆
        var vod = store.EnsurePromptTemplate("VodBatch", "你是翻譯員", "Default", null);
        Assert.NotNull(vod);
        Assert.NotEqual(first, vod);

        // GetPromptTemplates 只列該 kind、依 Id 由大到小，欄位都對得上；不存在的 Id 回 null
        store.EnsurePromptTemplate("LiveSystem", "你是翻譯員（改）", "User", null);
        var templates = store.GetPromptTemplates("LiveSystem");
        Assert.Equal([templates[0].Id, first!.Value], templates.Select(t => t.Id).ToList());
        var newest = templates[0];
        Assert.Equal("LiveSystem", newest.Kind);
        Assert.Equal("你是翻譯員（改）", newest.Content);
        Assert.Equal(AuditHash.Of("你是翻譯員（改）"), newest.ContentHash);
        Assert.Equal("User", newest.Origin);
        Assert.True(newest.CreatedAt > new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Null(store.GetPromptTemplate(999));

        // prompt 快照：同內容同一筆（template_id 不參與去重，保留第一次寫入的值），再次 Ensure 會更新 LastUsedAt
        var firstTemplateId = first;
        var secondTemplateId = templates[0].Id;
        var snapshotId = store.EnsurePromptSnapshot(firstTemplateId, "組好的 system prompt");
        Assert.NotNull(snapshotId);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Execute("UPDATE PromptSnapshots SET LastUsedAt = '2020-01-01 00:00:00' WHERE Id = @id",
                new { id = snapshotId });
        }
        var snapshotAgain = store.EnsurePromptSnapshot(secondTemplateId, "組好的 system prompt");
        Assert.Equal(snapshotId, snapshotAgain);
        var snapshotRow = Assert.Single(QuerySnapshots(path, "PromptSnapshots"));
        Assert.Equal(firstTemplateId, snapshotRow.TemplateId);
        Assert.True(snapshotRow.LastUsedAt!.CompareTo("2020-01-01 00:00:00") > 0);

        // 設定快照：同 JSON 內容同一筆，再次 Ensure 一樣更新 LastUsedAt
        var settingsSnapshotId = store.EnsureSettingsSnapshot("""{"timeoutMs":1500}""");
        Assert.NotNull(settingsSnapshotId);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Execute("UPDATE SettingsSnapshots SET LastUsedAt = '2020-01-01 00:00:00' WHERE Id = @id",
                new { id = settingsSnapshotId });
        }
        Assert.Equal(settingsSnapshotId, store.EnsureSettingsSnapshot("""{"timeoutMs":1500}"""));
        var settingsRow = Assert.Single(QuerySnapshots(path, "SettingsSnapshots"));
        Assert.Equal("""{"timeoutMs":1500}""", settingsRow.Content);
        Assert.True(settingsRow.LastUsedAt!.CompareTo("2020-01-01 00:00:00") > 0);

        // 模型呼叫紀錄：所有欄位寫入後讀得回來（直接 SQL 讀）
        var streamId = store.CreateStream(new StreamRecord("YouTube", "https://a", null, null, null, null));
        var id = store.InsertLlmCall(new LlmCallRecord
        {
            StreamId = streamId,
            JobId = "abcd1234",
            Purpose = LlmCallPurposes.LiveTranslation,
            TemplateId = firstTemplateId,
            SystemSnapshotId = snapshotId,
            SettingsSnapshotId = settingsSnapshotId,
            Model = "qwen3.8-flash",
            UserPrompt = "こんにちは",
            History = """[{"role":"user","content":"前一句"}]""",
            ResponseRaw = "你好",
            KeywordTermIds = "[11,12]",
            FixIds = "[7]",
            PromptTokens = 120,
            CompletionTokens = 30,
            LatencyMs = 180,
            Outcome = LlmCallOutcomes.Ok,
        });

        Assert.NotNull(id);
        var row = Assert.Single(QueryLlmCalls(path));
        Assert.Equal(streamId, row.StreamId);
        Assert.Equal("abcd1234", row.JobId);
        Assert.Equal(LlmCallPurposes.LiveTranslation, row.Purpose);
        Assert.Equal(firstTemplateId, row.TemplateId);
        Assert.Equal(snapshotId, row.SystemSnapshotId);
        Assert.Equal(settingsSnapshotId, row.SettingsSnapshotId);
        Assert.Equal("qwen3.8-flash", row.Model);
        Assert.Equal("こんにちは", row.UserPrompt);
        Assert.Equal("""[{"role":"user","content":"前一句"}]""", row.History);
        Assert.Equal("你好", row.ResponseRaw);
        Assert.Equal("[11,12]", row.KeywordTermIds);
        Assert.Equal("[7]", row.FixIds);
        Assert.Equal(120, row.PromptTokens);
        Assert.Equal(30, row.CompletionTokens);
        Assert.Equal(180, row.LatencyMs);
        Assert.Equal(LlmCallOutcomes.Ok, row.Outcome);
        Assert.False(string.IsNullOrEmpty(row.CreatedAt));

        // 過期清除的種子：沒被參照的舊 prompt 快照、只被舊 llm_call 參照的舊設定快照、一筆舊 llm_call
        var unreferencedSnapshotId = store.EnsurePromptSnapshot(firstTemplateId, "沒有被參照的快照");
        var oldSettingsSnapshotId = store.EnsureSettingsSnapshot("""{"retentionDays":30}""");
        var oldCallId = store.InsertLlmCall(new LlmCallRecord
        {
            Purpose = LlmCallPurposes.VodBatch,
            SettingsSnapshotId = oldSettingsSnapshotId,
            Outcome = LlmCallOutcomes.Timeout,
        });
        Assert.NotNull(oldCallId);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Execute("UPDATE LlmCalls SET CreatedAt = '2020-01-01 00:00:00' WHERE Id = @oldCallId", new { oldCallId });
            conn.Execute("UPDATE PromptSnapshots SET LastUsedAt = '2020-01-01 00:00:00'");
            conn.Execute("UPDATE SettingsSnapshots SET LastUsedAt = '2020-01-01 00:00:00'");
        }

        // cutoff 比所有資料都舊：一列都不刪
        var nothing = store.PurgeAudit(new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new AuditPurgeResult(0, 0, 0), nothing);
        Assert.Equal(2, QueryLlmCalls(path).Count);
        Assert.Equal(2, QuerySnapshots(path, "PromptSnapshots").Count);
        Assert.Equal(2, QuerySnapshots(path, "SettingsSnapshots").Count);

        // 過期清除：舊 llm_calls 刪除、新的保留；被新 llm_call 參照的舊快照保留、沒被參照的刪除
        var result = store.PurgeAudit(DateTimeOffset.UtcNow.AddDays(-10));

        var call = Assert.Single(QueryLlmCalls(path));
        Assert.Equal("abcd1234", call.JobId);
        Assert.Equal(1, result.LlmCalls);

        var snapshots = QuerySnapshots(path, "PromptSnapshots").Select(r => r.Id).ToList();
        Assert.Contains(snapshotId!.Value, snapshots);
        Assert.DoesNotContain(unreferencedSnapshotId!.Value, snapshots);
        Assert.Equal(1, result.PromptSnapshots);

        // 只被舊 llm_call 參照的設定快照刪掉（參照它的 llm_call 已先被刪）；被新 llm_call 參照的保留
        var settingsIds = QuerySnapshots(path, "SettingsSnapshots").Select(r => r.Id).ToList();
        Assert.DoesNotContain(oldSettingsSnapshotId!.Value, settingsIds);
        Assert.Contains(settingsSnapshotId!.Value, settingsIds);
        Assert.Equal(1, result.SettingsSnapshots);

        // prompt_templates 永不刪除
        Assert.Equal(2, store.GetPromptTemplates("LiveSystem").Count);
        Assert.Single(store.GetPromptTemplates("VodBatch"));
    }

    /// <summary>手動建立「沒有 CacheId 欄位的 SubtitleLog」的舊資料庫（模擬 T-2084 之前的資料庫）並插入一筆舊資料；
    /// Dispose 時清連線池並刪檔。</summary>
    private sealed class LegacySubtitleDatabase : IDisposable
    {
        private readonly string _path;

        private LegacySubtitleDatabase(string path) => _path = path;

        public string Path => _path;

        public static LegacySubtitleDatabase Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tg-test-{Guid.NewGuid():N}.db");
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                conn.Execute(@"
                    CREATE TABLE SubtitleLog (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        StreamId INTEGER,
                        JobId TEXT NOT NULL,
                        RawSource TEXT NOT NULL,
                        Source TEXT NOT NULL,
                        Translation TEXT NOT NULL,
                        TranslationSource TEXT NOT NULL,
                        RevisionOfId INTEGER,
                        ModelLatencyMs INTEGER,
                        IsEdited INTEGER NOT NULL DEFAULT 0,
                        IsDeleted INTEGER NOT NULL DEFAULT 0,
                        CreatedAt DATETIME NOT NULL DEFAULT (datetime('now')),
                        UpdatedAt DATETIME
                    )");
                conn.Execute(
                    "INSERT INTO SubtitleLog (JobId, RawSource, Source, Translation, TranslationSource) VALUES ('舊job', 'raw', 'src', '譯', 'Model')");
            }

            return new LegacySubtitleDatabase(path);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    // ===== VideoChapters（影片章節：說明欄、留言セトリ或人工貼上的章節與譯名）=====

    [Fact]
    public void Constructor_CreatesVideoChaptersTableAndIndex()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        Assert.Contains("VideoChapters", QueryNames(path, "table"));
        Assert.Contains("IX_VideoChapters_VideoId", QueryNames(path, "index"));
    }

    [Fact]
    public void GetVideoChapters_NoChapters_ReturnsEmpty()
    {
        using var db = new TempDatabase();

        Assert.Empty(db.Service.GetVideoChapters("dQw4w9WgXcQ"));
    }

    [Fact]
    public void ReplaceVideoChapters_ThenGet_ReturnsSortedRows()
    {
        using var db = new TempDatabase();

        // 故意亂序寫入：Get 要依 StartMs 排序
        db.Service.ReplaceVideoChapters("dQw4w9WgXcQ",
        [
            new StoredVideoChapter(120000, "曲B", "譯B", 184000, VideoChapterSources.Comment),
            new StoredVideoChapter(0, "配信開始", null, null, VideoChapterSources.Comment),
            new StoredVideoChapter(60000, "曲A", "譯A", null, VideoChapterSources.Comment),
        ]);

        Assert.Equal(
        [
            new StoredVideoChapter(0, "配信開始", null, null, VideoChapterSources.Comment),
            new StoredVideoChapter(60000, "曲A", "譯A", null, VideoChapterSources.Comment),
            new StoredVideoChapter(120000, "曲B", "譯B", 184000, VideoChapterSources.Comment),
        ], db.Service.GetVideoChapters("dQw4w9WgXcQ"));
    }

    [Fact]
    public void ReplaceVideoChapters_SameVideo_ReplacesAllRows()
    {
        using var db = new TempDatabase();
        var path = PathOf(db);

        db.Service.ReplaceVideoChapters("dQw4w9WgXcQ",
        [
            new StoredVideoChapter(60000, "曲A", "譯A", null, VideoChapterSources.Description),
            new StoredVideoChapter(120000, "曲B", null, null, VideoChapterSources.Description),
        ]);
        // 同一支影片改存人工章節：舊的全部換掉（不是追加）
        db.Service.ReplaceVideoChapters("dQw4w9WgXcQ",
        [
            new StoredVideoChapter(30000, "曲M", null, null, VideoChapterSources.Manual),
        ]);

        Assert.Equal(
            [new StoredVideoChapter(30000, "曲M", null, null, VideoChapterSources.Manual)],
            db.Service.GetVideoChapters("dQw4w9WgXcQ"));
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM VideoChapters"));
        }

        // 其他影片不受影響
        db.Service.ReplaceVideoChapters("aaaaaaaaaaa",
            [new StoredVideoChapter(0, "別支影片", null, null, VideoChapterSources.Comment)]);
        Assert.Single(db.Service.GetVideoChapters("aaaaaaaaaaa"));
        Assert.Single(db.Service.GetVideoChapters("dQw4w9WgXcQ"));
    }

    [Fact]
    public void DeleteVideoChapters_RemovesOnlyThatVideo()
    {
        using var db = new TempDatabase();

        db.Service.ReplaceVideoChapters("dQw4w9WgXcQ",
        [
            new StoredVideoChapter(60000, "曲A", "譯A", 100000, VideoChapterSources.Manual),
            new StoredVideoChapter(120000, "曲B", null, null, VideoChapterSources.Manual),
        ]);
        db.Service.ReplaceVideoChapters("aaaaaaaaaaa",
            [new StoredVideoChapter(0, "別支影片", null, null, VideoChapterSources.Comment)]);

        db.Service.DeleteVideoChapters("dQw4w9WgXcQ");

        Assert.Empty(db.Service.GetVideoChapters("dQw4w9WgXcQ"));
        Assert.Single(db.Service.GetVideoChapters("aaaaaaaaaaa"));

        // 不存在的影片：不做事也不丟例外
        db.Service.DeleteVideoChapters("zzzzzzzzzzz");
        Assert.Single(db.Service.GetVideoChapters("aaaaaaaaaaa"));
    }
}
