using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

/// <summary>回溯紀錄（prompt 版本、快照、模型呼叫紀錄）的讀寫與過期清除（與 ITranslationStore 同一個資料庫後端一起實作）。
/// 所有方法都不得往外擲例外：失敗時寫 Warning log，回傳 null、空清單或 0，回溯紀錄失敗不能影響翻譯。</summary>
public interface IAuditStore
{
    /// <summary>(kind, content 的雜湊) 已存在時回傳既有 Id（origin、app_version 不改）；不存在時新增。失敗回傳 null。</summary>
    long? EnsurePromptTemplate(string kind, string content, string origin, string? appVersion);

    /// <summary>指定種類的全部版本，依 Id 由大到小。失敗回傳空清單。</summary>
    IReadOnlyList<PromptTemplate> GetPromptTemplates(string kind);

    /// <summary>依 Id 取一個版本；不存在或失敗回傳 null。</summary>
    PromptTemplate? GetPromptTemplate(long id);

    /// <summary>內容雜湊已存在時把 last_used_at 設成現在並回傳既有 Id；不存在時新增（template_id 照傳入值）。失敗回傳 null。</summary>
    long? EnsurePromptSnapshot(long? templateId, string content);

    /// <summary>同 EnsurePromptSnapshot，對象是 settings_snapshots（content 是 JSON 字串）。</summary>
    long? EnsureSettingsSnapshot(string content);

    /// <summary>新增一筆模型呼叫紀錄，回傳 Id；失敗回傳 null。</summary>
    long? InsertLlmCall(LlmCallRecord record);

    /// <summary>過期清除：(1) 刪除 created_at 早於 cutoffUtc 的 llm_calls；
    /// (2) 刪除沒有任何 llm_calls 參照、而且 last_used_at 早於 cutoffUtc 的 prompt_snapshots 與 settings_snapshots。
    /// 每次最多刪 5000 列、重複到刪完為止。失敗時回傳已刪除的筆數。</summary>
    AuditPurgeResult PurgeAudit(DateTimeOffset cutoffUtc);
}
