using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 生成式字幕前處理（DI singleton）：字幕載入後在背景把 cue 每 100 段一批交給知識庫模型
/// 重新斷句、修正聽錯的詞、去掉方括號標記，結果寫進 caption_sentences（ICaptionStore）與
/// CaptionSentenceState；從目前的播放位置那批開始處理，前後批之後再補。
/// 一批失敗（LLM 例外、兩次都無法解析、驗證後是空的）只略過這次、繼續下一批，下次開同一支影片再試。
/// </summary>
public sealed class CaptionPreprocessor
{
    /// <summary>每批處理的 cue 段數。</summary>
    private const int ChunkSize = 100;

    /// <summary>本批前面多給幾段當前文（只供模型參考）。</summary>
    private const int ContextBefore = 3;

    /// <summary>本批後面多給幾段當後文（供最後一句補完整）。</summary>
    private const int LookAhead = 5;

    /// <summary>名詞清單（含卡名）的上限。</summary>
    private const int MaxTerms = 150;

    /// <summary>句子與原始字幕的相似度下限（CaptionMatcher.Similarity），避免模型編造內容。</summary>
    private const double SimilarityThreshold = 0.3;

    /// <summary>GLM 偶爾漏掉物件之間的逗號（}{ 之間只有換行）：補上逗號再解析一次。</summary>
    private static readonly Regex MissingComma = new(@"\}\s*\n\s*\{", RegexOptions.Compiled);

    /// <summary>YouTube 自動字幕的方括號標記（[音楽]、[拍手] 等）。</summary>
    private static readonly Regex BracketTags = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    private readonly ILorebookLlm _llm;
    private readonly ICaptionStore _store;
    private readonly GlossaryState _glossary;
    private readonly ISettingsService _settings;
    private readonly IPlaybackClock? _clock;
    private readonly LlmLanes? _lanes;
    private readonly CardReadinessGate? _cardGate;
    private readonly ILogger<CaptionPreprocessor> _logger;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public CaptionPreprocessor(
        ILorebookLlm llm,
        ICaptionStore store,
        CaptionSentenceState sentences,
        GlossaryState glossary,
        ISettingsService settings,
        IPlaybackClock? clock,
        ILogger<CaptionPreprocessor> logger,
        LlmLanes? lanes = null,
        CardReadinessGate? cardGate = null)
    {
        _llm = llm;
        _store = store;
        Sentences = sentences;
        _glossary = glossary;
        _settings = settings;
        _clock = clock;
        _lanes = lanes;
        _cardGate = cardGate;
        _logger = logger;
    }

    /// <summary>前處理句子的記憶體狀態（CaptionLoader 重設時經由這裡取得）。</summary>
    public CaptionSentenceState Sentences { get; }

    /// <summary>處理每一批時每隔多久檢查一次快轉（把正在跑的這批依插隊門檻取消）。</summary>
    public TimeSpan SeekPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>要附加到狀態列的一行文字（開始／完成／從快取載入）；可能從背景執行緒觸發。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>字幕載入後開始前處理：停掉上一次的工作、重設句子狀態，背景逐批處理（不等待）。
    /// 知識庫模型沒設定（Lorebook.ApiHost 或 Model 空白）時只記 log 直接返回。</summary>
    public void Start(long captionId, string videoId, IReadOnlyList<CaptionCue> cues)
    {
        Stop();
        Sentences.Reset(videoId, captionId);

        var lorebook = _settings.Current.Lorebook;
        if (string.IsNullOrWhiteSpace(lorebook.ApiHost) || string.IsNullOrWhiteSpace(lorebook.Model))
        {
            _logger.LogInformation("未設定知識庫模型，略過生成式字幕前處理");
            return;
        }

        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts = cts = new CancellationTokenSource();
        }

