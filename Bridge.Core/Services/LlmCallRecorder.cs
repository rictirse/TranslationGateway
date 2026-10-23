using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>一次模型呼叫的紀錄輸入：呼叫端在模型回覆後填好欄位交給 <see cref="LlmCallRecorder.Record"/>。
/// 完整欄位（UserPrompt、History、ResponseRaw、KeywordTermIds、FixIds）只在 Audit.FullRecordEnabled 開啟時才會寫進資料庫。</summary>
public sealed class LlmCallInput
{
    public required string Purpose { get; init; }
    public string? JobId { get; init; }
    public required string SystemPrompt { get; init; }
    /// <summary>已知的模板版本 Id；null 時由 Recorder 依 purpose 與 system 內容判斷。</summary>
    public long? TemplateId { get; init; }
    public string? UserPrompt { get; init; }
    public IReadOnlyList<ChatTurn>? History { get; init; }
    public string? ResponseRaw { get; init; }
    public IReadOnlyCollection<long>? KeywordTermIds { get; init; }
    public IReadOnlyCollection<long>? FixIds { get; init; }
    public string? Model { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public long? LatencyMs { get; init; }
    public string Outcome { get; init; } = LlmCallOutcomes.Ok;
}

/// <summary>模型呼叫紀錄器（DI singleton）：每個呼叫模型的地方把 <see cref="LlmCallInput"/> 丟進來，
/// 同步只做抓 stream id、設定快照與模板 Id 這類記憶體操作，寫資料庫一律由單一背景消費者依序處理——
/// 紀錄不能拖慢翻譯，更不能讓翻譯失敗（Record 絕不擲例外、絕不等 DB）。
///
/// 可測試性的設計：這裡選擇「Recorder 改收 Func 參數」而不是替 Registry 抽介面——
/// PromptTemplateRegistry 不在本次任務的檔案範圍內，因此建構子以 registry 加上三個 optional Func 接收：
/// liveTemplateId／defaultTemplateId（kind → Id）／findTemplateId（內容 → Id），
/// Func 有值時優先使用，否則退回 registry 的對應成員；測試可以直接傳 Func、不經過 Registry。</summary>
public sealed class LlmCallRecorder
{
    private readonly IAuditStore _store;
    private readonly ISettingsService _settings;
    private readonly StreamSession _session;
    private readonly ILogger<LlmCallRecorder> _logger;
    private readonly PromptTemplateRegistry? _registry;
    private readonly Func<long?>? _liveTemplateId;
    private readonly Func<string, long?>? _defaultTemplateId;
    private readonly Func<string, long?>? _findTemplateId;

    private readonly Channel<QueueItem> _channel;
    private readonly Task _processingTask;

    /// <summary>快照 Id 的記憶體快取（內容雜湊 → snapshot Id）；Ensure 失敗（null）不放進，下次再試。</summary>
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, long> _systemSnapshotIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _settingsSnapshotIds = new(StringComparer.Ordinal);

    /// <summary>JSON 輸出設定：屬性名稱固定、不轉義 CJK（DB 裡可以直接讀）。</summary>
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public LlmCallRecorder(
        IAuditStore store,
        ISettingsService settings,
        PromptTemplateRegistry? registry,
        StreamSession session,
        ILogger<LlmCallRecorder> logger,
        Func<long?>? liveTemplateId = null,
        Func<string, long?>? defaultTemplateId = null,
        Func<string, long?>? findTemplateId = null)
    {
        _store = store;
        _settings = settings;
        _registry = registry;
        _session = session;
        _logger = logger;
        _liveTemplateId = liveTemplateId;
        _defaultTemplateId = defaultTemplateId;
        _findTemplateId = findTemplateId;
        _channel = Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions { SingleReader = true });
        _processingTask = Task.Run(ProcessQueueAsync);
    }

