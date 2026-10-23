using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;
using Translation.Gateway.ViewModels;

namespace Translation.Gateway.Wpf.Tests;

/// <summary>設定頁 ViewModel：翻譯模型與知識庫模型的 ApiHost 相同時，儲存前要先經使用者確認。</summary>
public sealed class SettingsViewModelTests
{
    /// <summary>記憶體版的 ISettingsService：只記錄 SaveAsync 的呼叫次數。</summary>
    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public string SettingsPath => "settings.json";

        public Task LoadAsync() => Task.CompletedTask;

        public int SaveCount { get; private set; }

        public Task SaveAsync()
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>記憶體版的 IAuditStore：EnsurePromptTemplate 依 (kind, hash) 去重，供 PromptTemplateRegistry 使用。</summary>
    private sealed class MemoryAuditStore : IAuditStore
    {
        private readonly List<PromptTemplate> _templates = [];
        private long _nextId = 1;

        public long? EnsurePromptTemplate(string kind, string content, string origin, string? appVersion)
        {
            var hash = AuditHash.Of(content);
            var existing = _templates.FirstOrDefault(t => t.Kind == kind && t.ContentHash == hash);
            if (existing is not null) return existing.Id;

            var template = new PromptTemplate(_nextId++, kind, content, hash, origin, appVersion, DateTimeOffset.UtcNow);
            _templates.Add(template);
            return template.Id;
        }

        public IReadOnlyList<PromptTemplate> GetPromptTemplates(string kind) =>
            _templates.Where(t => t.Kind == kind).OrderByDescending(t => t.Id).ToList();

        public PromptTemplate? GetPromptTemplate(long id) => _templates.FirstOrDefault(t => t.Id == id);

        public long? EnsurePromptSnapshot(long? templateId, string content) => 1;

        public long? EnsureSettingsSnapshot(string content) => 1;

        public long? InsertLlmCall(LlmCallRecord record) => 1;

        public AuditPurgeResult PurgeAudit(DateTimeOffset cutoffUtc) => new(0, 0, 0);
    }

    /// <summary>設定頁只用到 Description 的最小 ITranslationStore。</summary>
    private sealed class FakeTranslationStore : ITranslationStore
    {
        public string Description => "SQLite（測試）";

        public string? GetCache(string source) => null;

        public void SaveCache(string source, string target, bool isManual = false) { }

        public void SaveCacheUnlessManual(string source, string target) { }

        public bool DeleteCache(string source, bool includeManual = false) => false;

        public IEnumerable<TranslationCacheItem> GetAllCache() => [];
    }

    /// <summary>設定頁建構用到的最小 ILorebookLlm（測試不會真的呼叫模型）。</summary>
    private sealed class FakeLorebookLlm : ILorebookLlm
    {
        public Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }

    private static (SettingsViewModel Vm, MemoryAuditStore Store) CreateViewModel(FakeSettingsService settings)
    {
        var store = new MemoryAuditStore();
        var registry = new PromptTemplateRegistry(
            store, settings, NullLogger<PromptTemplateRegistry>.Instance);
        registry.Initialize();
        return (new SettingsViewModel(
            settings,
            new FakeTranslationStore(),
            new FakeLorebookLlm(),
            registry,
            NullLogger<SettingsViewModel>.Instance), store);
    }

    [Fact]
    public async Task Save_SameHost_UserDeclines_DoesNotSave()
    {
        var settings = new FakeSettingsService();
        settings.Current.Translation.ApiHost = "http://192.168.5.4:8000/v1";
        settings.Current.Lorebook.ApiHost = "http://192.168.5.4:8000/v1/";
        var (vm, _) = CreateViewModel(settings);
        var messages = new List<string>();
        vm.ConfirmDialog = message =>
        {
            messages.Add(message);
            return false;
        };

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // 按否：不寫入檔案；訊息說明同一台伺服器可能互相拖慢
        Assert.Equal(0, settings.SaveCount);
        var message = Assert.Single(messages);
        Assert.Contains("翻譯模型和知識庫模型的 Host 相同", message);
        Assert.Contains("互相拖慢", message);
        Assert.Contains("確定要這樣設定嗎？", message);
    }

    [Fact]
    public async Task Save_SameHost_UserConfirms_Saves()
    {
        var settings = new FakeSettingsService();
        settings.Current.Translation.ApiHost = "http://192.168.5.4:8000/v1";
        settings.Current.Lorebook.ApiHost = "HTTP://192.168.5.4:8000/v1/";
        var (vm, _) = CreateViewModel(settings);
        vm.ConfirmDialog = _ => true;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(1, settings.SaveCount);
    }

    [Fact]
    public async Task Save_DifferentHost_SavesWithoutConfirm()
    {
        var settings = new FakeSettingsService();
        settings.Current.Translation.ApiHost = "http://192.168.5.4:8000/v1";
        settings.Current.Lorebook.ApiHost = "http://127.0.0.1:1234/v1";
        var (vm, _) = CreateViewModel(settings);
        var confirmed = 0;
        vm.ConfirmDialog = _ =>
        {
            confirmed++;
            return false;
        };

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // Host 不同：不跳確認，直接儲存
        Assert.Equal(0, confirmed);
        Assert.Equal(1, settings.SaveCount);
    }

