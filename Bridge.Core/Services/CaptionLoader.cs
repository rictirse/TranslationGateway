using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>背景下載 YouTube 字幕（一鍵開台與 PotPlayer 腳本的通知共用）：記錄目前影片與世代號碼，
/// 下載完成寫入 CaptionTrackState；中途 Reset（換場）就丟掉舊結果。</summary>
public sealed class CaptionLoader
{
    /// <summary>查快取用的語言代碼：下載回來的 ja／ja-JP 一律存成 ja（見 LoadAsync）。</summary>
    private const string CacheLanguage = "ja";

    private readonly CaptionTrackState _state;
    private readonly IYouTubeCaptionProvider _provider;
    private readonly IYouTubePageInfoProvider? _pageInfo;
    private readonly ILogger<CaptionLoader> _logger;
    private readonly ICaptionStore? _store;
    private readonly CaptionPreprocessor? _preprocessor;
    private readonly object _lock = new();
    private int _generation;
    private string? _currentVideoId;
    private string? _playingVideoId;
    private string? _lastPlaying;
    private Task? _currentLoad;

    /// <summary>要附加到狀態列的一行文字（下載完成／沒有字幕）；可能從背景執行緒觸發。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Gateway 判斷 PotPlayer 目前正在播放的 YouTube 影片；本機檔案等解析不出 videoId 時為 null。</summary>
    public string? PlayingVideoId
    {
        get { lock (_lock) return _playingVideoId; }
    }

    public CaptionLoader(
        CaptionTrackState state,
        IYouTubeCaptionProvider provider,
        IYouTubePageInfoProvider? pageInfo = null,
        ILogger<CaptionLoader>? logger = null,
        ICaptionStore? store = null,
        CaptionPreprocessor? preprocessor = null)
    {
        _state = state;
        _provider = provider;
        _pageInfo = pageInfo;
        _logger = logger ?? NullLogger<CaptionLoader>.Instance;
        _store = store;
        _preprocessor = preprocessor;
    }

