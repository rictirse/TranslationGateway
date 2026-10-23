using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>連線測試結果：Success 為是否能連線，Message 為給使用者看的訊息。</summary>
public sealed record ConnectionTestResult(bool Success, string Message);

/// <summary>依設定測試對應資料庫後端的連線；不修改任何資料、不建立資料表。</summary>
public static class DatabaseConnectionTester
{
    public static async Task<ConnectionTestResult> TestAsync(DatabaseSettings settings, CancellationToken cancellationToken = default)
    {
        return settings.UsePostgres
            ? await TestPostgresAsync(settings, cancellationToken).ConfigureAwait(false)
            : await TestSqliteAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ConnectionTestResult> TestSqliteAsync(DatabaseSettings settings, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(settings.SqliteFilePath)
            ? DatabaseSettings.DefaultSqlitePath
            : settings.SqliteFilePath;

        if (!File.Exists(path))
        {
            return new ConnectionTestResult(true, $"檔案不存在，啟動時會自動建立：{path}");
        }

        try
        {
            await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var count = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition("SELECT COUNT(*) FROM TranslationCache", cancellationToken: cancellationToken)).ConfigureAwait(false);
            return new ConnectionTestResult(true, $"連線成功，翻譯快取 {count} 筆");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such table"))
        {
            return new ConnectionTestResult(true, "連線成功，資料表尚未建立（啟動時會自動建立）");
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, $"連線失敗：{ex.Message}");
        }
    }

    private static async Task<ConnectionTestResult> TestPostgresAsync(DatabaseSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            return new ConnectionTestResult(false, "請先輸入主機");
        }

        if (!PostgresTranslationStore.IsValidSchema(NormalizeSchema(settings.Schema)))
        {
            return new ConnectionTestResult(false, $"Schema 名稱不合法：{settings.Schema}");
        }

        // 唯讀交易：測試連線不得修改任何資料或建立資料表
        var builder = new NpgsqlConnectionStringBuilder(
            PostgresTranslationStore.BuildConnectionString(settings, timeoutSeconds: 5))
        {
            Options = "-c default_transaction_read_only=on",
        };

        try
        {
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var count = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition("SELECT COUNT(*) FROM translation_cache", cancellationToken: cancellationToken)).ConfigureAwait(false);
            return new ConnectionTestResult(true, $"連線成功，翻譯快取 {count} 筆");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            return new ConnectionTestResult(true, "連線成功，資料表尚未建立（啟動時會自動建立）");
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, $"連線失敗：{MaskPassword(ex.Message, settings.Password)}");
        }
    }

    /// <summary>Schema 修剪前後空白；修剪後為空時視為 public（與 PostgresTranslationStore 相同規則）。</summary>
    private static string NormalizeSchema(string? schema)
    {
        var trimmed = (schema ?? string.Empty).Trim();
        return trimmed.Length == 0 ? "public" : trimmed;
    }

    /// <summary>訊息中出現的密碼一律換成 ***。</summary>
    private static string MaskPassword(string message, string password) =>
        string.IsNullOrEmpty(password) ? message : message.Replace(password, "***");
}
