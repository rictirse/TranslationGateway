using System.Net.Sockets;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// PostgreSQL 翻譯快取後端。建構子只組連線字串不連線；
/// 連線失敗時進入斷線狀態 30 秒（期間不嘗試連線），確保翻譯流程不被資料庫問題拖住。
/// </summary>
public sealed class PostgresTranslationStore : ITranslationStore, IGlossaryStore, ISubtitleLogStore, ICaptionStore, IAuditStore
{
    /// <summary>斷線後暫停嘗試連線的時間。</summary>
    private static readonly TimeSpan UnavailableWindow = TimeSpan.FromSeconds(30);

    /// <summary>Schema 名稱只允許字母或底線開頭，其後為字母、數字或底線。</summary>
    private static readonly Regex SchemaPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly DatabaseSettings _settings;
    private readonly string _schema;
    private readonly string _connectionString;
    private readonly ILogger<PostgresTranslationStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _initLock = new();

    /// <summary>資料表是否已在這個執行個體建立過（延遲執行，第一次連線成功時跑 DDL）。</summary>
    private bool _initialized;

    /// <summary>斷線期間的截止時間（UTC ticks）；0 表示未處於斷線狀態。以 Volatile 讀寫，避免多執行緒讀到不完整的值。</summary>
    private long _unavailableUntilTicks;

    /// <summary>是否處於（未曾恢復的）斷線狀態；用來讓警告只記一次、恢復時記錄一次。</summary>
    private bool _wasUnavailable;

    /// <summary>給人看的後端描述（不含密碼）。</summary>
    public string Description => $"PostgreSQL：{_settings.Host}:{_settings.Port}/{_settings.Database}（{_schema}）";

    /// <summary>目前是否可用（斷線後 30 秒內為 false）。</summary>
    public bool IsAvailable => _timeProvider.GetUtcNow().UtcTicks >= Volatile.Read(ref _unavailableUntilTicks);

    /// <summary>建構子不連線；Schema 不合法時丟 ArgumentException。</summary>
    public PostgresTranslationStore(DatabaseSettings settings, ILogger<PostgresTranslationStore> logger, TimeProvider? timeProvider = null)
    {
        var schema = NormalizeSchema(settings.Schema);
        if (!IsValidSchema(schema))
        {
            throw new ArgumentException($"Schema 名稱不合法：{schema}");
        }

        _settings = settings;
        _schema = schema;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionString = BuildConnectionString(settings);
    }

    /// <summary>Schema 名稱只允許 ^[A-Za-z_][A-Za-z0-9_]*$。</summary>
    public static bool IsValidSchema(string schema) =>
        schema is not null && SchemaPattern.IsMatch(schema);

    /// <summary>由設定組出 Npgsql 連線字串（含 Search Path）。</summary>
    public static string BuildConnectionString(DatabaseSettings settings, int timeoutSeconds = 3)
    {
        var schema = NormalizeSchema(settings.Schema);
        if (!IsValidSchema(schema))
        {
            throw new ArgumentException($"Schema 名稱不合法：{schema}");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = settings.Host,
            Port = settings.Port,
            Database = settings.Database,
            Username = settings.UserName,
            Password = settings.Password,
            SearchPath = schema,
            Timeout = timeoutSeconds,
            CommandTimeout = 5,
            Pooling = true,
        };
        return builder.ConnectionString;
    }

    /// <summary>Schema 修剪前後空白；修剪後為空時視為 public。</summary>
    private static string NormalizeSchema(string? schema)
    {
        var trimmed = (schema ?? string.Empty).Trim();
        return trimmed.Length == 0 ? "public" : trimmed;
    }

    public string? GetCache(string source)
    {
        return ExecuteWithFallback(() =>
        {
            using var connection = OpenConnection();
            return connection.QueryFirstOrDefault<string>(
                "SELECT target_text FROM translation_cache WHERE source_text = @source", new { source });
        }, (string?)null);
    }

    /// <summary>指定原文的快取 Id；沒有或失敗時回傳 null（不擲例外）。</summary>
    public long? GetCacheId(string source)
    {
        return ExecuteWithFallback(() =>
        {
            using var connection = OpenConnection();
            return connection.QueryFirstOrDefault<long?>(
                "SELECT id FROM translation_cache WHERE source_text = @source", new { source });
        }, (long?)null);
    }