    [Fact]
    public async Task Save_LorebookHostBlank_SavesWithoutConfirm()
    {
        var settings = new FakeSettingsService();
        settings.Current.Translation.ApiHost = "http://192.168.5.4:8000/v1";
        settings.Current.Lorebook.ApiHost = "   ";
        var (vm, _) = CreateViewModel(settings);
        var confirmed = 0;
        vm.ConfirmDialog = _ =>
        {
            confirmed++;
            return false;
        };

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // 知識庫模型的 Host 空白：不跳確認，直接儲存
        Assert.Equal(0, confirmed);
        Assert.Equal(1, settings.SaveCount);
    }

    /// <summary>版本清單第一項是「跟著最新預設」，其後是 DB 的歷史版本；建構時選中目前 Id 對應的項目。</summary>
    [Fact]
    public void LivePromptVersions_ListsDefaultFirst_AndSelectsCurrentId()
    {
        var settings = new FakeSettingsService();
        var (vm, store) = CreateViewModel(settings);
        var customId = store.EnsurePromptTemplate(PromptTemplateKinds.LiveSystem, "自訂A", PromptTemplateOrigins.User, null);
        settings.Current.Translation.SystemPromptTemplateId = customId;

        vm.ReloadSetting();

        Assert.Equal("跟著最新預設", vm.LivePromptVersions[0].Label);
        Assert.Null(vm.LivePromptVersions[0].Id);
        var customItem = Assert.Single(vm.LivePromptVersions, i => i.Id == customId);
        Assert.Same(customItem, vm.SelectedLivePromptVersion);
        Assert.Contains("自訂", customItem.Label);
        // 版本依 Id 由大到小：剛加的自訂版本排在預設前面，最後一項是程式預設
        Assert.Same(customItem, vm.LivePromptVersions[1]);
        Assert.Contains("預設", vm.LivePromptVersions[^1].Label);
    }

    /// <summary>使用者改選版本：選用 Id 與該版全文寫回 Translation，編輯框內容隨之更新。</summary>
    [Fact]
    public void SelectedLivePromptVersion_Change_ReplacesSystemPromptTemplate()
    {
        var settings = new FakeSettingsService();
        var (vm, store) = CreateViewModel(settings);
        var customId = store.EnsurePromptTemplate(PromptTemplateKinds.LiveSystem, "自訂A", PromptTemplateOrigins.User, null);
        vm.ReloadSetting();

        vm.SelectedLivePromptVersion = vm.LivePromptVersions.Single(i => i.Id == customId);

        Assert.Equal(customId, vm.Translation.SystemPromptTemplateId);
        Assert.Equal("自訂A", vm.Translation.SystemPromptTemplate);

        // 選回「跟著最新預設」：清空選用 Id，內容回到預設
        vm.SelectedLivePromptVersion = vm.LivePromptVersions[0];
        Assert.Null(vm.Translation.SystemPromptTemplateId);
        Assert.Equal(PromptBuilder.DefaultSystemTemplate, vm.Translation.SystemPromptTemplate);
    }

    /// <summary>編輯內容後存檔：Commit 成新的 User 版本，選用 Id 更新，清單重建並選中新版本。</summary>
    [Fact]
    public async Task Save_EditedContent_CommitsNewUserVersion()
    {
        var settings = new FakeSettingsService();
        var (vm, store) = CreateViewModel(settings);
        vm.Translation.SystemPromptTemplate = "編輯過的新模板";

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(1, settings.SaveCount);
        var newId = vm.Translation.SystemPromptTemplateId;
        Assert.NotNull(newId);
        Assert.Equal("編輯過的新模板", vm.Translation.SystemPromptTemplate);
        Assert.Equal(newId, vm.SelectedLivePromptVersion?.Id);
        var created = store.GetPromptTemplate(newId.Value);
        Assert.NotNull(created);
        Assert.Equal(PromptTemplateOrigins.User, created.Origin);
        Assert.Equal("編輯過的新模板", created.Content);
    }

    /// <summary>Host 確認按「否」：不存檔也不 Commit（內容不會寫成新的版本）。</summary>
    [Fact]
    public async Task Save_SameHost_UserDeclines_DoesNotCommitPromptVersion()
    {
        var settings = new FakeSettingsService();
        settings.Current.Translation.ApiHost = "http://192.168.5.4:8000/v1";
        settings.Current.Lorebook.ApiHost = "http://192.168.5.4:8000/v1/";
        var (vm, store) = CreateViewModel(settings);
        var countBefore = store.GetPromptTemplates(PromptTemplateKinds.LiveSystem).Count;
        vm.ConfirmDialog = _ => false;
        vm.Translation.SystemPromptTemplate = "按否不該存成版本";

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(0, settings.SaveCount);
        Assert.Equal(countBefore, store.GetPromptTemplates(PromptTemplateKinds.LiveSystem).Count);
        Assert.Null(vm.Translation.SystemPromptTemplateId);
    }

