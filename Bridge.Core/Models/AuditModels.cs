namespace Translation.Bridge.Core.Models;

/// <summary>回溯紀錄的設定。</summary>
public class AuditSettings
{
    /// <summary>保存完整紀錄：開啟時每次呼叫模型都存 user prompt、對話歷史、模型原始回覆等全文；關閉時只記輕量的 Id。預設關閉。</summary>
    public bool FullRecordEnabled { get; set; }

    /// <summary>紀錄保存天數：llm_calls 與快照超過這個天數就在程式啟動時刪除；0 表示永不刪除。</summary>
    public int RetentionDays { get; set; } = 30;
}

/// <summary>prompt 的種類（prompt_templates.kind）。</summary>
public static class PromptTemplateKinds
{
    public const string LiveSystem = "LiveSystem";
    public const string VodBatch = "VodBatch";
    public const string MiningExtract = "MiningExtract";
    public const string MiningCompose = "MiningCompose";
    public const string Mishearing = "Mishearing";
    public const string TermDiscovery = "TermDiscovery";
    public const string TermImport = "TermImport";
    public const string CaptionPreprocess = "CaptionPreprocess";
}

/// <summary>prompt 版本的來源。</summary>
public static class PromptTemplateOrigins
{
    public const string Default = "Default";
    public const string User = "User";
}

/// <summary>模型呼叫的用途（llm_calls.purpose）。</summary>
public static class LlmCallPurposes
{
    public const string LiveTranslation = "LiveTranslation";
    public const string Retranslate = "Retranslate";
    public const string VodBatch = "VodBatch";
    public const string CaptionPreprocess = "CaptionPreprocess";
    public const string Mining = "Mining";
    public const string Mishearing = "Mishearing";
    public const string TermDiscovery = "TermDiscovery";
    public const string TermImport = "TermImport";
    public const string TitleTranslation = "TitleTranslation";
    public const string ChapterTitleTranslation = "ChapterTitleTranslation";
    public const string Other = "Other";
}

/// <summary>模型呼叫的結果（llm_calls.outcome）。</summary>
public static class LlmCallOutcomes
{
    public const string Ok = "Ok";
    public const string Timeout = "Timeout";
    public const string Error = "Error";
}

/// <summary>prompt_templates 的一列。</summary>
public sealed record PromptTemplate(long Id, string Kind, string Content, string ContentHash, string Origin,
    string? AppVersion, DateTimeOffset CreatedAt);

/// <summary>要寫入 llm_calls 的一筆紀錄；完整紀錄才填的欄位在輕量模式下是 null。</summary>
public sealed class LlmCallRecord
{
    public long? StreamId { get; set; }
    public string? JobId { get; set; }
    public string Purpose { get; set; } = LlmCallPurposes.Other;
    public long? TemplateId { get; set; }
    public long? SystemSnapshotId { get; set; }
    public long? SettingsSnapshotId { get; set; }
    public string? Model { get; set; }
    public string? UserPrompt { get; set; }
    public string? History { get; set; }
    public string? ResponseRaw { get; set; }
    public string? KeywordTermIds { get; set; }
    public string? FixIds { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? LatencyMs { get; set; }
    public string Outcome { get; set; } = LlmCallOutcomes.Ok;
}

/// <summary>一次過期清除的結果。</summary>
public sealed record AuditPurgeResult(int LlmCalls, int PromptSnapshots, int SettingsSnapshots);
