using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>CaptionPreprocessor：生成式字幕前處理（假 LLM、假 store、假 clock，不連網、不連資料庫）。</summary>
public class CaptionPreprocessorTests
{
    private const string VideoId = "dQw4w9WgXcQ";
    private const long CaptionId = 9;

    private static CaptionCue[] Cues(int count)
    {
        var cues = new CaptionCue[count];
        for (var i = 0; i < count; i++)
        {
            cues[i] = new CaptionCue(i * 1000, 500, $"セリフ{i}");
        }
        return cues;
    }

    /// <summary>把 user 訊息裡「本批字幕」的每一段回成 from=to=該段的句子（text 與 cue 相同，相似度 1）。</summary>
    private static string EchoMainReply(string userPrompt)
    {
        var inMain = false;
        var sentences = new List<string>();
        foreach (var line in userPrompt.Split('\n'))
        {
            if (line.StartsWith("## 本批字幕", StringComparison.Ordinal))
            {
                inMain = true;
                continue;
            }
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                inMain = false;
                continue;
            }
            if (!inMain || line.Length == 0 || line[0] != '[')
            {
                continue;
            }

            var close = line.IndexOf(']');
            var index = int.Parse(line[1..close]);
            var text = line[(close + 1)..].Trim();
            sentences.Add($"{{\"from\":{index},\"to\":{index},\"text\":\"{text}\"}}");
        }
        return "{\"sentences\":[" + string.Join(",", sentences) + "]}";
    }

    /// <summary>取 user 訊息「本批字幕」第一段的編號（辨別處理到哪一批）。</summary>
    private static int FirstMainIndex(string userPrompt)
    {
        var inMain = false;
        foreach (var line in userPrompt.Split('\n'))
        {
            if (line.StartsWith("## 本批字幕", StringComparison.Ordinal))
            {
                inMain = true;
                continue;
            }
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                inMain = false;
            }
            else if (inMain && line.Length > 0 && line[0] == '[')
            {
                var close = line.IndexOf(']');
                return int.Parse(line[1..close]);
            }
        }
        throw new InvalidOperationException("user 訊息沒有本批字幕");
    }

    private sealed class TestContext
    {
        public FakeLorebookLlm Llm { get; } = new();
        public FakeCaptionStore Store { get; } = new();
        public FakeClock Clock { get; } = new();
        public CaptionSentenceState Sentences { get; } = new();
        public GlossaryState Glossary { get; } = new();
        public FakeSettingsService Settings { get; } = new();
        public Lanes.ManualTimeProvider Time { get; } = new();
        public LlmLanes? Lanes { get; set; }
        public CardReadinessGate? Gate { get; set; }
        public ConcurrentQueue<string> Status { get; } = [];

        public CaptionPreprocessor Build()
        {
            // 有 lanes 時假 LLM 要包進排程器，前處理的快轉取消才會作用在 lane 上
            ILorebookLlm llm = Lanes is { } lanes ? new ScheduledLorebookLlm(Llm, lanes.Lorebook) : Llm;
            var preprocessor = new CaptionPreprocessor(
                llm, Store, Sentences, Glossary, Settings, Clock, NullLogger<CaptionPreprocessor>.Instance, Lanes, Gate);
            preprocessor.StatusChanged += Status.Enqueue;
            return preprocessor;
        }
    }

    private static TestContext CreateContext(bool configured = true)
    {
        var context = new TestContext();
        if (configured)
        {
            context.Settings.Current.Lorebook.ApiHost = "http://127.0.0.1:8300/v1";
            context.Settings.Current.Lorebook.Model = "glm-test";
        }
        return context;
    }

    /// <summary>輪詢直到條件成立（逾時 5 秒視為失敗）：不用固定的 Delay 等背景工作。</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等候條件逾時");
            await Task.Delay(10);
        }
    }

    /// <summary>輪詢度過指定的時間：供「沒有再送」這類反面條件等到超過數個背景週期再斷言。</summary>
    private static async Task PassAsync(TimeSpan time)
    {
        var deadline = DateTime.UtcNow + time;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    // 1. 250 段 cue 分 3 批；clock 在第 2 批時，處理順序是第 2 批 → 第 3 批 → 第 1 批。
    [Fact]
    public async Task Start_ClockAtSecondBatch_ProcessesSecondThenThirdThenFirst()
    {
        var context = CreateContext();
        var cues = Cues(250);
        context.Clock.Position = cues[100].StartMs;
        context.Llm.Handler = EchoMainReply;
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);

        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        Assert.Equal([100, 200, 0], context.Llm.UserPrompts.Select(FirstMainIndex).ToList());
        Assert.Contains("生成式字幕前處理：開始（共 3 批，已完成 0 批）", context.Status);
        Assert.Contains($"生成式字幕前處理：完成，共 {250} 句", context.Status);
        Assert.Equal(250, context.Sentences.Sentences.Count);
        Assert.Equal(3, context.Store.Saves.Count);
    }

    // 1b. 3 批都要處理時：每完成一批回報一次進行中，done 從 1 數到 3。
    [Fact]
    public async Task Start_EveryBatchProcessed_ReportsProgressAfterEachBatch()
    {
        var context = CreateContext();
        var cues = Cues(250);
        context.Clock.Position = cues[100].StartMs;
        context.Llm.Handler = EchoMainReply;
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);

        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        var progress = context.Status.Where(s => s.StartsWith("生成式字幕前處理：進行中", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["生成式字幕前處理：進行中（1/3 批）", "生成式字幕前處理：進行中（2/3 批）", "生成式字幕前處理：進行中（3/3 批）"],
            progress);
    }

    // 1c. 第 1 批已從資料庫載入：第一個進行中訊息就從 2/3 開始（done 含已載入的批）。
    [Fact]
    public async Task Start_FirstBatchLoadedFromDb_FirstProgressStartsAtLoadedCount()
    {
        var context = CreateContext();
        var cues = Cues(250);
        context.Store.Seeded[CaptionId] = [new CaptionSentence(cues[10].StartMs, cues[50].StartMs + 500, "セリフ10", "セリフ10", "舊模型")];
        context.Clock.Position = cues[100].StartMs;
        context.Llm.Handler = EchoMainReply;
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);

        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        var progress = context.Status.Where(s => s.StartsWith("生成式字幕前處理：進行中", StringComparison.Ordinal)).ToList();
        Assert.Equal("生成式字幕前處理：進行中（2/3 批）", progress.First());
        Assert.Equal(
            ["生成式字幕前處理：進行中（2/3 批）", "生成式字幕前處理：進行中（3/3 批）"],
            progress);
    }

    // 2. 假 store 已有第 1 批的句子：不再處理第 1 批；開始訊息含「已完成 1 批」。
    [Fact]
    public async Task Start_FirstBatchAlreadyStored_SkipsItAndReportsDoneCount()
    {
        var context = CreateContext();
        var cues = Cues(250);
        context.Store.Seeded[CaptionId] = [new CaptionSentence(cues[10].StartMs, cues[50].StartMs + 500, "セリフ10", "セリフ10", "舊模型")];
        context.Clock.Position = cues[100].StartMs;
        context.Llm.Handler = EchoMainReply;
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);

        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        Assert.Contains("生成式字幕前處理：開始（共 3 批，已完成 1 批）", context.Status);
        // 第 1 批（cue 0-99）不再處理：每個請求的本批都從 100 之後開始
        Assert.DoesNotContain(context.Llm.UserPrompts, prompt => FirstMainIndex(prompt) < 100);
        Assert.Equal([100, 200], context.Llm.UserPrompts.Select(FirstMainIndex).ToList());
        // 種子 1 句 + 第 2 批 100 句 + 第 3 批（尾批）50 句
        Assert.Equal(151, context.Sentences.Sentences.Count);
        Assert.Equal(2, context.Store.Saves.Count);
    }

    // 3. 前一批最後一句延伸到本批的前兩段時，本批送出的「本批字幕」從第 3 段開始。
    [Fact]
    public async Task Start_PreviousBatchLastSentenceExtendsIntoBatch_MainStartsAfterIt()
    {
        var context = CreateContext();
        var cues = Cues(250);
        // 第 1 批（k=0）已完成，最後一句涵蓋到 cue 101（本批的前兩段 100、101）
        context.Store.Seeded[CaptionId] = [new CaptionSentence(cues[0].StartMs, cues[101].StartMs + 500, "全文", "前一批的句子", "舊模型")];
        context.Llm.Handler = EchoMainReply;
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);

        await UntilAsync(() => context.Llm.CallCount >= 1);
        // 第 2 批（k=1）的本批從 102 開始（略過已被前一句涵蓋的 100、101）
        Assert.Equal(102, FirstMainIndex(context.Llm.UserPrompts[0]));
    }

    // 4. 下一批已完成時，user 訊息不含「## 後文」。
    [Fact]
    public async Task Start_NextBatchAlreadyDone_UserPromptHasNoAfterSection()
    {
        var context = CreateContext();
        var cues = Cues(250);
        // 第 3 批（k=2）已有句子：處理第 2 批（k=1）時不給後文
        context.Store.Seeded[CaptionId] = [new CaptionSentence(cues[200].StartMs, cues[210].StartMs, "セリフ200", "セリフ200", "舊模型")];
        context.Clock.Position = cues[100].StartMs;
        context.Llm.Handler = EchoMainReply;
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);

        await UntilAsync(() => context.Llm.CallCount >= 1);
        Assert.DoesNotContain("## 後文", context.Llm.UserPrompts[0], StringComparison.Ordinal);
        // 前文照給（mainStart 前面 3 段）
        Assert.Contains("## 前文（只供參考）", context.Llm.UserPrompts[0], StringComparison.Ordinal);
        Assert.Contains("[97] セリフ97", context.Llm.UserPrompts[0], StringComparison.Ordinal);
    }

    // 5. 驗證規則：範圍外、往回跳、不像 raw 的丟掉；[音楽] 去掉；共用段落 StartMs 相差 1。
    [Fact]
    public async Task Start_ValidationRules_DropInvalidAndAdjustStartMs()
    {
        var context = CreateContext();
        var cues = new CaptionCue[]
        {
            new(0, 500, "こんにちは皆さん"),
            new(1000, 500, "本日はいい天気"),
            new(2000, 500, "そうですね"),
            new(3000, 500, "全然違う"),
            new(4000, 500, "[音楽]ありがとうございます"),
            new(5000, 500, "また明日ね"),
        };
        context.Llm.Replies.Enqueue(
            "{\"sentences\":[" +
            "{\"from\":6,\"to\":6,\"text\":\"範囲外の文\"}," +              // from 超出本批範圍 → 丟掉
            "{\"from\":2,\"to\":2,\"text\":\"そうですね\"}," +             // 接受
            "{\"from\":1,\"to\":1,\"text\":\"本日はいい天気\"}," +         // from 往回跳 → 丟掉
            "{\"from\":3,\"to\":3,\"text\":\"まったく別の文章ですううう\"}," + // 和 raw 完全不像 → 丟掉
            "{\"from\":4,\"to\":4,\"text\":\"[音楽]ありがとうございます\"}," + // [音楽] 去掉後接受
            "{\"from\":5,\"to\":5,\"text\":\"また明日ね\"}," +              // 接受
            "{\"from\":5,\"to\":5,\"text\":\"また明日ね\"}" +               // 共用段落：StartMs + 1
            "]}");
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);

        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        var sentences = context.Sentences.Sentences;
        Assert.Equal([2000L, 4000L, 5000L, 5001L], sentences.Select(s => s.StartMs).ToList());
        Assert.Equal(["そうですね", "ありがとうございます", "また明日ね", "また明日ね"], sentences.Select(s => s.Text).ToList());
        Assert.All(sentences, s => Assert.DoesNotContain("[音楽]", s.Text, StringComparison.Ordinal));
        // RawText 是對應 cue 的原文（去掉方括號標記）
        Assert.Equal("ありがとうございます", sentences[1].RawText);
        Assert.Equal("fake-model", sentences[0].Model);
        // 只送一次（JSON 沒壞不重送），只有一批
        Assert.Equal(1, context.Llm.CallCount);
        var saved = Assert.Single(context.Store.Saves);
        Assert.Equal(4, saved.Sentences.Count);
    }

    // 6a. JSON 少逗號（}{ 之間只有換行）：修補後能解析，不重送。
    [Fact]
    public async Task Start_MissingCommaBetweenObjects_RepairedWithoutResend()
    {
        var context = CreateContext();
        var cues = Cues(2);
        context.Llm.Replies.Enqueue(
            "{\"sentences\":[{\"from\":0,\"to\":0,\"text\":\"セリフ0\"}\n{\"from\":1,\"to\":1,\"text\":\"セリフ1\"}]}");
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);

        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        Assert.Equal(1, context.Llm.CallCount);
        Assert.Equal(2, context.Sentences.Sentences.Count);
    }

    // 6b. 完全壞掉時重送一次；第二次也壞就這批失敗，繼續下一批。
    [Fact]
    public async Task Start_CompletelyBrokenJson_RetriesOnceThenContinuesNextBatch()
    {
        var context = CreateContext();
        var cues = Cues(150);
        context.Llm.Replies.Enqueue("完全不是 JSON");
        context.Llm.Replies.Enqueue("還是不是 JSON");
        // 第 2 批（k=1）的正常回覆
        context.Llm.Replies.Enqueue("{\"sentences\":[{\"from\":100,\"to\":100,\"text\":\"セリフ100\"}]}");
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);

        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        // 第 1 批叫了兩次（重送一次），第 2 批一次
        Assert.Equal(3, context.Llm.CallCount);
        Assert.Equal(100, FirstMainIndex(context.Llm.UserPrompts[2]));
        // 第 1 批失敗不存檔，第 2 批存了 1 句
        var saved = Assert.Single(context.Store.Saves);
        Assert.Single(saved.Sentences);
        Assert.Equal("セリフ100", Assert.Single(context.Sentences.Sentences).Text);
        Assert.Contains("生成式字幕前處理：完成，共 1 句", context.Status);
    }

    // 7. LLM 擲例外：這批略過、繼續下一批，不往外拋。
    [Fact]
    public async Task Start_LlmThrows_SkipsBatchAndContinuesWithoutThrowing()
    {
        var context = CreateContext();
        var cues = Cues(150);
        context.Llm.Throw = new InvalidOperationException("llm down");
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);

        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        // 兩批各試一次，都不重試
        Assert.Equal(2, context.Llm.CallCount);
        Assert.Empty(context.Store.Saves);
        Assert.Empty(context.Sentences.Sentences);
        Assert.Contains("生成式字幕前處理：完成，共 0 句", context.Status);
    }

    // 8. 知識庫模型沒設定：不呼叫 LLM（Start 同步返回，沒有背景工作）。
    [Fact]
    public void Start_LorebookNotConfigured_DoesNotCallLlm()
    {
        var context = CreateContext(configured: false);
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, Cues(20));

        Assert.Equal(0, context.Llm.CallCount);
        Assert.Empty(context.Status);
        // 對象仍有設好（Reset 在設定檢查之前）
        Assert.Equal(VideoId, context.Sentences.VideoId);
        Assert.Equal(CaptionId, context.Sentences.CaptionId);
    }

    // 9. Stop() 之後不再呼叫 LLM。
    [Fact]
    public async Task Stop_AfterFirstCallBlocked_NoFurtherCalls()
    {
        var context = CreateContext();
        var cues = Cues(250);
        // 第 1 批（k=0）的正常回覆，但第一次呼叫先擋在閘門上
        context.Llm.Replies.Enqueue("{\"sentences\":[{\"from\":0,\"to\":0,\"text\":\"セリフ0\"}]}");
        context.Llm.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);
        await UntilAsync(() => context.Llm.CallCount == 1);
        await UntilAsync(() => context.Status.Any(s => s.Contains("開始", StringComparison.Ordinal)));

        sut.Stop();
        context.Llm.Gate.SetResult();

        // 第 1 批會把擋住的那次跑完（存檔），但取消後不再處理第 2、3 批
        await UntilAsync(() => context.Store.Saves.Count == 1);
        Assert.Equal(1, context.Llm.CallCount);
        Assert.DoesNotContain(context.Status, s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal));
    }

    // 10. 名詞清單含生效卡的名詞和卡名；沒有生效卡時，user 訊息不含「## 專有名詞」。
    [Fact]
    public async Task Start_TermsSection_ContainsCardAndTermNames_OrIsOmitted()
    {
        var context = CreateContext();
        var cues = Cues(5);
        context.Llm.Handler = EchoMainReply;
        context.Glossary.SetCatalog(new GlossaryCatalog(
            [new GlossaryCard { Id = 7, Kind = GlossaryKind.Character, Name = "宙科そぴあ", Status = "Approved" }],
            [new GlossaryTerm { Id = 71, CardId = 7, Source = "そぴあ", Target = "Sopia", Status = "Approved" }]));
        var sut = context.Build();

        // 沒有生效卡：不含專有名詞段
        sut.Start(CaptionId, VideoId, cues);
        await UntilAsync(() => context.Llm.CallCount == 1);
        Assert.DoesNotContain("## 專有名詞", context.Llm.UserPrompts[0], StringComparison.Ordinal);
        // 等第一輪整個跑完（已存檔）再重開：否則第一輪的結果可能在第二輪 Reset 之後才寫進同一部影片的句子，
        // 第二輪會以為已處理完而不送模型（測試競態）
        await UntilAsync(() => context.Store.Saves.Count == 1);

        // 生效後：含卡名本身與卡的名詞
        context.Glossary.SetActiveIds([7]);
        sut.Start(CaptionId, VideoId, cues);
        await UntilAsync(() => context.Llm.CallCount == 2);
        Assert.Contains("## 專有名詞", context.Llm.UserPrompts[1], StringComparison.Ordinal);
        Assert.Contains("宙科そぴあ（宙科そぴあ）— 宙科そぴあ", context.Llm.UserPrompts[1], StringComparison.Ordinal);
        Assert.Contains("そぴあ（Sopia）— 宙科そぴあ", context.Llm.UserPrompts[1], StringComparison.Ordinal);
        Assert.StartsWith("## 專有名詞", context.Llm.UserPrompts[1], StringComparison.Ordinal);
    }

    // ===== 快轉取消（用 ManualTimeProvider、真的 LlmLane、假 LLM、假 clock）=====

    /// <summary>建立帶知識庫 lane 的 context：第 nearPriority 批次的中位數先種成 10 秒。</summary>
    private static TestContext CreateLaneContext()
    {
        var context = CreateContext();
        var lane = new LlmLane("知識庫模型", () => context.Settings.Current.Vod.PreemptThresholdPercent,
            context.Time, NullLogger<LlmLane>.Instance);
        lane.RecordDuration(LlmPriority.PreprocessNear, TimeSpan.FromSeconds(10));
        context.Lanes = new LlmLanes(lane);
        return context;
    }

    /// <summary>等一段比 SeekPollInterval 長的時間（輪詢），確認快轉檢查沒有取消任何請求。</summary>
    private static async Task SettleNoCancellationAsync(TestContext context, int milliseconds = 120)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            Assert.True(context.Llm.CancelledCalls == 0, "不該取消的快轉檢查取消了請求");
            await Task.Delay(10);
        }
    }

    // 11. 快轉取消：第 1 批的 LLM 請求不回應、預估進度 20% < 80%，位置改到第 3 批後取消第 1 批，
    //     下一個請求是第 3 批；最後 3 批都完成。
    [Fact]
    public async Task Start_SeekToUnprocessedBatch_BelowThreshold_CancelsCurrentAndSeeksFirst()
    {
        var context = CreateLaneContext();
        var cues = Cues(250);
        context.Clock.Position = cues[0].StartMs;
        var calls = 0;
        context.Llm.AsyncHandler = async (user, ct) =>
        {
            // 第 1 批的第一次請求一直不回應（直到被快轉取消）
            if (Interlocked.Increment(ref calls) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return EchoMainReply(user);
        };
        var sut = context.Build();
        sut.SeekPollInterval = TimeSpan.FromMilliseconds(20);

        sut.Start(CaptionId, VideoId, cues);
        await UntilAsync(() => Volatile.Read(ref calls) == 1);

        context.Time.Advance(TimeSpan.FromSeconds(2)); // 預估進度 20%
        context.Clock.Position = cues[200].StartMs; // 快轉到第 3 批

        await UntilAsync(() => context.Llm.CallCount >= 2);
        Assert.Equal(1, context.Llm.CancelledCalls);
        Assert.Equal(200, FirstMainIndex(context.Llm.UserPrompts[1]));

        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        // 取消的第 1 批不算處理過：第 1 批的第一次請求（被取消）→ 第 3 批 → 回補第 1 批 → 第 2 批，最後 3 批都完成
        Assert.Equal([0, 200, 0, 100], context.Llm.UserPrompts.Select(FirstMainIndex).ToList());
        Assert.Equal(250, context.Sentences.Sentences.Count);
        Assert.Equal(3, context.Store.Saves.Count);
    }

    // 12. 位置還在同一批裡移動：不取消，第 1 批完成後依原順序繼續。
    [Fact]
    public async Task Start_SeekWithinSameBatch_DoesNotCancel()
    {
        var context = CreateLaneContext();
        var cues = Cues(250);
        context.Clock.Position = cues[0].StartMs;
        var calls = 0;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Llm.AsyncHandler = async (user, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await releaseFirst.Task.WaitAsync(ct);
            }

            return EchoMainReply(user);
        };
        var sut = context.Build();
        sut.SeekPollInterval = TimeSpan.FromMilliseconds(20);

        sut.Start(CaptionId, VideoId, cues);
        await UntilAsync(() => Volatile.Read(ref calls) == 1);

        context.Time.Advance(TimeSpan.FromSeconds(2)); // 預估進度 20% < 80%，但位置仍在第 1 批
        context.Clock.Position = cues[50].StartMs;
        await SettleNoCancellationAsync(context);

        releaseFirst.SetResult();
        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        Assert.Equal(0, context.Llm.CancelledCalls);
        Assert.Equal([0, 100, 200], context.Llm.UserPrompts.Select(FirstMainIndex).ToList());
        Assert.Equal(3, context.Llm.CallCount);
    }

    // 13. 超過門檻：時間前進 9 秒（90% ≥ 80%）才快轉，不取消；第 1 批完成後，下一個處理的是第 3 批。
    [Fact]
    public async Task Start_SeekAboveThreshold_LetsBatchFinishThenProcessesNewPosition()
    {
        var context = CreateLaneContext();
        var cues = Cues(250);
        context.Clock.Position = cues[0].StartMs;
        var calls = 0;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Llm.AsyncHandler = async (user, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await releaseFirst.Task.WaitAsync(ct);
            }

            return EchoMainReply(user);
        };
        var sut = context.Build();
        sut.SeekPollInterval = TimeSpan.FromMilliseconds(20);

        sut.Start(CaptionId, VideoId, cues);
        await UntilAsync(() => Volatile.Read(ref calls) == 1);

        context.Time.Advance(TimeSpan.FromSeconds(9)); // 預估進度 90% ≥ 門檻 80%
        context.Clock.Position = cues[200].StartMs;
        await SettleNoCancellationAsync(context);

        releaseFirst.SetResult();
        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        Assert.Equal(0, context.Llm.CancelledCalls);
        Assert.Equal([0, 200, 100], context.Llm.UserPrompts.Select(FirstMainIndex).ToList());
        Assert.Equal(3, context.Llm.CallCount);
    }

    // 14. 新位置那批已經完成（從資料庫載入）：不取消。
    [Fact]
    public async Task Start_SeekToAlreadyCompletedBatch_DoesNotCancel()
    {
        var context = CreateLaneContext();
        var cues = Cues(250);
        context.Clock.Position = cues[0].StartMs;
        // 第 3 批（k=2）已從資料庫載入：complete[2] = true
        context.Store.Seeded[CaptionId] = [new CaptionSentence(cues[200].StartMs, cues[210].StartMs, "セリフ200", "セリフ200", "舊模型")];
        var calls = 0;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Llm.AsyncHandler = async (user, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await releaseFirst.Task.WaitAsync(ct);
            }

            return EchoMainReply(user);
        };
        var sut = context.Build();
        sut.SeekPollInterval = TimeSpan.FromMilliseconds(20);

        sut.Start(CaptionId, VideoId, cues);
        await UntilAsync(() => Volatile.Read(ref calls) == 1);

        context.Time.Advance(TimeSpan.FromSeconds(2)); // 預估進度 20% < 80%，但目標批已完成
        context.Clock.Position = cues[200].StartMs;
        await SettleNoCancellationAsync(context);

        releaseFirst.SetResult();
        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        Assert.Equal(0, context.Llm.CancelledCalls);
        // 第 3 批已完成：第 1 批 → 回補第 2 批
        Assert.Equal([0, 100], context.Llm.UserPrompts.Select(FirstMainIndex).ToList());
        Assert.Equal(2, context.Llm.CallCount);
    }

    // 15. 詞彙卡準備門等待中：不呼叫 LLM、不出現「開始」訊息，狀態列顯示等資料探勘。
    [Fact]
    public async Task Start_GatePending_WaitsForMiningWithoutCallingLlm()
    {
        var context = CreateContext();
        var cues = Cues(5);
        context.Llm.Handler = EchoMainReply;
        context.Gate = new CardReadinessGate(context.Time);
        context.Gate.Begin();
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);

        await UntilAsync(() => context.Status.Contains("生成式字幕前處理：等資料探勘完成…"));
        await PassAsync(TimeSpan.FromMilliseconds(150));
        Assert.Equal(0, context.Llm.CallCount);
        Assert.DoesNotContain(context.Status, s => s.StartsWith("生成式字幕前處理：開始", StringComparison.Ordinal));
        Assert.Empty(context.Sentences.Sentences);
        Assert.Empty(context.Store.Saves);
    }

    // 16. 門打開（探勘完成）之後才開始處理：送出「開始」訊息、批次照常完成。
    [Fact]
    public async Task Start_GateCompletedAfterWait_ProcessesAndCompletes()
    {
        var context = CreateContext();
        var cues = Cues(5);
        context.Llm.Handler = EchoMainReply;
        context.Gate = new CardReadinessGate(context.Time);
        context.Gate.Begin();
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);
        await UntilAsync(() => context.Status.Contains("生成式字幕前處理：等資料探勘完成…"));
        context.Gate.Complete();

        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：完成", StringComparison.Ordinal)));
        Assert.Equal(1, context.Llm.CallCount);
        Assert.Contains("生成式字幕前處理：開始（共 1 批，已完成 0 批）", context.Status);
        Assert.Equal(5, context.Sentences.Sentences.Count);
    }

    // 17. 已從快取載入（全部批次完成）：門還在等待中也直接載入，不呼叫 LLM、不出現等待訊息。
    [Fact]
    public async Task Start_AllBatchesCached_GatePending_LoadsWithoutWaiting()
    {
        var context = CreateContext();
        var cues = Cues(5);
        context.Store.Seeded[CaptionId] = [new CaptionSentence(cues[0].StartMs, cues[4].StartMs + 500, "全文", "一句", "舊模型")];
        context.Gate = new CardReadinessGate(context.Time);
        context.Gate.Begin();
        var sut = context.Build();

        sut.Start(CaptionId, VideoId, cues);

        await UntilAsync(() => context.Status.Any(s => s.StartsWith("生成式字幕前處理：已從快取載入", StringComparison.Ordinal)));
        Assert.Equal(0, context.Llm.CallCount);
        Assert.DoesNotContain(context.Status, s => s.Contains("等資料探勘", StringComparison.Ordinal));
    }

    private sealed class FakeLorebookLlm : ILorebookLlm
    {
        public Queue<string> Replies { get; } = new();

        /// <summary>非 null 時以 user 訊息動態產生回覆（優先於 Replies）。</summary>
        public Func<string, string>? Handler { get; set; }

        /// <summary>非 null 時以 user 訊息與 token 動態產生回覆（優先於 Handler）：可以自行等待或回應取消。</summary>
        public Func<string, CancellationToken, Task<string>>? AsyncHandler { get; set; }

        public Exception? Throw { get; set; }

        /// <summary>非 null 時每次呼叫先等它完成（模擬慢回覆，測 Stop 用）。</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>AsyncHandler 因 token 取消而擲 OperationCanceledException 的次數。</summary>
        public int CancelledCalls { get; private set; }

        public List<string> SystemPrompts { get; } = [];

        public List<string> UserPrompts { get; } = [];

        public int CallCount => UserPrompts.Count;

        public async Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            SystemPrompts.Add(systemPrompt);
            UserPrompts.Add(userPrompt);
            if (Throw is not null)
            {
                throw Throw;
            }

            if (Gate is { } gate)
            {
                await gate.Task;
            }

            string content;
            if (AsyncHandler is { } asyncHandler)
            {
                try
                {
                    content = await asyncHandler(userPrompt, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancelledCalls++;
                    throw;
                }
            }
            else
            {
                content = Handler is not null ? Handler(userPrompt) : (Replies.Count > 0 ? Replies.Dequeue() : "{\"sentences\":[]}");
            }

            return new LlmJsonResult(content, 100, 50, 5, "fake-model");
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }

    private sealed class FakeCaptionStore : ICaptionStore
    {
        public Dictionary<long, IReadOnlyList<CaptionSentence>> Seeded { get; } = new();

        public List<(long CaptionId, IReadOnlyList<CaptionSentence> Sentences)> Saves { get; } = new();

        public int GetCount { get; private set; }

        public StoredCaption? GetCaption(string videoId, string language) => null;

        public long? SaveCaption(string videoId, string language, bool isAuto, string? clientName, IReadOnlyList<CaptionCue> cues) => null;

        public void TouchCaption(long id)
        {
        }

        public IReadOnlyList<CaptionSentence> GetSentences(long captionId)
        {
            GetCount++;
            return Seeded.TryGetValue(captionId, out var sentences) ? sentences : [];
        }

        public void SaveSentences(long captionId, IReadOnlyList<CaptionSentence> sentences) =>
            Saves.Add((captionId, sentences));
    }

    private sealed class FakeClock : IPlaybackClock
    {
        public long? Position { get; set; }

        public long? GetPositionMs() => Position;
    }
}
