using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class DatabaseConnectionTesterTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"tg-tester-{Guid.NewGuid():N}.db");

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public async Task Sqlite_FileMissing_ReturnsSuccessAndDoesNotCreateFile()
    {
        var path = TempPath();
        try
        {
            var result = await DatabaseConnectionTester.TestAsync(new DatabaseSettings { SqliteFilePath = path }, TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Contains("啟動時會自動建立", result.Message);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Sqlite_WithTwoEntries_MessageContainsCount()
    {
        using var db = new TempDatabase();
        db.Service.SaveCache("a", "甲");
        db.Service.SaveCache("b", "乙");
        var path = db.Service.Description["SQLite：".Length..];

        var result = await DatabaseConnectionTester.TestAsync(new DatabaseSettings { SqliteFilePath = path }, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("2 筆", result.Message);
    }

    [Fact]
    public async Task Sqlite_EmptyFile_MessageSaysTablesNotCreated()
    {
        var path = TempPath();
        try
        {
            File.WriteAllBytes(path, []);

            var result = await DatabaseConnectionTester.TestAsync(new DatabaseSettings { SqliteFilePath = path }, TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Contains("資料表尚未建立", result.Message);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Postgres_EmptyHost_FailsWithPrompt()
    {
        var result = await DatabaseConnectionTester.TestAsync(new DatabaseSettings { UsePostgres = true }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("請先輸入主機", result.Message);
    }

    [Fact]
    public async Task Postgres_RefusedConnection_FailsWithoutLeakingPassword()
    {
        var settings = new DatabaseSettings
        {
            UsePostgres = true,
            Host = "127.0.0.1",
            Port = 1,
            Database = "testdb",
            UserName = "tester",
            Password = "secret-pw",
        };

        var result = await DatabaseConnectionTester.TestAsync(settings, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.DoesNotContain("secret-pw", result.Message);
    }
}
