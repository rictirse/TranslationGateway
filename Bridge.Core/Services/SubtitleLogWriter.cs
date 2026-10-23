using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 字幕紀錄的背景寫入器（DI singleton）：把每一句字幕（含 Whisper 中間版本）排入佇列，
/// 由背景 Task 依序寫入資料庫，不得拖慢即時翻譯；寫入失敗只記警告並繼續。
/// </summary>
public sealed class SubtitleLogWriter : IDisposable
{
    /// <summary>佇列容量（超過時丟最舊的，保護記憶體）。</summary>
    private const int Capacity = 5000;

    /// <summary>同一種錯誤訊息的重複警告間隔。</summary>
    private static readonly TimeSpan WarnInterval = TimeSpan.FromSeconds(60);

    private readonly ISubtitleLogStore _store;
    private readonly StreamSession _session;
    private readonly ILogger<SubtitleLogWriter> _logger;
    private readonly Channel<LogItem> _channel;
    private readonly Task _processingTask;
    private readonly object _warnLock = new();

    private bool _registered;
    private bool _disposed;
    private string? _lastWarnMessage;
    private DateTimeOffset _lastWarnAt = DateTimeOffset.MinValue;

    public SubtitleLogWriter(ISubtitleLogStore store, StreamSession session, ILogger<SubtitleLogWriter> logger)
    {
        _store = store;
        _session = session;
        _logger = logger;
        _channel = Channel.CreateBounded<LogItem>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        _processingTask = Task.Run(ProcessQueueAsync);
    }

    /// <summary>向 WeakReferenceMessenger.Default 註冊 TranslationJob 訊息，收到時呼叫 Enqueue；重複呼叫不重複註冊。</summary>
    public void StartListening()
    {
        if (_registered) return;

        _registered = true;
        WeakReferenceMessenger.Default.Register<TranslationJob>(this, (r, job) => ((SubtitleLogWriter)r).Enqueue(job));
    }

    /// <summary>把 job 轉成 SubtitleLogEntry 排入佇列（立即返回，不寫資料庫）。</summary>
    public void Enqueue(TranslationJob job)
    {
        if (_disposed) return;

        // RawSource 沒有（舊資料或未設定）時退回 Current；StreamId 取排入當下的值
        var entry = new SubtitleLogEntry(
            job.Id,
            job.RevisionOf,
            _session.CurrentStreamId,
            string.IsNullOrEmpty(job.RawSource) ? job.Current : job.RawSource,
            job.Current,
            job.TranslatedText,
            job.Source.ToString(),
            job.ModelLatency,
            DateTimeOffset.UtcNow)
        {
            CacheId = job.CacheId,
        };
        _channel.Writer.TryWrite(new InsertItem(entry));
    }

    /// <summary>把使用者的手動修改（譯文，或修正原文後的重翻結果）排入佇列。</summary>
    public void EnqueueEdited(string jobId, string? source, string translation)
    {
        if (_disposed) return;

        _channel.Writer.TryWrite(new EditedItem(jobId, source, translation));
    }

    /// <summary>把刪除（只做標記）排入佇列。</summary>
    public void EnqueueDeleted(string jobId)
    {
        if (_disposed) return;

        _channel.Writer.TryWrite(new DeletedItem(jobId));
    }

    /// <summary>等待佇列中「呼叫當下已排入」的項目都處理完（測試與關閉程式用）；逾時回傳 false。</summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        if (_disposed)
        {
            return await Task.Run(() => _processingTask.IsCompleted).ConfigureAwait(false);
        }

        var marker = new FlushMarker(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _channel.Writer.TryWrite(marker);
        var completed = await Task.WhenAny(marker.Completed.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == marker.Completed.Task;
    }

    /// <summary>取消註冊、停止接收，最多等 2 秒把剩下的寫完。</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        if (_registered)
        {
            _registered = false;
            WeakReferenceMessenger.Default.Unregister<TranslationJob>(this);
        }

        // 完成寫入端讓背景迴圈自然結束；等它把剩下的項目寫完（最多 2 秒）
        _channel.Writer.TryComplete();
        try
        {
            _processingTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 背景迴圈不應該丟例外；保險起見吞掉，不讓關閉流程失敗
        }
    }

    /// <summary>背景處理迴圈：依排入順序處理三種項目與 Flush 標記；每一項的例外都捕捉後繼續。</summary>
    private async Task ProcessQueueAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (item)
                {
                    case InsertItem insert:
                        _store.InsertSubtitle(insert.Entry);
                        break;
                    case EditedItem edited:
                        _store.MarkSubtitleEdited(edited.JobId, edited.Source, edited.Translation);
                        break;
                    case DeletedItem deleted:
                        _store.MarkSubtitleDeleted(deleted.JobId);
                        break;
                    case FlushMarker marker:
                        marker.Completed.TrySetResult();
                        break;
                }
            }
            catch (Exception ex)
            {
                LogWarningOnce(ex);
            }
        }
    }

    /// <summary>同一種錯誤訊息 60 秒內只記一次，避免洗爆日誌。</summary>
    private void LogWarningOnce(Exception ex)
    {
        lock (_warnLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastWarnMessage == ex.Message && now - _lastWarnAt < WarnInterval) return;

            _lastWarnMessage = ex.Message;
            _lastWarnAt = now;
        }

        _logger.LogWarning(ex, "字幕紀錄寫入失敗，該筆已丟棄：{Message}", ex.Message);
    }

    /// <summary>佇列項目：新增、編輯、刪除與 Flush 標記。</summary>
    private abstract record LogItem;

    private sealed record InsertItem(SubtitleLogEntry Entry) : LogItem;

    private sealed record EditedItem(string JobId, string? Source, string Translation) : LogItem;

    private sealed record DeletedItem(string JobId) : LogItem;

    private sealed record FlushMarker(TaskCompletionSource Completed) : LogItem;
}
