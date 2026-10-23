using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>記憶體版的 IAuditStore：EnsurePromptTemplate 依 (kind, hash) 去重，模擬資料庫行為；Fail = true 模擬 DB 不可用。</summary>
internal sealed class MemoryAuditStore : IAuditStore
{
    private readonly List<PromptTemplate> _templates = [];
    private long _nextId = 1;

    public bool Fail { get; set; }

    public int TemplateCount => _templates.Count;

    public IReadOnlyList<PromptTemplate> Templates => _templates;

    public long? EnsurePromptTemplate(string kind, string content, string origin, string? appVersion)
    {
        if (Fail) return null;

        var hash = AuditHash.Of(content);
        var existing = _templates.FirstOrDefault(t => t.Kind == kind && t.ContentHash == hash);
        if (existing is not null) return existing.Id;

        var template = new PromptTemplate(_nextId++, kind, content, hash, origin, appVersion, DateTimeOffset.UtcNow);
        _templates.Add(template);
        return template.Id;
    }

    public IReadOnlyList<PromptTemplate> GetPromptTemplates(string kind) =>
        Fail ? [] : _templates.Where(t => t.Kind == kind).OrderByDescending(t => t.Id).ToList();

    public PromptTemplate? GetPromptTemplate(long id) =>
        Fail ? null : _templates.FirstOrDefault(t => t.Id == id);

    public long? EnsurePromptSnapshot(long? templateId, string content) => 1;

    public long? EnsureSettingsSnapshot(string content) => 1;

    public long? InsertLlmCall(LlmCallRecord record) => 1;

    public AuditPurgeResult PurgeAudit(DateTimeOffset cutoffUtc) => new(0, 0, 0);
}

/// <summary>PromptTemplateRegistry：啟動時寫入 8 種預設、直播 system prompt 依選用版本套用、存檔時 Commit 成新版本。</summary>
public class PromptTemplateRegistryTests
{
    private static PromptTemplateRegistry CreateRegistry(MemoryAuditStore store, AppSettings? settings = null) =>
        new(store, new FakeSettingsService(settings), NullLogger<PromptTemplateRegistry>.Instance, "1.0.0+test");

    [Fact]
    public void Initialize_WritesEightDefaults_AndSecondRunDoesNotDuplicate()
    {
        var store = new MemoryAuditStore();
        var settings = new AppSettings();
        var sut = CreateRegistry(store, settings);

        sut.Initialize();

        // 8 種預設各一筆
        Assert.Equal(8, store.TemplateCount);
        Assert.Equal(
            ["CaptionPreprocess", "LiveSystem", "MiningCompose", "MiningExtract", "Mishearing", "TermDiscovery", "TermImport", "VodBatch"],
            store.Templates.Select(t => t.Kind).OrderBy(k => k, StringComparer.Ordinal).ToList());
        Assert.All(store.Templates, t => Assert.Equal(PromptTemplateOrigins.Default, t.Origin));

        // 再 Initialize 一次：假 store 依 (kind, hash) 去重，不會重複新增
        sut.Initialize();
        Assert.Equal(8, store.TemplateCount);

        // 沒選版本：執行期內容是預設，LiveTemplateId 是預設 Id
        Assert.Null(settings.Translation.SystemPromptTemplateId);
        Assert.Equal(PromptBuilder.DefaultSystemTemplate, settings.Translation.SystemPromptTemplate);
        Assert.Equal(sut.DefaultTemplateId(PromptTemplateKinds.LiveSystem), sut.LiveTemplateId);
    }

    [Fact]
    public void Initialize_SelectedUserVersion_RuntimeContentIsThatVersion()
    {
        var store = new MemoryAuditStore();
        var settings = new AppSettings();
        var customId = store.EnsurePromptTemplate(PromptTemplateKinds.LiveSystem, "你是自訂翻譯器", PromptTemplateOrigins.User, null);
        settings.Translation.SystemPromptTemplateId = customId;
        var sut = CreateRegistry(store, settings);

        sut.Initialize();

        Assert.Equal("你是自訂翻譯器", settings.Translation.SystemPromptTemplate);
        Assert.Equal(customId, sut.LiveTemplateId);
        Assert.Equal(customId, settings.Translation.SystemPromptTemplateId);
    }

