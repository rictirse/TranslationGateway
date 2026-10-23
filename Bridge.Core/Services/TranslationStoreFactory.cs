using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>依設定建立翻譯快取後端（SQLite 或 PostgreSQL，同時只用一種，預設 SQLite）。</summary>
public static class TranslationStoreFactory
{
    public static ITranslationStore Create(DatabaseSettings settings, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(TranslationStoreFactory).FullName!);

        var store = CreateStore(settings, loggerFactory, logger);

        logger.LogInformation("資料庫：{Description}", store.Description);
        return store;
    }

    private static ITranslationStore CreateStore(DatabaseSettings settings, ILoggerFactory loggerFactory, ILogger logger)
    {
        if (settings.UsePostgres && !string.IsNullOrWhiteSpace(settings.Host))
        {
            try
            {
                return new PostgresTranslationStore(
                    settings, loggerFactory.CreateLogger<PostgresTranslationStore>());
            }
            catch (ArgumentException ex)
            {
                logger.LogError(ex, "PostgreSQL 設定不合法（Schema：{Schema}），改用 SQLite", settings.Schema);
                return CreateSqliteStore(settings, loggerFactory);
            }
        }

        if (settings.UsePostgres)
        {
            logger.LogWarning("PostgreSQL 主機未填，改用 SQLite");
        }

        return CreateSqliteStore(settings, loggerFactory);
    }

    private static SqliteTranslationStore CreateSqliteStore(DatabaseSettings settings, ILoggerFactory loggerFactory)
    {
        // 路徑空白時傳 null，由 SqliteTranslationStore 使用預設位置
        var dbFilePath = string.IsNullOrWhiteSpace(settings.SqliteFilePath) ? null : settings.SqliteFilePath;
        return new SqliteTranslationStore(loggerFactory.CreateLogger<SqliteTranslationStore>(), dbFilePath);
    }
}
