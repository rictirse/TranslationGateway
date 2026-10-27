using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

internal sealed class FakeOpenAiClient(string reply = "这是翻译", TimeSpan? delay = null, bool truncated = false, bool wrapCancellation = false) : IOpenAiClient
{
    public int CallCount { get; private set; }
    public int ChatCallCount { get; private set; }
    public int WarmUpCount { get; private set; }
    public string? LastUserText { get; private set; }
    public string? LastSystemText { get; private set; }
    public IReadOnlyList<ChatTurn>? LastHistory { get; private set; }
    public int? LastMaxOutputTokens { get; private set; }

    /// <summary>非空時每次呼叫模型依序 dequeue 一個作為回覆；空時使用建構子的 reply。</summary>
    public Queue<string> Replies { get; } = new();

    /// <summary>每次 SendChatAsync 帶入的 history 快照，依呼叫順序排列。</summary>
    public List<IReadOnlyList<ChatTurn>> Histories { get; } = new();

    public async Task<ChatResult> SendAsync(string userText, string systemText, CancellationToken cancellationToken, int? maxOutputTokens = null)
    {
        CallCount++;
        LastUserText = userText;
        LastSystemText = systemText;
        LastMaxOutputTokens = maxOutputTokens;
        return await RespondAsync(cancellationToken);
    }

    public async Task<ChatResult> SendChatAsync(string systemText, IReadOnlyList<ChatTurn> history, string userText, CancellationToken cancellationToken, int? maxOutputTokens = null)
    {
        CallCount++;
        ChatCallCount++;
        LastSystemText = systemText;
        LastHistory = history;
        Histories.Add(history);
        LastUserText = userText;
        LastMaxOutputTokens = maxOutputTokens;
        return await RespondAsync(cancellationToken);
    }

    public Task WarmUpAsync(string systemText, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken)
    {
        WarmUpCount++;
        return Task.CompletedTask;
    }

    /// <summary>SendAsync 與 SendChatAsync 共用的延遲、截斷與回覆行為。</summary>
    private async Task<ChatResult> RespondAsync(CancellationToken cancellationToken)
    {
        if (delay is { } d)
        {
            try
            {
                await Task.Delay(d, cancellationToken);
            }
            catch (OperationCanceledException) when (wrapCancellation)
            {
                // 模擬 SDK 重試時把取消例外包成 AggregateException 的行為
                throw new AggregateException(new TaskCanceledException());
            }
        }
        // Replies 非空時依序 dequeue 作為回覆；空時使用建構子指定的 reply
        var currentReply = Replies.Count > 0 ? Replies.Dequeue() : reply;
        return new ChatResult(currentReply, 1, 1, 2, "fake", truncated);
    }
}

internal sealed class FakeSettingsService(AppSettings? settings = null) : ISettingsService
{
    public AppSettings Current { get; } = settings ?? new AppSettings();
    public string SettingsPath => "settings.json";
    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync() => Task.CompletedTask;
}

/// <summary>記下快照與紀錄內容的記憶體版 IAuditStore（回溯紀錄測試用）：
/// ThrowWrites = true 時寫入方法擲例外；NullSnapshots = true 時 Ensure 回傳 null（Recorder 不快取，下次再試）。</summary>
internal sealed class SpyAuditStore : IAuditStore
{
    public bool ThrowWrites { get; set; }
    public bool NullSnapshots { get; set; }

    public List<string> PromptSnapshots { get; } = [];
    public List<string> SettingsSnapshots { get; } = [];
    public List<LlmCallRecord> Calls { get; } = [];

    /// <summary>EnsurePromptSnapshot 被呼叫的次數（含回傳 null 的失敗嘗試）。</summary>
    public int PromptSnapshotAttempts { get; private set; }

    public long? EnsurePromptTemplate(string kind, string content, string origin, string? appVersion)
    {
        if (ThrowWrites) throw new InvalidOperationException("資料庫不可用");
        return 1;
    }

    public IReadOnlyList<PromptTemplate> GetPromptTemplates(string kind) => [];

    public PromptTemplate? GetPromptTemplate(long id) => null;

    public long? EnsurePromptSnapshot(long? templateId, string content)
    {
        PromptSnapshotAttempts++;
        if (ThrowWrites) throw new InvalidOperationException("資料庫不可用");
        if (NullSnapshots) return null;
        PromptSnapshots.Add(content);
        return 100 + PromptSnapshots.Count;
    }

    public long? EnsureSettingsSnapshot(string content)
    {
        if (ThrowWrites) throw new InvalidOperationException("資料庫不可用");
        if (NullSnapshots) return null;
        SettingsSnapshots.Add(content);
        return 200 + SettingsSnapshots.Count;
    }

    public long? InsertLlmCall(LlmCallRecord record)
    {
        if (ThrowWrites) throw new InvalidOperationException("資料庫不可用");
        Calls.Add(record);
        return Calls.Count;
    }

    public AuditPurgeResult PurgeAudit(DateTimeOffset cutoffUtc) => new(0, 0, 0);
}

/// <summary>知識庫模型的假實作：直接回 Result、擲 Exception（含取消），或回連線成功。</summary>
internal sealed class FakeLorebookLlm : ILorebookLlm
{
    public LlmJsonResult? Result { get; set; }
    public Exception? Exception { get; set; }

    public Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (Exception is not null) throw Exception;
        return Task.FromResult(Result ?? new LlmJsonResult("{}", 1, 2, 3, "fake-model"));
    }

    public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult((true, "ok"));
}

/// <summary>每個測試使用獨立的暫存 SQLite 檔，測試結束後刪除。</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tg-test-{Guid.NewGuid():N}.db");

    public SqliteTranslationStore Service { get; }

    public TempDatabase()
    {
        Service = new SqliteTranslationStore(NullLogger<SqliteTranslationStore>.Instance, _path);
    }

    /// <summary>逾時測試的模型請求會在背景跑完（T-2095），測試結束時可能還在寫快取：
    /// Windows 不能刪除使用中的檔案，因此重試約 2 秒，仍刪不掉就留給暫存資料夾（不讓測試失敗）。</summary>
    public void Dispose()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