    [Fact]
    public void Initialize_SelectedIdMissing_FallsBackToDefault_AndKeepsSelection()
    {
        var store = new MemoryAuditStore();
        var settings = new AppSettings { Translation = new TranslationSettings { SystemPromptTemplateId = 999 } };
        var sut = CreateRegistry(store, settings);

        sut.Initialize();

        // 讀不到：內容退回預設、LiveTemplateId 是預設 Id，但設定裡的 Id 不改（DB 恢復後下次啟動還能用）
        Assert.Equal(PromptBuilder.DefaultSystemTemplate, settings.Translation.SystemPromptTemplate);
        Assert.Equal(sut.DefaultTemplateId(PromptTemplateKinds.LiveSystem), sut.LiveTemplateId);
        Assert.Equal(999, settings.Translation.SystemPromptTemplateId);
    }

    [Fact]
    public void Initialize_StoreUnavailable_DoesNotThrow_AndUsesDefault()
    {
        var store = new MemoryAuditStore { Fail = true };
        var settings = new AppSettings();
        var sut = CreateRegistry(store, settings);

        var exception = Record.Exception(() => sut.Initialize());

        Assert.Null(exception);
        Assert.Equal(PromptBuilder.DefaultSystemTemplate, settings.Translation.SystemPromptTemplate);
        Assert.Null(sut.LiveTemplateId);
        Assert.Null(sut.DefaultTemplateId(PromptTemplateKinds.LiveSystem));
    }

    [Fact]
    public void CommitLiveTemplate_UnchangedContent_ReturnsSelectedIdWithoutWrite()
    {
        var store = new MemoryAuditStore();
        var settings = new AppSettings();
        var sut = CreateRegistry(store, settings);
        sut.Initialize();
        var countBefore = store.TemplateCount;

        // selectedId 是 null：比較基準是預設內容；內容相同 → 回傳 null，不寫資料庫
        Assert.Null(sut.CommitLiveTemplate(null, PromptBuilder.DefaultSystemTemplate));
        Assert.Equal(countBefore, store.TemplateCount);

        // 有選用版本且內容相同：回傳原 Id，不寫資料庫
        var customId = store.EnsurePromptTemplate(PromptTemplateKinds.LiveSystem, "自訂A", PromptTemplateOrigins.User, null);
        Assert.Equal(countBefore + 1, store.TemplateCount);
        Assert.Equal(customId, sut.CommitLiveTemplate(customId, "自訂A"));
        Assert.Equal(countBefore + 1, store.TemplateCount);
    }

    [Fact]
    public void CommitLiveTemplate_ChangedContent_CreatesUserVersionAndAppliesIt()
    {
        var store = new MemoryAuditStore();
        var settings = new AppSettings();
        var sut = CreateRegistry(store, settings);
        sut.Initialize();

        var newId = sut.CommitLiveTemplate(null, "我的新模板");

        Assert.NotEqual(sut.DefaultTemplateId(PromptTemplateKinds.LiveSystem), newId);
        Assert.NotNull(newId);
        Assert.Equal(newId, settings.Translation.SystemPromptTemplateId);
        Assert.Equal("我的新模板", settings.Translation.SystemPromptTemplate);
        Assert.Equal(newId, sut.LiveTemplateId);
        var created = store.Templates.Single(t => t.Id == newId);
        Assert.Equal(PromptTemplateOrigins.User, created.Origin);
        Assert.Equal("我的新模板", created.Content);
    }

    [Fact]
    public void CommitLiveTemplate_ContentMatchesExistingVersion_ReturnsExistingId()
    {
        var store = new MemoryAuditStore();
        var settings = new AppSettings();
        var existingId = store.EnsurePromptTemplate(PromptTemplateKinds.LiveSystem, "既有版本", PromptTemplateOrigins.User, null);
        var sut = CreateRegistry(store, settings);
        sut.Initialize();
        var countBefore = store.TemplateCount;

        // 內容改成和另一個既有版本一樣：回傳既有 Id，不新增
        Assert.Equal(existingId, sut.CommitLiveTemplate(null, "既有版本"));
        Assert.Equal(countBefore, store.TemplateCount);
        Assert.Equal(existingId, sut.LiveTemplateId);
    }

