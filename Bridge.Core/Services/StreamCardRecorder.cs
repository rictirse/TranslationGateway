using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 直播生效詞彙卡的紀錄器（DI singleton）：開台自動套用與直播中的手動變更都寫入 StreamCards，
/// 一律以 Task.Run 在背景執行；寫入失敗只記 log，回傳的 Task 不會失敗。
/// </summary>
public sealed class StreamCardRecorder
{
    private readonly ISubtitleLogStore _store;
    private readonly StreamSession _session;
    private readonly ILogger<StreamCardRecorder> _logger;

    public StreamCardRecorder(ISubtitleLogStore store, StreamSession session, ILogger<StreamCardRecorder> logger)
    {
        _store = store;
        _session = session;
        _logger = logger;
    }

    /// <summary>開台後記錄生效中的每一張卡：原因取 matches 中同一張卡的 Reason，沒有就用 Carried（開台前就生效、沿用下來的卡）。</summary>
    public Task RecordStreamStart(long streamId, IReadOnlyList<long> activeIds, IReadOnlyList<GlossaryCardMatch> matches)
    {
        return Task.Run(() =>
        {
            var reasons = matches
                .GroupBy(m => m.Card.Id)
                .ToDictionary(g => g.Key, g => g.First().Reason);
            foreach (var cardId in activeIds)
            {
                SafeAdd(streamId, cardId, reasons.GetValueOrDefault(cardId, StreamCardReason.Carried));
            }
        });
    }

    /// <summary>直播中的手動變更：新增的卡記 addedReason（預設 Manual），被移除的卡結束其最新一筆未結束的紀錄；
    /// 沒有開台（CurrentStreamId 為 null）時不做事。</summary>
    public Task RecordChange(IReadOnlyList<long> before, IReadOnlyList<long> after, string addedReason = StreamCardReason.Manual)
    {
        return Task.Run(() =>
        {
            var streamId = _session.CurrentStreamId;
            if (streamId is not { } id) return;

            foreach (var cardId in after)
            {
                if (!before.Contains(cardId))
                {
                    SafeAdd(id, cardId, addedReason);
                }
            }

            foreach (var cardId in before)
            {
                if (!after.Contains(cardId))
                {
                    SafeEnd(id, cardId);
                }
            }
        });
    }

    private void SafeAdd(long streamId, long cardId, string reason)
    {
        try
        {
            _store.AddStreamCard(streamId, cardId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "新增直播詞彙卡紀錄失敗（Stream {StreamId}, Card {CardId}, {Reason}）", streamId, cardId, reason);
        }
    }

    private void SafeEnd(long streamId, long cardId)
    {
        try
        {
            _store.EndStreamCard(streamId, cardId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "結束直播詞彙卡紀錄失敗（Stream {StreamId}, Card {CardId}）", streamId, cardId);
        }
    }
}
