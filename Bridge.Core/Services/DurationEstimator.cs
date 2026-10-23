namespace Translation.Bridge.Core.Services;

/// <summary>耗時估計器：保留最近幾筆樣本，取中位數（偶數筆時取中間兩筆的平均）。
/// 用中位數是為了讓偶爾特別慢的請求不會把估計值拉高。執行緒安全。</summary>
public sealed class DurationEstimator
{
    /// <summary>保留的樣本數。</summary>
    public const int Capacity = 5;

    private readonly object _lock = new();
    private readonly Queue<TimeSpan> _samples = new();

    /// <summary>加一筆耗時樣本，只保留最近 <see cref="Capacity"/> 筆。</summary>
    public void Add(TimeSpan duration)
    {
        lock (_lock)
        {
            _samples.Enqueue(duration);
            while (_samples.Count > Capacity)
            {
                _samples.Dequeue();
            }
        }
    }

    /// <summary>樣本的中位數；沒有資料時為 null（偶數筆時取中間兩筆的平均）。</summary>
    public TimeSpan? Median
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count == 0)
                {
                    return null;
                }

                var sorted = _samples.OrderBy(d => d).ToList();
                var mid = sorted.Count / 2;
                return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
            }
        }
    }
}
