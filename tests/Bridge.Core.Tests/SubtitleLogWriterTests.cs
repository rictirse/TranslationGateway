using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class SubtitleLogWriterTests
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

    /// <summary>記錄呼叫順序與內容的假 store；可設定下一次呼叫丟例外或每次延遲。</summary>
    private sealed class FakeSubtitleLogStore : ISubtitleLogStore
    {
        private readonly object _lock = new();

        public List<string> Calls { get; } = new();

        public List<SubtitleLogEntry> Entries { get; } = new();

        public Exception? ThrowNext { get; set; }

        public TimeSpan DelayEach { get; set; }

        public long? CreateStream(StreamRecord stream)
        {
            Proceed();
            lock (_lock)
            {
                Calls.Add($"stream:{stream.Platform}");
            }
            return 1;
        }

        public void InsertSubtitle(SubtitleLogEntry entry)
        {
            Proceed();
            lock (_lock)
            {
                Calls.Add($"insert:{entry.JobId}");
                Entries.Add(entry);
            }
        }

        public void MarkSubtitleEdited(string jobId, string? source, string translation)
        {
            Proceed();
            lock (_lock)
            {
                Calls.Add($"edited:{jobId}:{source}:{translation}");
            }
        }

        public void MarkSubtitleDeleted(string jobId)
        {
            Proceed();
            lock (_lock)
            {
                Calls.Add($"deleted:{jobId}");
            }
        }

        public void AddStreamCard(long streamId, long cardId, string reason)
        {
            lock (_lock)
            {
                Calls.Add($"card:{streamId}:{cardId}:{reason}");
            }
        }

        public void EndStreamCard(long streamId, long cardId)
        {
            lock (_lock)
            {
                Calls.Add($"cardEnd:{streamId}:{cardId}");
            }
        }

        private void Proceed()
        {
            if (ThrowNext is { } ex)
            {
                ThrowNext = null;
                throw ex;
            }

            if (DelayEach > TimeSpan.Zero)
            {
                Thread.Sleep(DelayEach);
            }
        }
    }

    private static TranslationJob Job(string current = "原文", string translated = "譯文",
        string rawSource = "", string? revisionOf = null, TranslationSource source = TranslationSource.Model, long latency = 42) => new()
        {
            Current = current,
            TranslatedText = translated,
            RawSource = rawSource,
            RevisionOf = revisionOf,
            Source = source,
            ModelLatency = latency
        };

    [Fact]
    public async Task Enqueue_ConvertsFieldsIncludingRawSourceFallbackAndCurrentStreamId()
    {
        var store = new FakeSubtitleLogStore();
        var session = new StreamSession();
        using var writer = new SubtitleLogWriter(store, session, NullLogger<SubtitleLogWriter>.Instance);

        session.CurrentStreamId = 5;
        writer.Enqueue(Job(current: "ぺこら", translated: "佩克拉", rawSource: "ペコラ", revisionOf: "prev", latency: 87));
        session.CurrentStreamId = 9;
        // RawSource 為空：退回 Current
        writer.Enqueue(Job(current: "ハロー", translated: "哈囉"));

        Assert.True(await writer.FlushAsync(FlushTimeout));

        Assert.Equal(2, store.Entries.Count);
        var first = store.Entries[0];
        Assert.Equal(8, first.JobId.Length);
        Assert.Equal("prev", first.RevisionOfJobId);
        Assert.Equal(5, first.StreamId);
        Assert.Equal("ペコラ", first.RawSource);
        Assert.Equal("ぺこら", first.Source);
        Assert.Equal("佩克拉", first.Translation);
        Assert.Equal("Model", first.TranslationSource);
        Assert.Equal(87, first.ModelLatencyMs);

        var second = store.Entries[1];
        Assert.Equal(9, second.StreamId);
        Assert.Equal("ハロー", second.RawSource);
        Assert.Equal("ハロー", second.Source);
    }

    [Fact]
    public async Task Enqueue_InsertEditedDeleted_ProcessedInOrder()
    {
        var store = new FakeSubtitleLogStore();
        using var writer = new SubtitleLogWriter(store, new StreamSession(), NullLogger<SubtitleLogWriter>.Instance);
        var job = Job();

        writer.Enqueue(job);
        writer.EnqueueEdited(job.Id, null, "手動譯");
        writer.EnqueueEdited("job2", "修正原文", "重翻譯");
        writer.EnqueueDeleted("job3");

        Assert.True(await writer.FlushAsync(FlushTimeout));

        Assert.Equal(
        [
            $"insert:{job.Id}",
            $"edited:{job.Id}::手動譯",
            "edited:job2:修正原文:重翻譯",
            "deleted:job3"
        ], store.Calls);
    }

    [Fact]
    public async Task Enqueue_FirstCallThrows_SecondItemStillWritten()
    {
        var store = new FakeSubtitleLogStore { ThrowNext = new InvalidOperationException("資料庫暫時無法使用") };
        using var writer = new SubtitleLogWriter(store, new StreamSession(), NullLogger<SubtitleLogWriter>.Instance);

        writer.Enqueue(Job());
        writer.Enqueue(Job());

        Assert.True(await writer.FlushAsync(FlushTimeout));

        // 第一筆丟例外被吃掉，第二筆仍寫入
        Assert.Single(store.Entries);
        Assert.StartsWith("insert:", store.Calls.Single());
    }

    [Fact]
    public async Task Enqueue_SlowStore_ReturnsImmediately()
    {
        var store = new FakeSubtitleLogStore { DelayEach = TimeSpan.FromMilliseconds(200) };
        using var writer = new SubtitleLogWriter(store, new StreamSession(), NullLogger<SubtitleLogWriter>.Instance);

        var sw = Stopwatch.StartNew();
        writer.Enqueue(Job());
        writer.Enqueue(Job());
        writer.EnqueueEdited("job1", null, "手動譯");
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(50));

        Assert.True(await writer.FlushAsync(FlushTimeout));
        Assert.Equal(3, store.Calls.Count);
    }

    [Fact]
    public async Task FlushAsync_AllEnqueuedItemsProcessed()
    {
        var store = new FakeSubtitleLogStore();
        using var writer = new SubtitleLogWriter(store, new StreamSession(), NullLogger<SubtitleLogWriter>.Instance);

        for (var i = 0; i < 20; i++)
        {
            writer.Enqueue(Job());
        }
        writer.EnqueueDeleted("gone");

        Assert.True(await writer.FlushAsync(FlushTimeout));

        Assert.Equal(21, store.Calls.Count);
        Assert.Equal("deleted:gone", store.Calls[^1]);
    }

    [Fact]
    public void Dispose_ThenEnqueue_DoesNotThrow()
    {
        var store = new FakeSubtitleLogStore();
        var writer = new SubtitleLogWriter(store, new StreamSession(), NullLogger<SubtitleLogWriter>.Instance);

        writer.Dispose();

        writer.Enqueue(Job());
        writer.EnqueueEdited("job1", null, "譯");
        writer.EnqueueDeleted("job1");
    }
}
