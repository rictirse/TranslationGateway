using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>一段時間內的翻譯統計快照（供 Dashboard 顯示）。</summary>
/// <param name="Count">時間窗內的 Job 總數。</param>
/// <param name="ModelCount">來源為模型的 Job 數量。</param>
/// <param name="ModelAvgMs">模型耗時平均（ms）；沒有模型 Job 時為 null。</param>
/// <param name="ModelP95Ms">模型耗時 P95（ms，nearest-rank）；沒有模型 Job 時為 null。</param>
/// <param name="CacheHitRate">快取命中率（0～1）；Count 為 0 時為 0。</param>
/// <param name="TimeoutCount">逾時次數。</param>
public sealed record TranslationStatsSnapshot(
    int Count,
    int ModelCount,
    long? ModelAvgMs,
    long? ModelP95Ms,
    double CacheHitRate,
    int TimeoutCount);

/// <summary>翻譯統計計算。</summary>
public static class TranslationStats
{
    /// <summary>計算 <paramref name="now"/> 往回 <paramref name="window"/> 時間窗內的翻譯統計。</summary>
    public static TranslationStatsSnapshot Compute(IEnumerable<TranslationJob> jobs, DateTime now, TimeSpan window)
    {
        var cutoff = now - window;
        var inWindow = jobs.Where(j => j.Timestamp >= cutoff).ToList();

        var count = inWindow.Count;
        var modelCount = inWindow.Count(j => j.Source == TranslationSource.Model);
        var timeoutCount = inWindow.Count(j => j.Source == TranslationSource.Timeout);
        var cacheCount = inWindow.Count(j => j.Source == TranslationSource.Cache);

        long? avgMs = null;
        long? p95Ms = null;
        if (modelCount > 0)
        {
            var latencies = inWindow
                .Where(j => j.Source == TranslationSource.Model)
                .Select(j => j.ModelLatency)
                .OrderBy(ms => ms)
                .ToList();

            // 四捨五入到整數（.5 進位，而非銀行家捨入）
            avgMs = (long)Math.Round(latencies.Average(), MidpointRounding.AwayFromZero);

            // nearest-rank P95：升冪排序後取第 ceil(0.95 * n) 個（1-based）
            var rank = (int)Math.Ceiling(0.95 * latencies.Count) - 1;
            p95Ms = latencies[rank];
        }

        var cacheHitRate = count == 0 ? 0 : (double)cacheCount / count;

        return new TranslationStatsSnapshot(count, modelCount, avgMs, p95Ms, cacheHitRate, timeoutCount);
    }
}