    public void SaveCache(string source, string target, bool isManual = false)
    {
        ExecuteWithFallback<object?>(() =>
        {
            using var connection = OpenConnection();
            connection.Execute(@"
                INSERT INTO translation_cache (source_text, target_text, is_manual, last_used)
                VALUES (@source, @target, @isManual, now())
                ON CONFLICT (source_text) DO UPDATE SET
                    target_text = excluded.target_text,
                    is_manual = excluded.is_manual,
                    last_used = now()", new { source, target, isManual = isManual });
            return null;
        }, null);
    }

    /// <summary>寫入快取（is_manual = false）；同原文已存在而且是手動修正時不覆蓋。</summary>
    public void SaveCacheUnlessManual(string source, string target)
    {
        ExecuteWithFallback<object?>(() =>
        {
            using var connection = OpenConnection();
            connection.Execute(@"
                INSERT INTO translation_cache (source_text, target_text, is_manual, last_used)
                VALUES (@source, @target, false, now())
                ON CONFLICT (source_text) DO UPDATE SET
                    target_text = excluded.target_text,
                    is_manual = excluded.is_manual,
                    last_used = now()
                WHERE translation_cache.is_manual = false", new { source, target });
            return null;
        }, null);
    }

    /// <summary>刪除指定原文的快取項目（預設不含手動修正的項目，<paramref name="includeManual"/> 為 true 時一併刪除）；回傳是否有刪除任何資料列。</summary>
    public bool DeleteCache(string source, bool includeManual = false)
    {
        return ExecuteWithFallback(() =>
        {
            using var connection = OpenConnection();
            var sql = includeManual
                ? "DELETE FROM translation_cache WHERE source_text = @source"
                : "DELETE FROM translation_cache WHERE source_text = @source AND is_manual = false";
            return connection.Execute(sql, new { source }) > 0;
        }, false);
    }

    public IEnumerable<TranslationCacheItem> GetAllCache()
    {
        return ExecuteWithFallback(() =>
        {
            using var connection = OpenConnection();
            return connection.Query<TranslationCacheItem>(@"
                SELECT source_text AS SourceText,
                       target_text AS TargetText,
                       is_manual AS IsManual,
                       last_used AS LastUsed
                FROM translation_cache
                ORDER BY last_used DESC
                LIMIT 500");
        }, []);
    }

    public IReadOnlyList<GlossaryCard> GetGlossaryCards()
    {
        return ExecuteWithFallback<IReadOnlyList<GlossaryCard>>(() =>
        {
            using var connection = OpenConnection();
            return connection.Query<GlossaryCard>(@"
                SELECT id AS Id,
                       kind AS Kind,
                       name AS Name,
                       parent_id AS ParentId,
                       channel_id AS ChannelId,
                       channel_handle AS ChannelHandle,
                       aliases AS Aliases,
                       description AS Description,
                       source_url AS SourceUrl,
                       status AS Status
                FROM glossary_cards
                WHERE status <> 'Rejected'
                ORDER BY id").ToList();
        }, []);
    }

    public IReadOnlyList<GlossaryTerm> GetGlossaryTerms()
    {
        return ExecuteWithFallback<IReadOnlyList<GlossaryTerm>>(() =>
        {
            using var connection = OpenConnection();
            return connection.Query<GlossaryTerm>(@"
                SELECT id AS Id,
                       card_id AS CardId,
                       source AS Source,
                       target AS Target,
                       aliases AS Aliases,
                       category AS Category,
                       note AS Note,
                       status AS Status
                FROM glossary_terms
                WHERE status <> 'Rejected'
                ORDER BY card_id, id").ToList();
        }, []);
    }

    /// <summary>審核頁用：所有詞彙卡（含 Rejected）；連線失敗時回傳空清單。</summary>
    public IReadOnlyList<GlossaryCard> GetAllGlossaryCards()
    {
        return ExecuteWithFallback<IReadOnlyList<GlossaryCard>>(() =>
        {
            using var connection = OpenConnection();
            return connection.Query<GlossaryCard>(@"
                SELECT id AS Id,
                       kind AS Kind,
                       name AS Name,
                       parent_id AS ParentId,
                       channel_id AS ChannelId,
                       channel_handle AS ChannelHandle,
                       aliases AS Aliases,
                       description AS Description,
                       source_url AS SourceUrl,
                       status AS Status
                FROM glossary_cards
                ORDER BY id").ToList();
        }, []);
    }

    /// <summary>審核頁用：所有名詞（含 Rejected）；連線失敗時回傳空清單。</summary>
    public IReadOnlyList<GlossaryTerm> GetAllGlossaryTerms()
    {
        return ExecuteWithFallback<IReadOnlyList<GlossaryTerm>>(() =>
        {
            using var connection = OpenConnection();
            return connection.Query<GlossaryTerm>(@"
                SELECT id AS Id,
                       card_id AS CardId,
                       source AS Source,
                       target AS Target,
                       aliases AS Aliases,
                       category AS Category,
                       note AS Note,
                       status AS Status
                FROM glossary_terms
                ORDER BY card_id, id").ToList();
        }, []);
    }

    /// <summary>同一個交易中更新卡片與名詞（依 Id）並把 updated_at 設為 now()；任一筆失敗就整批回復。
    /// 不走 <see cref="ExecuteWithFallback{T}"/>：儲存失敗時使用者必須知道（例外往外拋）。</summary>
    public void SaveGlossaryChanges(IReadOnlyList<GlossaryCard> cards, IReadOnlyList<GlossaryTerm> terms)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            connection.Execute(@"
                UPDATE glossary_cards SET
                    name = @Name,
                    parent_id = @ParentId,
                    channel_handle = @ChannelHandle,
                    aliases = @Aliases,
                    description = @Description,
                    source_url = @SourceUrl,
                    status = @Status,
                    updated_at = now()
                WHERE id = @Id",
                cards.Select(card => new
                {
                    card.Name,
                    card.ParentId,
                    card.ChannelHandle,
                    Aliases = GlossaryCatalog.NormalizeCardAliases(card.Aliases, card.Name),
                    card.Description,
                    card.SourceUrl,
                    card.Status,
                    card.Id
                }),
                transaction);
            connection.Execute(@"
                UPDATE glossary_terms SET
                    source = @Source,
                    target = @Target,
                    aliases = @Aliases,
                    category = @Category,
                    note = @Note,
                    status = @Status,
                    updated_at = now()
                WHERE id = @Id",
                terms.Select(term => new
                {
                    term.Source,
                    term.Target,
                    term.Aliases,
                    term.Category,
                    term.Note,
                    term.Status,
                    term.Id
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

    /// <summary>同一個交易中更新既有名詞（依 Id，updated_at = now()）並新增名詞（created_at 用預設值）；任一筆失敗就整批回復。
    /// 不走 <see cref="ExecuteWithFallback{T}"/>：儲存失敗時使用者必須知道（例外往外拋）。</summary>
    public void SaveGlossaryTermEdits(IReadOnlyList<GlossaryTerm> updated, IReadOnlyList<GlossaryTerm> inserted)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            connection.Execute(@"
                UPDATE glossary_terms SET
                    source = @Source,
                    target = @Target,
                    aliases = @Aliases,
                    category = @Category,
                    note = @Note,
                    status = @Status,
                    updated_at = now()
                WHERE id = @Id",
                updated.Select(term => new
                {
                    term.Source,
                    term.Target,
                    term.Aliases,
                    term.Category,
                    term.Note,
                    term.Status,
                    term.Id
                }),
                transaction);
            connection.Execute(@"
                INSERT INTO glossary_terms (card_id, source, target, aliases, category, note, status)
                VALUES (@CardId, @Source, @Target, @Aliases, @Category, @Note, @Status)",
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

    /// <summary>同一個交易中新增一張卡與它的名詞，回傳新卡 Id；任一筆失敗就整批回復並擲出例外。
    /// 不走 <see cref="ExecuteWithFallback{T}"/>：寫入失敗時使用者必須知道（例外往外拋）。</summary>
    public long CreateGlossaryCard(GlossaryCard card, IReadOnlyList<GlossaryTerm> terms)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            var cardId = connection.ExecuteScalar<long>(@"
                INSERT INTO glossary_cards (kind, name, parent_id, channel_id, channel_handle, aliases, description, source_url, status, created_at)
                VALUES (@Kind, @Name, @ParentId, @ChannelId, @ChannelHandle, @Aliases, @Description, @SourceUrl, @Status, now())
                RETURNING id",
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
                    card.Status
                },
                transaction);
            connection.Execute(@"
                INSERT INTO glossary_terms (card_id, source, target, aliases, category, note, status)
                VALUES (@CardId, @Source, @Target, @Aliases, @Category, @Note, @Status)",
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
    /// 任一步失敗就整批回復並擲出例外。兩個 Id 相同或任一張卡不存在時擲 ArgumentException。
    /// 不走 <see cref="ExecuteWithFallback{T}"/>：寫入失敗時使用者必須知道（例外往外拋）。</summary>
    public GlossaryMergeResult MergeGlossaryCards(long sourceId, long targetId)
    {
        if (sourceId == targetId)
        {
            throw new ArgumentException($"合併的兩張詞彙卡不能是同一張：{sourceId}。");
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            var source = connection.QuerySingleOrDefault<GlossaryCard>(@"
                SELECT id AS Id, kind AS Kind, name AS Name, parent_id AS ParentId,
                       channel_id AS ChannelId, channel_handle AS ChannelHandle, aliases AS Aliases,
                       description AS Description, source_url AS SourceUrl, status AS Status
                FROM glossary_cards WHERE id = @sourceId", new { sourceId }, transaction);
            var target = connection.QuerySingleOrDefault<GlossaryCard>(@"
                SELECT id AS Id, kind AS Kind, name AS Name, parent_id AS ParentId,
                       channel_id AS ChannelId, channel_handle AS ChannelHandle, aliases AS Aliases,
                       description AS Description, source_url AS SourceUrl, status AS Status
                FROM glossary_cards WHERE id = @targetId", new { targetId }, transaction);
            if (source is null || target is null)
            {
                throw new ArgumentException($"找不到要合併的詞彙卡：source {sourceId}、target {targetId}。");
            }

            var sourceTerms = connection.Query<GlossaryTerm>(@"
                SELECT id AS Id, card_id AS CardId, source AS Source, target AS Target,
                       aliases AS Aliases, category AS Category, note AS Note, status AS Status
                FROM glossary_terms WHERE card_id = @sourceId ORDER BY id", new { sourceId }, transaction).ToList();
            var targetTerms = connection.Query<GlossaryTerm>(@"
                SELECT id AS Id, card_id AS CardId, source AS Source, target AS Target,
                       aliases AS Aliases, category AS Category, note AS Note, status AS Status
                FROM glossary_terms WHERE card_id = @targetId ORDER BY id", new { targetId }, transaction).ToList();
            var plan = GlossaryMerger.Plan(source, sourceTerms, target, targetTerms);

            // 丟掉的名詞的聽錯字修正：先刪掉與既有名詞重複（同 kind、wrong_text）的，剩下的改掛到既有名詞
            foreach (var (droppedTermId, keptTermId) in plan.DroppedTerms)
            {
                connection.Execute(
                    "DELETE FROM mishearing_fixes WHERE term_id = @droppedTermId AND EXISTS (" +
                    "SELECT 1 FROM mishearing_fixes kept WHERE kept.term_id = @keptTermId " +
                    "AND kept.kind = mishearing_fixes.kind AND kept.wrong_text = mishearing_fixes.wrong_text)",
                    new { droppedTermId, keptTermId }, transaction);
                connection.Execute(
                    "UPDATE mishearing_fixes SET term_id = @keptTermId, card_id = @targetId WHERE term_id = @droppedTermId",
                    new { keptTermId, targetId, droppedTermId }, transaction);
            }

            // 刪掉丟掉的名詞（既有名詞不動）
            if (plan.DroppedTerms.Count > 0)
            {
                connection.Execute(
                    "DELETE FROM glossary_terms WHERE id = ANY(@ids)",
                    new { ids = plan.DroppedTerms.Select(d => d.DroppedTermId).ToArray() }, transaction);
            }

            // 搬名詞：改掛到 target 卡、Status 一律改 Pending
            if (plan.TermsToMove.Count > 0)
            {
                connection.Execute(
                    "UPDATE glossary_terms SET card_id = @targetId, status = 'Pending', updated_at = now() WHERE id = ANY(@ids)",
                    new { targetId, ids = plan.TermsToMove.Select(t => t.Id).ToArray() }, transaction);
            }

            // 剩下的聽錯字修正（搬過去的名詞的）跟著換卡
            connection.Execute(
                "UPDATE mishearing_fixes SET card_id = @targetId WHERE card_id = @sourceId",
                new { targetId, sourceId }, transaction);

            // 更新 target 卡：別名與補進來的欄位（kind、name、status 維持 target 的）
            connection.Execute(@"
                UPDATE glossary_cards SET
                    aliases = @aliases,
                    parent_id = @parentId,
                    channel_id = @channelId,
                    channel_handle = @channelHandle,
                    description = @description,
                    source_url = @sourceUrl,
                    updated_at = now()
                WHERE id = @targetId",
                new
                {
                    aliases = plan.NewTargetAliases,
                    parentId = plan.NewTargetParentId,
                    channelId = plan.NewTargetChannelId,
                    channelHandle = plan.NewTargetChannelHandle,
                    description = plan.NewTargetDescription,
                    sourceUrl = plan.NewTargetSourceUrl,
                    targetId
                },
                transaction);

            // 改指向：開台生效紀錄、直播的主播卡、source 的子卡都改指 target；target 的上層原本是 source 時清空
            connection.Execute(
                "UPDATE stream_cards SET card_id = @targetId WHERE card_id = @sourceId", new { targetId, sourceId }, transaction);
            connection.Execute(
                "UPDATE streams SET character_card_id = @targetId WHERE character_card_id = @sourceId",
                new { targetId, sourceId }, transaction);
            connection.Execute(
                "UPDATE glossary_cards SET parent_id = @targetId WHERE parent_id = @sourceId AND id <> @targetId",
                new { targetId, sourceId }, transaction);
            connection.Execute(
                "UPDATE glossary_cards SET parent_id = NULL WHERE id = @targetId AND parent_id = @sourceId",
                new { targetId, sourceId }, transaction);

            // 刪掉 source 卡（這時它已經沒有名詞）
            connection.Execute("DELETE FROM glossary_cards WHERE id = @sourceId", new { sourceId }, transaction);

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
    /// 失敗擲例外。卡不存在時不動作。不走 <see cref="ExecuteWithFallback{T}"/>：刪除必須確實生效（例外往外拋）。</summary>
    public void DeleteGlossaryCard(long cardId)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            if (connection.QuerySingle<long>(
                    "SELECT COUNT(*) FROM glossary_cards WHERE id = @cardId", new { cardId }, transaction) == 0)
            {
                return;
            }

            connection.Execute("DELETE FROM mishearing_fixes WHERE card_id = @cardId", new { cardId }, transaction);
            connection.Execute("DELETE FROM glossary_terms WHERE card_id = @cardId", new { cardId }, transaction);
            connection.Execute("DELETE FROM stream_cards WHERE card_id = @cardId", new { cardId }, transaction);
            connection.Execute(
                "UPDATE streams SET character_card_id = NULL WHERE character_card_id = @cardId", new { cardId }, transaction);
            connection.Execute(
                "UPDATE glossary_cards SET parent_id = NULL WHERE parent_id = @cardId", new { cardId }, transaction);
            connection.Execute("DELETE FROM glossary_cards WHERE id = @cardId", new { cardId }, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>同一個交易中刪除指定的名詞與它們的聽錯字修正（先刪聽錯字修正，再刪名詞）；失敗擲例外。
    /// 不依賴外鍵 CASCADE；不存在的 Id 略過；空清單不動作。只刪名詞，不更新卡片與別名。
    /// 不走 <see cref="ExecuteWithFallback{T}"/>：刪除必須確實生效（例外往外拋）。
    /// 清單參數一律用 <c>= ANY(@ids)</c>（Npgsql 不支援 Dapper 的展開 IN）。</summary>
    public void DeleteGlossaryTerms(IReadOnlyList<long> termIds)
    {
        if (termIds.Count == 0)
        {
            return;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            var ids = termIds.ToArray();
            connection.Execute("DELETE FROM mishearing_fixes WHERE term_id = ANY(@ids)", new { ids }, transaction);
            connection.Execute("DELETE FROM glossary_terms WHERE id = ANY(@ids)", new { ids }, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>新增或累加名詞候選：(surface, kind) 已存在時 occurrences + 1、更新 last_seen_at、stream_id、context、importance 取較大值。
    /// 不走 <see cref="ExecuteWithFallback{T}"/>：寫入失敗時使用者必須知道（例外往外拋）。</summary>
    public void UpsertTermCandidates(IReadOnlyList<TermCandidate> candidates)
    {
        using var connection = OpenConnection();
        connection.Execute(@"
            INSERT INTO term_candidates (surface, kind, importance, origin, stream_id, context, occurrences, created_at, last_seen_at)
            VALUES (@Surface, @Kind, @Importance, @Origin, @StreamId, @Context, 1, now(), now())
            ON CONFLICT (surface, kind) DO UPDATE SET
                occurrences = term_candidates.occurrences + 1,
                importance = GREATEST(term_candidates.importance, excluded.importance),
                stream_id = excluded.stream_id,
                context = excluded.context,
                last_seen_at = now()",
            candidates);
    }

    // ===== mishearing_fixes =====

    /// <summary>全部聽錯字修正（Pending 與 Approved），依 card_id、term_id、wrong_text 排序；連線失敗時回傳空清單。</summary>
    public IReadOnlyList<MishearingFix> GetMishearingFixes()
    {
        return ExecuteWithFallback<IReadOnlyList<MishearingFix>>(() =>
        {
            using var connection = OpenConnection();
            return connection.Query<MishearingFix>(@"
                SELECT id AS Id,
                       card_id AS CardId,
                       term_id AS TermId,
                       kind AS Kind,
                       wrong_text AS Wrong,
                       right_text AS Right,
                       status AS Status,
                       evidence AS Evidence,
                       occurrences AS Occurrences,
                       created_at AS CreatedAt,
                       updated_at AS UpdatedAt
                FROM mishearing_fixes
                ORDER BY card_id, term_id, wrong_text").ToList();
        }, []);
    }

    /// <summary>新增或累加聽錯字修正：(term_id, kind, wrong_text) 已存在時 occurrences + 1、evidence 換新、updated_at = now()（status 不變）；
    /// 不存在時新增為 Pending；Wrong 太短、Wrong == Right、Evidence 空白的略過。整批一個交易，失敗回復。
    /// 不走 <see cref="ExecuteWithFallback{T}"/>：寫入失敗時使用者必須知道（例外往外拋）。</summary>
    public int UpsertMishearingFixes(IReadOnlyList<MishearingProposal> proposals)
    {
        using var connection = OpenConnection();
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
                    "SELECT id FROM mishearing_fixes WHERE term_id = @termId AND kind = @kind AND wrong_text = @wrong",
                    new { termId = proposal.TermId, kind, wrong },
                    transaction);
                if (existingId is { } id)
                {
                    connection.Execute(
                        "UPDATE mishearing_fixes SET occurrences = occurrences + 1, evidence = @evidence, updated_at = now() WHERE id = @id",
                        new { evidence, id },
                        transaction);
                }
                else
                {
                    connection.Execute(
                        "INSERT INTO mishearing_fixes (card_id, term_id, kind, wrong_text, right_text, status, evidence, occurrences, created_at) " +
                        "VALUES (@cardId, @termId, @kind, @wrong, @right, 'Pending', @evidence, 1, now())",
                        new
                        {
                            cardId = proposal.CardId,
                            termId = proposal.TermId,
                            kind,
                            wrong,
                            right,
                            evidence
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

    /// <summary>把指定的 Id 改成 Approved、updated_at = now()。
    /// 不走 <see cref="ExecuteWithFallback{T}"/>：審核結果必須確實寫入（例外往外拋）。</summary>
    public void ApproveMishearingFixes(IReadOnlyList<long> ids)
    {
        using var connection = OpenConnection();
        connection.Execute(
            "UPDATE mishearing_fixes SET status = 'Approved', updated_at = now() WHERE id = ANY(@ids)",
            new { ids = ids.ToArray() });
    }

    /// <summary>刪除指定的 Id（駁回）。
    /// 不走 <see cref="ExecuteWithFallback{T}"/>：刪除必須確實生效（例外往外拋）。</summary>
    public void DeleteMishearingFixes(IReadOnlyList<long> ids)
    {
        using var connection = OpenConnection();
        connection.Execute("DELETE FROM mishearing_fixes WHERE id = ANY(@ids)", new { ids = ids.ToArray() });
    }

    // ===== ISubtitleLogStore（都走 ExecuteWithFallback：失敗時不做事，CreateStream 回傳 null） =====

    /// <summary>新增直播，回傳 Id；失敗回傳 null。</summary>
    public long? CreateStream(StreamRecord stream)
    {
        return ExecuteWithFallback(() =>
        {
            using var connection = OpenConnection();
            return connection.ExecuteScalar<long>(
                "INSERT INTO streams (platform, url, channel_name, title, category, character_card_id, is_vod, video_started_at, video_ended_at, created_at) " +
                "VALUES (@Platform, @Url, @ChannelName, @Title, @Category, @CharacterCardId, @IsVod, @VideoStartedAt, @VideoEndedAt, @CreatedAt) " +
                "RETURNING id",
                new
                {
                    stream.Platform,
                    stream.Url,
                    stream.ChannelName,
                    stream.Title,
                    stream.Category,
                    stream.CharacterCardId,
                    stream.IsVod,
                    VideoStartedAt = stream.VideoStartedAt?.UtcDateTime,
                    VideoEndedAt = stream.VideoEndedAt?.UtcDateTime,
                    CreatedAt = DateTimeOffset.UtcNow
                });
        }, (long?)null);
    }

    /// <summary>新增一句字幕；RevisionOfId 以子查詢指向該 JobId 最新一列（找不到為 NULL），cache_id 照 entry 帶入的值寫入。</summary>
    public void InsertSubtitle(SubtitleLogEntry entry)
    {
        ExecuteWithFallback<object?>(() =>
        {
            using var connection = OpenConnection();
            connection.Execute(
                "INSERT INTO subtitle_log (stream_id, job_id, raw_source, source, translation, translation_source, revision_of_id, model_latency_ms, cache_id, created_at) " +
                "VALUES (@StreamId, @JobId, @RawSource, @Source, @Translation, @TranslationSource, " +
                "(SELECT id FROM subtitle_log WHERE job_id = @RevisionOfJobId ORDER BY id DESC LIMIT 1), " +
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
                    entry.CreatedAt
                });
            return null;
        }, null);
    }

    /// <summary>更新該 JobId 最新一列：source 不為 null 才改 Source（COALESCE），Translation 一律覆寫。</summary>
    public void MarkSubtitleEdited(string jobId, string? source, string translation)
    {
        ExecuteWithFallback<object?>(() =>
        {
            using var connection = OpenConnection();
            connection.Execute(
                "UPDATE subtitle_log SET " +
                "source = COALESCE(@source, source), translation = @translation, is_edited = true, updated_at = now() " +
                "WHERE id = (SELECT id FROM subtitle_log WHERE job_id = @jobId ORDER BY id DESC LIMIT 1)",
                new { jobId, source, translation });
            return null;
        }, null);
    }

    /// <summary>該 JobId 最新一列 is_deleted = true、updated_at = now（資料保留）。</summary>
    public void MarkSubtitleDeleted(string jobId)
    {
        ExecuteWithFallback<object?>(() =>
        {
            using var connection = OpenConnection();
            connection.Execute(
                "UPDATE subtitle_log SET is_deleted = true, updated_at = now() " +
                "WHERE id = (SELECT id FROM subtitle_log WHERE job_id = @jobId ORDER BY id DESC LIMIT 1)",
                new { jobId });
            return null;
        }, null);
    }

    /// <summary>新增一筆直播生效的詞彙卡（activated_at = now()）；失敗不擲例外。</summary>
    public void AddStreamCard(long streamId, long cardId, string reason)
    {
        ExecuteWithFallback<object?>(() =>
        {
            using var connection = OpenConnection();
            connection.Execute(
                "INSERT INTO stream_cards (stream_id, card_id, reason, activated_at) " +
                "VALUES (@streamId, @cardId, @reason, now())",
                new { streamId, cardId, reason });
            return null;
        }, null);
    }

    /// <summary>該直播、該卡最新一筆 deactivated_at 為空的紀錄，設 deactivated_at = now()；找不到時不做事。失敗不擲例外。</summary>
    public void EndStreamCard(long streamId, long cardId)
    {
        ExecuteWithFallback<object?>(() =>
        {
            using var connection = OpenConnection();
            connection.Execute(
                "UPDATE stream_cards SET deactivated_at = now() " +
                "WHERE id = (SELECT id FROM stream_cards WHERE stream_id = @streamId AND card_id = @cardId " +
                "AND deactivated_at IS NULL ORDER BY id DESC LIMIT 1)",
                new { streamId, cardId });
            return null;
        }, null);
    }

    /// <summary>最近 maxStreams 場直播（依 Id 由大到小，排除 excludeStreamId）的字幕原文（is_deleted = false、stream_id 非 null）；
    /// GameCardId = 該直播 stream_cards 中卡別是 'Game' 的最小 card_id（沒有為 null）。
    /// 不走 <see cref="ExecuteWithFallback{T}"/>：呼叫端需要知道失敗（失敗擲例外）。</summary>
    public IReadOnlyList<TopicSubtitleSource> GetTopicSubtitleSources(long? excludeStreamId, int maxStreams)
    {
        using var connection = OpenConnection();
        return connection.Query<TopicSourceRow>(@"
            SELECT sl.stream_id AS ""StreamId"", sl.source AS ""Source"",
                (SELECT MIN(sc.card_id)
                 FROM stream_cards sc
                 JOIN glossary_cards gc ON gc.id = sc.card_id
                 WHERE sc.stream_id = sl.stream_id AND gc.kind = 'Game') AS ""GameCardId""
            FROM subtitle_log sl
            WHERE sl.is_deleted = false AND sl.stream_id IS NOT NULL
              AND sl.stream_id IN (
                  SELECT id FROM streams
                  WHERE (@excludeStreamId::bigint IS NULL OR id <> @excludeStreamId)
                  ORDER BY id DESC LIMIT @maxStreams)
            ORDER BY sl.stream_id DESC, sl.id",
            new { excludeStreamId, maxStreams })
            .Select(row => new TopicSubtitleSource(row.StreamId, row.GameCardId, row.Source))
            .ToList();
    }

    /// <summary>GetTopicSubtitleSources 查詢結果的一列：先對到類別再轉成 record（record 建構子的參數順序與查詢欄位順序不同）。</summary>
    private sealed class TopicSourceRow
    {
        public long StreamId { get; set; }

        public long? GameCardId { get; set; }

        public string Source { get; set; } = string.Empty;
    }

    // ===== ICaptionStore（都走 ExecuteWithFallback：失敗時 Get 回 null、Save 回 null、Touch 不做事） =====

    /// <summary>youtube_captions 一列的原始欄位。</summary>
    private sealed class CaptionRow
    {
        public long Id { get; set; }
        public string VideoId { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public bool IsAuto { get; set; }
        public string? ClientName { get; set; }
        public string CuesJson { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastUsedAt { get; set; }
    }

    /// <summary>依影片與語言讀回快取的字幕；找不到或連線失敗時回傳 null（CuesJson 格式壞掉時擲例外，由呼叫端處理）。</summary>
    public StoredCaption? GetCaption(string videoId, string language)
    {
        return ExecuteWithFallback(() =>
        {
            using var connection = OpenConnection();
            var row = connection.QueryFirstOrDefault<CaptionRow>(
                "SELECT id AS Id, video_id AS VideoId, language AS Language, is_auto AS IsAuto, " +
                "client_name AS ClientName, cues_json AS CuesJson, created_at AS CreatedAt, last_used_at AS LastUsedAt " +
                "FROM youtube_captions WHERE video_id = @videoId AND language = @language",
                new { videoId, language });
            return row is null
                ? null
                : new StoredCaption(row.Id, row.VideoId, row.Language, row.IsAuto, row.ClientName,
                    CaptionCueJson.Deserialize(row.CuesJson), row.CreatedAt, row.LastUsedAt);
        }, (StoredCaption?)null);
    }

    /// <summary>新增或覆寫字幕快取（同 video_id + language）；created_at、last_used_at 都設為現在。回傳 Id，失敗回傳 null。</summary>
    public long? SaveCaption(string videoId, string language, bool isAuto, string? clientName, IReadOnlyList<CaptionCue> cues)
    {
        return ExecuteWithFallback(() =>
        {
            using var connection = OpenConnection();
            return connection.ExecuteScalar<long>(
                "INSERT INTO youtube_captions (video_id, language, is_auto, client_name, cue_count, cues_json, created_at, last_used_at) " +
                "VALUES (@videoId, @language, @isAuto, @clientName, @cueCount, @cuesJson, now(), now()) " +
                "ON CONFLICT (video_id, language) DO UPDATE SET " +
                "is_auto = excluded.is_auto, client_name = excluded.client_name, cue_count = excluded.cue_count, " +
                "cues_json = excluded.cues_json, created_at = now(), last_used_at = now() " +
                "RETURNING id",
                new { videoId, language, isAuto, clientName, cueCount = cues.Count, cuesJson = CaptionCueJson.Serialize(cues) });
        }, (long?)null);
    }

    /// <summary>last_used_at 設為現在；找不到或連線失敗時不做事。</summary>
    public void TouchCaption(long id)
    {
        ExecuteWithFallback<object?>(() =>
        {
            using var connection = OpenConnection();
            connection.Execute("UPDATE youtube_captions SET last_used_at = now() WHERE id = @id", new { id });
            return null;
        }, null);
    }

    /// <summary>該字幕前處理過的句子，依 start_ms 排序；連線或查詢失敗時回傳空清單。</summary>
    public IReadOnlyList<CaptionSentence> GetSentences(long captionId)
    {
        try
        {
            return ExecuteWithFallback<IReadOnlyList<CaptionSentence>>(() =>
            {
                using var connection = OpenConnection();
                return connection.Query<CaptionSentence>(@"
                    SELECT start_ms AS StartMs,
                           end_ms AS EndMs,
                           raw_text AS RawText,
                           text AS Text,
                           model AS Model
                    FROM caption_sentences
                    WHERE caption_id = @captionId
                    ORDER BY start_ms", new { captionId }).ToList();
            }, []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取 caption_sentences 失敗：caption {CaptionId}", captionId);
            return [];
        }
    }

    /// <summary>同一個交易內寫入句子；(caption_id, start_ms) 已存在時覆寫；失敗只寫 Warning log。</summary>
    public void SaveSentences(long captionId, IReadOnlyList<CaptionSentence> sentences)
    {
        try
        {
            ExecuteWithFallback<object?>(() =>
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                try
                {
                    connection.Execute(@"
                        INSERT INTO caption_sentences (caption_id, start_ms, end_ms, raw_text, text, model, created_at)
                        VALUES (@captionId, @StartMs, @EndMs, @RawText, @Text, @Model, now())
                        ON CONFLICT (caption_id, start_ms) DO UPDATE SET
                            end_ms = excluded.end_ms,
                            raw_text = excluded.raw_text,
                            text = excluded.text,
                            model = excluded.model",
                        sentences.Select(s => new { captionId, s.StartMs, s.EndMs, s.RawText, s.Text, s.Model }),
                        transaction);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
                return null;
            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寫入 caption_sentences 失敗：caption {CaptionId}", captionId);
        }
    }

    // ===== IAuditStore（都不得往外擲例外：失敗只寫 Warning log，回溯紀錄失敗不能影響翻譯） =====

    /// <summary>prompt_templates 的一列：先對到類別再轉成 record（positional record 的建構子要求欄位型別完全相符，timestamptz 讀出來是 DateTime）。</summary>
    private sealed class PromptTemplateRow
    {
        public long Id { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string? AppVersion { get; set; }
        public DateTimeOffset CreatedAt { get; set; }

        public PromptTemplate ToRecord() => new(Id, Kind, Content, ContentHash, Origin, AppVersion, CreatedAt);
    }

    /// <summary>(kind, content_hash) 已存在時回傳既有 Id（origin、app_version 不改）；不存在時新增。失敗回傳 null。</summary>
    public long? EnsurePromptTemplate(string kind, string content, string origin, string? appVersion)
    {
        try
        {
            return ExecuteWithFallback(() =>
            {
                using var connection = OpenConnection();
                var hash = AuditHash.Of(content);
                var existing = connection.QueryFirstOrDefault<long?>(
                    "SELECT id FROM prompt_templates WHERE kind = @kind AND content_hash = @hash", new { kind, hash });
                if (existing is { } id)
                {
                    return id;
                }

                return connection.ExecuteScalar<long>(
                    "INSERT INTO prompt_templates (kind, content, content_hash, origin, app_version, created_at) " +
                    "VALUES (@kind, @content, @hash, @origin, @appVersion, now()) RETURNING id",
                    new { kind, content, hash, origin, appVersion });
            }, (long?)null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寫入 prompt_templates 失敗：kind {Kind}", kind);
            return null;
        }
    }

    /// <summary>指定種類的全部版本，依 Id 由大到小。失敗回傳空清單。</summary>
    public IReadOnlyList<PromptTemplate> GetPromptTemplates(string kind)
    {
        try
        {
            return ExecuteWithFallback<IReadOnlyList<PromptTemplate>>(() =>
            {
                using var connection = OpenConnection();
                return connection.Query<PromptTemplateRow>(@"
                    SELECT id AS Id,
                           kind AS Kind,
                           content AS Content,
                           content_hash AS ContentHash,
                           origin AS Origin,
                           app_version AS AppVersion,
                           created_at AS CreatedAt
                    FROM prompt_templates
                    WHERE kind = @kind
                    ORDER BY id DESC", new { kind })
                    .Select(row => row.ToRecord())
                    .ToList();
            }, []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取 prompt_templates 失敗：kind {Kind}", kind);
            return [];
        }
    }

    /// <summary>依 Id 取一個版本；不存在或失敗回傳 null。</summary>
    public PromptTemplate? GetPromptTemplate(long id)
    {
        try
        {
            return ExecuteWithFallback(() =>
            {
                using var connection = OpenConnection();
                return connection.QueryFirstOrDefault<PromptTemplateRow>(@"
                    SELECT id AS Id,
                           kind AS Kind,
                           content AS Content,
                           content_hash AS ContentHash,
                           origin AS Origin,
                           app_version AS AppVersion,
                           created_at AS CreatedAt
                    FROM prompt_templates
                    WHERE id = @id", new { id })?.ToRecord();
            }, (PromptTemplate?)null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取 prompt_templates 失敗：id {Id}", id);
            return null;
        }
    }

    /// <summary>內容雜湊已存在時把 last_used_at 設成現在並回傳既有 Id；不存在時新增（template_id 照傳入值，不參與去重）。失敗回傳 null。</summary>
    public long? EnsurePromptSnapshot(long? templateId, string content)
    {
        try
        {
            return ExecuteWithFallback(() =>
            {
                using var connection = OpenConnection();
                var hash = AuditHash.Of(content);
                var existing = connection.QueryFirstOrDefault<long?>(
                    "SELECT id FROM prompt_snapshots WHERE content_hash = @hash", new { hash });
                if (existing is { } id)
                {
                    connection.Execute(
                        "UPDATE prompt_snapshots SET last_used_at = now() WHERE id = @id", new { id });
                    return id;
                }

                return connection.ExecuteScalar<long>(
                    "INSERT INTO prompt_snapshots (template_id, content, content_hash, created_at, last_used_at) " +
                    "VALUES (@templateId, @content, @hash, now(), now()) RETURNING id",
                    new { templateId, content, hash });
            }, (long?)null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寫入 prompt_snapshots 失敗");
            return null;
        }
    }

    /// <summary>同 EnsurePromptSnapshot，對象是 settings_snapshots（content 是 JSON 字串）。失敗回傳 null。</summary>
    public long? EnsureSettingsSnapshot(string content)
    {
        try
        {
            return ExecuteWithFallback(() =>
            {
                using var connection = OpenConnection();
                var hash = AuditHash.Of(content);
                var existing = connection.QueryFirstOrDefault<long?>(
                    "SELECT id FROM settings_snapshots WHERE content_hash = @hash", new { hash });
                if (existing is { } id)
                {
                    connection.Execute(
                        "UPDATE settings_snapshots SET last_used_at = now() WHERE id = @id", new { id });
                    return id;
                }

                return connection.ExecuteScalar<long>(
                    "INSERT INTO settings_snapshots (content, content_hash, created_at, last_used_at) " +
                    "VALUES (@content, @hash, now(), now()) RETURNING id",
                    new { content, hash });
            }, (long?)null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寫入 settings_snapshots 失敗");
            return null;
        }
    }

    /// <summary>新增一筆模型呼叫紀錄，回傳 Id；失敗回傳 null。</summary>
    public long? InsertLlmCall(LlmCallRecord record)
    {
        try
        {
            return ExecuteWithFallback(() =>
            {
                using var connection = OpenConnection();
                return connection.ExecuteScalar<long>(
                    "INSERT INTO llm_calls (stream_id, job_id, purpose, template_id, system_snapshot_id, settings_snapshot_id, " +
                    "model, user_prompt, history, response_raw, keyword_term_ids, fix_ids, prompt_tokens, completion_tokens, latency_ms, outcome, created_at) " +
                    "VALUES (@StreamId, @JobId, @Purpose, @TemplateId, @SystemSnapshotId, @SettingsSnapshotId, " +
                    "@Model, @UserPrompt, @History, @ResponseRaw, @KeywordTermIds, @FixIds, @PromptTokens, @CompletionTokens, @LatencyMs, @Outcome, now()) " +
                    "RETURNING id",
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
                        record.Outcome
                    });
            }, (long?)null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寫入 llm_calls 失敗：purpose {Purpose}、job {JobId}", record.Purpose, record.JobId);
            return null;
        }
    }

    /// <summary>過期清除：先刪舊的 llm_calls，再刪沒被任何 llm_calls 參照而且 last_used_at 過期的兩種快照；
    /// 每次最多刪 5000 列、重複到刪完為止。失敗時回傳已刪除的筆數。</summary>
    public AuditPurgeResult PurgeAudit(DateTimeOffset cutoffUtc)
    {
        var llmCalls = PurgeInBatches(
            "DELETE FROM llm_calls WHERE id IN (SELECT id FROM llm_calls WHERE created_at < @cutoff LIMIT 5000)", cutoffUtc);
        var promptSnapshots = PurgeInBatches(
            "DELETE FROM prompt_snapshots WHERE id IN (SELECT id FROM prompt_snapshots WHERE last_used_at < @cutoff " +
            "AND id NOT IN (SELECT system_snapshot_id FROM llm_calls WHERE system_snapshot_id IS NOT NULL) LIMIT 5000)", cutoffUtc);
        var settingsSnapshots = PurgeInBatches(
            "DELETE FROM settings_snapshots WHERE id IN (SELECT id FROM settings_snapshots WHERE last_used_at < @cutoff " +
            "AND id NOT IN (SELECT settings_snapshot_id FROM llm_calls WHERE settings_snapshot_id IS NOT NULL) LIMIT 5000)", cutoffUtc);
        return new AuditPurgeResult(llmCalls, promptSnapshots, settingsSnapshots);
    }

    /// <summary>分批刪除（每批最多 5000 列，刪到沒有符合條件的為止），回傳總刪除筆數；
    /// 連線失敗走斷線視窗（回傳 0），其他錯誤只寫 Warning log 並回傳已刪除的筆數。</summary>
    private int PurgeInBatches(string deleteSql, DateTimeOffset cutoffUtc)
    {
        var total = 0;
        try
        {
            while (true)
            {
                var deleted = ExecuteWithFallback(() =>
                {
                    using var connection = OpenConnection();
                    return connection.Execute(deleteSql, new { cutoff = cutoffUtc });
                }, 0);
                total += deleted;
                if (deleted < 5000)
                {
                    return total;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "回溯紀錄清除中斷（已刪除 {Deleted} 列）", total);
            return total;
        }
    }

    /// <summary>開啟連線並確保資料表已建立；失敗時拋出例外，由 <see cref="ExecuteWithFallback{T}"/> 處理斷線狀態。</summary>
    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            connection.Open();
            EnsureTables(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>第一次成功開啟連線時執行 DDL（建 schema 與全部資料表），以 lock 確保只跑一次；失敗視為連線失敗，下次再試。</summary>
    private void EnsureTables(NpgsqlConnection connection)
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;

            // 不加引號：與資料表參照、Search Path 一樣由 PostgreSQL 轉成小寫，大小寫混用時才會指向同一個 schema
            connection.Execute($"CREATE SCHEMA IF NOT EXISTS {_schema}");

            // 建表 SQL 集中在嵌入資源 Sql/postgres-schema.sql：表名不帶 schema，靠連線的 Search Path 指向設定的 schema
            connection.Execute(SchemaScripts.Postgres);

            _initialized = true;
        }
    }

    /// <summary>執行資料庫操作：斷線期間直接回傳替代值；連線或查詢失敗時進入斷線狀態並回傳替代值；成功時處理恢復記錄。</summary>
    private T ExecuteWithFallback<T>(Func<T> action, T fallback)
    {
        if (!IsAvailable) return fallback;

        try
        {
            var result = action();
            MarkAvailable();
            return result;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            MarkUnavailable(ex);
            return fallback;
        }
    }

    /// <summary>四個資料庫操作都視為可失敗的連線問題（其餘例外照常往外拋）。</summary>
    private static bool IsConnectionFailure(Exception ex) =>
        ex is NpgsqlException or SocketException or TimeoutException or InvalidOperationException;

    private void MarkUnavailable(Exception ex)
    {
        Volatile.Write(ref _unavailableUntilTicks, (_timeProvider.GetUtcNow() + UnavailableWindow).UtcTicks);
        if (_wasUnavailable) return;

        _wasUnavailable = true;
        var message = string.IsNullOrEmpty(_settings.Password)
            ? ex.Message
            : ex.Message.Replace(_settings.Password, "***");
        _logger.LogWarning("PostgreSQL 連線失敗，{Seconds} 秒內不再嘗試：{Message}", UnavailableWindow.TotalSeconds, message);
    }

    private void MarkAvailable()
    {
        Volatile.Write(ref _unavailableUntilTicks, 0);
        if (!_wasUnavailable) return;

        _wasUnavailable = false;
        _logger.LogInformation("PostgreSQL 已恢復連線");
    }
}
