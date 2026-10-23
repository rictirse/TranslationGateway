using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class StreamCardRecorderTests
{
    /// <summary>記錄寫入內容、可設定丟例外的假 store。</summary>
    private sealed class FakeSubtitleLogStore : ISubtitleLogStore
    {
        public List<(long StreamId, long CardId, string Reason)> Adds { get; } = new();

        public List<(long StreamId, long CardId)> Ends { get; } = new();

        public Exception? ThrowOnWrite { get; set; }

        public long? CreateStream(StreamRecord stream) => 1;

        public void InsertSubtitle(SubtitleLogEntry entry) { }

        public void MarkSubtitleEdited(string jobId, string? source, string translation) { }

        public void MarkSubtitleDeleted(string jobId) { }

        public void AddStreamCard(long streamId, long cardId, string reason)
        {
            if (ThrowOnWrite is { } ex) throw ex;
            Adds.Add((streamId, cardId, reason));
        }

        public void EndStreamCard(long streamId, long cardId)
        {
            if (ThrowOnWrite is { } ex) throw ex;
            Ends.Add((streamId, cardId));
        }
    }

    private static GlossaryCard Card(long id) => new()
    {
        Id = id,
        Kind = GlossaryKind.Character,
        Name = $"卡{id}",
    };

    private static StreamCardRecorder Create(FakeSubtitleLogStore store, StreamSession? session = null) =>
        new(store, session ?? new StreamSession(), NullLogger<StreamCardRecorder>.Instance);

    [Fact]
    public async Task RecordStreamStart_MatchesTakeReason_OthersRecordedAsCarried()
    {
        var store = new FakeSubtitleLogStore();
        var recorder = Create(store);

        await recorder.RecordStreamStart(
            3,
            [11, 22, 33, 44],
            [
                new GlossaryCardMatch(Card(11), StreamCardReason.Host),
                new GlossaryCardMatch(Card(22), StreamCardReason.Title),
            ]);

        Assert.Equal(
            [(3L, 11L, StreamCardReason.Host), (3L, 22L, StreamCardReason.Title), (3L, 33L, StreamCardReason.Carried), (3L, 44L, StreamCardReason.Carried)],
            store.Adds);
        Assert.Empty(store.Ends);
    }

    [Fact]
    public async Task RecordStreamStart_EmptyActiveIds_WritesNothing()
    {
        var store = new FakeSubtitleLogStore();
        var recorder = Create(store);

        await recorder.RecordStreamStart(3, [], [new GlossaryCardMatch(Card(11), StreamCardReason.Host)]);

        Assert.Empty(store.Adds);
    }

    [Fact]
    public async Task RecordChange_NoCurrentStream_WritesNothing()
    {
        var store = new FakeSubtitleLogStore();
        var recorder = Create(store);

        await recorder.RecordChange([1, 2], [2, 3]);

        Assert.Empty(store.Adds);
        Assert.Empty(store.Ends);
    }

    [Fact]
    public async Task RecordChange_AddedCardsRecordedAsManual_RemovedCardsEnded()
    {
        var store = new FakeSubtitleLogStore();
        var session = new StreamSession { CurrentStreamId = 9 };
        var recorder = Create(store, session);

        await recorder.RecordChange([1, 2], [2, 5]);

        Assert.Equal([(9L, 5L, StreamCardReason.Manual)], store.Adds);
        Assert.Equal([(9L, 1L)], store.Ends);
    }

    [Fact]
    public async Task RecordChange_WithSituationReason_AddedCardsUseIt()
    {
        var store = new FakeSubtitleLogStore();
        var session = new StreamSession { CurrentStreamId = 9 };
        var recorder = Create(store, session);

        await recorder.RecordChange([1], [2, 3], StreamCardReason.Situation);

        Assert.Equal([(9L, 2L, StreamCardReason.Situation), (9L, 3L, StreamCardReason.Situation)], store.Adds);
        Assert.Equal([(9L, 1L)], store.Ends);
    }

    [Fact]
    public async Task RecordChange_NoDifference_WritesNothing()
    {
        var store = new FakeSubtitleLogStore();
        var session = new StreamSession { CurrentStreamId = 9 };
        var recorder = Create(store, session);

        await recorder.RecordChange([1, 2], [1, 2]);

        Assert.Empty(store.Adds);
        Assert.Empty(store.Ends);
    }

    [Fact]
    public async Task StoreThrows_ReturnedTasksStillComplete()
    {
        var store = new FakeSubtitleLogStore { ThrowOnWrite = new InvalidOperationException("資料庫暫時無法使用") };
        var session = new StreamSession { CurrentStreamId = 9 };
        var recorder = Create(store, session);

        // 例外被捕捉記 log：兩個方法的 Task 都不會失敗
        await recorder.RecordStreamStart(9, [1, 2], [new GlossaryCardMatch(Card(1), StreamCardReason.Host)]);
        await recorder.RecordChange([1], [2, 3]);
    }
}