    /// <summary>換場（一鍵開台成功）時呼叫：清空字幕狀態、世代號碼加一，讓進行中的下載結果作廢；
    /// 也停掉前處理並清空句子狀態（CaptionSentenceState 經由 preprocessor 取得）。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _generation++;
            _currentVideoId = null;
            _playingVideoId = null;
            _currentLoad = null;
            _state.Clear();
        }

        _preprocessor?.Stop();
        _preprocessor?.Sentences.Reset(null, null);
    }

    /// <summary>背景下載指定影片的日文字幕並寫入狀態（呼叫端不等待）。先查資料庫快取：有資料就不讀頁面、不下載。
    /// 沒有快取時先重讀影片頁拿 visitorData（失敗就傳 null，改走 iOS 身分）；
    /// <paramref name="skipIfLive"/> 為 true 且頁面顯示正在直播時略過。例外只記 Warning log，不往外拋。</summary>
    public Task LoadAsync(string videoId, string? pageUrl, bool skipIfLive = false)
    {
        int generation;
        lock (_lock)
        {
            _currentVideoId = videoId;
            _playingVideoId = videoId;
            generation = _generation;
        }

        var task = LoadCoreAsync(videoId, pageUrl, skipIfLive, generation);
        lock (_lock)
        {
            // 記下目前影片的載入工作，供 EnsureLoadedAsync 等待；期間被 Reset（換場）就不記錄
            if (generation == _generation && _currentVideoId == videoId)
            {
                _currentLoad = task;
            }
        }

        return task;
    }

    /// <summary>PotPlayer 要求 Gateway 字幕軌（GET /subtitle）時呼叫：目前正在載入（或已載入）同支影片時，
    /// 回傳同一個載入工作（下載中共用、已完成則立即返回）；否則背景下載，不重複下載同支影片。</summary>
    public Task EnsureLoadedAsync(string videoId)
    {
        lock (_lock)
        {
            if (videoId == _currentVideoId && _currentLoad is not null)
            {
                _playingVideoId = videoId;
                return _currentLoad;
            }
        }

        _logger.LogInformation("PotPlayer 要求 Gateway 字幕軌：{VideoId}，載入 YouTube 字幕", videoId);
        return LoadAsync(videoId, null, skipIfLive: true);
    }

    /// <summary>LoadAsync 的實作：generation 是出發時的世代號碼，中途 Reset（換場）就丟掉舊結果。</summary>
    private async Task LoadCoreAsync(string videoId, string? pageUrl, bool skipIfLive, int generation)
    {
        try
        {
            // 先查資料庫快取（store 是同步方法，用 Task.Run 不阻塞呼叫端）；有資料就不讀頁面、不下載
            if (_store is not null)
            {
                try
                {
                    var cached = await Task.Run(() => _store.GetCaption(videoId, CacheLanguage));
                    if (cached is { Cues.Count: > 0 })
                    {
                        lock (_lock)
                        {
                            // 查快取期間又被 Reset（清掉或換掉對象）時，結果直接丟棄
                            if (generation != _generation || _currentVideoId != videoId)
                            {
                                return;
                            }

                            _state.Set(videoId, cached.Cues);
                        }

                        _ = Task.Run(() => _store.TouchCaption(cached.Id));
                        _logger.LogInformation(
                            "YouTube 字幕：{VideoId} 從快取載入 {Count} 段（寫入時間 {CreatedAt}）",
                            videoId, cached.Cues.Count, cached.CreatedAt);
                        // 快取命中：用 StoredCaption.Id 啟動前處理（state.Set 之後）
                        _preprocessor?.Start(cached.Id, videoId, cached.Cues);
                        StatusChanged?.Invoke($"YouTube 字幕：已從快取載入 {cached.Cues.Count} 段");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "讀取 YouTube 字幕快取失敗，改為下載：{VideoId}", videoId);
                }
            }

            var url = pageUrl ?? $"https://www.youtube.com/watch?v={videoId}";
            YouTubePageInfo? pageInfo = null;
            if (_pageInfo is not null)
            {
                try
                {
                    pageInfo = await _pageInfo.GetPageInfoAsync(url);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "讀 YouTube 影片頁面拿 visitorData 失敗，字幕改用 iOS 身分下載：{Url}", url);
                }
            }

            if (skipIfLive && pageInfo is { IsLiveNow: true })
            {
                _logger.LogInformation("PotPlayer 正在播放直播，不下載 YouTube 字幕：{VideoId}", videoId);
                return;
            }

            var caption = await _provider.GetJapaneseCaptionsAsync(videoId, pageInfo?.VisitorData);

            string status;
            bool loaded = false;
            lock (_lock)
            {
                // 下載期間又被 Reset（清掉或換掉對象）時，舊結果直接丟棄
                if (generation != _generation || _currentVideoId != videoId)
                {
                    return;
                }

                if (caption is { Cues.Count: > 0 })
                {
                    _state.Set(videoId, caption.Cues);
                    loaded = true;
                    status = $"YouTube 字幕：已載入 {caption.Cues.Count} 段";
                }
                else
                {
                    status = "YouTube 字幕：這支影片沒有可用的日文字幕";
                }
            }

            // 下載成功後存進資料庫快取，下次開同一支影片直接載入；查快取固定查 ja，
            // 所以回應的語言以 ja 開頭（ja、ja-JP）一律存成 ja。查不到字幕不寫入。
            // 存檔要在 LoadAsync 內等 SaveCaption 回傳 Id 之後才啟動前處理
            // （LoadAsync 本身已是背景工作，不會阻塞呼叫端）
            if (loaded && _store is not null && caption is { } downloaded)
            {
                var language = downloaded.LanguageCode.StartsWith("ja", StringComparison.Ordinal)
                    ? CacheLanguage
                    : downloaded.LanguageCode;
                long? savedId = null;
                try
                {
                    savedId = await Task.Run(
                        () => _store.SaveCaption(videoId, language, downloaded.IsAutoGenerated, downloaded.ClientName, downloaded.Cues));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "寫入 YouTube 字幕快取失敗：{VideoId}", videoId);
                }

                // 存檔期間又被 Reset（清掉或換掉對象）時，不對舊影片啟動前處理
                bool stale;
                lock (_lock)
                {
                    stale = generation != _generation || _currentVideoId != videoId;
                }

                if (!stale && _preprocessor is not null)
                {
                    if (savedId is { } captionId)
                    {
                        _preprocessor.Start(captionId, videoId, downloaded.Cues);
                    }
                    else
                    {
                        // 資料庫不可用：沒有 caption Id 就沒有前處理
                        _logger.LogInformation("字幕沒有存進資料庫，略過前處理");
                    }
                }
            }

            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "背景下載 YouTube 字幕失敗：{VideoId}", videoId);
        }
    }

    /// <summary>PotPlayer 腳本隨每個翻譯請求回報正在播放的檔名或網址：值變了就記 Information log
    /// （供使用者確認 HostGetPlayingFileName 的實際內容），解析出新的 YouTube videoId 時背景下載字幕。
    /// 不擲例外、不阻塞呼叫端。</summary>
    public void NotifyPlaying(string? playing)
    {
        try
        {
            lock (_lock)
            {
                // 同一句字幕的每個請求都會送同樣的值：沒變就不重複處理
                if (string.Equals(playing, _lastPlaying, StringComparison.Ordinal))
                {
                    return;
                }

                _lastPlaying = playing;
            }

            _logger.LogInformation("PotPlayer 正在播放：{Playing}", playing);

            var videoId = YouTubeVideoId.TryParse(playing);
            if (!string.IsNullOrEmpty(playing))
            {
                // 只有明確換成別的東西（本機路徑或完整網址）才清掉正在播放的影片；
                // 「watch」這種認不得的名稱（PotPlayer 對 YouTube 網址只給檔名部分）沿用目前的影片
                if (videoId is not null || LooksLikePathOrUrl(playing))
                {
                    lock (_lock)
                    {
                        // 記錄 PotPlayer 正在播放的影片（本機檔案、直播的本機串流網址解析不出 videoId 時清為 null）
                        _playingVideoId = videoId;
                    }
                }
                else
                {
                    _logger.LogDebug("PotPlayer 回報的名稱無法判斷影片，沿用目前的影片：{Playing}", playing);
                }
            }

            if (videoId is null)
            {
                return;
            }

            lock (_lock)
            {
                // 同一支影片（下載中、已載入或已判斷為直播略過）不再重複下載
                if (videoId == _currentVideoId)
                {
                    return;
                }
            }

            _logger.LogInformation("PotPlayer 換了影片：{VideoId}，背景載入 YouTube 字幕", videoId);
            // 不 await：翻譯請求的執行緒不能被字幕下載阻塞
            _ = LoadAsync(videoId, null, skipIfLive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "處理 PotPlayer 播放資訊失敗：{Playing}", playing);
        }
    }

    /// <summary>playing 是否像本機路徑或完整網址：含路徑分隔符（\ 或 /）或第 2 個字元是冒號（C: 這類磁碟機）。
    /// 「watch」（YouTube watch 網址的檔名部分）這類名稱回傳 false，不算換片。</summary>
    private static bool LooksLikePathOrUrl(string playing) =>
        playing.Contains('\\') || playing.Contains('/') ||
        (playing.Length >= 2 && playing[1] == ':');
}
