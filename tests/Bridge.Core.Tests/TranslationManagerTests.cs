using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class TranslationManagerTests
{
    private static AppSettings Settings(bool throughPass = false, int timeoutMs = 0, string replacementRules = "", bool conversation = false) => new()
    {
        ThroughPass = throughPass,
        ReplacementRules = replacementRules,
        Translation = new TranslationSettings { SystemPromptTemplate = "你是翻譯專家", TimeoutMs = timeoutMs, UseConversationMode = conversation }
    };

    private static TranslationManager Create(FakeOpenAiClient client, TempDatabase db, AppSettings settings, TimeProvider? timeProvider = null, PrefetchInFlight? prefetchInFlight = null) =>
        new(client, new TextProcessingService(), db.Service, new FakeSettingsService(settings), timeProvider, prefetchInFlight: prefetchInFlight);

    [Fact]
    public async Task TranslateAsync_WithGlossaryState_SystemPromptContainsSection()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var glossary = new GlossaryState();
        glossary.SetPromptSection("# 詞彙表（參考資料，原文=譯名，譯名請照用）\n## 英リサ\n英リサ=英莉莎");
        var sut = new TranslationManager(
            client, new TextProcessingService(), db.Service, new FakeSettingsService(Settings()), glossary: glossary);

        await sut.TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

        Assert.Contains("英リサ=英莉莎", client.LastSystemText);
        Assert.Contains("你是翻譯專家", client.LastSystemText);
    }

    // NormalizeSource 的結果就是 TranslateAsync 查快取用的值：寫進快取後 TranslateAsync 命中快取、不叫模型。
    [Fact]
    public async Task NormalizeSource_MatchesCacheKeyUsedByTranslateAsync()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var settings = Settings();
        settings.SourceReplacementRules = "ペコーラ=ぺこら";
        var sut = Create(client, db, settings);

        var normalized = sut.NormalizeSource("ペコーラです");
        Assert.Equal("ぺこらです", normalized);

        db.Service.SaveCache(normalized, "你好");
        var result = await sut.TranslateAsync(
            new PotplayerRequest { Current = "ペコーラです" }, TestContext.Current.CancellationToken);

        Assert.Equal("你好", result);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_NullRequest_ReturnsEmpty()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();

        var result = await Create(client, db, Settings()).TranslateAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_ThroughPass_EchoesCurrentWithoutCallingModel()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();

        var result = await Create(client, db, Settings(throughPass: true))
            .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

        Assert.Equal("こんにちは", result);
        Assert.Equal(0, client.CallCount);
        Assert.Null(db.Service.GetCache("こんにちは"));
    }

    [Fact]
    public async Task TranslateAsync_BuildsPromptFromContextAndCurrent()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var request = new PotplayerRequest { Current = "今の文", Context = ["前の文1", "前の文2"] };

        await Create(client, db, Settings()).TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("你是翻譯專家", client.LastSystemText);
        Assert.Contains("前の文1\n前の文2", client.LastUserText);
        Assert.Contains("今の文", client.LastUserText);
        Assert.True(client.LastUserText!.IndexOf("前の文2", StringComparison.Ordinal)
            < client.LastUserText.LastIndexOf("今の文", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TranslateAsync_ConvertsModelOutputToTraditional()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient("这是一个测试");

        var result = await Create(client, db, Settings())
            .TranslateAsync(new PotplayerRequest { Current = "これはテスト" }, TestContext.Current.CancellationToken);

        Assert.Contains("測試", result);
    }

    [Fact]
    public async Task TranslateAsync_PublishesJobMessage()
    {
        using var db = new TempDatabase();
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await Create(new FakeOpenAiClient("你好"), db, Settings())
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは", Context = ["前"] }, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.NotNull(received);
        Assert.Equal("こんにちは", received.Current);
        Assert.Equal("前", received.Context);
        Assert.Equal("你好", received.TranslatedText);
    }

    [Fact]
    public async Task TranslateAsync_SecondIdenticalRequest_UsesCache()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings());
        var request = new PotplayerRequest { Current = "こんにちは" };

        await sut.TranslateAsync(request, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_CacheHitOnCurrent_SkipsModel()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var request = new PotplayerRequest { Current = "こんにちは" };
        db.Service.SaveCache("こんにちは", "快取結果");

        var result = await Create(client, db, Settings()).TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("快取結果", result);
        Assert.Equal(0, client.CallCount);
    }

    // 這句正在預先翻譯的批次裡：等批次完成後直接用快取，不另外送模型。
    [Fact]
    public async Task TranslateAsync_KeyInPrefetchBatch_WaitsForBatchAndUsesCache_Temp()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var inFlight = new PrefetchInFlight();
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            var sut = Create(client, db, Settings(), prefetchInFlight: inFlight);
            using var handle = inFlight.Begin(["こんにちは"]);
            var translateTask = sut.TranslateAsync(
                new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

            // 批次結束前把譯文寫進快取（模擬 VodPrefetcher 寫好快取後才 Dispose）
            await Task.Delay(50, TestContext.Current.CancellationToken);
            db.Service.SaveCache("こんにちは", "批次譯文");
            handle.Dispose();

            var result = await translateTask;

            Assert.Equal("批次譯文", result);
            Assert.Equal(0, client.CallCount);
            Assert.NotNull(received);
            Assert.Equal(TranslationSource.Cache, received.Source);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    // 等待逾時（批次一直沒結束、TimeoutMs 很短）時照常送模型。
    [Fact]
    public async Task TranslateAsync_KeyInPrefetchBatch_WaitTimesOut_SendsModel_Temp()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var inFlight = new PrefetchInFlight();
        var sut = Create(client, db, Settings(timeoutMs: 50), prefetchInFlight: inFlight);

        using var handle = inFlight.Begin(["こんにちは"]);
        var result = await sut.TranslateAsync(
            new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task TranslateAsync_SameCurrentDifferentContext_UsesCache()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings());

        await sut.TranslateAsync(new PotplayerRequest { Current = "こんにちは", Context = ["前の文1"] }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "こんにちは", Context = ["別の文"] }, TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_ManualCacheEntry_IsReturnedWithoutModel()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        db.Service.SaveCache("src", "手動", isManual: true);

        var result = await Create(client, db, Settings())
            .TranslateAsync(new PotplayerRequest { Current = "src" }, TestContext.Current.CancellationToken);

        Assert.Equal("手動", result);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_NoiseInput_ReturnsOriginalWithoutCallingModelOrCache()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();

        var result = await Create(client, db, Settings())
            .TranslateAsync(new PotplayerRequest { Current = "♪" }, TestContext.Current.CancellationToken);

        Assert.Equal("♪", result);
        Assert.Equal(0, client.CallCount);
        Assert.Null(db.Service.GetCache("♪"));
    }

    [Fact]
    public async Task TranslateAsync_NormalShortInput_StillTranslated()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();

        await Create(client, db, Settings())
            .TranslateAsync(new PotplayerRequest { Current = "はい" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_ModelSlowerThanTimeout_ReturnsOriginalAndDoesNotCache()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(delay: TimeSpan.FromSeconds(2));
        var sw = Stopwatch.StartNew();

        var result = await Create(client, db, Settings(timeoutMs: 100))
            .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

        sw.Stop();
        Assert.Equal("こんにちは", result);
        Assert.Null(db.Service.GetCache("こんにちは"));
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task TranslateAsync_TimeoutPublishesTimeoutJob()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(delay: TimeSpan.FromSeconds(1));
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            var result = await Create(client, db, Settings(timeoutMs: 100))
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

            Assert.Equal("こんにちは", result);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.NotNull(received);
        Assert.Equal("TIMEOUT", received.SystemPrompt);
        Assert.Equal("こんにちは", received.TranslatedText);
    }

    [Fact]
    public async Task TranslateAsync_ModelFasterThanTimeout_ReturnsTranslation()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(delay: TimeSpan.FromMilliseconds(50));

        var result = await Create(client, db, Settings(timeoutMs: 2000))
            .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
        Assert.Contains("翻譯", result);
    }

    [Fact]
    public async Task TranslateAsync_TimeoutDisabled_WaitsForModel()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(delay: TimeSpan.FromMilliseconds(300));

        var result = await Create(client, db, Settings(timeoutMs: 0))
            .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
        Assert.Contains("翻譯", result);
    }

    [Fact]
    public async Task TranslateAsync_ExternalCancellation_Throws()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(delay: TimeSpan.FromSeconds(2));
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(client, db, Settings(timeoutMs: 5000))
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, cts.Token));
    }

    [Fact]
    public async Task TranslateAsync_TruncatedResult_ReturnedButNotCached()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient("这是截断的翻译", truncated: true);

        var result = await Create(client, db, Settings())
            .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

        Assert.Contains("翻譯", result);
        Assert.Equal(1, client.CallCount);
        Assert.Null(db.Service.GetCache("こんにちは"));
    }

    [Fact]
    public async Task TranslateAsync_BlankModelResult_ReturnsOriginalAndNotCached()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient("   ");

        var result = await Create(client, db, Settings())
            .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

        Assert.Equal("こんにちは", result);
        Assert.Equal(1, client.CallCount);
        Assert.Null(db.Service.GetCache("こんにちは"));
    }

    [Fact]
    public async Task TranslateAsync_WrappedCancellationOnTimeout_ReturnsOriginal()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(delay: TimeSpan.FromSeconds(2), wrapCancellation: true);

        var result = await Create(client, db, Settings(timeoutMs: 100))
            .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

        Assert.Equal("こんにちは", result);
        Assert.Null(db.Service.GetCache("こんにちは"));
    }

    [Fact]
    public async Task TranslateAsync_ExternalCancellation_DoesNotCache()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(delay: TimeSpan.FromSeconds(2));
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(client, db, Settings(timeoutMs: 5000))
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, cts.Token));

        Assert.Null(db.Service.GetCache("こんにちは"));
    }

    [Fact]
    public async Task TranslateAsync_PassesBoundedMaxOutputTokens()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings());

        await sut.TranslateAsync(new PotplayerRequest { Current = "a" }, TestContext.Current.CancellationToken);
        Assert.Equal(64, client.LastMaxOutputTokens);

        await sut.TranslateAsync(new PotplayerRequest { Current = string.Concat(Enumerable.Repeat("あいうえお", 6)) }, TestContext.Current.CancellationToken);
        Assert.Equal(90, client.LastMaxOutputTokens);

        await sut.TranslateAsync(new PotplayerRequest { Current = string.Concat(Enumerable.Repeat("あいうえおかきくけこ", 20)) }, TestContext.Current.CancellationToken);
        Assert.Equal(256, client.LastMaxOutputTokens);
    }

    [Fact]
    public async Task TranslateAsync_CacheHit_PublishesJobWithSourceCacheAndCurrent()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        db.Service.SaveCache("こんにちは", "快取結果");
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            var result = await Create(client, db, Settings())
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは", Context = ["前の文"] }, TestContext.Current.CancellationToken);

            Assert.Equal("快取結果", result);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.NotNull(received);
        Assert.Equal(TranslationSource.Cache, received.Source);
        Assert.Equal("こんにちは", received.Current);
        Assert.Equal("前の文", received.Context);
        Assert.Equal("快取結果", received.TranslatedText);
        Assert.Equal("CACHED", received.SystemPrompt);
    }

    [Fact]
    public async Task TranslateAsync_ModelResult_PublishesJobWithSourceModel()
    {
        using var db = new TempDatabase();
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await Create(new FakeOpenAiClient("你好"), db, Settings())
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.NotNull(received);
        Assert.Equal(TranslationSource.Model, received.Source);
    }

    [Fact]
    public async Task TranslateAsync_Timeout_PublishesJobWithSourceTimeout()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(delay: TimeSpan.FromSeconds(1));
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await Create(client, db, Settings(timeoutMs: 100))
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.NotNull(received);
        Assert.Equal(TranslationSource.Timeout, received.Source);
        Assert.Equal("TIMEOUT", received.SystemPrompt);
    }

    [Fact]
    public async Task TranslateAsync_ThroughPass_PublishesJobWithSourceThroughPass()
    {
        using var db = new TempDatabase();
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await Create(new FakeOpenAiClient(), db, Settings(throughPass: true))
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.NotNull(received);
        Assert.Equal(TranslationSource.ThroughPass, received.Source);
        Assert.Equal("こんにちは", received.TranslatedText);
    }

    [Fact]
    public async Task TranslateAsync_ModelResult_AppliesRulesButCachesRaw()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient("佩可說你好");

        var result = await Create(client, db, Settings(replacementRules: "佩可=佩可拉"))
            .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

        Assert.Equal("佩可拉說你好", result);
        Assert.Equal("佩可說你好", db.Service.GetCache("こんにちは"));
    }

    [Fact]
    public async Task TranslateAsync_CacheHit_AppliesCurrentRules()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        db.Service.SaveCache("こんにちは", "佩可說你好");

        var result = await Create(client, db, Settings(replacementRules: "佩可=佩可拉"))
            .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

        Assert.Equal("佩可拉說你好", result);
        Assert.Equal(0, client.CallCount);
        Assert.Equal("佩可說你好", db.Service.GetCache("こんにちは"));
    }

    [Fact]
    public async Task TranslateAsync_SameRequestTwice_RulesAppliedOnlyOnce()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient("佩可說你好");
        var sut = Create(client, db, Settings(replacementRules: "佩可=佩可拉"));
        var request = new PotplayerRequest { Current = "こんにちは" };

        var first = await sut.TranslateAsync(request, TestContext.Current.CancellationToken);
        var second = await sut.TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
        Assert.Equal("佩可拉說你好", first);
        Assert.Equal("佩可拉說你好", second);
    }

    [Fact]
    public async Task TranslateAsync_ThroughPass_DoesNotApplyRules()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();

        var result = await Create(client, db, Settings(throughPass: true, replacementRules: "佩可=佩可拉"))
            .TranslateAsync(new PotplayerRequest { Current = "佩可說你好" }, TestContext.Current.CancellationToken);

        Assert.Equal("佩可說你好", result);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Probe_Flag_ReturnsCurrentWithoutModelCacheOrHistory()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            var result = await sut.TranslateAsync(new PotplayerRequest { Current = "hello", Probe = true }, TestContext.Current.CancellationToken);

            Assert.Equal("hello", result);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.Equal(0, client.CallCount);
        Assert.Null(db.Service.GetCache("hello"));
        Assert.Null(received);

        // 不進歷史：下一個正常請求帶出的歷史仍為空
        await sut.TranslateAsync(new PotplayerRequest { Current = "normal" }, TestContext.Current.CancellationToken);
        Assert.Empty(client.LastHistory!);
    }

    [Fact]
    public async Task Probe_Text_ReturnsCurrentWithoutModel()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();

        var result = await Create(client, db, Settings(conversation: true))
            .TranslateAsync(new PotplayerRequest { Current = PromptBuilder.ProbeText }, TestContext.Current.CancellationToken);

        Assert.Equal(PromptBuilder.ProbeText, result);
        Assert.Equal(0, client.CallCount);
        Assert.Null(db.Service.GetCache(PromptBuilder.ProbeText));
    }

    [Fact]
    public async Task Conversation_SendsOnlyCurrentAsUserText()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var request = new PotplayerRequest { Current = "今の文", Context = ["前の文1", "前の文2"] };

        await Create(client, db, Settings(conversation: true)).TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(1, client.ChatCallCount);
        Assert.Equal("今の文", client.LastUserText);
        Assert.DoesNotContain("前の文1", client.LastUserText);
        Assert.DoesNotContain("前の文2", client.LastUserText);
        Assert.Empty(client.LastHistory!);
    }

    [Fact]
    public async Task Conversation_SecondRequest_IncludesFirstTurnWithRawModelOutput()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(); // 預設回覆為簡體「这是翻译」
        var sut = Create(client, db, Settings(conversation: true));

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "two" }, TestContext.Current.CancellationToken);

        var turn = Assert.Single(client.LastHistory!);
        Assert.Equal(new ChatTurn("one", "这是翻译"), turn);
    }

    [Fact]
    public async Task Conversation_CacheHit_AppendsTurn()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        db.Service.SaveCache("こんにちは", "快取結果");
        var sut = Create(client, db, Settings(conversation: true));

        await sut.TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "next" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
        var turn = Assert.Single(client.LastHistory!);
        Assert.Equal(new ChatTurn("こんにちは", "快取結果"), turn);
    }

    [Fact]
    public async Task Conversation_Timeout_DoesNotAppend()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(delay: TimeSpan.FromSeconds(1));
        var sut = Create(client, db, Settings(timeoutMs: 100, conversation: true));

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "two" }, TestContext.Current.CancellationToken);

        Assert.Empty(client.LastHistory!);
    }

    [Fact]
    public async Task Conversation_SituationChanged_ResetsHistory()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var settings = Settings(conversation: true);
        var sut = Create(client, db, settings);

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "two" }, TestContext.Current.CancellationToken);
        Assert.Single(client.LastHistory!);

        settings.StreamSituation = "有人在玩遊戲";
        await sut.TranslateAsync(new PotplayerRequest { Current = "three" }, TestContext.Current.CancellationToken);

        Assert.Empty(client.LastHistory!);
    }

    [Fact]
    public async Task Conversation_IdleBeyondLimit_ResetsHistory()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var time = new ManualTimeProvider();
        var sut = Create(client, db, Settings(conversation: true), time);

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "two" }, TestContext.Current.CancellationToken);
        Assert.Single(client.LastHistory!);

        time.UtcNow = time.UtcNow.AddSeconds(91); // IdleReset 預設 90 秒
        await sut.TranslateAsync(new PotplayerRequest { Current = "three" }, TestContext.Current.CancellationToken);

        Assert.Empty(client.LastHistory!);
    }

    [Fact]
    public async Task Conversation_SystemPromptIncludesSituationAndCharacterTable()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var settings = Settings(conversation: true);
        settings.StreamSituation = "遊戲實況";
        settings.CharacterTable = "アリス=愛麗絲";

        await Create(client, db, settings)
            .TranslateAsync(new PotplayerRequest { Current = "hi" }, TestContext.Current.CancellationToken);

        Assert.Contains("你是翻譯專家", client.LastSystemText);
        Assert.Contains("遊戲實況", client.LastSystemText);
        Assert.Contains("アリス=愛麗絲", client.LastSystemText);
    }

    [Fact]
    public async Task WarmUp_ConversationMode_CallsClientWarmUp()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));

        await sut.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, client.WarmUpCount);
    }

    [Fact]
    public async Task WarmUp_SingleTurnMode_DoesNotCallClient()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings());

        await sut.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, client.WarmUpCount);
    }

    [Fact]
    public async Task ResetConversation_ClearsHistory()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "two" }, TestContext.Current.CancellationToken);
        Assert.Single(client.LastHistory!);

        sut.ResetConversation();
        await sut.TranslateAsync(new PotplayerRequest { Current = "three" }, TestContext.Current.CancellationToken);

        Assert.Empty(client.LastHistory!);
    }

    [Fact]
    public async Task Conversation_CollapsedReply_RetriesOnceWithEmptyHistory()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));

        // 先用正常回覆累積 2 輪歷史（第二次請求帶第 1 輪）
        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "two" }, TestContext.Current.CancellationToken);
        Assert.Single(client.LastHistory!);

        client.Replies.Enqueue("這");
        client.Replies.Enqueue("這也太誇張了吧");
        var callsBefore = client.ChatCallCount;

        var result = await sut.TranslateAsync(new PotplayerRequest { Current = "カーレスターどこ？" }, TestContext.Current.CancellationToken);

        Assert.Equal("這也太誇張了吧", result);
        Assert.Equal(2, client.ChatCallCount - callsBefore);
        Assert.Empty(client.Histories[^1]);
        Assert.Equal("這也太誇張了吧", db.Service.GetCache("カーレスターどこ？"));

        // 重試成功後只記錄這一輪，歷史只剩 1 輪
        await sut.TranslateAsync(new PotplayerRequest { Current = "next" }, TestContext.Current.CancellationToken);
        var turn = Assert.Single(client.LastHistory!);
        Assert.Equal(new ChatTurn("カーレスターどこ？", "這也太誇張了吧"), turn);
    }

    [Fact]
    public async Task Conversation_BothRepliesCollapsed_ReturnsCollapsedWithoutCacheOrHistory()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "two" }, TestContext.Current.CancellationToken);

        client.Replies.Enqueue("卡");
        client.Replies.Enqueue("卡");
        var callsBefore = client.ChatCallCount;
        var result = await sut.TranslateAsync(new PotplayerRequest { Current = "カーレスターどこ？" }, TestContext.Current.CancellationToken);

        Assert.Equal("卡", result);
        Assert.Equal(2, client.ChatCallCount - callsBefore);
        Assert.Null(db.Service.GetCache("カーレスターどこ？"));

        // 重試仍崩壞：歷史已被重置且不再增加
        await sut.TranslateAsync(new PotplayerRequest { Current = "next" }, TestContext.Current.CancellationToken);
        Assert.Empty(client.LastHistory!);
    }

    [Fact]
    public async Task Conversation_EmptyHistoryCollapsed_DoesNotRetry()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));

        client.Replies.Enqueue("卡");
        var result = await sut.TranslateAsync(new PotplayerRequest { Current = "カーレスターどこ？" }, TestContext.Current.CancellationToken);

        Assert.Equal("卡", result);
        Assert.Equal(1, client.ChatCallCount);
        Assert.Null(db.Service.GetCache("カーレスターどこ？"));
    }

    [Fact]
    public async Task SingleTurn_CollapsedReply_DoesNotRetryOrCache()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: false));

        client.Replies.Enqueue("卡");
        var result = await sut.TranslateAsync(new PotplayerRequest { Current = "カーレスターどこ？" }, TestContext.Current.CancellationToken);

        Assert.Equal("卡", result);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, client.ChatCallCount);
        Assert.Null(db.Service.GetCache("カーレスターどこ？"));
    }

    // ===== 單輪模式：Context 的最後幾句附上最近翻過的譯文（原文 → 譯文）=====

    [Fact]
    public async Task SingleTurn_ContextLines_IncludeRecentTranslations()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings());

        await sut.TranslateAsync(new PotplayerRequest { Current = "一文目" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "二文目" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(
            new PotplayerRequest { Current = "三文目", Context = ["一文目", "二文目"] },
            TestContext.Current.CancellationToken);

        // 前兩句最近翻過：Context 段落以「原文 → 譯文」附上當時的譯文（後處理後、替換前）
        Assert.Contains("一文目 → 這是翻譯", client.LastUserText);
        Assert.Contains("二文目 → 這是翻譯", client.LastUserText);
        Assert.Contains("# Current（只翻譯這句）\n三文目", client.LastUserText);
    }

    [Fact]
    public async Task SingleTurn_ContextLongerThanThreeLines_OnlyLastThreeLookedUp()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings());

        await sut.TranslateAsync(new PotplayerRequest { Current = "三号" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "四号" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "五号" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(
            new PotplayerRequest { Current = "別の話題", Context = ["三号", "四号", "五号", "六号", "七号"] },
            TestContext.Current.CancellationToken);

        // 只有最後 3 行會找譯文：前 2 行即使最近翻過（_recent 裡有）也只寫原文；找不到的行也是原文
        Assert.Contains(
            "# Context（前面幾句，只用來理解，不要翻譯）\n三号\n四号\n五号 → 這是翻譯\n六号\n七号\n",
            client.LastUserText);
    }

    [Fact]
    public async Task SingleTurn_ConversationMode_UserPromptUnchangedWithoutTranslations()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "two" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(
            new PotplayerRequest { Current = "three", Context = ["one", "two"] },
            TestContext.Current.CancellationToken);

        // 多輪模式的 user 只放 Current（譯文在歷史訊息裡），不帶 →
        Assert.Equal("three", client.LastUserText);
        Assert.DoesNotContain("→", client.LastUserText);
    }

    [Fact]
    public async Task CaptionReference_SingleTurnMode_HasCurrentHeaderWithoutSentenceWrapper()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = new TranslationManager(client, new TextProcessingService(), db.Service,
            new FakeSettingsService(Settings(conversation: false)),
            captionReference: CreateCaptionReference(new FakePlaybackClock { PositionMs = 55_000 }, "ぴんくの部屋着じゃなかったら"));

        await sut.TranslateAsync(new PotplayerRequest { Current = "これで部屋着じゃなかったら" }, TestContext.Current.CancellationToken);

        // 單輪模式已經有 # Current 標題：不再包一層 # 本句，Current 一律是原句
        var text = client.LastUserText!;
        Assert.StartsWith("# 參考：同一時段的 YouTube 自動字幕", text);
        Assert.DoesNotContain("# 本句", text);
        Assert.EndsWith(
            "---\n" + PromptBuilder.BuildSingleTurnUserPrompt(new PotplayerRequest { Current = "これで部屋着じゃなかったら" }),
            text);
    }

    [Fact]
    public async Task CacheHit_NonManualCollapsedEntry_DeletedAndUsesModel()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));
        db.Service.SaveCache("カーレスターどこ？", "卡");

        client.Replies.Enqueue("這也太誇張了吧");
        var result = await sut.TranslateAsync(new PotplayerRequest { Current = "カーレスターどこ？" }, TestContext.Current.CancellationToken);

        Assert.Equal("這也太誇張了吧", result);
        Assert.Equal(1, client.ChatCallCount);
        Assert.Equal("這也太誇張了吧", db.Service.GetCache("カーレスターどこ？"));
    }

    [Fact]
    public async Task CacheHit_ManualCollapsedEntry_ReturnsCacheWithoutHistory()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));
        db.Service.SaveCache("カーレスターどこ？", "卡", isManual: true);

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);

        var result = await sut.TranslateAsync(new PotplayerRequest { Current = "カーレスターどこ？" }, TestContext.Current.CancellationToken);

        Assert.Equal("卡", result);
        Assert.Equal(1, client.ChatCallCount);
        Assert.Equal("卡", db.Service.GetCache("カーレスターどこ？"));

        // 手動項目不進歷史：之後的歷史仍只有先前那一輪
        await sut.TranslateAsync(new PotplayerRequest { Current = "next" }, TestContext.Current.CancellationToken);
        var turn = Assert.Single(client.LastHistory!);
        Assert.Equal(new ChatTurn("one", "这是翻译"), turn);
    }

    [Fact]
    public async Task TranslateAsync_SourceReplacementRules_AppliedBeforeTranslation()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var settings = Settings();
        settings.SourceReplacementRules = "ペコラ=ぺこら";
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);
        var request = new PotplayerRequest { Current = "ペコラかわいい", Context = ["ペコラだよ"] };

        try
        {
            await Create(client, db, settings).TranslateAsync(request, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        // Current 與 Context 都以修正後的原文送給模型
        Assert.Contains("ぺこらかわいい", client.LastUserText);
        Assert.Contains("ぺこらだよ", client.LastUserText);
        Assert.DoesNotContain("ペコラ", client.LastUserText);
        // 快取以修正後的原文為索引（存後處理後的繁體譯文）
        Assert.Equal("這是翻譯", db.Service.GetCache("ぺこらかわいい"));
        Assert.Null(db.Service.GetCache("ペコラかわいい"));
        // Job 的 Current 與 CacheKey 都是修正後的原文
        Assert.NotNull(received);
        Assert.Equal("ぺこらかわいい", received!.Current);
        Assert.Equal("ぺこらかわいい", received.CacheKey);
        // 呼叫端傳入的物件不被修改
        Assert.Equal("ペコラかわいい", request.Current);
        Assert.Equal(["ペコラだよ"], request.Context);
    }

    [Fact]
    public async Task TranslateAsync_SourceReplacementRules_SecondIdenticalMistranscription_UsesCache()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var settings = Settings();
        settings.SourceReplacementRules = "ペコラ=ぺこら";
        var sut = Create(client, db, settings);

        await sut.TranslateAsync(new PotplayerRequest { Current = "ペコラかわいい" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "ペコラかわいい" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_ModelJob_CacheKeyEqualsOriginalCurrent()
    {
        using var db = new TempDatabase();
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await Create(new FakeOpenAiClient("你好"), db, Settings())
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.NotNull(received);
        Assert.Equal(TranslationSource.Model, received.Source);
        Assert.Equal("こんにちは", received.CacheKey);
    }

    [Fact]
    public async Task TranslateAsync_CacheHitJob_CacheKeyEqualsOriginalCurrent()
    {
        using var db = new TempDatabase();
        db.Service.SaveCache("こんにちは", "快取結果");
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await Create(new FakeOpenAiClient(), db, Settings())
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.NotNull(received);
        Assert.Equal(TranslationSource.Cache, received.Source);
        Assert.Equal("こんにちは", received.CacheKey);
    }

    [Fact]
    public async Task TranslateAsync_SourceReplacementRules_JobRawSourceIsBeforeCorrection()
    {
        using var db = new TempDatabase();
        var settings = Settings();
        settings.SourceReplacementRules = "ペコラ=ぺこら";
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await Create(new FakeOpenAiClient("你好"), db, settings)
                .TranslateAsync(new PotplayerRequest { Current = "ペコラかわいい" }, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.NotNull(received);
        Assert.Equal("ペコラかわいい", received!.RawSource);
        Assert.Equal("ぺこらかわいい", received.Current);
    }

    [Fact]
    public async Task TranslateAsync_NoRules_JobRawSourceEqualsCurrent()
    {
        using var db = new TempDatabase();
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await Create(new FakeOpenAiClient("你好"), db, Settings())
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.NotNull(received);
        Assert.Equal("こんにちは", received!.RawSource);
        Assert.Equal(received.Current, received.RawSource);
    }

    [Fact]
    public async Task TranslateAsync_CacheHitJob_HasRawSource()
    {
        using var db = new TempDatabase();
        db.Service.SaveCache("こんにちは", "快取結果");
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await Create(new FakeOpenAiClient(), db, Settings())
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.NotNull(received);
        Assert.Equal(TranslationSource.Cache, received.Source);
        Assert.Equal("こんにちは", received!.RawSource);
    }

    [Fact]
    public async Task TranslateAsync_TimeoutJob_HasRawSource()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(delay: TimeSpan.FromSeconds(1));
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            var result = await Create(client, db, Settings(timeoutMs: 100))
                .TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);

            Assert.Equal("こんにちは", result);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.NotNull(received);
        Assert.Equal(TranslationSource.Timeout, received.Source);
        Assert.Equal("こんにちは", received!.RawSource);
    }

    [Fact]
    public async Task DeleteTranslation_RemovesCacheIncludingManual_AndRemovesHistoryTurn()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "bad" }, TestContext.Current.CancellationToken);
        db.Service.SaveCache("bad", "手動譯文", isManual: true);

        sut.DeleteTranslation("bad", "bad");

        // 快取被刪除（包含手動項目）
        Assert.Null(db.Service.GetCache("bad"));
        // 對話歷史中的該輪被移除
        await sut.TranslateAsync(new PotplayerRequest { Current = "next" }, TestContext.Current.CancellationToken);
        var turn = Assert.Single(client.LastHistory!);
        Assert.Equal(new ChatTurn("one", "这是翻译"), turn);
    }

    [Fact]
    public async Task DeleteTranslation_CorrectedSourceDifferent_RemovesBothHistoryTurns()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "bad" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "fixed" }, TestContext.Current.CancellationToken);

        sut.DeleteTranslation("bad", "fixed");

        await sut.TranslateAsync(new PotplayerRequest { Current = "next" }, TestContext.Current.CancellationToken);
        var turn = Assert.Single(client.LastHistory!);
        Assert.Equal(new ChatTurn("one", "这是翻译"), turn);
    }

    [Fact]
    public async Task SaveCorrection_CachesAsManual_AndReplacesHistoryTurn()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "two" }, TestContext.Current.CancellationToken);

        sut.SaveCorrection("two", "two", "修正譯文");

        // 快取為新譯文，且為手動項目（預設 DeleteCache 刪不掉）
        Assert.Equal("修正譯文", db.Service.GetCache("two"));
        Assert.False(db.Service.DeleteCache("two"));
        Assert.Equal("修正譯文", db.Service.GetCache("two"));

        // 對話歷史中該輪的 assistant 被替換
        await sut.TranslateAsync(new PotplayerRequest { Current = "three" }, TestContext.Current.CancellationToken);
        Assert.Equal(3, client.ChatCallCount);
        var turns = client.LastHistory!;
        Assert.Equal(2, turns.Count);
        Assert.Equal(new ChatTurn("one", "这是翻译"), turns[0]);
        Assert.Equal(new ChatTurn("two", "修正譯文"), turns[1]);
    }

    [Fact]
    public async Task SaveCorrection_CorrectedSourceDifferent_CachesUnderCacheKey()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));

        await sut.TranslateAsync(new PotplayerRequest { Current = "bad" }, TestContext.Current.CancellationToken);

        sut.SaveCorrection("bad", "fixed", "修正譯文");

        Assert.Equal("修正譯文", db.Service.GetCache("bad"));
        Assert.Null(db.Service.GetCache("fixed"));

        // 歷史中該輪的 user 換成修正後原文
        await sut.TranslateAsync(new PotplayerRequest { Current = "next" }, TestContext.Current.CancellationToken);
        var turn = Assert.Single(client.LastHistory!);
        Assert.Equal(new ChatTurn("fixed", "修正譯文"), turn);
    }

    [Fact]
    public async Task RetranslateAsync_Success_SendsCorrectedSourceCachesUnderCacheKeyAndReplacesHistory()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "wrong" }, TestContext.Current.CancellationToken);

        client.Replies.Enqueue("這是修正後的翻譯");
        var result = await sut.RetranslateAsync("wrong", "corrected", TestContext.Current.CancellationToken);

        // 送給模型的 user 是 correctedSource
        Assert.Equal("這是修正後的翻譯", result);
        Assert.Equal("corrected", client.LastUserText);
        // 快取以 cacheKey 存入新譯文（後處理後、替換前），且為手動項目
        Assert.Equal("這是修正後的翻譯", db.Service.GetCache("wrong"));
        Assert.False(db.Service.DeleteCache("wrong"));
        // 傳給模型的 history 不含 cacheKey 那一輪
        var sentHistory = Assert.Single(client.Histories[^1]);
        Assert.Equal(new ChatTurn("one", "这是翻译"), sentHistory);

        // 歷史中該輪變為 (corrected, 模型原始輸出)
        await sut.TranslateAsync(new PotplayerRequest { Current = "next" }, TestContext.Current.CancellationToken);
        var turns = client.LastHistory!;
        Assert.Equal(2, turns.Count);
        Assert.Equal(new ChatTurn("corrected", "這是修正後的翻譯"), turns[1]);
    }

    [Fact]
    public async Task RetranslateAsync_SingleTurn_BuildsSingleTurnPrompt()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: false));

        client.Replies.Enqueue("這是修正後的翻譯");
        var result = await sut.RetranslateAsync("wrong", "corrected source", TestContext.Current.CancellationToken);

        Assert.Equal("這是修正後的翻譯", result);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, client.ChatCallCount);
        Assert.Contains("corrected source", client.LastUserText);
        Assert.Equal("這是修正後的翻譯", db.Service.GetCache("wrong"));
    }

    [Fact]
    public async Task RetranslateAsync_CollapsedReply_ReturnsNullWithoutCacheOrHistoryChange()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "wrong" }, TestContext.Current.CancellationToken);
        db.Service.SaveCache("wrong", "舊譯文");

        client.Replies.Enqueue("卡");
        var result = await sut.RetranslateAsync("wrong", "corrected", TestContext.Current.CancellationToken);

        Assert.Null(result);
        // 快取與歷史不變
        Assert.Equal("舊譯文", db.Service.GetCache("wrong"));
        await sut.TranslateAsync(new PotplayerRequest { Current = "next" }, TestContext.Current.CancellationToken);
        var turns = client.LastHistory!;
        Assert.Equal(2, turns.Count);
        Assert.Equal(new ChatTurn("one", "这是翻译"), turns[0]);
        Assert.Equal(new ChatTurn("wrong", "这是翻译"), turns[1]);
    }

    [Fact]
    public async Task RetranslateAsync_DoesNotPublishJobMessage()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            client.Replies.Enqueue("這是修正後的翻譯");
            await sut.RetranslateAsync("wrong", "corrected", TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        Assert.Null(received);
    }

    // ===== 重複字幕判斷（RevisionDetector / RevisionWindowSeconds）=====

    [Fact]
    public async Task Revision_IdenticalSecondSentence_ReusesFirstTranslation()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings());
        var recipient = new object();
        var jobs = new List<TranslationJob>();
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => jobs.Add(job));

        try
        {
            var first = await sut.TranslateAsync(new PotplayerRequest { Current = "ギャルみたいになっちゃったけど。" }, TestContext.Current.CancellationToken);
            var second = await sut.TranslateAsync(new PotplayerRequest { Current = "ギャルみたいになっちゃったけど" }, TestContext.Current.CancellationToken);

            // 第二句沿用第一句的譯文，模型只被呼叫一次
            Assert.Equal(first, second);
            Assert.Equal(1, client.CallCount);
            Assert.Equal(2, jobs.Count);
            Assert.Equal(TranslationSource.Reused, jobs[1].Source);
            Assert.Equal("REUSED", jobs[1].SystemPrompt);
            Assert.Equal(jobs[0].Id, jobs[1].RevisionOf);
            Assert.Null(db.Service.GetCache("ギャルみたいになっちゃったけど"));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task Revision_ShrinkSecondSentence_ReusesFirstTranslation()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings());
        var recipient = new object();
        var jobs = new List<TranslationJob>();
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => jobs.Add(job));

        try
        {
            var first = await sut.TranslateAsync(new PotplayerRequest { Current = "ありがとうございました! えーすごい!" }, TestContext.Current.CancellationToken);
            var second = await sut.TranslateAsync(new PotplayerRequest { Current = "ありがとうございました!" }, TestContext.Current.CancellationToken);

            Assert.Equal(first, second);
            Assert.Equal(1, client.CallCount);
            Assert.Equal(2, jobs.Count);
            Assert.Equal(TranslationSource.Reused, jobs[1].Source);
            Assert.Equal(jobs[0].Id, jobs[1].RevisionOf);
            Assert.Null(db.Service.GetCache("ありがとうございました!"));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task Revision_ReviseSecondSentence_ConversationMode_ReplacesHistoryTurn()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));
        var recipient = new object();
        var jobs = new List<TranslationJob>();
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => jobs.Add(job));
        var first = "テンディを食べました はい先ほど言った通り";
        var second = "天理を食べました はい先ほど言った通り";

        try
        {
            await sut.TranslateAsync(new PotplayerRequest { Current = first }, TestContext.Current.CancellationToken);
            await sut.TranslateAsync(new PotplayerRequest { Current = second }, TestContext.Current.CancellationToken);

            // 修正版照常呼叫模型，且第二次送出的歷史保留第一句那輪（讓模型沿用用詞）
            Assert.Equal(2, client.ChatCallCount);
            Assert.Contains(client.Histories[1], turn => turn.User == first);
            Assert.Equal(2, jobs.Count);
            Assert.Equal(jobs[0].Id, jobs[1].RevisionOf);

            // 結束後多輪歷史只剩一輪，user 為第二句（以下一個無關請求帶出的歷史檢查）
            await sut.TranslateAsync(new PotplayerRequest { Current = "全然違う話題になりますね" }, TestContext.Current.CancellationToken);
            var only = Assert.Single(client.LastHistory!);
            Assert.Equal(second, only.User);
            Assert.Equal("这是翻译", only.Assistant);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task Revision_UnrelatedSentences_KeepBothTurnsAndNoRevisionOf()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));
        var recipient = new object();
        var jobs = new List<TranslationJob>();
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => jobs.Add(job));
        var first = "今日はいい天気ですね";
        var second = "晩ご飯は何にしますか";

        try
        {
            await sut.TranslateAsync(new PotplayerRequest { Current = first }, TestContext.Current.CancellationToken);
            await sut.TranslateAsync(new PotplayerRequest { Current = second }, TestContext.Current.CancellationToken);

            Assert.Equal(2, jobs.Count);
            Assert.Null(jobs[1].RevisionOf);

            // 無關的兩句：歷史各自保留一輪（以下一個無關請求帶出的歷史檢查）
            await sut.TranslateAsync(new PotplayerRequest { Current = "全然違う話題になりますね" }, TestContext.Current.CancellationToken);
            Assert.Equal(2, client.LastHistory!.Count);
            Assert.Equal(first, client.LastHistory[0].User);
            Assert.Equal(second, client.LastHistory[1].User);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task Revision_BeyondTimeWindow_TranslatesNormally()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var time = new ManualTimeProvider();
        var sut = Create(client, db, Settings(), time);
        var recipient = new object();
        var jobs = new List<TranslationJob>();
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => jobs.Add(job));

        try
        {
            await sut.TranslateAsync(new PotplayerRequest { Current = "ギャルみたいになっちゃったけど。" }, TestContext.Current.CancellationToken);

            time.UtcNow = time.UtcNow.AddSeconds(9); // 超過預設 8 秒時間窗
            await sut.TranslateAsync(new PotplayerRequest { Current = "ギャルみたいになっちゃったけど" }, TestContext.Current.CancellationToken);

            Assert.Equal(2, client.CallCount);
            Assert.Equal(2, jobs.Count);
            Assert.Null(jobs[1].RevisionOf);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task Revision_WindowDisabled_TranslatesPunctuationOnlyNormally()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var settings = Settings();
        settings.Translation.RevisionWindowSeconds = 0;
        var sut = Create(client, db, settings);

        await sut.TranslateAsync(new PotplayerRequest { Current = "ギャルみたいになっちゃったけど。" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "ギャルみたいになっちゃったけど" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task Revision_CacheHitOnRevision_ReplacesHistoryTurn()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings(conversation: true));
        var first = "テンディを食べました はい先ほど言った通り";
        var second = "天理を食べました はい先ほど言った通り";
        db.Service.SaveCache(second, "快取譯文結果");
        var recipient = new object();
        var jobs = new List<TranslationJob>();
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => jobs.Add(job));

        try
        {
            await sut.TranslateAsync(new PotplayerRequest { Current = first }, TestContext.Current.CancellationToken);
            var result = await sut.TranslateAsync(new PotplayerRequest { Current = second }, TestContext.Current.CancellationToken);

            Assert.Equal("快取譯文結果", result);
            Assert.Equal(1, client.ChatCallCount);
            Assert.Equal(2, jobs.Count);
            Assert.Equal(TranslationSource.Cache, jobs[1].Source);
            Assert.NotNull(jobs[1].RevisionOf);
            Assert.Equal(jobs[0].Id, jobs[1].RevisionOf);

            // 歷史只剩一輪，user 為第二句（快取內容作為 assistant）
            await sut.TranslateAsync(new PotplayerRequest { Current = "全然違う話題になりますね" }, TestContext.Current.CancellationToken);
            var only = Assert.Single(client.LastHistory!);
            Assert.Equal(second, only.User);
            Assert.Equal("快取譯文結果", only.Assistant);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task DeleteTranslation_RemovesRecent_ThenPunctuationOnlyVariant_CallsModel()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = Create(client, db, Settings());
        var first = "ギャルみたいになっちゃったけど。";

        await sut.TranslateAsync(new PotplayerRequest { Current = first }, TestContext.Current.CancellationToken);
        sut.DeleteTranslation(first, first);
        await sut.TranslateAsync(new PotplayerRequest { Current = "ギャルみたいになっちゃったけど" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, client.CallCount);
    }

    // ===== 關鍵字詞彙提示（KeywordGlossary 的 user prompt 段落）=====

    /// <summary>建立含一張未生效詞彙卡（ヴァンダル=暴徒）的 KeywordGlossary 與 GlossaryState。</summary>
    private static (GlossaryState State, KeywordGlossary Keyword) CreateKeywordGlossary(AppSettings settings)
    {
        var state = new GlossaryState();
        state.SetCatalog(new GlossaryCatalog(
            [new GlossaryCard { Id = 1, Kind = GlossaryKind.Game, Name = "VALORANT" }],
            [new GlossaryTerm { Id = 11, CardId = 1, Source = "ヴァンダル", Target = "暴徒" }]));
        return (state, new KeywordGlossary(state));
    }

    [Fact]
    public async Task KeywordGlossary_TermInCurrent_UserPromptStartsWithSection_SystemPromptUnchanged()
    {
        using var db = new TempDatabase();
        var settings = Settings(conversation: true);
        var clientWith = new FakeOpenAiClient();
        var clientWithout = new FakeOpenAiClient();
        var (state, keyword) = CreateKeywordGlossary(settings);
        var with = new TranslationManager(clientWith, new TextProcessingService(), db.Service,
            new FakeSettingsService(settings), glossary: state, keywordGlossary: keyword);
        var without = new TranslationManager(clientWithout, new TextProcessingService(), db.Service,
            new FakeSettingsService(settings), glossary: state);

        // 名詞出現在本句：user prompt 前面加上參考詞彙段落
        await with.TranslateAsync(new PotplayerRequest { Current = "今日はヴァンダルの話" }, TestContext.Current.CancellationToken);
        await without.TranslateAsync(new PotplayerRequest { Current = "普通の文" }, TestContext.Current.CancellationToken);

        Assert.StartsWith("# 參考詞彙（本句或前文提到，原文=譯名，譯名請照用）", clientWith.LastUserText);
        Assert.Contains("ヴァンダル=暴徒", clientWith.LastUserText);
        Assert.EndsWith("\n---\n今日はヴァンダルの話", clientWith.LastUserText);
        // 只有 keyword 段落、沒有 YouTube 字幕參考時：格式完全不變，不加「# 本句」標題
        Assert.DoesNotContain("# 本句", clientWith.LastUserText);
        // system prompt 完全不受關鍵字詞彙提示影響
        Assert.Equal(clientWithout.LastSystemText, clientWith.LastSystemText);

        // 多輪歷史存的 user 內容維持本句原文（不含段落）
        await with.TranslateAsync(new PotplayerRequest { Current = "次の文" }, TestContext.Current.CancellationToken);
        Assert.Equal(["今日はヴァンダルの話"], clientWith.Histories[^1].Select(t => t.User).ToList());
    }

    [Fact]
    public async Task KeywordGlossary_SingleTurnMode_UserPromptStartsWithSection_SystemPromptUnchanged()
    {
        using var db = new TempDatabase();
        var settings = Settings(conversation: false);
        var clientWith = new FakeOpenAiClient();
        var clientWithout = new FakeOpenAiClient();
        var (state, keyword) = CreateKeywordGlossary(settings);
        var with = new TranslationManager(clientWith, new TextProcessingService(), db.Service,
            new FakeSettingsService(settings), glossary: state, keywordGlossary: keyword);
        var without = new TranslationManager(clientWithout, new TextProcessingService(), db.Service,
            new FakeSettingsService(settings), glossary: state);

        await with.TranslateAsync(new PotplayerRequest { Current = "今日はヴァンダルの話" }, TestContext.Current.CancellationToken);
        await without.TranslateAsync(new PotplayerRequest { Current = "普通の文" }, TestContext.Current.CancellationToken);

        Assert.StartsWith("# 參考詞彙（本句或前文提到，原文=譯名，譯名請照用）", clientWith.LastUserText);
        Assert.Contains("ヴァンダル=暴徒", clientWith.LastUserText);
        Assert.EndsWith($"---\n{PromptBuilder.BuildSingleTurnUserPrompt(new PotplayerRequest { Current = "今日はヴァンダルの話" })}", clientWith.LastUserText);
        Assert.Equal(clientWithout.LastSystemText, clientWith.LastSystemText);
    }

    [Fact]
    public async Task KeywordGlossary_NoMention_UserPromptSameAsWithoutKeyword()
    {
        // 兩個 manager 各用獨立的資料庫，避免第二個直接命中第一個的快取而不送模型
        using var dbWith = new TempDatabase();
        using var dbWithout = new TempDatabase();
        var settings = Settings(conversation: true);
        var clientWith = new FakeOpenAiClient();
        var clientWithout = new FakeOpenAiClient();
        var (state, keyword) = CreateKeywordGlossary(settings);
        var with = new TranslationManager(clientWith, new TextProcessingService(), dbWith.Service,
            new FakeSettingsService(settings), glossary: state, keywordGlossary: keyword);
        var without = new TranslationManager(clientWithout, new TextProcessingService(), dbWithout.Service,
            new FakeSettingsService(settings), glossary: state);

        // 字幕沒有提到任何名詞：user prompt 與沒有關鍵字提示時完全相同
        await with.TranslateAsync(new PotplayerRequest { Current = "全然関係ない話" }, TestContext.Current.CancellationToken);
        await without.TranslateAsync(new PotplayerRequest { Current = "全然関係ない話" }, TestContext.Current.CancellationToken);

        Assert.Equal(clientWithout.LastUserText, clientWith.LastUserText);
        Assert.Equal("全然関係ない話", clientWith.LastUserText);
    }

    [Fact]
    public async Task KeywordGlossary_ThroughPass_JobUserPromptHasNoSection()
    {
        using var db = new TempDatabase();
        var settings = Settings(throughPass: true);
        var (state, keyword) = CreateKeywordGlossary(settings);
        var sut = new TranslationManager(new FakeOpenAiClient(), new TextProcessingService(), db.Service,
            new FakeSettingsService(settings), glossary: state, keywordGlossary: keyword);
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await sut.TranslateAsync(new PotplayerRequest { Current = "今日はヴァンダルの話" }, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        // 直通模式不加關鍵字詞彙段落
        Assert.NotNull(received);
        Assert.DoesNotContain("參考詞彙", received!.UserPrompt);
        Assert.DoesNotContain("ヴァンダル=暴徒", received.UserPrompt);
    }

    // ===== 聽錯字修正（MishearingFixState 前處理，只套用生效中詞彙卡的 Approved + Pre）=====

    /// <summary>建立含核准 Pre 修正的 MishearingFixState（Wrong → Right 各屬指定的卡）。</summary>
    private static MishearingFixState CreateMishearingFixes(params (long CardId, string Wrong, string Right)[] fixes)
    {
        var state = new MishearingFixState();
        state.SetFixes(
            fixes.Select((f, i) => new MishearingFix
            {
                Id = i + 1,
                CardId = f.CardId,
                TermId = 1,
                Kind = MishearingKind.Pre,
                Wrong = f.Wrong,
                Right = f.Right,
                Status = MishearingStatus.Approved,
            }).ToList());
        return state;
    }

    [Fact]
    public async Task MishearingFix_ActiveCard_SendsFixedSourceAndKeepsRawSource()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var glossary = new GlossaryState();
        glossary.SetActiveIds([1L]);
        var sut = new TranslationManager(client, new TextProcessingService(), db.Service,
            new FakeSettingsService(Settings()), glossary: glossary,
            mishearingFixes: CreateMishearingFixes((1, "バンダル", "ヴァンダル")));
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await sut.TranslateAsync(
                new PotplayerRequest { Current = "バンダル強い", Context = ["バンダルだよ"] },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        // 送給模型的是修正後的原文（Current 與 Context 都是）
        Assert.Contains("ヴァンダル強い", client.LastUserText);
        Assert.Contains("ヴァンダルだよ", client.LastUserText);
        Assert.DoesNotContain("バンダル", client.LastUserText);
        // Job：Current／CacheKey 是修正後的原文，RawSource 維持 Whisper 原始文字
        Assert.NotNull(received);
        Assert.Equal("ヴァンダル強い", received!.Current);
        Assert.Equal("ヴァンダル強い", received.CacheKey);
        Assert.Equal("バンダル強い", received.RawSource);
        // 快取以修正後的原文為索引
        Assert.Equal("這是翻譯", db.Service.GetCache("ヴァンダル強い"));
        Assert.Null(db.Service.GetCache("バンダル強い"));
    }

    [Fact]
    public async Task MishearingFix_InactiveCard_NotApplied()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var glossary = new GlossaryState();
        var sut = new TranslationManager(client, new TextProcessingService(), db.Service,
            new FakeSettingsService(Settings()), glossary: glossary,
            mishearingFixes: CreateMishearingFixes((1, "バンダル", "ヴァンダル")));

        await sut.TranslateAsync(new PotplayerRequest { Current = "バンダル強い" }, TestContext.Current.CancellationToken);

        // 卡 1 不在生效清單（ActiveIds 空）：原文照舊送模型
        Assert.Contains("バンダル強い", client.LastUserText);
        Assert.DoesNotContain("ヴァンダル", client.LastUserText);
    }

    [Fact]
    public async Task MishearingFix_AppliedBeforeSourceReplacementRules()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var settings = Settings();
        settings.SourceReplacementRules = "ヴァンダル=Vandal";
        var glossary = new GlossaryState();
        glossary.SetActiveIds([1L]);
        var sut = new TranslationManager(client, new TextProcessingService(), db.Service,
            new FakeSettingsService(settings), glossary: glossary,
            mishearingFixes: CreateMishearingFixes((1, "バンダル", "ヴァンダル")));

        await sut.TranslateAsync(new PotplayerRequest { Current = "バンダル強い" }, TestContext.Current.CancellationToken);

        // 先修正（バンダル → ヴァンダル）、再套用使用者的原文規則（ヴァンダル → Vandal）：
        // 若順序相反，規則一開始就對 バンダル 無從套用，結果不會是 Vandal
        Assert.Contains("Vandal強い", client.LastUserText);
        Assert.DoesNotContain("バンダル", client.LastUserText);
        Assert.DoesNotContain("ヴァンダル", client.LastUserText);
        Assert.Equal("這是翻譯", db.Service.GetCache("Vandal強い"));
    }

    [Fact]
    public async Task MishearingFix_KeywordHitCard_NotApplied()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var settings = Settings();
        var (state, keyword) = CreateKeywordGlossary(settings);
        var sut = new TranslationManager(client, new TextProcessingService(), db.Service,
            new FakeSettingsService(settings), glossary: state, keywordGlossary: keyword,
            mishearingFixes: CreateMishearingFixes((1, "ヴァンダル", "バンダル")));

        // 卡 1 不在生效清單（ActiveIds 空）：即使名詞出現在本句、關鍵字提示列出，修正也不套用
        Assert.Empty(state.ActiveIds);

        await sut.TranslateAsync(new PotplayerRequest { Current = "ヴァンダル強い" }, TestContext.Current.CancellationToken);

        // 原文照舊送模型（未被修正），名詞以關鍵字提示列出
        Assert.Contains("ヴァンダル強い", client.LastUserText);
        Assert.DoesNotContain("バンダル強い", client.LastUserText);
        Assert.Contains("ヴァンダル=暴徒", client.LastUserText);
    }

    [Fact]
    public async Task MishearingFix_ThroughPass_EchoesFixedSource()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var glossary = new GlossaryState();
        glossary.SetActiveIds([1L]);
        var sut = new TranslationManager(client, new TextProcessingService(), db.Service,
            new FakeSettingsService(Settings(throughPass: true)), glossary: glossary,
            mishearingFixes: CreateMishearingFixes((1, "バンダル", "ヴァンダル")));

        var result = await sut.TranslateAsync(new PotplayerRequest { Current = "バンダル強い" }, TestContext.Current.CancellationToken);

        // 直通模式也套用（修正的是原文，跟要不要送模型無關）
        Assert.Equal("ヴァンダル強い", result);
        Assert.Equal(0, client.CallCount);
    }

    /// <summary>建立含遊戲卡 VALORANT（Id=1）與其共通用語子卡（Id=2，名詞一個）的 GlossaryState。</summary>
    private static GlossaryState CreateGameWithCommonChild()
    {
        var state = new GlossaryState();
        state.SetCatalog(new GlossaryCatalog(
            [
                new GlossaryCard { Id = 1, Kind = GlossaryKind.Game, Name = "VALORANT" },
                new GlossaryCard { Id = 2, Kind = GlossaryKind.Common, Name = "VALORANT用語", ParentId = 1 },
            ],
            [new GlossaryTerm { Id = 11, CardId = 2, Source = "ガチランク", Target = "認真排名" }]));
        return state;
    }

    [Fact]
    public async Task MishearingFix_SupplementCardOfActiveGameCard_Applied()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var glossary = CreateGameWithCommonChild();
        // 遊戲卡 1 生效：它的共通用語子卡 2 是附屬用語卡，名詞的修正也套用
        glossary.SetActiveIds([1L]);
        var sut = new TranslationManager(client, new TextProcessingService(), db.Service,
            new FakeSettingsService(Settings()), glossary: glossary,
            mishearingFixes: CreateMishearingFixes((2, "ガチランク", "ランクマッチ")));

        await sut.TranslateAsync(new PotplayerRequest { Current = "ガチランク行く" }, TestContext.Current.CancellationToken);

        // 修正掛在附屬用語卡的名詞上：原文替換後才送模型
        Assert.Contains("ランクマッチ行く", client.LastUserText);
        Assert.DoesNotContain("ガチランク行く", client.LastUserText);
    }

    [Fact]
    public async Task MishearingFix_SupplementCard_GameCardNotActive_NotApplied()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var glossary = CreateGameWithCommonChild();
        // 生效的是別張遊戲卡（Id=99，不在型錄中）：子卡 2 的上層遊戲卡沒生效，不算附屬用語卡
        glossary.SetActiveIds([99L]);
        var sut = new TranslationManager(client, new TextProcessingService(), db.Service,
            new FakeSettingsService(Settings()), glossary: glossary,
            mishearingFixes: CreateMishearingFixes((2, "ガチランク", "ランクマッチ")));

        await sut.TranslateAsync(new PotplayerRequest { Current = "ガチランク行く" }, TestContext.Current.CancellationToken);

        // 原文照舊送模型（未被替換）
        Assert.Contains("ガチランク行く", client.LastUserText);
        Assert.DoesNotContain("ランクマッチ行く", client.LastUserText);
    }

    // ===== YouTube 字幕參考（CaptionReference 的 user prompt 段落）=====

    /// <summary>可以切換回傳值的假播放時鐘。</summary>
    private sealed class FakePlaybackClock : IPlaybackClock
    {
        public long? PositionMs { get; set; }

        public long? GetPositionMs() => PositionMs;
    }

    /// <summary>建立載入一段 YouTube 字幕（在播放位置範圍內）的 CaptionReference。</summary>
    private static CaptionReference CreateCaptionReference(IPlaybackClock clock, string cueText)
    {
        var captions = new CaptionTrackState();
        captions.Set("vod", [new CaptionCue(50_000, 2000, cueText)]);
        return new CaptionReference(captions, clock);
    }

    [Fact]
    public async Task CaptionReference_MatchFound_UserMessageContainsSectionAndMatchedText()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var sut = new TranslationManager(client, new TextProcessingService(), db.Service,
            new FakeSettingsService(Settings(conversation: true)),
            captionReference: CreateCaptionReference(new FakePlaybackClock { PositionMs = 55_000 }, "ぴんくの部屋着じゃなかったら"));

        await sut.TranslateAsync(new PotplayerRequest { Current = "これで部屋着じゃなかったら" }, TestContext.Current.CancellationToken);

        Assert.StartsWith("# 參考：同一時段的 YouTube 自動字幕", client.LastUserText);
        Assert.Contains("ぴんくの部屋着じゃなかったら", client.LastUserText);
        // 有參考段落時，本句前面加標題：在參考段落之後、本句原文之前
        var text = client.LastUserText!;
        var labelIndex = text.IndexOf("# 本句（只翻譯這段）", StringComparison.Ordinal);
        Assert.True(labelIndex > text.IndexOf("# 參考：同一時段的 YouTube 自動字幕", StringComparison.Ordinal));
        Assert.EndsWith("\n---\n# 本句（只翻譯這段）\nこれで部屋着じゃなかったら", text);

        // 多輪歷史存的仍是本句原文（不含參考段落與標題），比照 keywordSection 的作法
        await sut.TranslateAsync(new PotplayerRequest { Current = "次の文" }, TestContext.Current.CancellationToken);
        Assert.Equal(["これで部屋着じゃなかったら"], client.Histories[^1].Select(t => t.User).ToList());
    }

    [Fact]
    public async Task CaptionReference_ThroughPass_JobUserPromptHasNoSection()
    {
        using var db = new TempDatabase();
        var sut = new TranslationManager(new FakeOpenAiClient(), new TextProcessingService(), db.Service,
            new FakeSettingsService(Settings(throughPass: true)),
            captionReference: CreateCaptionReference(new FakePlaybackClock { PositionMs = 55_000 }, "これで部屋着じゃなかったら"));
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await sut.TranslateAsync(new PotplayerRequest { Current = "これで部屋着じゃなかったら" }, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        // 直通模式不加 YouTube 字幕參考段落，也不加「# 本句」標題
        Assert.NotNull(received);
        Assert.DoesNotContain("YouTube 自動字幕", received!.UserPrompt);
        Assert.DoesNotContain("# 本句", received.UserPrompt);
    }

    [Fact]
    public async Task CaptionReference_NoMatch_UserMessageSameAsWithoutReference()
    {
        using var dbWith = new TempDatabase();
        using var dbWithout = new TempDatabase();
        var settings = Settings(conversation: true);
        var clientWith = new FakeOpenAiClient();
        var clientWithout = new FakeOpenAiClient();
        // 沒有播放位置、從來沒對到過，而且句子短於 8 字：不參考
        var with = new TranslationManager(clientWith, new TextProcessingService(), dbWith.Service,
            new FakeSettingsService(settings),
            captionReference: CreateCaptionReference(new FakePlaybackClock(), "どうしよう"));
        var without = new TranslationManager(clientWithout, new TextProcessingService(), dbWithout.Service,
            new FakeSettingsService(settings));

        await with.TranslateAsync(new PotplayerRequest { Current = "どうしよう" }, TestContext.Current.CancellationToken);
        await without.TranslateAsync(new PotplayerRequest { Current = "どうしよう" }, TestContext.Current.CancellationToken);

        Assert.Equal(clientWithout.LastUserText, clientWith.LastUserText);
        Assert.Equal("どうしよう", clientWith.LastUserText);
    }

    [Fact]
    public async Task CaptionAndKeywordSections_KeywordFirstThenCaptionThenCurrent()
    {
        using var db = new TempDatabase();
        var settings = Settings(conversation: true);
        var (state, keyword) = CreateKeywordGlossary(settings);
        var client = new FakeOpenAiClient();
        var sut = new TranslationManager(client, new TextProcessingService(), db.Service,
            new FakeSettingsService(settings), glossary: state, keywordGlossary: keyword,
            captionReference: CreateCaptionReference(new FakePlaybackClock { PositionMs = 55_000 }, "今日はヴァンダルの話らしい"));

        await sut.TranslateAsync(new PotplayerRequest { Current = "今日はヴァンダルの話" }, TestContext.Current.CancellationToken);

        // 順序：關鍵字詞彙 → YouTube 字幕參考 → 本句標題 → 本句原文，各以 \n---\n 分隔
        var text = client.LastUserText!;
        var keywordIndex = text.IndexOf("# 參考詞彙", StringComparison.Ordinal);
        var captionIndex = text.IndexOf("# 參考：同一時段的 YouTube 自動字幕", StringComparison.Ordinal);
        var labelIndex = text.IndexOf("# 本句（只翻譯這段）", StringComparison.Ordinal);
        Assert.True(keywordIndex >= 0);
        Assert.True(captionIndex > keywordIndex);
        Assert.True(labelIndex > captionIndex);
        Assert.Contains("\n---\n# 參考：同一時段的 YouTube 自動字幕", text);
        Assert.Contains("ヴァンダル=暴徒", text);
        Assert.Contains("今日はヴァンダルの話らしい", text);
        Assert.EndsWith("\n---\n# 本句（只翻譯這段）\n今日はヴァンダルの話", text);
    }

    // ===== 回溯紀錄（llm_calls）：真 LlmCallRecorder 搭配記憶體 SpyAuditStore；recorder 是 null 的情況由上面既有測試覆蓋 =====

    private static (TranslationManager Manager, SpyAuditStore Store, LlmCallRecorder Recorder) CreateWithRecorder(
        FakeOpenAiClient client, TempDatabase db, AppSettings settings)
    {
        var store = new SpyAuditStore();
        var recorder = new LlmCallRecorder(
            store, new FakeSettingsService(settings), registry: null, new StreamSession(),
            NullLogger<LlmCallRecorder>.Instance);
        var manager = new TranslationManager(
            client, new TextProcessingService(), db.Service, new FakeSettingsService(settings),
            recorder: recorder);
        return (manager, store, recorder);
    }

    // 1. 單輪模式翻一句：記 1 筆，UserPrompt 等於送出的內容，JobId 等於 Job 的 Id。
    [Fact]
    public async Task LlmCall_SingleTurn_RecordsOneCallWithUserPromptAndJobId()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient("你好");
        var settings = Settings();
        settings.Audit.FullRecordEnabled = true;
        var (sut, store, recorder) = CreateWithRecorder(client, db, settings);
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            await sut.TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
        await recorder.FlushAsync();

        var call = Assert.Single(store.Calls);
        Assert.Equal(LlmCallPurposes.LiveTranslation, call.Purpose);
        Assert.Equal(received!.Id, call.JobId);
        Assert.Equal(client.LastSystemText, Assert.Single(store.PromptSnapshots));
        Assert.Equal(client.LastUserText, call.UserPrompt);
        Assert.Null(call.History); // 單輪模式沒有 history
        Assert.Equal("fake", call.Model);
        Assert.Equal(LlmCallOutcomes.Ok, call.Outcome);
    }

    // 2. 多輪模式：History 是送出的歷史（JSON）。
    [Fact]
    public async Task LlmCall_ConversationMode_RecordsSentHistoryAsJson()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(); // 預設回覆「这是翻译」
        var settings = Settings(conversation: true);
        settings.Audit.FullRecordEnabled = true;
        var (sut, store, recorder) = CreateWithRecorder(client, db, settings);

        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "two" }, TestContext.Current.CancellationToken);
        await recorder.FlushAsync();

        Assert.Equal(2, store.Calls.Count);
        var second = store.Calls[1];
        Assert.Equal("""[{"user":"one","assistant":"这是翻译"}]""", second.History);
        Assert.Equal("two", second.UserPrompt);
    }

    // 3. 崩壞重試：記 2 筆（第一次的原始回覆與重試的成功回覆）。
    [Fact]
    public async Task LlmCall_CollapseRetry_RecordsTwoCalls()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var settings = Settings(conversation: true);
        settings.Audit.FullRecordEnabled = true;
        var (sut, store, recorder) = CreateWithRecorder(client, db, settings);

        // 先累積歷史（第二次請求帶第 1 輪），再送出會崩壞的回覆
        await sut.TranslateAsync(new PotplayerRequest { Current = "one" }, TestContext.Current.CancellationToken);
        await sut.TranslateAsync(new PotplayerRequest { Current = "two" }, TestContext.Current.CancellationToken);
        client.Replies.Enqueue("這");
        client.Replies.Enqueue("這也太誇張了吧");
        await sut.TranslateAsync(new PotplayerRequest { Current = "カーレスターどこ？" }, TestContext.Current.CancellationToken);
        await recorder.FlushAsync();

        Assert.Equal(4, store.Calls.Count); // 前兩句各 1 筆 + 崩壞重試 2 筆
        // 第一次帶著歷史送出、回覆崩壞；重試以空歷史送出
        Assert.Equal(LlmCallOutcomes.Ok, store.Calls[2].Outcome);
        Assert.Equal("這", store.Calls[2].ResponseRaw);
        Assert.Contains("""{"user":"two","assistant":"这是翻译"}""", store.Calls[2].History);
        Assert.Equal(LlmCallOutcomes.Ok, store.Calls[3].Outcome);
        Assert.Equal("這也太誇張了吧", store.Calls[3].ResponseRaw);
        Assert.Equal("[]", store.Calls[3].History);
    }

    // 4. 逾時：記 1 筆 Timeout。
    [Fact]
    public async Task LlmCall_Timeout_RecordsOneTimeoutCall()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(delay: TimeSpan.FromSeconds(1));
        var settings = Settings(timeoutMs: 100);
        settings.Audit.FullRecordEnabled = true;
        var (sut, store, recorder) = CreateWithRecorder(client, db, settings);

        var result = await sut.TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);
        await recorder.FlushAsync();

        Assert.Equal("こんにちは", result);
        var call = Assert.Single(store.Calls);
        Assert.Equal(LlmCallPurposes.LiveTranslation, call.Purpose);
        Assert.Equal(LlmCallOutcomes.Timeout, call.Outcome);
        Assert.Equal(client.LastUserText, call.UserPrompt); // 單輪模式的 UserPrompt 是整個組好的 prompt
        Assert.Contains("こんにちは", call.UserPrompt);
        Assert.Null(call.ResponseRaw);
    }

    // 4b. 逾時：TIMEOUT Job 沿用原本的句子編號，llm_calls 的 Timeout 紀錄才對得回字幕紀錄。
    [Fact]
    public async Task LlmCall_Timeout_TimeoutJobIdMatchesRecordedJobId()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient(delay: TimeSpan.FromSeconds(1));
        var (sut, store, recorder) = CreateWithRecorder(client, db, Settings(timeoutMs: 100));
        const string source = "逾時編號テスト";
        var recipient = new object();
        TranslationJob? timeoutJob = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) =>
        {
            // Messenger 是全域的：只收這個測試的逾時 Job
            if (job.Source == TranslationSource.Timeout && job.Current == source) timeoutJob = job;
        });

        try
        {
            await sut.TranslateAsync(new PotplayerRequest { Current = source }, TestContext.Current.CancellationToken);
            await recorder.FlushAsync();
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        var call = Assert.Single(store.Calls);
        Assert.Equal(LlmCallOutcomes.Timeout, call.Outcome);
        Assert.NotNull(timeoutJob);
        Assert.Equal(call.JobId, timeoutJob.Id);
    }

    // 5. 命中快取：不記 llm_calls；送出的 Job 的 CacheId 等於 store 回傳的 Id。
    [Fact]
    public async Task LlmCall_CacheHit_NoRecord_JobCarriesCacheId()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        db.Service.SaveCache("こんにちは", "快取結果");
        var (sut, store, recorder) = CreateWithRecorder(client, db, Settings());
        var recipient = new object();
        TranslationJob? received = null;
        WeakReferenceMessenger.Default.Register<TranslationJob>(recipient, (_, job) => received = job);

        try
        {
            var result = await sut.TranslateAsync(new PotplayerRequest { Current = "こんにちは" }, TestContext.Current.CancellationToken);
            Assert.Equal("快取結果", result);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
        await recorder.FlushAsync();

        Assert.Empty(store.Calls);
        Assert.NotNull(received);
        Assert.Equal(TranslationSource.Cache, received!.Source);
        Assert.NotNull(db.Service.GetCacheId("こんにちは"));
        Assert.Equal(db.Service.GetCacheId("こんにちは"), received.CacheId);
    }

    // 重翻：記 1 筆 Purpose＝Retranslate、JobId＝null。
    [Fact]
    public async Task LlmCall_Retranslate_RecordsRetranslateCallWithoutJobId()
    {
        using var db = new TempDatabase();
        var client = new FakeOpenAiClient();
        var settings = Settings(conversation: true);
        settings.Audit.FullRecordEnabled = true;
        var (sut, store, recorder) = CreateWithRecorder(client, db, settings);

        await sut.TranslateAsync(new PotplayerRequest { Current = "wrong" }, TestContext.Current.CancellationToken);
        client.Replies.Enqueue("這是修正後的翻譯");
        var result = await sut.RetranslateAsync("wrong", "corrected", TestContext.Current.CancellationToken);
        await recorder.FlushAsync();

        Assert.NotNull(result);
        var call = Assert.Single(store.Calls, c => c.Purpose == LlmCallPurposes.Retranslate);
        Assert.Null(call.JobId);
        Assert.Equal("corrected", call.UserPrompt);
        Assert.Equal(LlmCallOutcomes.Ok, call.Outcome);
    }
}
