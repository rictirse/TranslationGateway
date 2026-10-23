namespace Translation.Bridge.Core.Models;

public class AppSettings
{
    public string LastSelectedControlName { get; set; } = string.Empty;
    /// <summary>
    /// 程式的起始路徑
    /// </summary>
    public string BaseStoragePath { get; set; } = string.Empty;
    /// <summary>
    /// 打進來的訊號直接反射，不過處理
    /// </summary>
    public bool ThroughPass { get; set; } = false;
    /// <summary>前處理有進度時自動把整理過的字幕交給 PotPlayer（會蓋掉 YouTube 原本的字幕軌）。</summary>
    public bool AutoUpdatePotPlayerSubtitle { get; set; } = true;
    /// <summary>譯名修正規則，每行「錯誤譯名=正確譯名」。</summary>
    public string ReplacementRules { get; set; } = string.Empty;
    /// <summary>直播情境（例如誰在直播、在做什麼、玩什麼遊戲）。</summary>
    public string StreamSituation { get; set; } = string.Empty;
    /// <summary>角色表，每行「原名=譯名」，可加備註。</summary>
    public string CharacterTable { get; set; } = string.Empty;
    /// <summary>直播情境或角色表最後更新時間。</summary>
    public DateTime? StreamInfoUpdatedAt { get; set; }
    /// <summary>原文修正規則（翻譯前套用），每行「聽錯=正確」。</summary>
    public string SourceReplacementRules { get; set; } = string.Empty;
    /// <summary>直播設定檔清單（依主播／直播儲存情境、角色表與修正規則）。</summary>
    public List<StreamProfile> StreamProfiles { get; set; } = [];
    /// <summary>目前套用的直播設定檔名稱；未套用時為空字串。</summary>
    public string ActiveProfileName { get; set; } = string.Empty;
    /// <summary>目前生效的詞彙卡 Id（依生效順序）。</summary>
    public List<long> ActiveGlossaryCardIds { get; set; } = [];
    /// <summary>上一次成功開台的網址；下一次開台的網址不同（Ordinal）時視為全新的直播，清空詞彙卡與情境。</summary>
    public string LastStreamUrl { get; set; } = string.Empty;
    /// <summary>字幕監看頁「直播資訊」區塊最後的展開狀態（重開程式照樣維持）。</summary>
    public bool StreamInfoExpanded { get; set; }
    /// <summary>字幕監看頁「修正規則」區塊最後的展開狀態（重開程式照樣維持）。</summary>
    public bool RulesExpanded { get; set; }
    public TranslationSettings Translation { get; set; } = new();
    /// <summary>知識庫用的模型連線（從字幕紀錄挖名詞；GLM 直接呼叫，不經 LiteLLM）。目前只用 ApiHost、Model、ApiKey。</summary>
    public TranslationSettings Lorebook { get; set; } = new();
    /// <summary>一鍵開台：streamlink 與播放器路徑、畫質。</summary>
    public StreamlinkSettings Streamlink { get; set; } = new();
    /// <summary>開台前更換 IPv6（MikroTik RouterOS REST API，選用）：填了路由器與帳密時，開 YouTube 前先重新取得 IPv6 前綴。</summary>
    public RouterSettings Router { get; set; } = new();
    /// <summary>錄影的處理設定（預先翻譯、開片等待、插隊門檻）。</summary>
    public VodSettings Vod { get; set; } = new();
    /// <summary>資料庫後端與連線資訊（切換後需重新啟動才生效）。</summary>
    public DatabaseSettings Database { get; set; } = new();
    /// <summary>回溯紀錄設定（模型呼叫紀錄與快照的完整保存、保存天數）。</summary>
    public AuditSettings Audit { get; set; } = new();
}