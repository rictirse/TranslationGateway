using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>LlmCallRecorder：不經過 Registry、以 Func 判斷模板 Id；寫入一律在背景佇列，
/// FlushAsync 之後才斷言（記憶體 SpyAuditStore，不連資料庫）。</summary>
public class LlmCallRecorderTests
{
    private static LlmCallRecorder Create(SpyAuditStore store, AppSettings? settings = null, StreamSession? session = null,
        Func<long?>? liveTemplateId = null, Func<string, long?>? defaultTemplateId = null, Func<string, long?>? findTemplateId = null) =>
        new(store, new FakeSettingsService(settings), registry: null, session ?? new StreamSession(),
            NullLogger<LlmCallRecorder>.Instance, liveTemplateId, defaultTemplateId, findTemplateId);

    private static LlmCallInput Input(string purpose = LlmCallPurposes.LiveTranslation, string system = "直播 system") => new()
    {
        Purpose = purpose,
        JobId = "job1",
        SystemPrompt = system,
    };

    // 1. 完整紀錄關閉：完整欄位都是 null；輕量欄位都有值；stream_id 是 Record 當下的 CurrentStreamId。
    [Fact]
    public async Task Record_FullRecordDisabled_StoresOnlyLightweightFields()
    {
        var store = new SpyAuditStore();
        var session = new StreamSession { CurrentStreamId = 7 };
        var sut = Create(store, session: session);

        sut.Record(new LlmCallInput
        {
            Purpose = LlmCallPurposes.LiveTranslation,
            JobId = "job1",
            SystemPrompt = "直播 system",
            UserPrompt = "user prompt",
            History = [new ChatTurn("a", "b")],
            ResponseRaw = "raw reply",
            KeywordTermIds = [3],
            FixIds = [9],
            Model = "qwen",
            PromptTokens = 11,
            CompletionTokens = 22,
            LatencyMs = 123,
        });
        session.CurrentStreamId = null; // Record 之後就換掉：stream_id 必須是 Record 當下的值
        await sut.FlushAsync();

        var call = Assert.Single(store.Calls);
        Assert.Equal(LlmCallPurposes.LiveTranslation, call.Purpose);
        Assert.Equal("job1", call.JobId);
        Assert.Equal(7, call.StreamId);
        Assert.Equal("qwen", call.Model);
        Assert.Equal(11, call.PromptTokens);
        Assert.Equal(22, call.CompletionTokens);
        Assert.Equal(123, call.LatencyMs);
        Assert.Equal(LlmCallOutcomes.Ok, call.Outcome);
        Assert.NotNull(call.SystemSnapshotId);
        Assert.NotNull(call.SettingsSnapshotId);
        // 完整欄位：關閉時一律 null
        Assert.Null(call.UserPrompt);
        Assert.Null(call.History);
        Assert.Null(call.ResponseRaw);
        Assert.Null(call.KeywordTermIds);
        Assert.Null(call.FixIds);
    }

    // 2. 完整紀錄開啟：History 是 [{"user":"…","assistant":"…"}]；兩種 Id 清單是 JSON 數字陣列（空集合 []）。
    [Fact]
    public async Task Record_FullRecordEnabled_SerializesHistoryAndIdsAsJson()
    {
        var store = new SpyAuditStore();
        var settings = new AppSettings { Audit = new AuditSettings { FullRecordEnabled = true } };
        var sut = Create(store, settings);

        sut.Record(new LlmCallInput
        {
            Purpose = LlmCallPurposes.LiveTranslation,
            SystemPrompt = "直播 system",
            UserPrompt = "user prompt",
            History = [new ChatTurn("a", "b"), new ChatTurn("c", "d")],
            ResponseRaw = "raw reply",
            KeywordTermIds = [5, 9],
            FixIds = [1],
        });
        sut.Record(new LlmCallInput
        {
            Purpose = LlmCallPurposes.LiveTranslation,
            SystemPrompt = "直播 system",
            History = [],
            KeywordTermIds = [],
            FixIds = [],
        });
        await sut.FlushAsync();

        Assert.Equal(2, store.Calls.Count);
        var first = store.Calls[0];
        Assert.Equal("user prompt", first.UserPrompt);
        Assert.Equal("raw reply", first.ResponseRaw);
        Assert.Equal("""[{"user":"a","assistant":"b"},{"user":"c","assistant":"d"}]""", first.History);
        Assert.Equal("[5,9]", first.KeywordTermIds);
        Assert.Equal("[1]", first.FixIds);
        var second = store.Calls[1];
        Assert.Equal("[]", second.KeywordTermIds);
        Assert.Equal("[]", second.FixIds);
        Assert.Equal("[]", second.History); // 有給歷史（空集合）時也存 JSON 陣列
    }

