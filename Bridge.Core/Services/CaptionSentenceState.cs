using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 目前這支影片前處理句子的狀態（DI singleton）：CaptionPreprocessor 寫入（快取載入與每批完成），
/// 之後的卡片處理讀取；換影片時 Reset，不寫資料庫（資料庫版本在 caption_sentences）。
/// </summary>
public sealed class CaptionSentenceState
{
    private readonly object _lock = new();
    private string? _videoId;
    private long? _captionId;
    private IReadOnlyList<CaptionSentence> _sentences = [];

    /// <summary>句子內容變更（Reset 或 Add 有實際改動）時觸發；可能從背景執行緒呼叫。</summary>
    public event Action? Changed;

    /// <summary>目前持有的句子是哪支影片；沒有時為 null。</summary>
    public string? VideoId
    {
        get { lock (_lock) return _videoId; }
    }

    /// <summary>目前持有的句子屬於哪個字幕快取；沒有時為 null。</summary>
    public long? CaptionId
    {
        get { lock (_lock) return _captionId; }
    }

    /// <summary>前處理後的句子（依 StartMs 排序）；沒有時是空清單。</summary>
    public IReadOnlyList<CaptionSentence> Sentences
    {
        get { lock (_lock) return _sentences; }
    }

    /// <summary>清空並換對象（videoId 為 null 時代表沒有對象）。</summary>
    public void Reset(string? videoId, long? captionId)
    {
        lock (_lock)
        {
            _videoId = videoId;
            _captionId = captionId;
            _sentences = [];
        }

        Changed?.Invoke();
    }

    /// <summary>加入句批次：videoId 與目前對象不同時整批忽略；同 StartMs 的句子取代舊的，結果依 StartMs 排序。</summary>
    public void Add(string videoId, IReadOnlyList<CaptionSentence> sentences)
    {
        lock (_lock)
        {
            if (!string.Equals(videoId, _videoId, StringComparison.Ordinal))
            {
                return;
            }

            // 依 StartMs 合併：同 StartMs 取代（後寫的贏）
            var merged = new Dictionary<long, CaptionSentence>();
            foreach (var sentence in _sentences)
            {
                merged[sentence.StartMs] = sentence;
            }

            var changed = false;
            foreach (var sentence in sentences)
            {
                if (merged.TryGetValue(sentence.StartMs, out var existing) && existing == sentence)
                {
                    continue;
                }

                merged[sentence.StartMs] = sentence;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            _sentences = [.. merged.Values.OrderBy(s => s.StartMs, Comparer<long>.Default)];
        }

        Changed?.Invoke();
    }

    /// <summary>回傳 [fromMs, toMs] 時間範圍有重疊的句子（StartMs &lt;= toMs &amp;&amp; EndMs &gt;= fromMs），依 StartMs 排序。</summary>
    public IReadOnlyList<CaptionSentence> GetOverlapping(long fromMs, long toMs)
    {
        lock (_lock)
        {
            return [.. _sentences.Where(s => s.StartMs <= toMs && s.EndMs >= fromMs)];
        }
    }
}