        _ = Task.Run(() => RunAsync(captionId, videoId, cues, cts.Token));
    }

    /// <summary>取消目前的背景工作（換影片或重設時呼叫）。</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    private async Task RunAsync(long captionId, string videoId, IReadOnlyList<CaptionCue> cues, CancellationToken ct)
    {
        try
        {
            // 已處理過的句子先從資料庫載入（store 是同步方法，RunAsync 已在背景執行緒）
            var stored = await Task.Run(() => _store.GetSentences(captionId), ct).ConfigureAwait(false);
            if (stored.Count > 0)
            {
                Sentences.Add(videoId, stored);
            }

            var total = (cues.Count + ChunkSize - 1) / ChunkSize;
            var sentencesNow = Sentences.Sentences;
            var complete = new bool[total];
            var done = 0;
            for (var k = 0; k < total; k++)
            {
                if (IsBatchComplete(cues, k, sentencesNow))
                {
                    complete[k] = true;
                    done++;
                }
            }

            if (done == total)
            {
                StatusChanged?.Invoke($"生成式字幕前處理：已從快取載入 {Sentences.Sentences.Count} 句");
                return;
            }

            // 詞彙卡還沒就緒（開台資料探勘進行中）：等門打開或逾時才開始，
            // 之後每批組 prompt 時才讀名詞（BuildTerms），prompt 才會帶到新建的詞彙卡
            if (_cardGate is { IsPending: true } gate)
            {
                StatusChanged?.Invoke("生成式字幕前處理：等資料探勘完成…");
                var waitSeconds = Math.Max(0, _settings.Current.Vod.MiningWaitSeconds);
                var ready = await gate.WaitAsync(TimeSpan.FromSeconds(waitSeconds), ct).ConfigureAwait(false);
                if (ready)
                {
                    _logger.LogInformation("生成式字幕前處理：資料探勘完成，開始處理");
                }
                else
                {
                    _logger.LogInformation("生成式字幕前處理：等資料探勘逾時（{Seconds} 秒），先開始處理", waitSeconds);
                }
            }

            StatusChanged?.Invoke($"生成式字幕前處理：開始（共 {total} 批，已完成 {done} 批）");

            // 一次只處理一批：優先播放位置之後（含）的第一個未完成批，沒有才往回補
            var processed = new HashSet<int>();

            // 目前播放位置落在第幾批（最後一批到無限大）；沒有位置資訊時為 null
            int? BatchAtPosition()
            {
                if (_clock?.GetPositionMs() is not { } positionMs)
                {
                    return null;
                }

                for (var k = total - 1; k >= 0; k--)
                {
                    if (positionMs >= cues[k * ChunkSize].StartMs)
                    {
                        return k;
                    }
                }

                return 0;
            }

            while (!ct.IsCancellationRequested)
            {
                var p = BatchAtPosition() ?? 0;

                var next = -1;
                for (var k = p; k < total; k++)
                {
                    if (!complete[k] && !processed.Contains(k))
                    {
                        next = k;
                        break;
                    }
                }
                if (next < 0)
                {
                    for (var k = 0; k < p; k++)
                    {
                        if (!complete[k] && !processed.Contains(k))
                        {
                            next = k;
                            break;
                        }
                    }
                }
                if (next < 0)
                {
                    break;
                }

                // 播放位置之後（含）是 PreprocessNear，之前往回補的是 PreprocessBackfill
                var priority = next >= p ? LlmPriority.PreprocessNear : LlmPriority.PreprocessBackfill;
                using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var call = new LlmCallInfo(priority);
                Task<bool> task;
                using (LlmPriorityScope.Enter(call))
                {
                    task = ProcessBatchAsync(next, captionId, videoId, cues, complete, batchCts.Token);
                }

                // 這批還在跑時每隔 SeekPollInterval 檢查一次快轉：新位置不在手上這批、目標批未處理、
                // 而且預估進度未達插隊門檻時，取消手上這批改處理新位置那批（之後再回來補）
                var seekCancelled = false;
                while (!task.IsCompleted && !ct.IsCancellationRequested)
                {
                    var finished = await Task.WhenAny(task, Task.Delay(SeekPollInterval, ct)).ConfigureAwait(false);
                    if (finished == task)
                    {
                        break;
                    }

                    if (seekCancelled || _lanes is null || BatchAtPosition() is not { } q ||
                        q == next || complete[q] || processed.Contains(q))
                    {
                        continue;
                    }

                    var progress = _lanes.Lorebook.EstimateProgress(call);
                    var threshold = Math.Clamp(_settings.Current.Vod.PreemptThresholdPercent, 0, 100);
                    if (progress is null || progress * 100 >= threshold)
                    {
                        continue;
                    }

                    seekCancelled = true;
                    batchCts.Cancel();
                    _logger.LogInformation(
                        "生成式字幕前處理：快轉到第 {To}/{Total} 批，取消第 {From}/{Total} 批（預估進度 {Progress:P0}）",
                        q + 1, total, next + 1, total, progress);
                }

                var ok = false;
                try
                {
                    ok = await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (seekCancelled && !ct.IsCancellationRequested)
                {
                    // 快轉取消：這批不算處理過也不算失敗，下一輪依新位置選批，之後再回來補這批
                    continue;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // LLM 例外等：這批這次略過，繼續下一批，不重試同一批
                    _logger.LogWarning(ex, "生成式字幕前處理：第 {Batch}/{Total} 批失敗，這次略過", next + 1, total);
                }

                processed.Add(next);
                if (ok)
                {
                    complete[next] = true;
                    done++;
                }

                // 不論成功或失敗，每完成一批就回報進度（done 只計成功的批）
                StatusChanged?.Invoke($"生成式字幕前處理：進行中（{done}/{total} 批）");
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            StatusChanged?.Invoke($"生成式字幕前處理：完成，共 {Sentences.Sentences.Count} 句");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 被 Stop 或新的 Start 取代：安靜結束
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "生成式字幕前處理背景工作失敗：{VideoId}", videoId);
        }
    }

    /// <summary>處理第 k 批：組 prompt → 呼叫模型 → 解析（壞 JSON 補逗號重試、再壞重送一次）→ 驗證 → 換算時間 → 存檔。
    /// 回傳這批是否成功（含「整批已被前一句涵蓋」的直接完成）。</summary>
    private async Task<bool> ProcessBatchAsync(int k, long captionId, string videoId,
        IReadOnlyList<CaptionCue> cues, bool[] complete, CancellationToken ct)
    {
        var count = cues.Count;
        var total = complete.Length;
        var mainEnd = Math.Min((k + 1) * ChunkSize, count) - 1;
        var mainStart = k * ChunkSize;

        // 前一批已完成、而且它的最後一句延伸到本批：本批從那句結束段的下一段開始
        if (k > 0 && complete[k - 1] && LastSentenceOfBatch(cues, k - 1) is { } carried)
        {
            var carriedTo = LastCueIndexBefore(cues, carried.EndMs);
            if (carriedTo >= mainStart)
            {
                mainStart = carriedTo + 1;
                if (mainStart > mainEnd)
                {
                    // 整批都被前一句涵蓋：直接算完成，不必叫模型
                    _logger.LogInformation("生成式字幕前處理：第 {Batch}/{Total} 批已由前一句涵蓋，視為完成", k + 1, total);
                    return true;
                }
            }
        }

        var before = new List<(int Index, string Text)>();
        for (var i = Math.Max(0, mainStart - ContextBefore); i < mainStart; i++)
        {
            before.Add((i, cues[i].Text));
        }

        var main = new List<(int Index, string Text)>(mainEnd - mainStart + 1);
        for (var i = mainStart; i <= mainEnd; i++)
        {
            main.Add((i, cues[i].Text));
        }

        // 後文：下一批已完成時不給，避免和已存的句子重疊
        var after = new List<(int Index, string Text)>();
        if (mainEnd + 1 < count && !(k + 1 < total && complete[k + 1]))
        {
            var afterEnd = Math.Min(mainEnd + LookAhead, count - 1);
            for (var i = mainEnd + 1; i <= afterEnd; i++)
            {
                after.Add((i, cues[i].Text));
            }
        }

        var user = CaptionPreprocessPrompts.BuildUser(BuildTerms(), before, main, after);

        var stopwatch = Stopwatch.StartNew();
        var tokens = 0;
        List<RawSentence>? parsed = null;
        LlmJsonResult? result = null;
        using (var purpose = LlmCallPurposeScope.Enter(LlmCallPurposes.CaptionPreprocess))
        {
            for (var attempt = 0; attempt < 2 && parsed is null; attempt++)
            {
                result = await _llm.CompleteJsonAsync(CaptionPreprocessPrompts.System, user, ct).ConfigureAwait(false);
                tokens += result.PromptTokens + result.CompletionTokens;
                parsed = TryParseSentences(result.Content);
            }
        }

        if (parsed is null)
        {
            _logger.LogWarning("生成式字幕前處理：第 {Batch}/{Total} 批兩次都無法解析，這次略過", k + 1, total);
            return false;
        }

        var accepted = new List<CaptionSentence>();
        var dropped = 0;
        var toUpper = after.Count > 0 ? after[^1].Index : mainEnd;
        var prevFrom = mainStart;

        foreach (var sentence in parsed)
        {
            // from 要在本批範圍內、to 不能超過後文最後一段（沒有後文時是 mainEnd），且 from <= to
            if (sentence.From < mainStart || sentence.From > mainEnd || sentence.To < sentence.From || sentence.To > toUpper)
            {
                dropped++;
                continue;
            }

            // from 只能往前走（可以和前一句相同或共用段落）
            if (sentence.From < prevFrom)
            {
                dropped++;
                continue;
            }

            var text = BracketTags.Replace(sentence.Text, string.Empty).Trim();
            if (text.Length == 0)
            {
                dropped++;
                continue;
            }

            var rawText = BuildRawText(cues, sentence.From, sentence.To);
            if (CaptionMatcher.Similarity(text, rawText) < SimilarityThreshold)
            {
                dropped++;
                continue;
            }

            var startMs = cues[sentence.From].StartMs;
            var endMs = cues[sentence.To].StartMs + cues[sentence.To].DurationMs;
            // (caption_id, start_ms) 是唯一鍵：共用段落時前後句只差 1 毫秒，不影響排序與定位
            if (accepted.Count > 0 && accepted[^1].StartMs >= startMs)
            {
                startMs = accepted[^1].StartMs + 1;
            }

            accepted.Add(new CaptionSentence(startMs, endMs, rawText, text, result!.Model));
            prevFrom = sentence.From;
        }

        stopwatch.Stop();
        _logger.LogInformation(
            "生成式字幕前處理：第 {Batch}/{Total} 批，{Count} 句（丟掉 {Dropped}），{Tokens} tokens，{Elapsed} ms",
            k + 1, total, accepted.Count, dropped, tokens, stopwatch.ElapsedMilliseconds);

        if (accepted.Count == 0)
        {
            _logger.LogWarning("生成式字幕前處理：第 {Batch}/{Total} 批驗證後是空的，這次略過", k + 1, total);
            return false;
        }

        await Task.Run(() => _store.SaveSentences(captionId, accepted), CancellationToken.None).ConfigureAwait(false);
        Sentences.Add(videoId, accepted);
        return true;
    }

    /// <summary>生效中詞彙卡的名詞（Source=Target 對照）與卡名本身（Source、Target 都是卡名），
    /// 依 Source 去重，最多 <see cref="MaxTerms"/> 筆。</summary>
    private IReadOnlyList<PreprocessTerm> BuildTerms()
    {
        var catalog = _glossary.Catalog;
        var terms = new List<PreprocessTerm>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in _glossary.ActiveIds)
        {
            if (catalog.FindCard(id) is not { } card)
            {
                continue;
            }

            AddTerm(card.Name, card.Name, card.Name);
            foreach (var term in catalog.TermsOf(id))
            {
                AddTerm(term.Source, term.Target, card.Name);
            }
        }

        return terms;

        void AddTerm(string? source, string? target, string? cardName)
        {
            if (terms.Count >= MaxTerms)
            {
                return;
            }

            var s = (source ?? string.Empty).Trim();
            var t = (target ?? string.Empty).Trim();
            var name = (cardName ?? string.Empty).Trim();
            if (s.Length == 0 || t.Length == 0 || name.Length == 0 || !seen.Add(s))
            {
                return;
            }

            terms.Add(new PreprocessTerm(s, t, name));
        }
    }

    /// <summary>取第一個 { 到最後一個 } 之間的 JSON 解析；失敗時補上漏掉的逗號再試一次；再失敗回傳 null。</summary>
    private static List<RawSentence>? TryParseSentences(string content)
    {
        try
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }

            var json = content[start..(end + 1)];
            try
            {
                return ParseSentences(json);
            }
            catch (JsonException)
            {
                return ParseSentences(MissingComma.Replace(json, "},\n{"));
            }
        }
        catch (Exception)
        {
            // 解析或修補都失敗：交由呼叫端決定重送
            return null;
        }
    }

    private static List<RawSentence> ParseSentences(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("sentences", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("回覆缺少 sentences 陣列");
        }

        var result = new List<RawSentence>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            int? from = null;
            int? to = null;
            string? text = null;
            if (item.TryGetProperty("from", out var fromElement))
            {
                from = ReadInt(fromElement);
            }
            if (item.TryGetProperty("to", out var toElement))
            {
                to = ReadInt(toElement);
            }
            if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                text = textElement.GetString();
            }

            // 缺欄位的句子直接不列入（驗證規則會把它當不存在）
            if (from is null || to is null || text is null)
            {
                continue;
            }

            result.Add(new RawSentence(from.Value, to.Value, text));
        }

        return result;
    }

    /// <summary>讀整數欄位：數字或字串（GLM 偶爾把數字回成字串）都接受，其他回 null。</summary>
    private static int? ReadInt(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var value) ? value : null,
            JsonValueKind.String when int.TryParse(element.GetString(), out var value) => value,
            _ => null,
        };

    /// <summary>cues[from..to] 的 Text 以空白串接並去掉方括號標記。</summary>
    private static string BuildRawText(IReadOnlyList<CaptionCue> cues, int from, int to)
    {
        var parts = new string[to - from + 1];
        for (var i = from; i <= to; i++)
        {
            parts[i - from] = cues[i].Text;
        }
        return BracketTags.Replace(string.Join(" ", parts), string.Empty).Trim();
    }

    /// <summary>該批是否已完成：有任何一句的 StartMs 落在批的時間範圍 [cues[k*100].StartMs, 下一批第一段的 StartMs) 內；
    /// 最後一批到無限大。</summary>
    private bool IsBatchComplete(IReadOnlyList<CaptionCue> cues, int k, IReadOnlyList<CaptionSentence> sentences)
    {
        var from = cues[k * ChunkSize].StartMs;
        var to = k + 1 < (cues.Count + ChunkSize - 1) / ChunkSize ? cues[(k + 1) * ChunkSize].StartMs : long.MaxValue;
        foreach (var sentence in sentences)
        {
            if (sentence.StartMs >= from && sentence.StartMs < to)
            {
                return true;
            }
            if (sentence.StartMs >= to)
            {
                break;
            }
        }
        return false;
    }

    /// <summary>該批時間範圍內 StartMs 最後一句（句子依 StartMs 排序）；沒有時為 null。</summary>
    private CaptionSentence? LastSentenceOfBatch(IReadOnlyList<CaptionCue> cues, int k)
    {
        var from = cues[k * ChunkSize].StartMs;
        var to = k + 1 < (cues.Count + ChunkSize - 1) / ChunkSize ? cues[(k + 1) * ChunkSize].StartMs : long.MaxValue;
        CaptionSentence? last = null;
        foreach (var sentence in Sentences.Sentences)
        {
            if (sentence.StartMs >= from && sentence.StartMs < to)
            {
                last = sentence;
            }
            if (sentence.StartMs >= to)
            {
                break;
            }
        }
        return last;
    }

    /// <summary>最後一個 StartMs &lt; timeMs 的 cue 索引（句子換算 to 段用）；沒有為 -1。</summary>
    private static int LastCueIndexBefore(IReadOnlyList<CaptionCue> cues, long timeMs)
    {
        var result = -1;
        var lo = 0;
        var hi = cues.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (cues[mid].StartMs < timeMs)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }

    /// <summary>模型回覆裡的一句（未驗證）。</summary>
    private sealed record RawSentence(int From, int To, string Text);
}
