using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>章節名稱疊加的一章：開始時間、原文標題與譯文（還沒翻好或翻不出來為 null）、
/// 原曲長度（LRCLIB 查到的毫秒數；還沒查到或查不到為 null）。</summary>
public sealed record ChapterOverlayItem(long StartMs, string Title, string? Translation, long? DurationMs = null);

/// <summary>
/// 章節名稱疊加的狀態（DI singleton）：開片時決定章節來源（人工貼上 &gt; 說明欄 &gt; 留言セトリ）、
/// 在背景翻譯標題、查每章的原曲長度，供 PotPlayerSubtitlePusher 把章節名加進推送的字幕。
/// 有 <see cref="IVideoChapterStore"/> 時章節與譯名會存進資料庫，重開同一支影片直接使用、不重抓重翻。
/// <see cref="Changed"/> 觸發後呼叫端自行取 <see cref="Items"/> 快照；換片或 Reset 後回來的舊結果會被丟掉。
/// </summary>
public sealed class ChapterOverlayState(
    VideoChapterService chapters,
    ChapterTitleTranslator translator,
    ILogger<ChapterOverlayState> logger,
    ISongInfoProvider? songInfo = null,
    IVideoChapterStore? store = null)
{
    private readonly object _lock = new();
    private int _version;
    private string? _videoId;
    private string? _description;
    private IReadOnlyList<ChapterOverlayItem> _items = [];
    private string? _source;
    private bool _translationSettled;

    /// <summary>章節標題比對時要移除的 {\…} 標記（PotPlayer 送來的 cue 可能帶著 {\an7\fs12} 等）。</summary>
    private static readonly Regex OverlayTags = new(@"\{[^}]*\}", RegexOptions.Compiled);

    /// <summary>目前章節所屬的影片；Reset 後為 null。</summary>
    public string? VideoId
    {
        get { lock (_lock) return _videoId; }
    }

    /// <summary>目前的章節清單（依開始時間排序、不可變的快照）；沒有章節時為空清單。</summary>
    public IReadOnlyList<ChapterOverlayItem> Items
    {
        get { lock (_lock) return _items; }
    }

    /// <summary>目前章節的來源（<see cref="VideoChapterSources"/> 的值）；沒有章節時為 null。</summary>
    public string? Source
    {
        get { lock (_lock) return _source; }
    }

    /// <summary>這支影片的章節標題翻譯已結束（成功、失敗或沒有章節）；false 時 overlay 先不推送，
    /// 避免 PotPlayer 先拿到只有原文的 cue、之後不再重問中文。</summary>
    public bool TranslationSettled
    {
        get { lock (_lock) return _translationSettled; }
    }

    /// <summary>Items 有變動（載入章節、譯文回來、Reset）或翻譯結束（成功、失敗、例外）時觸發；可能在背景執行緒。</summary>
    public event Action? Changed;

    /// <summary>開片時呼叫：決定章節來源（資料庫存的（含人工）&gt; 說明欄 &gt; 留言セトリ）、在背景翻譯標題；
    /// 換片或 Reset 後回來的舊結果丟掉。不擲例外（呼叫端取消除外）。</summary>
    public async Task LoadAsync(string videoId, string? description, CancellationToken cancellationToken = default)
    {
        int version;
        lock (_lock)
        {
            version = ++_version;
            _translationSettled = false;
        }

        try
        {
            // 章節來源（人工 > 說明欄 > 留言）：先讀資料庫存的章節；人工貼上時存的就是人工章節
            var stored = store is null
                ? (IReadOnlyList<StoredVideoChapter>)[]
                : await Task.Run(() => store.GetVideoChapters(videoId), cancellationToken).ConfigureAwait(false);

            string source;
            bool translateAll;
            bool queryDurations;
            List<ChapterOverlayItem> items;
            if (stored.Count > 0)
            {
                // 有存的直接用：Source 照存的；有存譯文／長度就不重翻、不重查
                // （只有譯文全部是 null 才重翻，只有長度全部是 null 才重查）
                source = stored[0].Source;
                items =
                [
                    .. stored
                        .Select(static chapter => new ChapterOverlayItem(
                            chapter.StartMs, chapter.Title, chapter.Translation, chapter.DurationMs))
                        .OrderBy(static item => item.StartMs),
                ];
                translateAll = stored.All(static chapter => chapter.Translation is null);
                queryDurations = stored.All(static chapter => chapter.DurationMs is null);
            }
            else
            {
                // 說明欄有章節就用說明欄（PotPlayer 進度條顯示的也是它），沒有才用留言セトリ
                var parsed = ChapterExtractor.FromDescription(description, durationMs: null);
                source = VideoChapterSources.Description;
                if (parsed.Count == 0)
                {
                    parsed = await chapters.GetChaptersAsync(videoId, cancellationToken).ConfigureAwait(false);
                    source = VideoChapterSources.Comment;
                }

                // 去掉 FromComments/FromDescription 補的「開始」（StartMs == 0 且標題 == 開始）
                items =
                [
                    .. parsed
                        .Where(static chapter => chapter.StartMs != 0 || chapter.Title != "開始")
                        .Select(static chapter => new ChapterOverlayItem(chapter.StartMs, chapter.Title, Translation: null))
                        .OrderBy(static item => item.StartMs),
                ];
                translateAll = true;
                queryDurations = true;
            }

            List<ChapterOverlayItem> current;
            lock (_lock)
            {
                if (version != _version)
                {
                    return;
                }

                _videoId = videoId;
                _description = description;
                _items = current = items;
                _source = current.Count == 0 ? null : source;
                // 沒有章節就沒有標題要翻；用存的譯文（不必重翻）也算已結束
                _translationSettled = current.Count == 0 || !translateAll;
            }

            Changed?.Invoke();
            if (current.Count == 0)
            {
                return;
            }

            // 全部照存的用（不必重翻、不必重查）就不用重存；其餘（說明欄／留言／部分要重查）在流程結束後存檔
            var save = stored.Count == 0 || translateAll || queryDurations;
            await TranslateAndStoreAsync(
                version, videoId, source, current, translateAll, queryDurations && songInfo is not null, save,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "章節名稱載入失敗：{VideoId}", videoId);
            lock (_lock)
            {
                if (version != _version)
                {
                    return;
                }

                _translationSettled = true;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>用人工貼上的文字取代目前影片的章節：解析成功（≥3 章）時存檔（Source = Manual）、
    /// 清空譯文與長度後重新翻譯與查長度，回傳章節數；解析失敗回 0、目前章節不變。
    /// 目前沒有影片（VideoId 為 null）回 0。不擲例外。</summary>
    public Task<int> ApplyManualAsync(string text, CancellationToken cancellationToken = default)
    {
        string? videoId;
        lock (_lock)
        {
            videoId = _videoId;
        }

        if (videoId is null)
        {
            return Task.FromResult(0);
        }

        // 規則同說明欄：每行一個時間戳＋標題；去掉自動補的「開始」（StartMs == 0 且標題 == 開始）
        List<ChapterOverlayItem> items =
        [
            .. ChapterExtractor.FromText(text, durationMs: null)
                .Where(static chapter => chapter.StartMs != 0 || chapter.Title != "開始")
                .Select(static chapter => new ChapterOverlayItem(chapter.StartMs, chapter.Title, Translation: null))
                .OrderBy(static item => item.StartMs),
        ];
        if (items.Count < 3)
        {
            return Task.FromResult(0);
        }

        int version;
        lock (_lock)
        {
            version = ++_version;
            _items = items;
            _source = VideoChapterSources.Manual;
            _translationSettled = false;
        }

        Changed?.Invoke();

        // 與 LoadAsync 相同的翻譯＋查長度＋存檔流程在背景完成：方法在 Items 換好後就回傳，不等翻譯
        _ = TranslateInBackgroundAsync(version, videoId, items, cancellationToken);

        return Task.FromResult(items.Count);
    }

    /// <summary>刪掉這支影片存的章節（含人工），重新依說明欄／留言載入。</summary>
    public async Task ClearManualAsync(CancellationToken cancellationToken = default)
    {
        string? videoId;
        string? description;
        lock (_lock)
        {
            videoId = _videoId;
            description = _description;
        }

        if (videoId is null)
        {
            return;
        }

        try
        {
            if (store is not null)
            {
                await Task.Run(() => store.DeleteVideoChapters(videoId), cancellationToken).ConfigureAwait(false);
            }

            await LoadAsync(videoId, description, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清除人工章節失敗：{VideoId}", videoId);
        }
    }

    /// <summary>翻譯標題與查原曲長度（兩者都結束後存檔）；<see cref="LoadAsync"/> 的主流程。
    /// 翻譯整體失敗（數量不符或例外）只記 log，照樣視為已結束（不擲例外、呼叫端取消除外）。</summary>
    private async Task TranslateAndStoreAsync(
        int version, string videoId, string source, IReadOnlyList<ChapterOverlayItem> current,
        bool translateAll, bool queryDurations, bool save, CancellationToken cancellationToken)
    {
        // 原曲長度查詢與標題翻譯並行：兩邊各自完成、版本沒變時各自把結果寫回 Items
        var durationsTask = queryDurations
            ? LoadDurationsAsync(version, current, cancellationToken)
            : null;

        if (translateAll)
        {
            IReadOnlyList<string?>? translations = null;
            Exception? translationError = null;
            try
            {
                var titles = current.Select(static item => item.Title).ToList();
                translations = await translator.TranslateAsync(titles, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                translationError = ex;
            }

            lock (_lock)
            {
                if (version != _version)
                {
                    // 換片或 Reset 了：舊譯文丟掉
                    return;
                }

                if (translationError is null && translations is not null && translations.Count == current.Count)
                {
                    // 長度可能已經先寫回：以最新的 Items 為底補譯文（順序與 current 相同），不要蓋掉 DurationMs
                    var baseItems = _items.Count == current.Count ? _items : current;
                    _items = [.. baseItems.Select((item, i) => item with { Translation = translations[i] })];
                }

                // 翻譯結束（成功或整體失敗）：疊加狀態已定，可以推送 overlay
                _translationSettled = true;
            }

            if (translationError is not null)
            {
                logger.LogWarning(translationError, "章節標題翻譯失敗：{VideoId}", videoId);
            }

            Changed?.Invoke();
        }

        if (durationsTask is not null)
        {
            // 長度查詢自己會把結果寫回 Items 並觸發 Changed（只會因呼叫端取消往外拸）
            await durationsTask.ConfigureAwait(false);
        }

        if (save)
        {
            await SaveChaptersAsync(version, videoId, source, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary><see cref="ApplyManualAsync"/> 的背景流程：包一層例外處理，
    /// 不讓未觀察的 Task 例外；失敗時照樣標記翻譯已結束並觸發 Changed。</summary>
    private async Task TranslateInBackgroundAsync(
        int version, string videoId, IReadOnlyList<ChapterOverlayItem> items, CancellationToken cancellationToken)
    {
        try
        {
            await TranslateAndStoreAsync(
                version, videoId, VideoChapterSources.Manual, items,
                translateAll: true, queryDurations: songInfo is not null, save: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消：不做事
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "人工章節翻譯背景流程失敗：{VideoId}", videoId);
            lock (_lock)
            {
                if (version != _version)
                {
                    return;
                }

                _translationSettled = true;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>版本沒變且有章節時，把目前的 Items（含譯文與長度）存進資料庫（背景執行緒，不阻塞呼叫端）。</summary>
    private async Task SaveChaptersAsync(int version, string videoId, string source, CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return;
        }

        IReadOnlyList<ChapterOverlayItem> snapshot;
        lock (_lock)
        {
            if (version != _version)
            {
                return;
            }

            snapshot = _items;
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        List<StoredVideoChapter> chapters =
        [
            .. snapshot.Select(item =>
                new StoredVideoChapter(item.StartMs, item.Title, item.Translation, item.DurationMs, source)),
        ];
        await Task.Run(() => store.ReplaceVideoChapters(videoId, chapters), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>依序查每章的原曲長度（不打擾標題翻譯）：全部查完、版本沒變時把 DurationMs 寫回 Items
    /// （保留當下已有的譯文）並觸發 Changed。章節標題拆不出曲名、查不到長度時該章維持 null。
    /// 不擲例外（呼叫端取消除外）；呼叫端沒 await 時結果照樣寫回（背景完成）。</summary>
    private async Task LoadDurationsAsync(int version, IReadOnlyList<ChapterOverlayItem> loaded, CancellationToken cancellationToken)
    {
        var durations = new long?[loaded.Count];
        for (var i = 0; i < loaded.Count; i++)
        {
            var query = SongTitleParser.Parse(loaded[i].Title);
            if (query is null)
            {
                continue;
            }

            durations[i] = await songInfo!.GetDurationMsAsync(query, cancellationToken).ConfigureAwait(false);
        }

        bool updated;
        lock (_lock)
        {
            if (version != _version)
            {
                // 換片或 Reset 了：舊的查詢結果丟掉
                return;
            }

            updated = false;
            if (_items.Count == durations.Length)
            {
                // 譯文可能已經先寫回：以最新的 Items 為底，只補 DurationMs
                var items = new List<ChapterOverlayItem>(_items.Count);
                for (var i = 0; i < _items.Count; i++)
                {
                    var item = _items[i];
                    if (durations[i] is { } durationMs && item.DurationMs != durationMs)
                    {
                        item = item with { DurationMs = durationMs };
                        updated = true;
                    }

                    items.Add(item);
                }

                if (updated)
                {
                    _items = items;
                }
            }
        }

        if (updated)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>換片（或離開錄影）時清空：VideoId 設 null、Items 清空、觸發 Changed。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _version++;
            _videoId = null;
            _description = null;
            _items = [];
            _source = null;
            _translationSettled = false;
        }

        Changed?.Invoke();
    }

    /// <summary>text 是目前影片某個章節的標題（比對前去掉 {\…} 標記、合併空白、去頭尾空白）且已有譯文時回傳譯文，否則 null。</summary>
    public string? FindTranslation(string text)
    {
        var normalized = NormalizeTitle(text);
        if (normalized.Length == 0)
        {
            return null;
        }

        lock (_lock)
        {
            foreach (var item in _items)
            {
                if (!string.Equals(NormalizeTitle(item.Title), normalized, StringComparison.Ordinal))
                {
                    continue;
                }

                return string.IsNullOrWhiteSpace(item.Translation) ? null : item.Translation;
            }
        }

        return null;
    }

    /// <summary>章節標題比對用的正規化：移除 {\…} 標記、所有空白（含全形空白、換行）合併成一個半形空白、去頭尾空白。</summary>
    private static string NormalizeTitle(string text)
    {
        var stripped = OverlayTags.Replace(text, string.Empty);
        var builder = new StringBuilder(stripped.Length);
        var pendingSpace = false;
        foreach (var ch in stripped)
        {
            if (char.IsWhiteSpace(ch))
            {
                // 頭尾不留空白：只有已經有字時才記待補的半形空白
                pendingSpace = builder.Length > 0;
            }
            else
            {
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
