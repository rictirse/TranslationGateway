using System.Text;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>前處理句子有進度時，重新產生整份 SRT 交給正在播放的 PotPlayer（會蓋掉 YouTube 原本的字幕軌）：
/// 訂閱 CaptionSentenceState.Changed，debounce 之後組字幕、寫檔並呼叫 IPotPlayerSubtitleLoader。
/// 有章節疊加時也把目前章節名稱（只放日文原文）加進 SRT 左上角（見 ChapterOverlayState）；
/// PotPlayer 把這條 cue 送來翻譯時，TranslationManager 會直接回傳已翻好的中文，雙語顯示由 PotPlayer 處理。
/// 推送條件不符或失敗只記 log，不影響翻譯與前處理。</summary>
public sealed class PotPlayerSubtitlePusher : IDisposable
{
    private readonly CaptionSentenceState _sentences;
    private readonly CaptionTrackState _captions;
    private readonly CaptionLoader _loader;
    private readonly IPotPlayerSubtitleLoader _potPlayer;
    private readonly Func<bool> _isEnabled;
    private readonly string _outputDirectory;
    private readonly ChapterOverlayState? _chapterOverlay;
    private readonly Func<VodSettings>? _vodSettings;
    private readonly IPlaybackClock? _playbackClock;
    private readonly ILogger<PotPlayerSubtitlePusher> _logger;
    private readonly object _lock = new();
    private CancellationTokenSource? _scheduled;
    private string? _lastPushedVideoId;
    private string? _lastPushedContent;
    private string? _playbackConfirmedVideoId;

    /// <summary>輸出資料夾裡字幕檔的搜尋 pattern（{videoId}.ja.srt）。</summary>
    private const string SrtSearchPattern = "*.srt";

    public PotPlayerSubtitlePusher(
        CaptionSentenceState sentences,
        CaptionTrackState captions,
        CaptionLoader loader,
        IPotPlayerSubtitleLoader potPlayer,
        Func<bool> isEnabled,
        string outputDirectory,
        ILogger<PotPlayerSubtitlePusher> logger,
        ChapterOverlayState? chapterOverlay = null,
        Func<VodSettings>? vodSettings = null,
        IPlaybackClock? playbackClock = null)
    {
        _sentences = sentences;
        _captions = captions;
        _loader = loader;
        _potPlayer = potPlayer;
        _isEnabled = isEnabled;
        _outputDirectory = outputDirectory;
        _chapterOverlay = chapterOverlay;
        _vodSettings = vodSettings;
        _playbackClock = playbackClock;
        _logger = logger;
        _sentences.Changed += OnSentencesChanged;
        // 字幕一載入就先推送程式合併的版本，不必等前處理第一批完成
        _captions.Changed += OnCaptionsChanged;
        // 章節載入或翻譯結束（成功、失敗）時照樣重新推送（疊加的章節名也在 SRT 裡）
        if (_chapterOverlay is not null)
        {
            _chapterOverlay.Changed += OnSentencesChanged;
        }

        // 舊影片留下的字幕檔一律清掉（PotPlayer 只需要目前正在播放的這份）
        DeleteOtherSrtFiles(null);
    }

    /// <summary>事件觸發後等多久才推送（這段期間的新觸發不再重排）；測試可以縮短。</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>等播放開始時，查詢播放位置的間隔；測試可縮短。</summary>
    public TimeSpan PlaybackPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>最多等播放開始多久；逾時就照常推送（避免使用者暫停在 0 秒時永遠不推）。</summary>
    public TimeSpan PlaybackStartTimeout { get; set; } = TimeSpan.FromSeconds(90);

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

    /// <summary>字幕狀態有變：每次開片都會先清空（VideoId 變 null），這時把「已確認開始播放」的紀錄一起清掉——
    /// 重開同一支影片也要重新等 PotPlayer 開始播放，否則開片 10 秒就推送字幕會打斷影片載入（只記影片 Id 時重開同一支就會發生）。
    /// 「上次推送的內容」也一起清掉：重開的是新的 PotPlayer，內容跟上次一樣也要再推一次，否則新播放器永遠拿不到 Gateway 的字幕。
    /// 之後照常排推送。</summary>
    private void OnCaptionsChanged()
    {
        if (_captions.VideoId is null)
        {
            lock (_lock)
            {
                _playbackConfirmedVideoId = null;
                _lastPushedVideoId = null;
                _lastPushedContent = null;
            }
        }

        OnSentencesChanged();
    }

