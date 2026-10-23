using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>目前這支影片的 YouTube 字幕（記憶體快取，供翻譯時參考）；換直播時 Clear，不寫資料庫。</summary>
public sealed class CaptionTrackState
{
    private readonly object _lock = new();
    private string? _videoId;
    private IReadOnlyList<CaptionCue> _cues = [];
    private long[] _startTimes = [];

    /// <summary>字幕內容變更（Set 或 Clear）時觸發；供 CaptionReference 等重置內部對照狀態。</summary>
    public event Action? Changed;

    /// <summary>目前持有的字幕是哪支影片；沒有字幕時為 null。</summary>
    public string? VideoId
    {
        get
        {
            lock (_lock)
            {
                return _videoId;
            }
        }
    }

    /// <summary>字幕段落（依 StartMs 排序）；沒有時是空清單。</summary>
    public IReadOnlyList<CaptionCue> Cues
    {
        get
        {
            lock (_lock)
            {
                return _cues;
            }
        }
    }

    public bool IsLoaded
    {
        get
        {
            lock (_lock)
            {
                return _cues.Count > 0;
            }
        }
    }

    /// <summary>更新目前影片的字幕（不正確的輸入由呼叫端把關）。</summary>
    public void Set(string videoId, IReadOnlyList<CaptionCue> cues)
    {
        lock (_lock)
        {
            _videoId = videoId;
            _cues = [.. cues.OrderBy(c => c.StartMs)];
            _startTimes = [.. _cues.Select(c => c.StartMs)];
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _videoId = null;
            _cues = [];
            _startTimes = [];
        }

        Changed?.Invoke();
    }

    /// <summary>回傳 StartMs 落在 [fromMs, toMs] 的 cue（用二分搜尋找起點）。</summary>
    public IReadOnlyList<CaptionCue> GetRange(long fromMs, long toMs)
    {
        lock (_lock)
        {
            if (_cues.Count == 0)
            {
                return [];
            }

            // 二分搜尋找出第一個 StartMs >= fromMs 的位置
            var lo = 0;
            var hi = _cues.Count;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                if (_startTimes[mid] < fromMs)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            var count = 0;
            while (lo + count < _cues.Count && _cues[lo + count].StartMs <= toMs)
            {
                count++;
            }

            return count == 0 ? [] : [.. _cues.Skip(lo).Take(count)];
        }
    }
}
