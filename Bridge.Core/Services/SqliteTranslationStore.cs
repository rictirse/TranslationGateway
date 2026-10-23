using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

public sealed class SqliteTranslationStore : ITranslationStore, IGlossaryStore, ISubtitleLogStore, ICaptionStore, IAuditStore
{
    /// <summary>時間欄位一律存 UTC，格式與 datetime('now') 一致。</summary>
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly string _dbFilePath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteTranslationStore> _logger;

    /// <summary>給人看的後端描述（不含敏感資訊）。</summary>
    public string Description => $"SQLite：{_dbFilePath}";

    /// <param name="dbFilePath">資料庫檔案路徑；null 或空白時用 DatabaseSettings.DefaultSqlitePath（會建立資料夾）。</param>
    public SqliteTranslationStore(ILogger<SqliteTranslationStore> logger, string? dbFilePath = null)
    {
        _logger = logger;

        if (string.IsNullOrWhiteSpace(dbFilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabaseSettings.DefaultSqlitePath)!);
            _dbFilePath = DatabaseSettings.DefaultSqlitePath;
        }
        else
        {
            _dbFilePath = dbFilePath;
        }

        _connectionString = $"Data Source={_dbFilePath}";

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // 建表 SQL 集中在嵌入資源 Sql/sqlite-schema.sql（整份執行，只能 IF NOT EXISTS 新增）
            connection.Execute(SchemaScripts.Sqlite);

            // 既有資料庫補欄位：GlossaryCards.Aliases（schema 檔整份每次都會執行，ALTER 不能放進 schema 檔）
            if (connection.QuerySingle<long>(
                    "SELECT COUNT(*) FROM pragma_table_info('GlossaryCards') WHERE name = 'Aliases'") == 0)
            {
                connection.Execute("ALTER TABLE GlossaryCards ADD COLUMN Aliases TEXT");
            }

            // 既有資料庫補欄位：Streams 的錄影與影片起訖時間（同上，ALTER 不能放進 schema 檔）
            if (connection.QuerySingle<long>(
                    "SELECT COUNT(*) FROM pragma_table_info('Streams') WHERE name = 'IsVod'") == 0)
            {
                connection.Execute("ALTER TABLE Streams ADD COLUMN IsVod INTEGER NOT NULL DEFAULT 0");
            }
            if (connection.QuerySingle<long>(
                    "SELECT COUNT(*) FROM pragma_table_info('Streams') WHERE name = 'VideoStartedAt'") == 0)
            {
                connection.Execute("ALTER TABLE Streams ADD COLUMN VideoStartedAt DATETIME");
            }
            if (connection.QuerySingle<long>(
                    "SELECT COUNT(*) FROM pragma_table_info('Streams') WHERE name = 'VideoEndedAt'") == 0)
            {
                connection.Execute("ALTER TABLE Streams ADD COLUMN VideoEndedAt DATETIME");
            }

            // 既有資料庫補欄位：SubtitleLog.CacheId（命中翻譯快取時該筆 TranslationCache 的 Id；不加外鍵，快取列可能被刪除）
            if (connection.QuerySingle<long>(
                    "SELECT COUNT(*) FROM pragma_table_info('SubtitleLog') WHERE name = 'CacheId'") == 0)
            {
                connection.Execute("ALTER TABLE SubtitleLog ADD COLUMN CacheId INTEGER");
            }