    /// <summary>等 Debounce 之後推送；取消（Dispose）時直接收掉，不擲例外。</summary>
    private async Task PushAfterDebounceAsync(CancellationTokenSource scheduled)
    {
        try
        {
            await Task.Delay(Debounce, scheduled.Token);

            // 同一支影片的第一次推送要等 PotPlayer 真的開始播放（播放位置在前進）才推：
            // 影片還在載入時就再啟動 PotPlayer 載字幕會打斷載入。等待屬於推送前，_scheduled
            // 標記維持不清（期間的變動不重排），Push 本來就是即時組 SRT，等完推的就是最新內容
            await WaitForPlaybackStartAsync(scheduled.Token);

            // 推送「之前」就清掉排定標記：推送進行中才來的變動（例如章節譯文剛好在這時回來）會另外排一次，
            // 不會被當成「已經排了」丟掉——之後字幕不再變動時，那次變動就永遠推不出去
            lock (_lock)
            {
                if (ReferenceEquals(_scheduled, scheduled))
                {
                    _scheduled = null;
                }
            }

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

    /// <summary>有播放時鐘、且目前影片還沒確認過「開始播放」時，等播放位置確實在前進：
    /// 每隔 <see cref="PlaybackPollInterval"/> 讀一次位置，連續兩次非 null 而且後一次大於前一次才算開始。
    /// 超過 <see cref="PlaybackStartTimeout"/> 仍未確認就記 Information log 後照常返回（不讓暫停中的影片永遠推不出去）；
    /// 兩種情況都記下影片 Id（之後同一支影片不再等）。等待被取消（Dispose）時擲出
    /// OperationCanceledException，由呼叫端的 catch 吸收、不推送。沒有時鐘或已確認過時直接返回。</summary>
    private async Task WaitForPlaybackStartAsync(CancellationToken cancellationToken)
    {
        if (_playbackClock is null)
        {
            return;
        }

        var videoId = _captions.VideoId;
        lock (_lock)
        {
            if (videoId is null || videoId == _playbackConfirmedVideoId)
            {
                return;
            }
        }

        _logger.LogDebug("等 PotPlayer 開始播放再推送字幕：{VideoId}", videoId);
        var startedAt = DateTime.UtcNow;
        long? previous = null;
        while (true)
        {
            await Task.Delay(PlaybackPollInterval, cancellationToken);
            var position = _playbackClock.GetPositionMs();
            if (position is not null && previous is not null && position > previous)
            {
                _logger.LogInformation("PotPlayer 已開始播放（等了 {Seconds:F1} 秒），推送字幕：{VideoId}",
                    (DateTime.UtcNow - startedAt).TotalSeconds, videoId);
                break;
            }

            if (DateTime.UtcNow - startedAt >= PlaybackStartTimeout)
            {
                _logger.LogInformation("等不到 PotPlayer 開始播放，照常推送字幕：{VideoId}", videoId);
                break;
            }

            previous = position;
        }

        lock (_lock)
        {
            _playbackConfirmedVideoId = videoId;
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

            // 章節名稱疊加：同一支影片、設定開啟且標題翻譯已結束時加進 SRT（結束時間用最後一行字幕的 EndMs）；
            // 翻譯還沒結束先不推送疊加，避免 PotPlayer 先拿到只有原文的 cue、之後不再重問中文
            IReadOnlyList<SubtitleOverlay> overlays = [];
            var settings = _vodSettings?.Invoke();
            if (settings is { ShowChapterTitles: true } && _chapterOverlay is not null &&
                _chapterOverlay.VideoId == videoId && _chapterOverlay.TranslationSettled)
            {
                var videoEndMs = lines.Count > 0 ? lines[^1].EndMs : 0;
                overlays = CaptionSubtitleBuilder.BuildChapterOverlays(
                    _chapterOverlay.Items, videoEndMs, settings.ChapterTitleFontSize,
                    settings.ChapterTitleFallbackSeconds * 1000L);
            }

            var srt = CaptionSubtitleBuilder.ToSrt(lines, overlays);

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
            try
            {
                File.WriteAllText(path, srt, Encoding.UTF8);
            }
            catch (IOException ex)
            {
                // 字幕檔正被別的程式讀取（Windows 不能同時寫）：這次放棄、排下一次推送，不然這次的變動
                //（例如章節名稱）要等到字幕下次變動才推得出去
                _logger.LogInformation(ex, "字幕檔正被使用中，稍後重試推送：{Path}", path);
                OnSentencesChanged();
                return;
            }

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
        _captions.Changed -= OnCaptionsChanged;
        if (_chapterOverlay is not null)
        {
            _chapterOverlay.Changed -= OnSentencesChanged;
        }

        lock (_lock)
        {
            _scheduled?.Cancel();
        }
    }
}