    /// <summary>回溯紀錄設定：Audit 就是目前設定的同一個物件（設定頁勾選與天數直接寫進設定，切換頁面不會消失）。</summary>
    [Fact]
    public void Audit_IsCurrentSettingsInstance_EditsReflectInSettings()
    {
        var settings = new FakeSettingsService();
        var (vm, _) = CreateViewModel(settings);

        Assert.Same(settings.Current.Audit, vm.Audit);

        vm.Audit.FullRecordEnabled = true;
        vm.Audit.RetentionDays = 7;
        vm.ReloadSetting();

        Assert.True(settings.Current.Audit.FullRecordEnabled);
        Assert.Equal(7, settings.Current.Audit.RetentionDays);
        Assert.Same(settings.Current.Audit, vm.Audit);
    }

    /// <summary>路由器（被擋時更換 IPv6）設定：Router 就是目前設定的同一個物件（Routers 清單直接綁定）。</summary>
    [Fact]
    public void ReloadSetting_RouterIsCurrentSettingsInstance()
    {
        var settings = new FakeSettingsService();
        var (vm, _) = CreateViewModel(settings);

        Assert.Same(settings.Current.Router, vm.Router);

        settings.Current.Router = new RouterSettings
        {
            Routers = [new RouterEndpoint { Host = "192.168.81.254:8844", UserName = "renew", Password = "pw" }]
        };
        vm.ReloadSetting();

        Assert.Same(settings.Current.Router, vm.Router);
        Assert.Equal("192.168.81.254:8844", vm.Router.Routers.Single().Host);
    }

    /// <summary>「新增」：在路由器清單加一列空的並選取它。</summary>
    [Fact]
    public void AddRouterCommand_AddsEmptyEndpointAndSelectsIt()
    {
        var settings = new FakeSettingsService();
        settings.Current.Router.Routers.Add(new RouterEndpoint { Host = "192.168.5.254", UserName = "a", Password = "b" });
        var (vm, _) = CreateViewModel(settings);

        vm.AddRouterCommand.Execute(null);

        var added = Assert.Single(vm.Router.Routers, r => string.IsNullOrEmpty(r.Host));
        Assert.Equal(2, vm.Router.Routers.Count);
        Assert.Same(added, vm.SelectedRouter);
    }

    /// <summary>「刪除選取」：移除路由器清單裡選取的那列。</summary>
    [Fact]
    public void RemoveRouterCommand_RemovesSelectedEndpoint()
    {
        var settings = new FakeSettingsService();
        var first = new RouterEndpoint { Host = "192.168.5.254", UserName = "a", Password = "b" };
        var second = new RouterEndpoint { Host = "192.168.81.254", UserName = "c", Password = "d" };
        settings.Current.Router.Routers.Add(first);
        settings.Current.Router.Routers.Add(second);
        var (vm, _) = CreateViewModel(settings);
        vm.SelectedRouter = second;

        vm.RemoveRouterCommand.Execute(null);

        Assert.Equal([first], vm.Router.Routers);
    }

    /// <summary>沒有選取任何列時「刪除選取」不能執行；選了之後才可執行。</summary>
    [Fact]
    public void RemoveRouterCommand_CannotExecuteWithoutSelection()
    {
        var settings = new FakeSettingsService();
        var endpoint = new RouterEndpoint { Host = "192.168.5.254", UserName = "a", Password = "b" };
        settings.Current.Router.Routers.Add(endpoint);
        var (vm, _) = CreateViewModel(settings);

        Assert.Null(vm.SelectedRouter);
        Assert.False(vm.RemoveRouterCommand.CanExecute(null));

        vm.SelectedRouter = endpoint;
        Assert.True(vm.RemoveRouterCommand.CanExecute(null));
    }

    /// <summary>「設定說明」按鈕：用 RouterHelpText 呼叫 InfoDialog（測試替換 InfoDialog 攔截訊息）。</summary>
    [Fact]
    public void ShowRouterHelpCommand_CallsInfoDialogWithHelpText()
    {
        var settings = new FakeSettingsService();
        var (vm, _) = CreateViewModel(settings);
        var messages = new List<string>();
        vm.InfoDialog = messages.Add;

        vm.ShowRouterHelpCommand.Execute(null);

        var message = Assert.Single(messages);
        Assert.Equal(SettingsViewModel.RouterHelpText, message);
    }

    /// <summary>沒有 RouterIpv6Refresher（測試環境）時按「測試連線」：顯示未啟用，不擲例外。</summary>
    [Fact]
    public async Task TestRouterCommand_WithoutRefresher_ShowsDisabledMessage()
    {
        var settings = new FakeSettingsService();
        var (vm, _) = CreateViewModel(settings);

        await vm.TestRouterCommand.ExecuteAsync(null);

        Assert.Equal("路由器功能未啟用", vm.RouterTestMessage);
    }
}