            _logger.LogInformation("SQLite Database initialized at: {Path}", _connectionString);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SQLite database.");
            throw;
        }
    }

    public string? GetCache(string source)
    {
        using var connection = new SqliteConnection(_connectionString);
        return connection.QueryFirstOrDefault<string>(
            "SELECT TargetText FROM TranslationCache WHERE SourceText = @source", new { source });
    }

    /// <summary>指定原文的快取 Id；沒有時回傳 null（失敗不擲例外）。</summary>
    public long? GetCacheId(string source)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            return connection.QueryFirstOrDefault<long?>(
                "SELECT Id FROM TranslationCache WHERE SourceText = @source", new { source });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查詢快取 Id 失敗（回溯紀錄用，不影響翻譯）");
            return null;
        }
    }

    public void SaveCache(string source, string target, bool isManual = false)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Execute(@"
            INSERT INTO TranslationCache (SourceText, TargetText, IsManual, LastUsed)
            VALUES (@source, @target, @isManual, datetime('now'))
            ON CONFLICT(SourceText) DO UPDATE SET
                TargetText = excluded.TargetText,
                IsManual = excluded.IsManual,
                LastUsed = datetime('now')", new { source, target, isManual });
    }

    /// <summary>寫入快取（is_manual = false）；同原文已存在而且是手動修正時不覆蓋。</summary>
    public void SaveCacheUnlessManual(string source, string target)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Execute(@"
            INSERT INTO TranslationCache (SourceText, TargetText, IsManual, LastUsed)
            VALUES (@source, @target, 0, datetime('now'))
            ON CONFLICT(SourceText) DO UPDATE SET
                TargetText = excluded.TargetText,
                IsManual = excluded.IsManual,
                LastUsed = datetime('now')
            WHERE TranslationCache.IsManual = 0", new { source, target });
    }

    /// <summary>刪除指定原文的快取項目（預設不含手動修正的項目，<paramref name="includeManual"/> 為 true 時一併刪除）；回傳是否有刪除任何資料列。</summary>
    public bool DeleteCache(string source, bool includeManual = false)
    {
        using var connection = new SqliteConnection(_connectionString);
        var sql = includeManual
            ? "DELETE FROM TranslationCache WHERE SourceText = @source"
            : "DELETE FROM TranslationCache WHERE SourceText = @source AND IsManual = 0";
        return connection.Execute(sql, new { source }) > 0;
    }

    public IEnumerable<TranslationCacheItem> GetAllCache()
    {
        using var connection = new SqliteConnection(_connectionString);
        return connection.Query<TranslationCacheItem>(
            "SELECT SourceText, TargetText, IsManual, LastUsed FROM TranslationCache ORDER BY LastUsed DESC LIMIT 500");
    }

    public IReadOnlyList<GlossaryCard> GetGlossaryCards()
    {
        using var connection = new SqliteConnection(_connectionString);
        return connection.Query<GlossaryCard>(
            "SELECT Id, Kind, Name, ParentId, ChannelId, ChannelHandle, Aliases, Description, SourceUrl, Status " +
            "FROM GlossaryCards WHERE Status <> 'Rejected' ORDER BY Id").ToList();
    }

    public IReadOnlyList<GlossaryTerm> GetGlossaryTerms()
    {
        using var connection = new SqliteConnection(_connectionString);
        return connection.Query<GlossaryTerm>(
            "SELECT Id, CardId, Source, Target, Aliases, Category, Note, Status " +
            "FROM GlossaryTerms WHERE Status <> 'Rejected' ORDER BY CardId, Id").ToList();
    }

    /// <summary>審核頁用：所有詞彙卡（含 Rejected）。</summary>
    public IReadOnlyList<GlossaryCard> GetAllGlossaryCards()
    {
        using var connection = new SqliteConnection(_connectionString);
        return connection.Query<GlossaryCard>(
            "SELECT Id, Kind, Name, ParentId, ChannelId, ChannelHandle, Aliases, Description, SourceUrl, Status " +
            "FROM GlossaryCards ORDER BY Id").ToList();
    }

    /// <summary>審核頁用：所有名詞（含 Rejected）。</summary>
    public IReadOnlyList<GlossaryTerm> GetAllGlossaryTerms()
    {
        using var connection = new SqliteConnection(_connectionString);
        return connection.Query<GlossaryTerm>(
            "SELECT Id, CardId, Source, Target, Aliases, Category, Note, Status " +
            "FROM GlossaryTerms ORDER BY CardId, Id").ToList();
    }

    /// <summary>同一個交易中更新卡片與名詞（依 Id）並把 UpdatedAt 設為現在（UTC）；任一筆失敗就整批回復。</summary>
    public void SaveGlossaryChanges(IReadOnlyList<GlossaryCard> cards, IReadOnlyList<GlossaryTerm> terms)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var updatedAt = FormatUtc(DateTime.UtcNow);
            connection.Execute(
                "UPDATE GlossaryCards SET " +
                "Name = @Name, ParentId = @ParentId, ChannelHandle = @ChannelHandle, " +
                "Aliases = @Aliases, Description = @Description, SourceUrl = @SourceUrl, Status = @Status, UpdatedAt = @updatedAt " +
                "WHERE Id = @Id",
                cards.Select(card => new
                {
                    card.Name,
                    card.ParentId,
                    card.ChannelHandle,
                    Aliases = GlossaryCatalog.NormalizeCardAliases(card.Aliases, card.Name),
                    card.Description,
                    card.SourceUrl,
                    card.Status,
                    card.Id,
                    updatedAt
                }),
                transaction);
            connection.Execute(
                "UPDATE GlossaryTerms SET " +
                "Source = @Source, Target = @Target, Aliases = @Aliases, " +
                "Category = @Category, Note = @Note, Status = @Status, UpdatedAt = @updatedAt " +
                "WHERE Id = @Id",
                terms.Select(term => new
                {
                    term.Source,
                    term.Target,
                    term.Aliases,
                    term.Category,
                    term.Note,
                    term.Status,
                    term.Id,
                    updatedAt
                }),
                transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>同一個交易中更新既有名詞（依 Id，UpdatedAt = 現在）並新增名詞（CreatedAt 用預設值）；任一筆失敗就整批回復。</summary>
    public void SaveGlossaryTermEdits(IReadOnlyList<GlossaryTerm> updated, IReadOnlyList<GlossaryTerm> inserted)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var updatedAt = FormatUtc(DateTime.UtcNow);
            connection.Execute(
                "UPDATE GlossaryTerms SET " +
                "Source = @Source, Target = @Target, Aliases = @Aliases, " +
                "Category = @Category, Note = @Note, Status = @Status, UpdatedAt = @updatedAt " +
                "WHERE Id = @Id",
                updated.Select(term => new
                {
                    term.Source,
                    term.Target,
                    term.Aliases,
                    term.Category,
                    term.Note,
                    term.Status,
                    term.Id,
                    updatedAt
                }),
                transaction);
            connection.Execute(
                "INSERT INTO GlossaryTerms (CardId, Source, Target, Aliases, Category, Note, Status) " +
                "VALUES (@CardId, @Source, @Target, @Aliases, @Category, @Note, @Status)",
                inserted.Select(term => new
                {
                    term.CardId,
                    term.Source,
                    term.Target,
                    term.Aliases,
                    term.Category,
                    term.Note,
                    term.Status
                }),
                transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>同一個交易中新增一張卡與它的名詞，回傳新卡 Id；任一筆失敗就整批回復並擲出例外。</summary>
    public long CreateGlossaryCard(GlossaryCard card, IReadOnlyList<GlossaryTerm> terms)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var cardId = connection.QuerySingle<long>(
                "INSERT INTO GlossaryCards (Kind, Name, ParentId, ChannelId, ChannelHandle, Aliases, Description, SourceUrl, Status, CreatedAt) " +
                "VALUES (@Kind, @Name, @ParentId, @ChannelId, @ChannelHandle, @Aliases, @Description, @SourceUrl, @Status, @createdAt); " +
                "SELECT last_insert_rowid();",
                new
                {
                    card.Kind,
                    card.Name,
                    card.ParentId,
                    card.ChannelId,
                    card.ChannelHandle,
                    Aliases = GlossaryCatalog.NormalizeCardAliases(card.Aliases, card.Name),
                    card.Description,
                    card.SourceUrl,
                    card.Status,
                    createdAt = FormatUtc(DateTime.UtcNow)
                },
                transaction);
            connection.Execute(
                "INSERT INTO GlossaryTerms (CardId, Source, Target, Aliases, Category, Note, Status) " +
                "VALUES (@CardId, @Source, @Target, @Aliases, @Category, @Note, @Status)",
                terms.Select(term => new
                {
                    CardId = cardId,
                    term.Source,
                    term.Target,
                    term.Aliases,
                    term.Category,
                    term.Note,
                    term.Status
                }),
                transaction);
            transaction.Commit();
            card.Id = cardId;
            return cardId;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>同一個交易中把 sourceId 卡併進 targetId 卡並刪掉 sourceId 卡（規則見 GlossaryMerger）；
    /// 任一步失敗就整批回復並擲出例外。兩個 Id 相同或任一張卡不存在時擲 ArgumentException。</summary>
    public GlossaryMergeResult MergeGlossaryCards(long sourceId, long targetId)
    {
        if (sourceId == targetId)
        {
            throw new ArgumentException($"合併的兩張詞彙卡不能是同一張：{sourceId}。");
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var source = connection.QuerySingleOrDefault<GlossaryCard>(
                "SELECT Id, Kind, Name, ParentId, ChannelId, ChannelHandle, Aliases, Description, SourceUrl, Status " +
                "FROM GlossaryCards WHERE Id = @sourceId", new { sourceId }, transaction);
            var target = connection.QuerySingleOrDefault<GlossaryCard>(
                "SELECT Id, Kind, Name, ParentId, ChannelId, ChannelHandle, Aliases, Description, SourceUrl, Status " +
                "FROM GlossaryCards WHERE Id = @targetId", new { targetId }, transaction);
            if (source is null || target is null)
            {
                throw new ArgumentException($"找不到要合併的詞彙卡：source {sourceId}、target {targetId}。");
            }

            var sourceTerms = connection.Query<GlossaryTerm>(
                "SELECT Id, CardId, Source, Target, Aliases, Category, Note, Status " +
                "FROM GlossaryTerms WHERE CardId = @sourceId ORDER BY Id", new { sourceId }, transaction).ToList();
            var targetTerms = connection.Query<GlossaryTerm>(
                "SELECT Id, CardId, Source, Target, Aliases, Category, Note, Status " +
                "FROM GlossaryTerms WHERE CardId = @targetId ORDER BY Id", new { targetId }, transaction).ToList();
            var plan = GlossaryMerger.Plan(source, sourceTerms, target, targetTerms);
            var updatedAt = FormatUtc(DateTime.UtcNow);

            // 丟掉的名詞的聽錯字修正：先刪掉與既有名詞重複（同 Kind、Wrong）的，剩下的改掛到既有名詞
            foreach (var (droppedTermId, keptTermId) in plan.DroppedTerms)
            {
                connection.Execute(
                    "DELETE FROM MishearingFixes WHERE TermId = @droppedTermId AND EXISTS (" +
                    "SELECT 1 FROM MishearingFixes kept WHERE kept.TermId = @keptTermId " +
                    "AND kept.Kind = MishearingFixes.Kind AND kept.Wrong = MishearingFixes.Wrong)",
                    new { droppedTermId, keptTermId }, transaction);
                connection.Execute(
                    "UPDATE MishearingFixes SET TermId = @keptTermId, CardId = @targetId WHERE TermId = @droppedTermId",
                    new { keptTermId, targetId, droppedTermId }, transaction);
            }

            // 刪掉丟掉的名詞（既有名詞不動）
            if (plan.DroppedTerms.Count > 0)
            {
                connection.Execute(
                    "DELETE FROM GlossaryTerms WHERE Id IN @ids",
                    new { ids = plan.DroppedTerms.Select(d => d.DroppedTermId).ToList() }, transaction);
            }

            // 搬名詞：改掛到 target 卡、Status 一律改 Pending
            if (plan.TermsToMove.Count > 0)
            {
                connection.Execute(
                    "UPDATE GlossaryTerms SET CardId = @targetId, Status = 'Pending', UpdatedAt = @updatedAt WHERE Id IN @ids",
                    new { targetId, updatedAt, ids = plan.TermsToMove.Select(t => t.Id).ToList() }, transaction);
            }

            // 剩下的聽錯字修正（搬過去的名詞的）跟著換卡
            connection.Execute(
                "UPDATE MishearingFixes SET CardId = @targetId WHERE CardId = @sourceId",
                new { targetId, sourceId }, transaction);

            // 更新 target 卡：別名與補進來的欄位（Kind、Name、Status 維持 target 的）
            connection.Execute(
                "UPDATE GlossaryCards SET Aliases = @aliases, ParentId = @parentId, ChannelId = @channelId, " +
                "ChannelHandle = @channelHandle, Description = @description, SourceUrl = @sourceUrl, UpdatedAt = @updatedAt " +
                "WHERE Id = @targetId",
                new
                {
                    aliases = plan.NewTargetAliases,
                    parentId = plan.NewTargetParentId,
                    channelId = plan.NewTargetChannelId,
                    channelHandle = plan.NewTargetChannelHandle,
                    description = plan.NewTargetDescription,
                    sourceUrl = plan.NewTargetSourceUrl,
                    updatedAt,
                    targetId
                },
                transaction);

            // 改指向：開台生效紀錄、直播的主播卡、source 的子卡都改指 target；target 的上層原本是 source 時清空
            connection.Execute(
                "UPDATE StreamCards SET CardId = @targetId WHERE CardId = @sourceId", new { targetId, sourceId }, transaction);
            connection.Execute(
                "UPDATE Streams SET CharacterCardId = @targetId WHERE CharacterCardId = @sourceId", new { targetId, sourceId }, transaction);
            connection.Execute(
                "UPDATE GlossaryCards SET ParentId = @targetId WHERE ParentId = @sourceId AND Id <> @targetId",
                new { targetId, sourceId }, transaction);
            connection.Execute(
                "UPDATE GlossaryCards SET ParentId = NULL WHERE Id = @targetId AND ParentId = @sourceId",
                new { targetId, sourceId }, transaction);

            // 刪掉 source 卡（這時它已經沒有名詞）
            connection.Execute("DELETE FROM GlossaryCards WHERE Id = @sourceId", new { sourceId }, transaction);

            transaction.Commit();
            return new GlossaryMergeResult(plan.TermsToMove.Count, plan.DroppedTerms.Count, plan.AddedAliases);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>同一個交易中刪除一張卡與它的名詞、聽錯字修正、開台生效紀錄，子卡的上層與直播的主播卡改成空；
    /// 失敗擲例外。卡不存在時不動作。</summary>
    public void DeleteGlossaryCard(long cardId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            if (connection.QuerySingle<long>(
                    "SELECT COUNT(*) FROM GlossaryCards WHERE Id = @cardId", new { cardId }, transaction) == 0)
            {
                return;
            }

            connection.Execute("DELETE FROM MishearingFixes WHERE CardId = @cardId", new { cardId }, transaction);
            connection.Execute("DELETE FROM GlossaryTerms WHERE CardId = @cardId", new { cardId }, transaction);
            connection.Execute("DELETE FROM StreamCards WHERE CardId = @cardId", new { cardId }, transaction);
            connection.Execute(
                "UPDATE Streams SET CharacterCardId = NULL WHERE CharacterCardId = @cardId", new { cardId }, transaction);
            connection.Execute(
                "UPDATE GlossaryCards SET ParentId = NULL WHERE ParentId = @cardId", new { cardId }, transaction);
            connection.Execute("DELETE FROM GlossaryCards WHERE Id = @cardId", new { cardId }, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>同一個交易中刪除指定的名詞與它們的聽錯字修正（先刪聽錯字修正，再刪名詞）；失敗擲例外。
    /// 不依賴外鍵 CASCADE；不存在的 Id 略過；空清單不動作。只刪名詞，不更新卡片與別名。</summary>
    public void DeleteGlossaryTerms(IReadOnlyList<long> termIds)
    {
        if (termIds.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            connection.Execute("DELETE FROM MishearingFixes WHERE TermId IN @termIds", new { termIds }, transaction);
            connection.Execute("DELETE FROM GlossaryTerms WHERE Id IN @termIds", new { termIds }, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>新增或累加名詞候選：(Surface, Kind) 已存在時 Occurrences + 1、更新 LastSeenAt、StreamId、Context、Importance 取較大值。</summary>
    public void UpsertTermCandidates(IReadOnlyList<TermCandidate> candidates)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Execute(@"
            INSERT INTO TermCandidates (Surface, Kind, Importance, Origin, StreamId, Context, Occurrences, CreatedAt, LastSeenAt)
            VALUES (@Surface, @Kind, @Importance, @Origin, @StreamId, @Context, 1, datetime('now'), datetime('now'))
            ON CONFLICT(Surface, Kind) DO UPDATE SET
                Occurrences = Occurrences + 1,
                Importance = MAX(Importance, excluded.Importance),
                StreamId = excluded.StreamId,
                Context = excluded.Context,
                LastSeenAt = datetime('now')",
            candidates);
    }

    // ===== MishearingFixes =====

    /// <summary>全部聽錯字修正（Pending 與 Approved），依 CardId、TermId、Wrong 排序。</summary>
    public IReadOnlyList<MishearingFix> GetMishearingFixes()
    {
        using var connection = new SqliteConnection(_connectionString);
        return connection.Query<MishearingFix>(
            "SELECT Id, CardId, TermId, Kind, Wrong, Right, Status, Evidence, Occurrences, CreatedAt, UpdatedAt " +
            "FROM MishearingFixes ORDER BY CardId, TermId, Wrong").ToList();
    }

    /// <summary>新增或累加聽錯字修正：(TermId, Kind, Wrong) 已存在時 Occurrences + 1、Evidence 換新、UpdatedAt = 現在（Status 不變）；
    /// 不存在時新增為 Pending；Wrong 太短、Wrong == Right、Evidence 空白的略過。整批一個交易，失敗回復。</summary>
    public int UpsertMishearingFixes(IReadOnlyList<MishearingProposal> proposals)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var inserted = 0;
            foreach (var proposal in proposals)
            {
                var wrong = proposal.Wrong?.Trim() ?? string.Empty;
                var right = proposal.Right?.Trim();
                var evidence = proposal.Evidence?.Trim() ?? string.Empty;
                var kind = string.IsNullOrWhiteSpace(proposal.Kind) ? MishearingKind.Pre : proposal.Kind;
                if (wrong.Length < 2 || string.Equals(wrong, right, StringComparison.Ordinal) || evidence.Length == 0)
                {
                    continue;
                }

                var existingId = connection.QueryFirstOrDefault<long?>(
                    "SELECT Id FROM MishearingFixes WHERE TermId = @termId AND Kind = @kind AND Wrong = @wrong",
                    new { termId = proposal.TermId, kind, wrong },
                    transaction);
                if (existingId is { } id)
                {
                    connection.Execute(
                        "UPDATE MishearingFixes SET Occurrences = Occurrences + 1, Evidence = @evidence, UpdatedAt = @updatedAt WHERE Id = @id",
                        new { evidence, updatedAt = FormatUtc(DateTime.UtcNow), id },
                        transaction);
                }
                else
                {
                    connection.Execute(
                        "INSERT INTO MishearingFixes (CardId, TermId, Kind, Wrong, Right, Status, Evidence, Occurrences, CreatedAt) " +
                        "VALUES (@cardId, @termId, @kind, @wrong, @right, 'Pending', @evidence, 1, @createdAt)",
                        new
                        {
                            cardId = proposal.CardId,
                            termId = proposal.TermId,
                            kind,
                            wrong,
                            right,
                            evidence,
                            createdAt = FormatUtc(DateTime.UtcNow)
                        },
                        transaction);
                    inserted++;
                }
            }

            transaction.Commit();
            return inserted;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>把指定的 Id 改成 Approved、UpdatedAt = 現在。</summary>
    public void ApproveMishearingFixes(IReadOnlyList<long> ids)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Execute(
            "UPDATE MishearingFixes SET Status = 'Approved', UpdatedAt = @updatedAt WHERE Id IN @ids",
            new { updatedAt = FormatUtc(DateTime.UtcNow), ids });
    }

    /// <summary>刪除指定的 Id（駁回）。</summary>
    public void DeleteMishearingFixes(IReadOnlyList<long> ids)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Execute("DELETE FROM MishearingFixes WHERE Id IN @ids", new { ids });
    }

    // ===== ISubtitleLogStore =====

    /// <summary>新增直播，回傳 Id。</summary>
    public long? CreateStream(StreamRecord stream)
    {
        using var connection = new SqliteConnection(_connectionString);
        return connection.QuerySingle<long>(
            "INSERT INTO Streams (Platform, Url, ChannelName, Title, Category, CharacterCardId, IsVod, VideoStartedAt, VideoEndedAt, CreatedAt) " +
            "VALUES (@Platform, @Url, @ChannelName, @Title, @Category, @CharacterCardId, @IsVod, @VideoStartedAt, @VideoEndedAt, @CreatedAt); " +
            "SELECT last_insert_rowid();",
            new
            {
                stream.Platform,
                stream.Url,
                stream.ChannelName,
                stream.Title,
                stream.Category,
                stream.CharacterCardId,
                stream.IsVod,
                VideoStartedAt = stream.VideoStartedAt is { } start ? FormatUtc(start) : null,
                VideoEndedAt = stream.VideoEndedAt is { } end ? FormatUtc(end) : null,
                CreatedAt = FormatUtc(DateTime.UtcNow)
            });
    }

    /// <summary>新增一句字幕；RevisionOfId 以子查詢指向該 JobId 最新一列（找不到為 NULL），CacheId 照 entry 帶入的值寫入。</summary>
    public void InsertSubtitle(SubtitleLogEntry entry)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Execute(
            "INSERT INTO SubtitleLog (StreamId, JobId, RawSource, Source, Translation, TranslationSource, RevisionOfId, ModelLatencyMs, CacheId, CreatedAt) " +
            "VALUES (@StreamId, @JobId, @RawSource, @Source, @Translation, @TranslationSource, " +
            "(SELECT Id FROM SubtitleLog WHERE JobId = @RevisionOfJobId ORDER BY Id DESC LIMIT 1), " +
            "@ModelLatencyMs, @CacheId, @CreatedAt)",
            new
            {
                entry.StreamId,
                entry.JobId,
                entry.RawSource,
                entry.Source,
                entry.Translation,
                entry.TranslationSource,
                entry.RevisionOfJobId,
                entry.ModelLatencyMs,
                entry.CacheId,
                CreatedAt = FormatUtc(entry.CreatedAt)
            });
    }

    /// <summary>更新該 JobId 最新一列：source 不為 null 才改 Source（COALESCE），Translation 一律覆寫。</summary>
    public void MarkSubtitleEdited(string jobId, string? source, string translation)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Execute(
            "UPDATE SubtitleLog SET " +
            "Source = COALESCE(@source, Source), Translation = @translation, IsEdited = 1, UpdatedAt = @updatedAt " +
            "WHERE Id = (SELECT Id FROM SubtitleLog WHERE JobId = @jobId ORDER BY Id DESC LIMIT 1)",
            new { jobId, source, translation, updatedAt = FormatUtc(DateTime.UtcNow) });
    }

    /// <summary>該 JobId 最新一列 IsDeleted = 1（資料保留）。</summary>
    public void MarkSubtitleDeleted(string jobId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Execute(
            "UPDATE SubtitleLog SET IsDeleted = 1, UpdatedAt = @updatedAt " +
            "WHERE Id = (SELECT Id FROM SubtitleLog WHERE JobId = @jobId ORDER BY Id DESC LIMIT 1)",
            new { jobId, updatedAt = FormatUtc(DateTime.UtcNow) });
    }

    /// <summary>新增一筆直播生效的詞彙卡（ActivatedAt = 現在，UTC）。</summary>
    public void AddStreamCard(long streamId, long cardId, string reason)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Execute(
            "INSERT INTO StreamCards (StreamId, CardId, Reason, ActivatedAt) " +
            "VALUES (@streamId, @cardId, @reason, @activatedAt)",
            new { streamId, cardId, reason, activatedAt = FormatUtc(DateTime.UtcNow) });
    }

    /// <summary>該直播、該卡最新一筆 DeactivatedAt 為空的紀錄，設 DeactivatedAt = 現在；找不到時不做事。</summary>
    public void EndStreamCard(long streamId, long cardId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Execute(
            "UPDATE StreamCards SET DeactivatedAt = @deactivatedAt " +
            "WHERE Id = (SELECT Id FROM StreamCards WHERE StreamId = @streamId AND CardId = @cardId " +
            "AND DeactivatedAt IS NULL ORDER BY Id DESC LIMIT 1)",
            new { streamId, cardId, deactivatedAt = FormatUtc(DateTime.UtcNow) });
    }

    /// <summary>GetTopicSubtitleSources 查詢結果的一列：GameCardId 可能是 NULL，
    /// 直接管線到 record 會被 NULL 列的欄位型別（byte[]）絆倒，先對到類別再轉。</summary>
    private sealed class TopicSourceRow
    {
        public long StreamId { get; set; }

        public long? GameCardId { get; set; }

        public string Source { get; set; } = string.Empty;
    }

    /// <summary>最近 maxStreams 場直播（依 Id 由大到小，排除 excludeStreamId）的字幕原文（IsDeleted = 0、StreamId 非 null）；
    /// GameCardId = 該直播 StreamCards 中卡別是 Game 的最小 CardId（沒有為 null）。失敗擲例外。</summary>
    public IReadOnlyList<TopicSubtitleSource> GetTopicSubtitleSources(long? excludeStreamId, int maxStreams)
    {
        using var connection = new SqliteConnection(_connectionString);
        return connection.Query<TopicSourceRow>(@"
            SELECT sl.StreamId AS StreamId, sl.Source AS Source,
                (SELECT MIN(sc.CardId)
                 FROM StreamCards sc
                 JOIN GlossaryCards gc ON gc.Id = sc.CardId
                 WHERE sc.StreamId = sl.StreamId AND gc.Kind = 'Game') AS GameCardId
            FROM SubtitleLog sl
            WHERE sl.IsDeleted = 0 AND sl.StreamId IS NOT NULL
              AND sl.StreamId IN (
                  SELECT Id FROM Streams
                  WHERE (@excludeStreamId IS NULL OR Id <> @excludeStreamId)
                  ORDER BY Id DESC LIMIT @maxStreams)
            ORDER BY sl.StreamId DESC, sl.Id",
            new { excludeStreamId, maxStreams })
            .Select(row => new TopicSubtitleSource(row.StreamId, row.GameCardId, row.Source))
            .ToList();
    }

    // ===== ICaptionStore =====

    /// <summary>YoutubeCaptions 一列的原始欄位（時間為字串，再依 UTC 解析）。</summary>
    private sealed class CaptionRow
    {
        public long Id { get; set; }
        public string VideoId { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public long IsAuto { get; set; }
        public string? ClientName { get; set; }
        public string CuesJson { get; set; } = string.Empty;
        public string? CreatedAt { get; set; }
        public string? LastUsedAt { get; set; }
    }

    /// <summary>依影片與語言讀回快取的字幕；找不到時回傳 null（CuesJson 格式壞掉時擲例外，由呼叫端處理）。</summary>
    public StoredCaption? GetCaption(string videoId, string language)
    {
        using var connection = new SqliteConnection(_connectionString);
        var row = connection.QueryFirstOrDefault<CaptionRow>(
            "SELECT Id, VideoId, Language, IsAuto, ClientName, CuesJson, CreatedAt, LastUsedAt " +
            "FROM YoutubeCaptions WHERE VideoId = @videoId AND Language = @language",
            new { videoId, language });
        if (row is null)
        {
            return null;
        }

        return new StoredCaption(
            row.Id, row.VideoId, row.Language, row.IsAuto != 0, row.ClientName,
            CaptionCueJson.Deserialize(row.CuesJson), ParseUtc(row.CreatedAt), ParseUtc(row.LastUsedAt));
    }

    /// <summary>新增或覆寫字幕快取（同 VideoId + Language）；CreatedAt、LastUsedAt 都設為現在。回傳 Id。</summary>
    public long? SaveCaption(string videoId, string language, bool isAuto, string? clientName, IReadOnlyList<CaptionCue> cues)
    {
        using var connection = new SqliteConnection(_connectionString);
        return connection.QuerySingle<long>(
            "INSERT INTO YoutubeCaptions (VideoId, Language, IsAuto, ClientName, CueCount, CuesJson, CreatedAt, LastUsedAt) " +
            "VALUES (@videoId, @language, @isAuto, @clientName, @cueCount, @cuesJson, datetime('now'), datetime('now')) " +
            "ON CONFLICT(VideoId, Language) DO UPDATE SET " +
            "IsAuto = excluded.IsAuto, ClientName = excluded.ClientName, CueCount = excluded.CueCount, " +
            "CuesJson = excluded.CuesJson, CreatedAt = datetime('now'), LastUsedAt = datetime('now') " +
            "RETURNING Id",
            new { videoId, language, isAuto, clientName, cueCount = cues.Count, cuesJson = CaptionCueJson.Serialize(cues) });
    }

    /// <summary>LastUsedAt 設為現在；找不到時不做事。</summary>
    public void TouchCaption(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Execute(
            "UPDATE YoutubeCaptions SET LastUsedAt = datetime('now') WHERE Id = @id", new { id });
    }

    /// <summary>該字幕前處理過的句子，依 StartMs 排序；查詢失敗時回傳空清單。</summary>
    public IReadOnlyList<CaptionSentence> GetSentences(long captionId)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            return connection.Query<CaptionSentence>(
                "SELECT StartMs, EndMs, RawText, Text, Model FROM CaptionSentences " +
                "WHERE CaptionId = @captionId ORDER BY StartMs",
                new { captionId }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取 CaptionSentences 失敗：caption {CaptionId}", captionId);
            return [];
        }
    }

    /// <summary>同一個交易內寫入句子；(CaptionId, StartMs) 已存在時覆寫；失敗只寫 Warning log。</summary>
    public void SaveSentences(long captionId, IReadOnlyList<CaptionSentence> sentences)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                connection.Execute(@"
                    INSERT INTO CaptionSentences (CaptionId, StartMs, EndMs, RawText, Text, Model, CreatedAt)
                    VALUES (@captionId, @StartMs, @EndMs, @RawText, @Text, @Model, datetime('now'))
                    ON CONFLICT(CaptionId, StartMs) DO UPDATE SET
                        EndMs = excluded.EndMs,
                        RawText = excluded.RawText,
                        Text = excluded.Text,
                        Model = excluded.Model",
                    sentences.Select(s => new { captionId, s.StartMs, s.EndMs, s.RawText, s.Text, s.Model }),
                    transaction);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寫入 CaptionSentences 失敗：caption {CaptionId}", captionId);
        }
    }

    // ===== IAuditStore（都不得往外擲例外：失敗只寫 Warning log，回溯紀錄失敗不能影響翻譯） =====

    /// <summary>PromptTemplates 的一列（時間為字串，再依 UTC 解析）。</summary>
    private sealed class PromptTemplateRow
    {
        public long Id { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string? AppVersion { get; set; }
        public string? CreatedAt { get; set; }
    }

    /// <summary>(Kind, ContentHash) 已存在時回傳既有 Id（Origin、AppVersion 不改）；不存在時新增。失敗回傳 null。</summary>
    public long? EnsurePromptTemplate(string kind, string content, string origin, string? appVersion)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            var hash = AuditHash.Of(content);
            var existing = connection.QueryFirstOrDefault<long?>(
                "SELECT Id FROM PromptTemplates WHERE Kind = @kind AND ContentHash = @hash", new { kind, hash });
            if (existing is { } id)
            {
                return id;
            }

            return connection.QuerySingle<long>(
                "INSERT INTO PromptTemplates (Kind, Content, ContentHash, Origin, AppVersion, CreatedAt) " +
                "VALUES (@kind, @content, @hash, @origin, @appVersion, @createdAt); SELECT last_insert_rowid();",
                new { kind, content, hash, origin, appVersion, createdAt = FormatUtc(DateTime.UtcNow) });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寫入 PromptTemplates 失敗：kind {Kind}", kind);
            return null;
        }
    }

    /// <summary>指定種類的全部版本，依 Id 由大到小。失敗回傳空清單。</summary>
    public IReadOnlyList<PromptTemplate> GetPromptTemplates(string kind)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            return connection.Query<PromptTemplateRow>(
                "SELECT Id, Kind, Content, ContentHash, Origin, AppVersion, CreatedAt " +
                "FROM PromptTemplates WHERE Kind = @kind ORDER BY Id DESC", new { kind })
                .Select(row => new PromptTemplate(row.Id, row.Kind, row.Content, row.ContentHash, row.Origin,
                    row.AppVersion, ParseUtc(row.CreatedAt)))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取 PromptTemplates 失敗：kind {Kind}", kind);
            return [];
        }
    }

    /// <summary>依 Id 取一個版本；不存在或失敗回傳 null。</summary>
    public PromptTemplate? GetPromptTemplate(long id)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            return connection.Query<PromptTemplateRow>(
                    "SELECT Id, Kind, Content, ContentHash, Origin, AppVersion, CreatedAt " +
                    "FROM PromptTemplates WHERE Id = @id", new { id })
                .Select(row => new PromptTemplate(row.Id, row.Kind, row.Content, row.ContentHash, row.Origin,
                    row.AppVersion, ParseUtc(row.CreatedAt)))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取 PromptTemplates 失敗：id {Id}", id);
            return null;
        }
    }

    /// <summary>內容雜湊已存在時把 LastUsedAt 設成現在並回傳既有 Id；不存在時新增（TemplateId 照傳入值，不參與去重）。失敗回傳 null。</summary>
    public long? EnsurePromptSnapshot(long? templateId, string content)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            var hash = AuditHash.Of(content);
            var existing = connection.QueryFirstOrDefault<long?>(
                "SELECT Id FROM PromptSnapshots WHERE ContentHash = @hash", new { hash });
            if (existing is { } id)
            {
                connection.Execute(
                    "UPDATE PromptSnapshots SET LastUsedAt = @lastUsedAt WHERE Id = @id",
                    new { lastUsedAt = FormatUtc(DateTime.UtcNow), id });
                return id;
            }

            return connection.QuerySingle<long>(
                "INSERT INTO PromptSnapshots (TemplateId, Content, ContentHash, CreatedAt, LastUsedAt) " +
                "VALUES (@templateId, @content, @hash, @now, @now); SELECT last_insert_rowid();",
                new { templateId, content, hash, now = FormatUtc(DateTime.UtcNow) });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寫入 PromptSnapshots 失敗");
            return null;
        }
    }

    /// <summary>同 EnsurePromptSnapshot，對象是 SettingsSnapshots（content 是 JSON 字串）。失敗回傳 null。</summary>
    public long? EnsureSettingsSnapshot(string content)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            var hash = AuditHash.Of(content);
            var existing = connection.QueryFirstOrDefault<long?>(
                "SELECT Id FROM SettingsSnapshots WHERE ContentHash = @hash", new { hash });
            if (existing is { } id)
            {
                connection.Execute(
                    "UPDATE SettingsSnapshots SET LastUsedAt = @lastUsedAt WHERE Id = @id",
                    new { lastUsedAt = FormatUtc(DateTime.UtcNow), id });
                return id;
            }

            return connection.QuerySingle<long>(
                "INSERT INTO SettingsSnapshots (Content, ContentHash, CreatedAt, LastUsedAt) " +
                "VALUES (@content, @hash, @now, @now); SELECT last_insert_rowid();",
                new { content, hash, now = FormatUtc(DateTime.UtcNow) });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寫入 SettingsSnapshots 失敗");
            return null;
        }
    }

    /// <summary>新增一筆模型呼叫紀錄，回傳 Id；失敗回傳 null。</summary>
    public long? InsertLlmCall(LlmCallRecord record)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            return connection.QuerySingle<long>(
                "INSERT INTO LlmCalls (StreamId, JobId, Purpose, TemplateId, SystemSnapshotId, SettingsSnapshotId, " +
                "Model, UserPrompt, History, ResponseRaw, KeywordTermIds, FixIds, PromptTokens, CompletionTokens, LatencyMs, Outcome, CreatedAt) " +
                "VALUES (@StreamId, @JobId, @Purpose, @TemplateId, @SystemSnapshotId, @SettingsSnapshotId, " +
                "@Model, @UserPrompt, @History, @ResponseRaw, @KeywordTermIds, @FixIds, @PromptTokens, @CompletionTokens, @LatencyMs, @Outcome, @createdAt); " +
                "SELECT last_insert_rowid();",
                new
                {
                    record.StreamId,
                    record.JobId,
                    record.Purpose,
                    record.TemplateId,
                    record.SystemSnapshotId,
                    record.SettingsSnapshotId,
                    record.Model,
                    record.UserPrompt,
                    record.History,
                    record.ResponseRaw,
                    record.KeywordTermIds,
                    record.FixIds,
                    record.PromptTokens,
                    record.CompletionTokens,
                    record.LatencyMs,
                    record.Outcome,
                    createdAt = FormatUtc(DateTime.UtcNow)
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寫入 LlmCalls 失敗：purpose {Purpose}、job {JobId}", record.Purpose, record.JobId);
            return null;
        }
    }

    /// <summary>過期清除：先刪舊的 LlmCalls，再刪沒被任何 LlmCalls 參照而且 LastUsedAt 過期的兩種快照；
    /// 每次最多刪 5000 列、重複到刪完為止。失敗時回傳已刪除的筆數。</summary>
    public AuditPurgeResult PurgeAudit(DateTimeOffset cutoffUtc)
    {
        var llmCalls = 0;
        var promptSnapshots = 0;
        var settingsSnapshots = 0;
        try
        {
            var cutoff = FormatUtc(cutoffUtc);
            llmCalls = PurgeInBatches(
                "DELETE FROM LlmCalls WHERE Id IN (SELECT Id FROM LlmCalls WHERE CreatedAt < @cutoff LIMIT 5000)", cutoff);
            promptSnapshots = PurgeInBatches(
                "DELETE FROM PromptSnapshots WHERE Id IN (SELECT Id FROM PromptSnapshots WHERE LastUsedAt < @cutoff " +
                "AND Id NOT IN (SELECT SystemSnapshotId FROM LlmCalls WHERE SystemSnapshotId IS NOT NULL) LIMIT 5000)", cutoff);
            settingsSnapshots = PurgeInBatches(
                "DELETE FROM SettingsSnapshots WHERE Id IN (SELECT Id FROM SettingsSnapshots WHERE LastUsedAt < @cutoff " +
                "AND Id NOT IN (SELECT SettingsSnapshotId FROM LlmCalls WHERE SettingsSnapshotId IS NOT NULL) LIMIT 5000)", cutoff);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "回溯紀錄清除中斷（已刪除：模型呼叫 {LlmCalls}、prompt 快照 {PromptSnapshots}、設定快照 {SettingsSnapshots}）",
                llmCalls, promptSnapshots, settingsSnapshots);
        }

        return new AuditPurgeResult(llmCalls, promptSnapshots, settingsSnapshots);
    }

    /// <summary>分批刪除（每批最多 5000 列，刪到沒有符合條件的為止），回傳總刪除筆數。</summary>
    private int PurgeInBatches(string deleteSql, string cutoff)
    {
        using var connection = new SqliteConnection(_connectionString);
        var total = 0;
        while (true)
        {
            var deleted = connection.Execute(deleteSql, new { cutoff });
            total += deleted;
            if (deleted < 5000)
            {
                return total;
            }
        }
    }

    /// <summary>把 SQLite 的 UTC 時間字串（yyyy-MM-dd HH:mm:ss）解析成 DateTimeOffset（UTC）；空值回傳現在。</summary>
    private static DateTimeOffset ParseUtc(string? text) =>
        text is null
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.ParseExact(text, TimestampFormat, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal);

    /// <summary>時間欄位寫入格式：UTC、與 datetime('now') 一致。</summary>
    private static string FormatUtc(DateTimeOffset time) =>
        time.UtcDateTime.ToString(TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);
}