    /// <summary>排入一筆模型呼叫紀錄：絕不擲例外、絕不等待 DB。同步只做記憶體操作
    /// （stream id、完整紀錄開關、設定快照 JSON、模板 Id），寫入由背景消費者處理。</summary>
    public void Record(LlmCallInput input)
    {
        try
        {
            var item = new WorkItem(
                input,
                _session.CurrentStreamId,
                _settings.Current.Audit.FullRecordEnabled,
                BuildSettingsSnapshot(input.Purpose),
                input.TemplateId ?? ResolveTemplateIdNow(input.Purpose),
                AuditHash.Of(input.SystemPrompt));
            _channel.Writer.TryWrite(item);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "排入模型呼叫紀錄失敗（purpose：{Purpose}），這筆不記", input.Purpose);
        }
    }

    /// <summary>等佇列裡「呼叫當下已排入」的項目都處理完（測試用）；程式結束時不必等。</summary>
    public async Task FlushAsync()
    {
        var marker = new FlushItem(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _channel.Writer.TryWrite(marker);
        await marker.Completed.Task.ConfigureAwait(false);
    }

    /// <summary>呼叫當下就要決定的模板版本 Id：直播翻譯／重翻用 LiveTemplateId（使用者可能隨後改選版本）、
    /// 錄影批次用 VodBatch 預設版；其他（知識庫工作）回 null，改在背景以 system 全文比對（FindTemplateId 可能查 DB）。</summary>
    private long? ResolveTemplateIdNow(string purpose) => purpose switch
    {
        LlmCallPurposes.LiveTranslation or LlmCallPurposes.Retranslate => LiveTemplateId(),
        LlmCallPurposes.VodBatch => DefaultTemplateId(PromptTemplateKinds.VodBatch),
        _ => null,
    };

    /// <summary>背景補判模板版本 Id：呼叫端沒給、而且是知識庫工作時，以 system 全文比對。</summary>
    private long? ResolveTemplateIdLater(WorkItem item) =>
        item.TemplateId
        ?? (item.Input.Purpose is LlmCallPurposes.LiveTranslation or LlmCallPurposes.Retranslate or LlmCallPurposes.VodBatch
            ? null
            : FindTemplateId(item.Input.SystemPrompt));

    private long? LiveTemplateId() => _liveTemplateId?.Invoke() ?? _registry?.LiveTemplateId;

    private long? DefaultTemplateId(string kind) => _defaultTemplateId?.Invoke(kind) ?? _registry?.DefaultTemplateId(kind);

    private long? FindTemplateId(string content) => _findTemplateId?.Invoke(content) ?? _registry?.FindTemplateId(content);

    /// <summary>背景處理迴圈：依排入順序確保快照、寫入 llm_calls；任何一步失敗寫 Warning 後丟掉該筆、繼續下一筆。</summary>
    private async Task ProcessQueueAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item is FlushItem flush)
            {
                flush.Completed.TrySetResult();
                continue;
            }

            try
            {
                Process((WorkItem)item);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "模型呼叫紀錄寫入失敗（purpose：{Purpose}），這筆已丟棄", ((WorkItem)item).Input.Purpose);
            }
        }
    }

    private void Process(WorkItem item)
    {
        var templateId = ResolveTemplateIdLater(item);
        var systemSnapshotId = EnsureSystemSnapshot(templateId, item.Input.SystemPrompt, item.SystemHash);
        if (systemSnapshotId is null)
        {
            _logger.LogWarning("系統 prompt 快照寫入失敗（purpose：{Purpose}），這筆已丟棄", item.Input.Purpose);
            return;
        }

        var settingsSnapshotId = EnsureSettingsSnapshot(item.SettingsJson, AuditHash.Of(item.SettingsJson));
        if (settingsSnapshotId is null)
        {
            _logger.LogWarning("設定快照寫入失敗（purpose：{Purpose}），這筆已丟棄", item.Input.Purpose);
            return;
        }

        var input = item.Input;
        var record = new LlmCallRecord
        {
            StreamId = item.StreamId,
            JobId = input.JobId,
            Purpose = input.Purpose,
            TemplateId = templateId,
            SystemSnapshotId = systemSnapshotId,
            SettingsSnapshotId = settingsSnapshotId,
            Model = input.Model,
            PromptTokens = input.PromptTokens,
            CompletionTokens = input.CompletionTokens,
            LatencyMs = input.LatencyMs is { } ms ? (int)Math.Min(ms, int.MaxValue) : null,
            Outcome = input.Outcome,
        };

        // 完整欄位只在完整紀錄開啟時填；關閉時全部是 null（只記輕量 Id）
        if (item.FullRecord)
        {
            record.UserPrompt = input.UserPrompt;
            record.History = input.History is null ? null : SerializeHistory(input.History);
            record.ResponseRaw = input.ResponseRaw;
            record.KeywordTermIds = SerializeIds(input.KeywordTermIds);
            record.FixIds = SerializeIds(input.FixIds);
        }

        if (_store.InsertLlmCall(record) is null)
        {
            _logger.LogWarning("llm_calls 寫入失敗（purpose：{Purpose}），這筆已丟棄", input.Purpose);
        }
    }

    /// <summary>系統 prompt 快照：先用雜湊查記憶體快取，沒有才呼叫 store（成功後放進快取；null 不放，下次再試）。</summary>
    private long? EnsureSystemSnapshot(long? templateId, string systemPrompt, string hash)
    {
        lock (_cacheLock)
        {
            if (_systemSnapshotIds.TryGetValue(hash, out var cached))
            {
                return cached;
            }
        }

        var id = _store.EnsurePromptSnapshot(templateId, systemPrompt);
        if (id is not null)
        {
            lock (_cacheLock)
            {
                _systemSnapshotIds[hash] = id.Value;
            }
        }
        return id;
    }

    /// <summary>設定快照：與系統快照同樣以雜湊快取去重。</summary>
    private long? EnsureSettingsSnapshot(string json, string hash)
    {
        lock (_cacheLock)
        {
            if (_settingsSnapshotIds.TryGetValue(hash, out var cached))
            {
                return cached;
            }
        }

        var id = _store.EnsureSettingsSnapshot(json);
        if (id is not null)
        {
            lock (_cacheLock)
            {
                _settingsSnapshotIds[hash] = id.Value;
            }
        }
        return id;
    }

    /// <summary>依 purpose 組設定快照的 JSON：直播翻譯／重翻／錄影批次記翻譯模型的設定與對話設定，
    /// 其他（知識庫工作）記 Lorebook 的連線設定。屬性名稱與順序固定，不含 ApiKey 與任何密碼。</summary>
    private string BuildSettingsSnapshot(string purpose)
    {
        var s = _settings.Current;
        return purpose is LlmCallPurposes.LiveTranslation or LlmCallPurposes.Retranslate or LlmCallPurposes.VodBatch
            ? BuildTranslationSnapshot(s)
            : BuildLorebookSnapshot(s.Lorebook);
    }

    /// <summary>直播／重翻／錄影批次的設定快照：翻譯模型的連線與推理設定、對話與重複字幕設定、直通與兩組修正規則。</summary>
    private static string BuildTranslationSnapshot(AppSettings s)
    {
        var t = s.Translation;
        return WriteJson(writer =>
        {
            WriteModelSettings(writer, t);
            writer.WriteBoolean("use_conversation_mode", t.UseConversationMode);
            writer.WriteNumber("conversation_max_turns", t.ConversationMaxTurns);
            writer.WriteNumber("conversation_keep_turns", t.ConversationKeepTurns);
            writer.WriteNumber("conversation_idle_reset_seconds", t.ConversationIdleResetSeconds);
            writer.WriteNumber("revision_window_seconds", t.RevisionWindowSeconds);
            writer.WriteBoolean("through_pass", s.ThroughPass);
            writer.WriteString("source_replacement_rules", s.SourceReplacementRules);
            writer.WriteString("replacement_rules", s.ReplacementRules);
        });
    }

    /// <summary>知識庫工作的設定快照：Lorebook 模型的連線與推理設定。</summary>
    private static string BuildLorebookSnapshot(TranslationSettings lorebook) =>
        WriteJson(writer => WriteModelSettings(writer, lorebook));

    /// <summary>模型連線與推理設定（不含 ApiKey）：兩種快照共用的欄位、順序固定。</summary>
    private static void WriteModelSettings(Utf8JsonWriter writer, TranslationSettings t)
    {
        writer.WriteString("api_host", t.ApiHost);
        writer.WriteString("model", t.Model);
        writer.WriteNumber("temperature", t.Temperature);
        if (t.TopP is { } topP)
        {
            writer.WriteNumber("top_p", topP);
        }
        else
        {
            writer.WriteNull("top_p");
        }
        writer.WriteString("reasoning_effort", t.ReasoningEffort);
        writer.WriteNumber("reasoning_budget_tokens", t.ReasoningBudgetTokens);
        writer.WriteNumber("timeout_ms", t.TimeoutMs);
    }

    /// <summary>對話歷史存成 JSON 陣列：[{"user":"…","assistant":"…"}]。</summary>
    private static string SerializeHistory(IReadOnlyList<ChatTurn> history)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, JsonOptions))
        {
            writer.WriteStartArray();
            foreach (var turn in history)
            {
                writer.WriteStartObject();
                writer.WriteString("user", turn.User);
                writer.WriteString("assistant", turn.Assistant);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Id 清單存成 JSON 數字陣列；null（完整紀錄關閉）由呼叫端處理，空集合存 []。</summary>
    private static string? SerializeIds(IReadOnlyCollection<long>? ids) =>
        ids is null ? null : JsonSerializer.Serialize(ids);

    /// <summary>以 Utf8JsonWriter 寫出 JSON 物件字串（屬性順序＝寫入順序，欄位名稱固定）。</summary>
    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, JsonOptions))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>佇列項目：一筆紀錄或 Flush 標記。</summary>
    private abstract record QueueItem;

    private sealed record WorkItem(
        LlmCallInput Input,
        long? StreamId,
        bool FullRecord,
        string SettingsJson,
        long? TemplateId,
        string SystemHash) : QueueItem;

    private sealed record FlushItem(TaskCompletionSource Completed) : QueueItem;
}
