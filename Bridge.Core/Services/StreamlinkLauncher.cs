using System.Text.Json;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>一鍵開台：解析剪貼簿網址 → 先讀 YouTube 影片頁面（有的話）→ streamlink --json 查中繼資料 → streamlink 帶起 PotPlayer；
/// YouTube 待機室不開，錄影（streamlink 查不到，或只查得到非直播串流）改用 PotPlayer 直接開網址。
/// 有傳 <paramref name="preparer"/> 時，啟動播放器前先準備 PotPlayer（寫翻譯腳本與登錄檔設定），之後一律用準備好的路徑啟動；
/// 有傳 <c>beforePlayerStart</c> 掛勾時，會在啟動播放器前以最後回傳的中繼資料呼叫它（例如等開台資料探勘完成才播放）。</summary>
public sealed class StreamlinkLauncher(IProcessRunner runner, ISettingsService settings, IVideoInfoProvider videoInfo, ILogger<StreamlinkLauncher> logger,
    IYouTubePageInfoProvider? pageInfo = null, PotPlayerPreparer? preparer = null)
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);

    private const string UnparsableMessage = "無法解析 streamlink 的輸出";

    /// <summary>streamlink 的下載頁（找不到 streamlink 時狀態列「前往下載」按鈕用）。</summary>
    public const string StreamlinkDownloadUrl = "https://github.com/streamlink/streamlink/releases";

    /// <summary>PotPlayer 的下載頁（找不到 PotPlayer 時狀態列「前往下載」按鈕用）。</summary>
    public const string PotPlayerDownloadUrl = "https://potplayer.tv/";

    private IStartedProcess? _current;
    private string? _currentDescription;

    /// <summary>上一場直播是否還在執行（streamlink 行程尚未結束）。</summary>
    public bool IsRunning => _current is not null && !_current.HasExited;

    /// <summary>目前直播的說明文字（「作者 - 標題」，沒有中繼資料時為網址）；沒在播時為 null。</summary>
    public string? CurrentDescription => IsRunning ? _currentDescription : null;

    /// <summary>結束目前這場直播的整個行程樹（包含它帶起的播放器）；沒有記錄時不做事。</summary>
    public void StopCurrent()
    {
        if (_current is null) return;

        _current.KillTree();
        logger.LogInformation("已結束上一場直播：{Description}", _currentDescription);
        _current = null;
        _currentDescription = null;
    }

    public async Task<StreamLaunchResult> LaunchAsync(string? clipboardText,
        Func<StreamMetadata?, CancellationToken, Task>? beforePlayerStart = null,
        CancellationToken cancellationToken = default)
    {
        if (!StreamUrl.TryParse(clipboardText, out var url, out var platform))
        {
            return Fail("剪貼簿裡沒有 YouTube 或 Twitch 的網址");
        }

        var config = settings.Current.Streamlink;
        // streamlink 只在直播路徑用到（錄影直接用 PotPlayer 開網址），存在改到 LaunchViaStreamlinkAsync 檢查
        if (!runner.FileExists(config.PlayerPath))
        {
            return Fail($"找不到 PotPlayer：{config.PlayerPath}\n請先安裝 PotPlayer（64 位元），或到設定頁修改播放器路徑",
                PotPlayerDownloadUrl, "前往下載 PotPlayer");
        }

        // 開台前準備 PotPlayer：把 port 寫死進翻譯腳本寫入 PotPlayer、改登錄檔設定；
        // 安裝目錄不能寫時 preparer 會回傳複本的路徑，之後啟動播放器一律用它
        string? preparedPlayerPath = null;
        string? prepareNote = null;
        if (preparer is not null)
        {
            var prepared = await Task.Run(preparer.Prepare, cancellationToken);
            if (!prepared.Success)
            {
                return Fail(prepared.Message);
            }

            preparedPlayerPath = prepared.PlayerPath;
            prepareNote = prepared.Message;
        }

        StreamLaunchResult ApplyPrepareNote(StreamLaunchResult result) =>
            result.Success && !string.IsNullOrEmpty(prepareNote)
                ? result with { Message = result.Message + prepareNote }
                : result;

        // preparer 成功時一律用準備好的路徑（可能是 Gateway 複本裡的 exe），沒有 preparer 時用設定檔的播放器
        var playerPath = preparedPlayerPath ?? config.PlayerPath;

        // 開 YouTube 網址時先讀影片頁面：直播照走 streamlink、待機室不開、其他（一般影片／直播存檔）直接用 PotPlayer 開；
        // 頁面讀不到就退回 streamlink 判斷，開台不能因此失敗。被 YouTube 擋下（匿名存取次數太多）時直接回失敗，
        // 不跑 streamlink、不開 PotPlayer
        if (platform == StreamPlatform.YouTube && pageInfo is not null)
        {
            var page = await pageInfo.CheckPageAsync(url, cancellationToken);
            if (page.BlockedReason is { } blockedReason)
            {
                return Blocked(blockedReason);
            }

            if (page.Info is null)
            {
                logger.LogWarning("讀不到 YouTube 影片頁面資訊，改用 streamlink 判斷：{Url}", url);
            }
            else if (page.Info.IsLiveNow)
            {
                var result = await LaunchViaStreamlinkAsync(url, platform, config, playerPath, cancellationToken, page.Info, beforePlayerStart);
                return result.Success ? ApplyPrepareNote(result) : result;
            }
            else if (page.Info.IsUpcoming)
            {
                return Fail($"直播還沒開始（待機室）：{page.Info.Title}，開始後再按一次開啟直播");
            }
            else
            {
                // Whisper：有日文字幕就關（由 Gateway 的字幕接手），語言優先用生成式字幕軌
                var whisperPlan = preparer is not null ? WhisperDecider.ForVod(page.Info) : null;
                return ApplyPrepareNote(await LaunchVodWithMetadata(
                    url,
                    playerPath,
                    new StreamMetadata(StreamPlatform.YouTube, page.Info.VideoId, page.Info.Author, page.Info.Category, page.Info.Title,
                        page.Info.ChannelUrl, IsVod: true, Description: page.Info.Description, StartTime: page.Info.StartTime, EndTime: page.Info.EndTime),
                    "（不是直播中，改用 PotPlayer 直接播放，可在 PotPlayer 選畫質與字幕）",
                    whisperPlan, beforePlayerStart, cancellationToken));
            }
        }

        return ApplyPrepareNote(await LaunchViaStreamlinkAsync(url, platform, config, playerPath, cancellationToken, beforePlayerStart: beforePlayerStart));
    }

    private async Task<StreamLaunchResult> LaunchViaStreamlinkAsync(string url, StreamPlatform platform, StreamlinkSettings config,
        string playerPath, CancellationToken cancellationToken, YouTubePageInfo? page = null,
        Func<StreamMetadata?, CancellationToken, Task>? beforePlayerStart = null)
    {
        // 真的要跑 streamlink（YouTube 直播中、Twitch、頁面讀不到退回判斷）才檢查存在：錄影路徑不需要 streamlink
        if (!runner.FileExists(config.StreamlinkPath))
        {
            return Fail($"找不到 streamlink：{config.StreamlinkPath}\n看直播需要 streamlink（錄影不需要），請安裝，或到設定頁修改路徑",
                StreamlinkDownloadUrl, "前往下載 streamlink");
        }

        var query = await runner.RunAsync(config.StreamlinkPath, ["--json", url], QueryTimeout, cancellationToken);
        if (query.TimedOut)
        {
            return Fail("streamlink 查詢逾時，請稍後再試");
        }

        var (metadata, error, outcome, looksLikeVod, streamTypes) = ParseCore(query.StandardOutput, platform);

        // streamlink 的 YouTube 外掛只支援直播：查詢出錯（或拿不到 JSON 且 ExitCode 非 0）時視為錄影，
        // 不經 streamlink，直接用 PotPlayer 開網址（查詢逾時不算，照常回失敗）；
        // 錯誤內容是被 YouTube 擋下（要求登入確認不是機器人、429）時不算錄影，直接回被擋
        if (platform == StreamPlatform.YouTube &&
            (outcome == ParseOutcome.Error || (outcome == ParseOutcome.Unparsable && query.ExitCode != 0)))
        {
            var detail = outcome == ParseOutcome.Error ? error ?? UnparsableMessage : LastNonBlankLine(query.StandardError) ?? UnparsableMessage;
            if (LooksBlockedByYouTube(detail))
            {
                logger.LogWarning("streamlink 查詢 {Url} 被 YouTube 擋下（不是錄影）：{Detail}", url, detail);
                return Blocked($"streamlink：{detail}");
            }

            logger.LogWarning(
                "streamlink 無法開啟 {Url}，可能是錄影，改用 PotPlayer 直接播放。streamlink 原始輸出：{Detail}",
                url,
                detail);
            return await LaunchVodAsync(url, playerPath, cancellationToken, "（streamlink 無法開啟，可能是錄影，改用 PotPlayer 直接播放）", beforePlayerStart);
        }

        // YouTube 錄影：streamlink 查得到，但串流都不是 hls（http／muxed 等）時 best 只有低畫質，
        // 不經 streamlink，改用 PotPlayer 直接開網址（可在 PotPlayer 選畫質與字幕）
        if (platform == StreamPlatform.YouTube && outcome == ParseOutcome.Metadata && looksLikeVod)
        {
            logger.LogInformation(
                "streamlink 只找到非直播串流（{Types}），判斷為錄影，改用 PotPlayer 直接播放：{Url}",
                streamTypes,
                url);
            return await LaunchVodAsync(url, playerPath, cancellationToken, "（偵測為錄影，改用 PotPlayer 直接播放，可在 PotPlayer 選畫質與字幕）", beforePlayerStart);
        }

        if (outcome == ParseOutcome.Error)
        {
            return Fail($"streamlink 無法開啟：{error}");
        }

        if (outcome == ParseOutcome.Unparsable && query.ExitCode != 0)
        {
            // 拿不到 JSON 就退而求其次：用標準錯誤的最後一行當錯因（通常是 Python 的 traceback 結尾）
            return Fail($"streamlink 無法開啟：{LastNonBlankLine(query.StandardError)}");
        }

        // 非 JSON 但 ExitCode == 0：視為查不到中繼資料，照常開播
        var quality = string.IsNullOrWhiteSpace(config.Quality) ? "best" : config.Quality;
        // 直播沒有字幕可用：開 Whisper，語言從影片頁／streamlink 的標題與說明欄偵測（要在啟動播放器前寫進登錄檔）
        WhisperDecider.WhisperPlan? whisperPlan = null;
        if (preparer is not null)
        {
            whisperPlan = WhisperDecider.ForLive(page?.Title ?? metadata?.Title, page?.Description);
            preparer.ApplyWhisper(whisperPlan);
        }

        // YouTube 直播：先用頁面資訊補齊中繼資料，掛勾與回傳給呼叫端的都是補齊後的版本（只做一次）
        if (page is not null)
        {
            metadata = Enrich(metadata, page);
        }

        await RunBeforePlayerStartAsync(beforePlayerStart, metadata, cancellationToken);

        // 失敗路徑都在前面 return，走到這裡才記錄，上一場的記錄不會被蓋掉
        _current = runner.Start(
            config.StreamlinkPath,
            [url, quality, "--player", playerPath],
            line => logger.LogInformation("[streamlink] {Line}", line));

        string message;
        if (metadata?.Author is { } author && metadata.Title is { } title)
        {
            _currentDescription = $"{author} - {title}";
            message = $"已開啟：{author} - {title}";
        }
        else
        {
            // 沒有中繼資料時，說明文字退回網址
            _currentDescription = url;
            message = "已開啟直播";
        }

        if (whisperPlan is not null)
        {
            message += WhisperNote(whisperPlan);
        }

        return new StreamLaunchResult(true, message, metadata);
    }

    /// <summary>錄影流程：不經 streamlink，直接用 PotPlayer 開網址；標題與頻道改用 oEmbed 查（查不到仍照開）。</summary>
    /// <param name="note">附加在結果訊息結尾的說明文字（說明為何改走錄影流程）。</param>
    private async Task<StreamLaunchResult> LaunchVodAsync(string url, string playerPath, CancellationToken cancellationToken, string note,
        Func<StreamMetadata?, CancellationToken, Task>? beforePlayerStart = null)
    {
        // 影片頁讀不到，不知道這支錄影有沒有字幕：不動 Whisper，維持使用者原本的設定
        if (preparer is not null)
        {
            logger.LogInformation("不知道這支錄影有沒有字幕，Whisper 設定不變");
        }

        var info = await videoInfo.GetYouTubeInfoAsync(url, cancellationToken);
        return await LaunchVodWithMetadata(url, playerPath, info ?? new StreamMetadata(StreamPlatform.YouTube, null, null, null, null, IsVod: true),
            note, beforePlayerStart: beforePlayerStart, cancellationToken: cancellationToken);
    }

    /// <summary>錄影流程（已知中繼資料）：不經 streamlink，直接用 PotPlayer 開網址，不再查任何來源。</summary>
    /// <param name="note">附加在結果訊息結尾的說明文字（說明為何改走錄影流程）。</param>
    /// <param name="whisperPlan">有值時先把它寫進 PotPlayer 的設定（啟動播放器前），成功訊息也會附加說明。</param>
    private async Task<StreamLaunchResult> LaunchVodWithMetadata(string url, string playerPath, StreamMetadata metadata, string note,
        WhisperDecider.WhisperPlan? whisperPlan = null,
        Func<StreamMetadata?, CancellationToken, Task>? beforePlayerStart = null,
        CancellationToken cancellationToken = default)
    {
        if (preparer is not null && whisperPlan is not null)
        {
            preparer.ApplyWhisper(whisperPlan);
        }

        await RunBeforePlayerStartAsync(beforePlayerStart, metadata, cancellationToken);

        _current = runner.Start(playerPath, [url], line => logger.LogInformation("[player] {Line}", line));

        string message;
        if (metadata.Author is { } author && metadata.Title is { } title)
        {
            _currentDescription = $"{author} - {title}";
            message = $"已開啟錄影：{author} - {title}";
        }
        else
        {
            // 沒有中繼資料時，說明文字退回網址
            _currentDescription = url;
            message = "已開啟錄影";
        }

        message += "\n" + note;
        if (whisperPlan is not null)
        {
            message += WhisperNote(whisperPlan);
        }

        return new StreamLaunchResult(true, message, metadata);
    }

    /// <summary>啟動播放器前的掛勾：失敗路徑都走完、Whisper 設定寫完後、<c>runner.Start</c> 前呼叫。
    /// 擲 <see cref="OperationCanceledException"/> 時往外拋（不啟動播放器）；其他例外只記 Warning，照常啟動。</summary>
    private async Task RunBeforePlayerStartAsync(Func<StreamMetadata?, CancellationToken, Task>? beforePlayerStart,
        StreamMetadata? metadata, CancellationToken cancellationToken)
    {
        if (beforePlayerStart is null)
        {
            return;
        }

        try
        {
            await beforePlayerStart(metadata, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "啟動播放器前的準備失敗，照常播放");
        }
    }

    /// <summary>組成功訊息裡的 Whisper 說明行（放在準備 PotPlayer 的說明之前）。</summary>
    private static string WhisperNote(WhisperDecider.WhisperPlan plan) =>
        $"\nWhisper：{(plan.Enabled ? "開啟" : "關閉")}（{plan.Reason}）";

    /// <summary>用影片頁面的資訊補齊 streamlink／oEmbed 查不到的欄位（既有值優先，說明欄與直播起訖時間一律用頁面的）。</summary>
    private static StreamMetadata? Enrich(StreamMetadata? metadata, YouTubePageInfo page)
    {
        if (metadata is null)
        {
            return new StreamMetadata(StreamPlatform.YouTube, page.VideoId, page.Author, page.Category, page.Title,
                page.ChannelUrl, Description: page.Description, StartTime: page.StartTime, EndTime: page.EndTime);
        }

        return metadata with
        {
            ChannelUrl = metadata.ChannelUrl ?? page.ChannelUrl,
            Category = metadata.Category ?? page.Category,
            Description = page.Description,
            Author = metadata.Author ?? page.Author,
            Title = metadata.Title ?? page.Title,
            StartTime = metadata.StartTime ?? page.StartTime,
            EndTime = metadata.EndTime ?? page.EndTime,
        };
    }

    /// <summary>解析 streamlink --json 的輸出；回傳 (中繼資料, 錯誤訊息)，兩者至多一個不為 null。</summary>
    public static (StreamMetadata? Metadata, string? Error) ParseJson(string json, StreamPlatform platform)
    {
        var (metadata, error, outcome, _, _) = ParseCore(json, platform);
        return outcome switch
        {
            ParseOutcome.Error => (null, error),
            ParseOutcome.Unparsable => (null, UnparsableMessage),
            _ => (metadata, null)
        };
    }

    private enum ParseOutcome
    {
        Metadata,
        Error,
        Unparsable
    }

    private static (StreamMetadata? Metadata, string? Error, ParseOutcome Outcome, bool LooksLikeVod, string StreamTypes) ParseCore(string json, StreamPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null, ParseOutcome.Unparsable, false, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String)
            {
                return (null, errorElement.GetString(), ParseOutcome.Error, false, string.Empty);
            }

            // 缺少 metadata 或欄位為 null 時，對應值為 null
            StreamMetadata metadata;
            if (root.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Object)
            {
                string? Field(string name) => metadataElement.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : null;

                metadata = new StreamMetadata(platform, Field("id"), Field("author"), Field("category"), Field("title"));
            }
            else
            {
                metadata = new StreamMetadata(platform, null, null, null, null);
            }

            var (looksLikeVod, streamTypes) = ScanStreams(root);
            return (metadata, null, ParseOutcome.Metadata, looksLikeVod, streamTypes);
        }
        catch (JsonException)
        {
            return (null, null, ParseOutcome.Unparsable, false, string.Empty);
        }
        catch (ArgumentException)
        {
            // 空字串等無效輸入
            return (null, null, ParseOutcome.Unparsable, false, string.Empty);
        }
    }

    /// <summary>從 streams 判斷是否像錄影：至少一個串流、且沒有任何串流的 type 是 "hls"（不分大小寫）。
    /// 有串流缺少 type 欄位時不判斷為錄影（舊版 streamlink 與既有資料沒有 type，維持原本流程）。
    /// 回傳 (是否像錄影, 串流 type 去重後以「、」連接)。</summary>
    private static (bool LooksLikeVod, string Types) ScanStreams(JsonElement root)
    {
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Object)
        {
            return (false, string.Empty);
        }

        List<string> types = [];
        var hasStream = false;
        var hasHls = false;
        foreach (var stream in streams.EnumerateObject())
        {
            hasStream = true;
            if (stream.Value.ValueKind != JsonValueKind.Object ||
                !stream.Value.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return (false, string.Empty);
            }

            var type = typeElement.GetString() ?? string.Empty;
            if (type.Equals("hls", StringComparison.OrdinalIgnoreCase))
            {
                hasHls = true;
            }

            if (!types.Contains(type))
            {
                types.Add(type);
            }
        }

        return (hasStream && !hasHls, string.Join("、", types));
    }

    private static string? LastNonBlankLine(string text)
    {
        string? last = null;
        foreach (var raw in text.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length > 0) last = trimmed;
        }

        return last;
    }

    private StreamLaunchResult Fail(string message, string? helpUrl = null, string? helpLabel = null)
    {
        logger.LogWarning("{Message}", message);
        return new StreamLaunchResult(false, message, null, HelpUrl: helpUrl, HelpLabel: helpLabel);
    }

    /// <summary>被 YouTube 擋下的失敗結果（Message 是「無法開啟：」+ 判斷依據）：沒有跑 streamlink、沒有啟動播放器。</summary>
    private StreamLaunchResult Blocked(string blockedReason)
    {
        logger.LogWarning("無法開啟：{BlockedReason}", blockedReason);
        return new StreamLaunchResult(false, $"無法開啟：{blockedReason}", null, Blocked: true, BlockedReason: blockedReason);
    }

    /// <summary>streamlink 的錯誤文字是否代表被 YouTube 擋下（匿名存取次數太多）：要求登入確認不是機器人、429。</summary>
    private static bool LooksBlockedByYouTube(string detail) =>
        detail.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("not a bot", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("429", StringComparison.OrdinalIgnoreCase);
}
