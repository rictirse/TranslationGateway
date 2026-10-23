using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Gateway.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ITranslationStore _store;
    private readonly ILorebookLlm _lorebookLlm;
    private readonly PromptTemplateRegistry _promptRegistry;
    private readonly RouterIpv6Refresher? _routerRefresher;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>重建版本清單期間不把選取項寫回設定（覆寫邏輯只在使用者改選時作用）。</summary>
    private bool _suppressPromptVersionApply;

    // 直接暴露實體，UI 透過 Translation.ApiHost 進行綁定
    [ObservableProperty]
    private TranslationSettings _translation = null!;

    [ObservableProperty]
    private TranslationSettings _lorebook = null!;

    [ObservableProperty]
    private string _baseStoragePath = string.Empty;

    /// <summary>一鍵開台（streamlink）設定，UI 透過 Streamlink.StreamlinkPath 等綁定</summary>
    [ObservableProperty]
    private StreamlinkSettings _streamlink = null!;

    /// <summary>錄影設定，UI 透過 Vod.PrefetchBatchSize 等綁定</summary>
    [ObservableProperty]
    private VodSettings _vod = null!;

    /// <summary>資料庫後端與連線資訊，UI 透過 Database.Host 等綁定</summary>
    [ObservableProperty]
    private DatabaseSettings _database = null!;

    /// <summary>回溯紀錄設定（保存完整紀錄、保存天數），UI 透過 Audit.FullRecordEnabled 等綁定</summary>
    [ObservableProperty]
    private AuditSettings _audit = null!;

    /// <summary>被 YouTube 擋下時更換 IPv6（MikroTik 路由器，選用）設定，UI 透過 Router.Routers 等綁定</summary>
    [ObservableProperty]
    private RouterSettings _router = null!;

    /// <summary>路由器清單（DataGrid）目前選取的列；「刪除選取」的對象。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRouterCommand))]
    private RouterEndpoint? _selectedRouter;

    /// <summary>是否使用 SQLite（與 IsPostgres 互斥）</summary>
    [ObservableProperty]
    private bool _isSqlite = true;

    /// <summary>是否使用 PostgreSQL（與 IsSqlite 互斥）</summary>
    [ObservableProperty]
    private bool _isPostgres;

    /// <summary>連線測試的結果訊息。</summary>
    [ObservableProperty]
    private string _connectionTestMessage = string.Empty;

    /// <summary>連線測試結果：null 表示測試中或尚未測試，其餘依 Success 設定（供訊息變色）。</summary>
    [ObservableProperty]
    private bool? _connectionTestSucceeded;

    /// <summary>知識庫模型連線測試的結果訊息。</summary>
    [ObservableProperty]
    private string _lorebookTestMessage = string.Empty;

    /// <summary>知識庫模型連線測試結果：null 表示測試中或尚未測試，其餘依 Success 設定（供訊息變色）。</summary>
    [ObservableProperty]
    private bool? _lorebookTestSucceeded;

    /// <summary>路由器（開台前更換 IPv6）連線測試的結果訊息，每台一行。</summary>
    [ObservableProperty]
    private string _routerTestMessage = string.Empty;

    /// <summary>目前程式實際使用的資料庫描述（切換設定需重新啟動才生效）。</summary>
    public string ActiveDatabase => $"目前使用 {_store.Description}";

    /// <summary>直播 system prompt 的版本清單：第一項「跟著最新預設」，其後為 DB 的歷史版本（Id 由大到小）。</summary>
    [ObservableProperty]
    private ObservableCollection<PromptVersionItem> _livePromptVersions = [];

    /// <summary>目前選用的直播 system prompt 版本；改選會替換 Translation.SystemPromptTemplate。</summary>
    [ObservableProperty]
    private PromptVersionItem? _selectedLivePromptVersion;

    /// <summary>是／否確認對話框的進入點；測試可替換。回傳 true 表示「是」。</summary>
    public Func<string, bool> ConfirmDialog { get; set; } = _ => true;

    /// <summary>說明對話框的進入點（設定說明按鈕）；測試可替換。</summary>
    public Action<string> InfoDialog { get; set; } = _ => { };

    public SettingsViewModel(
        ISettingsService settingsService,
        ITranslationStore store,
        ILorebookLlm lorebookLlm,
        PromptTemplateRegistry promptRegistry,
        ILogger<SettingsViewModel> logger,
        RouterIpv6Refresher? routerRefresher = null)
    {
        _settingsService = settingsService;
        _store = store;
        _lorebookLlm = lorebookLlm;
        _promptRegistry = promptRegistry;
        _routerRefresher = routerRefresher;
        _logger = logger;
        ReloadSetting();
    }

    public void ReloadSetting()
    {
        var cur = _settingsService.Current;

        Translation = cur.Translation;
        Lorebook = cur.Lorebook;
        Streamlink = cur.Streamlink;
        Vod = cur.Vod;
        BaseStoragePath = cur.BaseStoragePath;
        Database = cur.Database;
        Audit = cur.Audit;
        Router = cur.Router;
        IsPostgres = cur.Database.UsePostgres;
        IsSqlite = !cur.Database.UsePostgres;
        RebuildLivePromptVersions();
    }

    /// <summary>重建直播 system prompt 的版本清單，選中目前 SystemPromptTemplateId 對應的項目（找不到時選第一項）。</summary>
    private void RebuildLivePromptVersions()
    {
        _suppressPromptVersionApply = true;
        try
        {
            var items = new List<PromptVersionItem>
            {
                new() { Id = null, Label = "跟著最新預設", Content = PromptBuilder.DefaultSystemTemplate },
            };
            items.AddRange(_promptRegistry.GetLiveVersions().Select(v => new PromptVersionItem
            {
                Id = v.Id,
                Label = $"#{v.Id} · {(v.Origin == PromptTemplateOrigins.User ? "自訂" : "預設")} · {v.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                Content = v.Content,
            }));

            LivePromptVersions = new ObservableCollection<PromptVersionItem>(items);
            SelectedLivePromptVersion = items.FirstOrDefault(i => i.Id == Translation?.SystemPromptTemplateId) ?? items[0];
        }
        finally
        {
            _suppressPromptVersionApply = false;
        }
    }

    /// <summary>使用者改選版本：把選用 Id 與該版全文寫回設定，編輯框隨之更新（重建清單時不觸發）。</summary>
    partial void OnSelectedLivePromptVersionChanged(PromptVersionItem? value)
    {
        if (_suppressPromptVersionApply || value is null || Translation is null) return;

        Translation.SystemPromptTemplateId = value.Id;
        Translation.SystemPromptTemplate = value.Content;
    }

    partial void OnIsSqliteChanged(bool value)
    {
        if (Database is null) return;
        Database.UsePostgres = !value;
    }

    partial void OnIsPostgresChanged(bool value)
    {
        if (Database is null) return;
        Database.UsePostgres = value;
    }

    [RelayCommand]
    private async Task TestDbConnectionAsync(CancellationToken cancellationToken)
    {
        ConnectionTestMessage = "測試中…";
        ConnectionTestSucceeded = null;
        try
        {
            var result = await DatabaseConnectionTester.TestAsync(Database, cancellationToken);
            ConnectionTestMessage = result.Message;
            ConnectionTestSucceeded = result.Success;
        }
        catch (Exception ex)
        {
            // 連線測試的任何錯誤都只顯示在訊息列，不得往外拋
            ConnectionTestMessage = $"連線測試失敗：{ex.Message}";
            ConnectionTestSucceeded = false;
        }
    }

    /// <summary>知識庫模型的連線測試：先存檔（client 每次呼叫都讀 settings.Current），再送極短的測試請求。</summary>
    [RelayCommand]
    private async Task TestLorebookConnectionAsync(CancellationToken cancellationToken)
    {
        LorebookTestMessage = "測試中…";
        LorebookTestSucceeded = null;
        try
        {
            await SaveSettings();
            var (success, message) = await _lorebookLlm.TestConnectionAsync(cancellationToken);
            LorebookTestMessage = message;
            LorebookTestSucceeded = success;
        }
        catch (Exception ex)
        {
            // 連線測試的任何錯誤都只顯示在訊息列，不得往外拋
            LorebookTestMessage = $"連線測試失敗：{ex.Message}";
            LorebookTestSucceeded = false;
        }
    }

    /// <summary>儲存設定。設定是直接綁定在設定物件上，按「否」只是不寫入檔案，畫面上的值不會還原。</summary>
    [RelayCommand]
    private async Task SaveSettings()
    {
        // 翻譯模型與知識庫模型設成同一台伺服器時，不能並行的伺服器會讓即時翻譯被資料探勘拖慢
        // （能不能並行取決於使用者的部署方式，Gateway 無法判斷）：由使用者對自己的設定負責
        if (ApiHostComparer.SameHost(Translation.ApiHost, Lorebook.ApiHost))
        {
            if (!ConfirmDialog(
                    "翻譯模型和知識庫模型的 Host 相同。\n" +
                    "如果這個伺服器不能同時處理多個請求，即時翻譯和資料探勘會互相拖慢，字幕可能逾時消失。\n" +
                    "確定要這樣設定嗎？"))
            {
                _logger.LogInformation("設定未儲存：使用者取消（翻譯模型與知識庫模型的 Host 相同）");
                return;
            }

            _logger.LogInformation("翻譯模型與知識庫模型的 Host 相同，使用者確認後儲存");
        }

        // 直播 system prompt：內容有改就存成新的自訂版本（User），結果寫回選用的版本 Id
        Translation.SystemPromptTemplateId = _promptRegistry.CommitLiveTemplate(
            Translation.SystemPromptTemplateId, Translation.SystemPromptTemplate);

        await _settingsService.SaveAsync();
        _logger.LogInformation("設定已儲存");

        // 存檔後版本清單可能有新版本：重建並選中新的版本
        RebuildLivePromptVersions();
    }

    /// <summary>路由器的連線測試：對每台 GET /rest/system/identity，結果每台一行顯示在訊息列；沒有 refresher（測試環境）時顯示未啟用。</summary>
    [RelayCommand]
    private async Task TestRouterAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_routerRefresher is null)
            {
                RouterTestMessage = "路由器功能未啟用";
                return;
            }

            RouterTestMessage = "測試中…";
            var results = await _routerRefresher.TestAsync(cancellationToken);
            RouterTestMessage = string.Join("\n", results.Select(r => r.Message));
        }
        catch (Exception ex)
        {
            // 連線測試的任何錯誤都只顯示在訊息列，不得往外拋
            RouterTestMessage = $"連線測試失敗：{ex.Message}";
        }
    }

    /// <summary>「新增」：在路由器清單加一列空的並選取它。</summary>
    [RelayCommand]
    private void AddRouter()
    {
        var endpoint = new RouterEndpoint();
        Router.Routers.Add(endpoint);
        SelectedRouter = endpoint;
    }

    /// <summary>「刪除選取」：移除路由器清單裡選取的那列；沒有選取時不能執行。</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveRouter))]
    private void RemoveRouter() => Router.Routers.Remove(SelectedRouter!);

    /// <summary>有選取路由器列才能刪除。</summary>
    private bool CanRemoveRouter() => SelectedRouter is not null;

    /// <summary>「設定說明」按鈕：顯示 MikroTik 路由器的設定步驟說明。</summary>
    [RelayCommand]
    private void ShowRouterHelp() => InfoDialog(RouterHelpText);

    /// <summary>被 YouTube 擋下時更換 IPv6 的設定說明文字（MikroTik RouterOS v7 的腳本、帳號與 HTTPS 設定步驟）。</summary>
    public const string RouterHelpText = """
        這是選用功能：YouTube 看太多次時，同一段 IPv6 會被擋，影片開不了。填好以下設定後，開台時如果偵測到被 YouTube 擋下，程式會請 MikroTik 路由器重新取得 IPv6 前綴，再自動重試一次。

        在路由器（RouterOS v7）的終端機依序執行：

        1. 建立腳本（pppoe-out1 換成你的 WAN 介面）
        /system script add name=ipv6-release policy=read,write,test source="/ipv6 dhcp-client release [find interface=pppoe-out1]"

        2. 建立專用群組與帳號（api 和 rest-api 都要有，少了 api 會出現「權限不足」）
        /user group add name=api-renew policy=read,write,test,api,rest-api
        /user add name=renew group=api-renew password=你的密碼

        3. 建立自簽憑證並開啟 HTTPS（只允許區網連入）
        /certificate add name=router-rest common-name=router
        /certificate sign router-rest
        /ip service set www-ssl certificate=router-rest port=8844 address=192.168.0.0/16 disabled=no

        4. 回到這裡按「新增」，每台路由器填一列：位址（例如 192.168.81.254:8844）、帳號、密碼，然後按「測試連線」確認。

        注意：
        • 執行時 IPv6 會中斷幾秒，屬於正常。
        • 有些 ISP 每次都發同一段前綴，這時更換不會有效果；開台後狀態列會顯示前綴有沒有變。
        • 有多台路由器時，只會呼叫和這台電腦同網段（/24）的那一台。
        • 密碼以明碼存在設定檔中，請使用只有上述權限的專用帳號。
        """;
}
