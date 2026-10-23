using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>PotPlayer 的 Gateway 字幕軌（GET /subtitle）：等 YouTube 字幕載入後，
/// 把前處理句子與程式合併的整句組成 WebVTT 回傳（一個 cue 就是一整句）。</summary>
public sealed class CaptionSubtitleService
{
    /// <summary>查快取用的語言代碼（與 CaptionLoader 相同，下載回來的 ja／ja-JP 一律存成 ja）。</summary>
    private const string CacheLanguage = "ja";

    private readonly CaptionLoader _loader;
    private readonly CaptionTrackState _state;
    private readonly ICaptionStore? _store;
    private readonly ILogger<CaptionSubtitleService> _logger;

    public CaptionSubtitleService(CaptionLoader loader, CaptionTrackState state, ICaptionStore? store = null,
        ILogger<CaptionSubtitleService>? logger = null)
    {
        _loader = loader;
        _state = state;
        _store = store;
        _logger = logger ?? NullLogger<CaptionSubtitleService>.Instance;
    }

    /// <summary>等 YouTube 字幕載入的最長時間；逾時就用當下有的資料繼續（測試可縮短）。</summary>
    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>組指定影片的 WebVTT 字幕軌：先等字幕載入（最多 LoadTimeout，可被取消），
    /// 再用 CaptionTrackState 的 cue 與資料庫裡的前處理句子組出整句。例外只記 Warning log，回傳空軌。</summary>
    public async Task<string> GetWebVttAsync(string videoId, CancellationToken ct)
    {
        try
        {
            var loadTask = _loader.EnsureLoadedAsync(videoId);
            if (await Task.WhenAny(loadTask, Task.Delay(LoadTimeout, ct)) != loadTask)
            {
                // 因為取消而結束的要往外丟；真正的逾時只記 log，用當下有的資料繼續
                ct.ThrowIfCancellationRequested();
                _logger.LogWarning("Gateway 字幕軌：等 YouTube 字幕逾時：{VideoId}", videoId);
            }

            var cues = _state.VideoId == videoId ? _state.Cues : [];

            IReadOnlyList<CaptionSentence> sentences = [];
            StoredCaption? caption = null;
            if (_store is not null)
            {
                try
                {
                    var stored = await Task.Run(() => _store.GetCaption(videoId, CacheLanguage), ct);
                    if (stored is not null)
                    {
                        caption = stored;
                        sentences = await Task.Run(() => _store.GetSentences(stored.Id), ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gateway 字幕軌：讀前處理句子失敗：{VideoId}", videoId);
                    sentences = [];
                }
            }

            // 狀態裡沒有 cue（還在等逾時的載入），但資料庫快取有：改用快取的 cue
            if (cues.Count == 0 && caption is { Cues.Count: > 0 })
            {
                cues = caption.Cues;
            }

            var lines = CaptionSubtitleBuilder.Build(cues, sentences);
            _logger.LogInformation("Gateway 字幕軌：{VideoId}，前處理 {SentenceLines} 句、程式合併 {MergedLines} 句",
                videoId, lines.Count(l => l.FromSentence), lines.Count(l => !l.FromSentence));
            return CaptionSubtitleBuilder.ToWebVtt(lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gateway 字幕軌：組字幕軌失敗：{VideoId}", videoId);
            return "WEBVTT\n";
        }
    }
}
