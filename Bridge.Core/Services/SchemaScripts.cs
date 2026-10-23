using System.Reflection;

namespace Translation.Bridge.Core.Services;

/// <summary>建表 SQL（嵌入資源 Sql/*.sql）的載入器；讀一次後快取。</summary>
public static class SchemaScripts
{
    /// <summary>嵌入資源 Sql/sqlite-schema.sql 的內容。</summary>
    public static string Sqlite { get; } = LoadResourceEndingWith("sqlite-schema.sql");

    /// <summary>嵌入資源 Sql/postgres-schema.sql 的內容。</summary>
    public static string Postgres { get; } = LoadResourceEndingWith("postgres-schema.sql");

    /// <summary>以資源名稱結尾比對找出嵌入資源並以 UTF-8 解碼（不寫死命名空間前綴）。</summary>
    private static string LoadResourceEndingWith(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"找不到嵌入資源 …{suffix}");

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            throw new InvalidOperationException($"無法讀取嵌入資源 {name}");
        }

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