    [Fact]
    public void CommitLiveTemplate_BlankContent_FollowsDefault()
    {
        var store = new MemoryAuditStore();
        var settings = new AppSettings();
        var customId = store.EnsurePromptTemplate(PromptTemplateKinds.LiveSystem, "自訂A", PromptTemplateOrigins.User, null);
        settings.Translation.SystemPromptTemplateId = customId;
        var sut = CreateRegistry(store, settings);
        sut.Initialize();

        Assert.Null(sut.CommitLiveTemplate(customId, "   "));

        Assert.Null(settings.Translation.SystemPromptTemplateId);
        Assert.Equal(PromptBuilder.DefaultSystemTemplate, settings.Translation.SystemPromptTemplate);
        Assert.Equal(sut.DefaultTemplateId(PromptTemplateKinds.LiveSystem), sut.LiveTemplateId);
    }

    [Fact]
    public void CommitLiveTemplate_StoreUnavailable_KeepsSelectedId()
    {
        var store = new MemoryAuditStore();
        var settings = new AppSettings();
        var customId = store.EnsurePromptTemplate(PromptTemplateKinds.LiveSystem, "自訂A", PromptTemplateOrigins.User, null);
        settings.Translation.SystemPromptTemplateId = customId;
        var sut = CreateRegistry(store, settings);
        sut.Initialize();
        store.Fail = true;

        // DB 不可用：寫入失敗沿用原選擇；內容也讀不到 → 退回預設，但選用 Id 不變
        Assert.Equal(customId, sut.CommitLiveTemplate(customId, "改過的內容"));
        Assert.Equal(customId, settings.Translation.SystemPromptTemplateId);
        Assert.Equal(PromptBuilder.DefaultSystemTemplate, settings.Translation.SystemPromptTemplate);
    }

    [Fact]
    public void FindTemplateId_MatchesDefaultPromptsByHash()
    {
        var store = new MemoryAuditStore();
        var sut = CreateRegistry(store, new AppSettings());
        sut.Initialize();

        Assert.Equal(sut.DefaultTemplateId(PromptTemplateKinds.Mishearing), sut.FindTemplateId(MishearingPrompts.System));
        Assert.Equal(sut.DefaultTemplateId(PromptTemplateKinds.LiveSystem), sut.FindTemplateId(PromptBuilder.DefaultSystemTemplate));
        Assert.Null(sut.FindTemplateId("跟任何預設都不一樣的內容"));
        Assert.Null(sut.FindTemplateId(""));
    }

    [Fact]
    public void FindTemplateId_LiveSystem_AlsoComparesLiveTemplateContent()
    {
        var store = new MemoryAuditStore();
        var settings = new AppSettings();
        var customId = store.EnsurePromptTemplate(PromptTemplateKinds.LiveSystem, "自訂B", PromptTemplateOrigins.User, null);
        settings.Translation.SystemPromptTemplateId = customId;
        var sut = CreateRegistry(store, settings);
        sut.Initialize();

        // 不是預設內容，但等於目前 LiveTemplateId 的內容
        Assert.Equal(customId, sut.FindTemplateId("自訂B"));
    }

    [Fact]
    public void GetLiveVersions_ReturnsNewestFirst()
    {
        var store = new MemoryAuditStore();
        var sut = CreateRegistry(store, new AppSettings());
        sut.Initialize();
        var defaultId = sut.DefaultTemplateId(PromptTemplateKinds.LiveSystem);
        var customId = store.EnsurePromptTemplate(PromptTemplateKinds.LiveSystem, "自訂C", PromptTemplateOrigins.User, null);

        var versions = sut.GetLiveVersions();

        // 只有 LiveSystem 一種：自訂版（較晚寫入、Id 較大）在前，預設版在後，依 Id 由大到小
        Assert.Equal([customId, defaultId], versions.Select(v => (long?)v.Id).ToList());
    }
}
