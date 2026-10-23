using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class TranslationStoreFactoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tg-factory-{Guid.NewGuid():N}.db");

    /// <summary>SQLite 案例一律指定暫存檔，不得寫到 LocalApplicationData。</summary>
    private DatabaseSettings SqliteSettings() => new() { SqliteFilePath = _path };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Create_DefaultSettings_ReturnsSqliteStore()
    {
        var store = TranslationStoreFactory.Create(SqliteSettings(), NullLoggerFactory.Instance);

        Assert.IsType<SqliteTranslationStore>(store);
    }

    [Fact]
    public void Create_UsePostgresWithoutHost_FallsBackToSqlite()
    {
        var settings = SqliteSettings();
        settings.UsePostgres = true;
        settings.Host = " ";

        var store = TranslationStoreFactory.Create(settings, NullLoggerFactory.Instance);

        Assert.IsType<SqliteTranslationStore>(store);
    }

    [Fact]
    public void Create_UsePostgresWithHost_ReturnsPostgresStoreWithoutConnecting()
    {
        // 指到必定拒絕連線的 127.0.0.1:1：Create 不得因此丟例外或卡住
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = "127.0.0.1",
            Port = 1,
            Database = "testdb",
            UserName = "tester",
            Password = "secret-pw",
            Schema = "public",
        };

        var store = TranslationStoreFactory.Create(settings, NullLoggerFactory.Instance);

        Assert.IsType<PostgresTranslationStore>(store);
    }

    [Fact]
    public void Create_UsePostgresWithInvalidSchema_FallsBackToSqlite()
    {
        var settings = SqliteSettings();
        settings.UsePostgres = true;
        settings.Host = "192.168.5.4";
        settings.Schema = "a-b";

        var store = TranslationStoreFactory.Create(settings, NullLoggerFactory.Instance);

        Assert.IsType<SqliteTranslationStore>(store);
    }
}
