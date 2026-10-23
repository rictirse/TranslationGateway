using System.Text;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>前處理句子有進度時，重新產生整份 SRT 交給正在播放的 PotPlayer（會蓋掉 YouTube 原本的字幕軌）：
/// 訂閱 CaptionSentenceState.Changed，debounce 之後組字幕、寫檔並呼叫 IPotPlayerSubtitleLoader。
/// 推送條件不符或失敗只記 log，不影響翻譯與前處理。</summary>
public sealed class PotPlayerSubtitlePusher : IDisposable
{
    private readonly CaptionSentenceState _sentences;
    private readonly CaptionTrackState _captions;
    private readonly CaptionLoader _loader;
    private readonly IPotPlayerSubtitleLoader _potPlayer;
    private readonly Func<bool> _isEnabled;
    private readonly string _outputDirectory;
    private readonly ILogger<PotPlayerSubtitlePusher> _logger;
    private readonly object _lock = new();
    private CancellationTokenSource? _scheduled;
    private string? _lastPushedVideoId;
    private string? _lastPushedContent;

    /// <summary>輸出資料夾裡字幕檔的搜尋 pattern（{videoId}.ja.srt）。</summary>
    private const string SrtSearchPattern = "*.srt";

    public PotPlayerSubtitlePusher(
        CaptionSentenceState sentences,
        CaptionTrackState captions,
        CaptionLoader loader,
        IPotPlayerSubtitleLoader potPlayer,
        Func<bool> isEnabled,
        string outputDirectory,
        ILogger<PotPlayerSubtitlePusher> logger)
    {
        _sentences = sentences;
        _captions = captions;
        _loader = loader;
        _potPlayer = potPlayer;
        _isEnabled = isEnabled;
        _outputDirectory = outputDirectory;
        _logger = logger;
        _sentences.Changed += OnSentencesChanged;
        // 字幕一載入就先推送程式合併的版本，不必等前處理第一批完成
        _captions.Changed += OnSentencesChanged;
        // 舊影片留下的字幕檔一律清掉（PotPlayer 只需要目前正在播放的這份）
        DeleteOtherSrtFiles(null);
    }

    /// <summary>事件觸發後等多久才推送（這段期間的新觸發不再重排）；測試可以縮短。</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>推送成功（potPlayer.Load 回 true 且內容已記下）後觸發：參數是影片 Id 與這次推送的字幕行。</summary>
    public event Action<string, IReadOnlyList<SubtitleLine>>? Pushed;

    /// <summary>句子有變動：還沒有排定的推送時，排一個在 Debounce 之後執行（不阻塞呼叫端）。</summary>
    private void OnSentencesChanged()
    {
        CancellationTokenSource scheduled;
        lock (_lock)
        {
            // 已經排了就不重排：每個 Debounce 期間最多推送一次
            if (_scheduled is not null)
            {
                return;
            }

            scheduled = _scheduled = new CancellationTokenSource();
        }

        _ = PushAfterDebounceAsync(scheduled);
    }

    /// <summary>等 Debounce 之後推送；取消（Dispose）時直接收掉，不擲例外。</summary>
    private async Task PushAfterDebounceAsync(CancellationTokenSource scheduled)
    {
        try
        {
            await Task.Delay(Debounce, scheduled.Token);
            Push();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新 PotPlayer 字幕失敗");
        }
        finally
        {
            lock (_lock)
            {
                scheduled.Dispose();
                if (ReferenceEquals(_scheduled, scheduled))
                {
                    _scheduled = null;
                }
            }
        }
    }

    /// <summary>組 SRT 並交給 PotPlayer：條件不符只記 Debug log，失敗記 Information／Warning log，不擲例外。</summary>
    private void Push()
    {
        try
        {
            if (!_isEnabled())
            {
                _logger.LogDebug("自動更新 PotPlayer 字幕已關閉，略過推送");
                return;
            }

            var videoId = _captions.VideoId;
            if (videoId is null || _captions.Cues.Count == 0)
            {
                _logger.LogDebug("還沒有載入 YouTube 字幕，略過 PotPlayer 字幕推送");
                return;
            }

            if (_loader.PlayingVideoId != videoId)
            {
                _logger.LogDebug("PotPlayer 不是正在播放這支影片，略過字幕推送：{VideoId}", videoId);
                return;
            }

            // 前處理句子不是同一支影片（還沒開始、資料庫不可用）時只用程式合併
            IReadOnlyList<CaptionSentence> sentences = _sentences.VideoId == videoId ? _sentences.Sentences : [];
            var lines = CaptionSubtitleBuilder.Build(_captions.Cues, sentences);
            var srt = CaptionSubtitleBuilder.ToSrt(lines);

            lock (_lock)
            {
                // 內容和上一次推送給同一支影片的完全相同時不再推送
                if (videoId == _lastPushedVideoId && srt == _lastPushedContent)
                {
                    _logger.LogDebug("字幕內容沒有變，略過 PotPlayer 字幕推送：{VideoId}", videoId);
                    return;
                }
            }

            Directory.CreateDirectory(_outputDirectory);
            var path = Path.Combine(_outputDirectory, $"{videoId}.ja.srt");
            File.WriteAllText(path, srt, Encoding.UTF8);

            if (!_potPlayer.Load(path))
            {
                // 失敗不記內容：下次句子有變動時會再試
                _logger.LogInformation("找不到正在執行的 PotPlayer，略過字幕更新");
                return;
            }

            lock (_lock)
            {
                _lastPushedVideoId = videoId;
                _lastPushedContent = srt;
            }

            // 推送成功後清掉其他影片留下的字幕檔（資料夾只留目前正在播放的這份）
            DeleteOtherSrtFiles($"{videoId}.ja.srt");

            try
            {
                Pushed?.Invoke(videoId, lines);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pushed 事件訂閱端擲出例外：{VideoId}", videoId);
            }

            _logger.LogInformation("已更新 PotPlayer 字幕：{VideoId}，前處理 {SentenceLines} 句、程式合併 {MergedLines} 句",
                videoId, lines.Count(l => l.FromSentence), lines.Count(l => !l.FromSentence));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新 PotPlayer 字幕失敗：{VideoId}", _sentences.VideoId);
        }
    }

    /// <summary>刪除輸出資料夾裡檔名不等於 keepFileName 的 *.srt（keepFileName 是 null 時全刪）。
    /// 資料夾不存在就略過；刪除失敗（檔案被佔用等）記 Debug log 繼續，不擲例外。</summary>
    private void DeleteOtherSrtFiles(string? keepFileName)
    {
        try
        {
            if (!Directory.Exists(_outputDirectory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(_outputDirectory, SrtSearchPattern))
            {
                var fileName = Path.GetFileName(path);
                if (keepFileName is not null && fileName == keepFileName)
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "刪除舊字幕檔失敗（可能被 PotPlayer 佔用）：{Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "清理字幕輸出資料夾失敗：{Directory}", _outputDirectory);
        }
    }

    /// <summary>取消訂閱事件並停掉排定的推送。</summary>
    public void Dispose()
    {
        _sentences.Changed -= OnSentencesChanged;
        _captions.Changed -= OnSentencesChanged;
        lock (_lock)
        {
            _scheduled?.Cancel();
        }
    }
}
