using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(ILogger<DatabaseService> logger)
    {
        _logger = logger;

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Translation.Gateway");

        Directory.CreateDirectory(folder);

        var dbFile = Path.Combine(folder, "translation_cache.db");
        _connectionString = $"Data Source={dbFile}";

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS TranslationCache (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SourceText TEXT UNIQUE,
                    TargetText TEXT,
                    IsManual INTEGER DEFAULT 0,
                    LastUsed DATETIME
                )");

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

    public IEnumerable<TranslationCacheItem> GetAllCache()
    {
        using var connection = new SqliteConnection(_connectionString);
        return connection.Query<TranslationCacheItem>(
            "SELECT SourceText, TargetText, IsManual, LastUsed FROM TranslationCache ORDER BY LastUsed DESC LIMIT 500");
    }
}