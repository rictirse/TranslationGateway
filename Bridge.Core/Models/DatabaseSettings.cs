namespace Translation.Bridge.Core.Models;

public class DatabaseSettings
{
    /// <summary>true 時使用 PostgreSQL；false（預設）使用 SQLite。</summary>
    public bool UsePostgres { get; set; }
    /// <summary>SQLite 檔案路徑；空字串時用 DefaultSqlitePath。</summary>
    public string SqliteFilePath { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    /// <summary>明碼存在 settings.json（使用者已同意）。</summary>
    public string Password { get; set; } = string.Empty;
    public string Schema { get; set; } = "public";

    /// <summary>%LOCALAPPDATA%\Translation.Gateway\translation_cache.db（與現行位置相同）。</summary>
    public static string DefaultSqlitePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Translation.Gateway",
        "translation_cache.db");
}