    // 3. 同一份 system 記兩次只 Ensure 一次快照（記憶體快取）；Ensure 回 null 時不快取、下次再試。
    [Fact]
    public async Task Record_SameSystemTwice_EnsuresSnapshotOnce_NullRetriedNextTime()
    {
        var store = new SpyAuditStore();
        var sut = Create(store);

        sut.Record(Input());
        sut.Record(Input());
        await sut.FlushAsync();

        Assert.Equal(2, store.Calls.Count);
        Assert.Single(store.PromptSnapshots);
        Assert.Equal(store.Calls[0].SystemSnapshotId, store.Calls[1].SystemSnapshotId);

        // Ensure 回 null：這筆被丟掉，而且不快取
        store.NullSnapshots = true;
        sut.Record(Input(system: "另一個 system"));
        await sut.FlushAsync();
        Assert.Equal(2, store.PromptSnapshotAttempts); // 有再試一次（失敗）
        Assert.Single(store.PromptSnapshots);
        Assert.Equal(2, store.Calls.Count);

        // 恢復後同一份 system 再記一次：因為沒放進快取，會再試並成功
        store.NullSnapshots = false;
        sut.Record(Input(system: "另一個 system"));
        await sut.FlushAsync();
        Assert.Equal(3, store.PromptSnapshotAttempts);
        Assert.Equal(2, store.PromptSnapshots.Count);
        Assert.Equal(3, store.Calls.Count);
    }

    // 4. 設定快照：JSON 不含 ApiKey 的值；LiveTranslation 用 Translation 的設定、Mining 用 Lorebook 的設定。
    [Fact]
    public async Task Record_SettingsSnapshot_OmitsApiKey_AndUsesSettingsByPurpose()
    {
        var store = new SpyAuditStore();
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                ApiHost = "http://translation-host",
                Model = "trans-model",
                ApiKey = "SECRET-TRANSLATION-KEY",
                Temperature = 0.3,
                TimeoutMs = 1500,
            },
            Lorebook = new TranslationSettings
            {
                ApiHost = "http://lorebook-host",
                Model = "lore-model",
                ApiKey = "SECRET-LOREBOOK-KEY",
            },
        };
        var sut = Create(store, settings);

        sut.Record(Input(purpose: LlmCallPurposes.LiveTranslation));
        sut.Record(Input(purpose: LlmCallPurposes.Mining, system: "探勘 system"));
        await sut.FlushAsync();

        Assert.Equal(2, store.SettingsSnapshots.Count);
        var live = store.SettingsSnapshots[0];
        Assert.Contains("trans-model", live);
        Assert.Contains("http://translation-host", live);
        Assert.DoesNotContain("lore-model", live);
        var mining = store.SettingsSnapshots[1];
        Assert.Contains("lore-model", mining);
        Assert.Contains("http://lorebook-host", mining);
        Assert.DoesNotContain("trans-model", mining);
        // 兩種快照都不能出現 ApiKey 的值與 ApiKey 這個欄位名
        Assert.DoesNotContain("SECRET-TRANSLATION-KEY", live);
        Assert.DoesNotContain("SECRET-LOREBOOK-KEY", live);
        Assert.DoesNotContain("SECRET-TRANSLATION-KEY", mining);
        Assert.DoesNotContain("SECRET-LOREBOOK-KEY", mining);
        Assert.DoesNotContain("api_key", live, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", mining, StringComparison.OrdinalIgnoreCase);
    }

    // 5. TemplateId 的判斷：LiveTranslation 用 LiveTemplateId、VodBatch 用預設 Id、其他用 FindTemplateId、input 有給時以 input 為準。
    [Fact]
    public async Task Record_ResolvesTemplateIdByPurpose_InputWins()
    {
        var store = new SpyAuditStore();
        string? defaultKind = null;
        string? foundContent = null;
        var sut = Create(store,
            liveTemplateId: () => 71,
            defaultTemplateId: kind => { defaultKind = kind; return 82; },
            findTemplateId: content => { foundContent = content; return 93; });

        sut.Record(Input(purpose: LlmCallPurposes.LiveTranslation));
        sut.Record(Input(purpose: LlmCallPurposes.Retranslate));
        sut.Record(Input(purpose: LlmCallPurposes.VodBatch));
        sut.Record(Input(purpose: LlmCallPurposes.Mining, system: "探勘 system"));
        sut.Record(new LlmCallInput { Purpose = LlmCallPurposes.Mining, SystemPrompt = "探勘 system", TemplateId = 42 });
        await sut.FlushAsync();

        Assert.Equal(5, store.Calls.Count);
        Assert.Equal(71, store.Calls[0].TemplateId);
        Assert.Equal(71, store.Calls[1].TemplateId);
        Assert.Equal(82, store.Calls[2].TemplateId);
        Assert.Equal("VodBatch", defaultKind);
        Assert.Equal(93, store.Calls[3].TemplateId);
        Assert.Equal("探勘 system", foundContent);
        Assert.Equal(42, store.Calls[4].TemplateId);
    }

    // 6. store 擲例外：Record 不擲例外，後面的紀錄照常寫入。
    [Fact]
    public async Task Record_StoreThrows_RecordDoesNotThrow_NextRecordWritten()
    {
        var store = new SpyAuditStore { ThrowWrites = true };
        var sut = Create(store);

        var exception = Record.Exception(() => sut.Record(Input()));
        Assert.Null(exception);
        await sut.FlushAsync(); // 等這筆（失敗）處理完再恢復 store

        store.ThrowWrites = false;
        sut.Record(Input(system: "後來的 system"));
        await sut.FlushAsync();

        var call = Assert.Single(store.Calls);
        Assert.NotNull(call);
    }
}
