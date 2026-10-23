using System.Security.Cryptography;
using System.Text;

namespace Translation.Bridge.Core.Services;

/// <summary>回溯紀錄的內容雜湊：prompt 與設定快照以此去重。</summary>
public static class AuditHash
{
    /// <summary>UTF-8 內容的 SHA-256，回傳小寫 hex（64 字）。</summary>
    public static string Of(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
