using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Translation.Bridge.Core.Services;

public class TranslationSettings : INotifyPropertyChanged
{
    public string ApiHost { get; set; } = string.Empty;

    private string _model = string.Empty;

    /// <summary>
    /// 模型名稱。設定頁的「思考強度／思考上限」依名稱是否為 GLM 切換顯示，
    /// 因此變更時要發 PropertyChanged 讓 Visibility 繫結立即更新。
    /// </summary>
    public string Model
    {
        get => _model;
        set
        {
            _model = value;
            OnPropertyChanged();
        }
    }

    public string? ApiKey { get; set; } = null;
    /// <summary>
    /// 是否使用本地模型
    /// </summary>
    public bool UseLocalModel { get; set; } = false;

    // --- 翻譯微調 (影響翻譯品質與風格) ---

    /// <summary>
    /// 控制輸出的隨機性。
    /// 0.0 代表最精準、最穩定（適合翻譯）。
    /// 若想讓語氣更生動活潑，可微調至 0.2-0.3，但過高可能導致格式崩潰。
    /// </summary>
    public double Temperature { get; set; } = 0.0;

    /// <summary>核取樣（nucleus sampling）的累積機率上限（top_p）；null 代表不送，使用模型預設值。</summary>
    public double? TopP { get; set; }

    /// <summary>存放在地化專家指令的模板；預設為 PromptBuilder.DefaultSystemTemplate，空白時組 prompt 亦會退回該預設模板。
    /// 不再存進 settings.json（讀檔時也忽略舊值）：執行期由 PromptTemplateRegistry 依選用的版本填入，
    /// 編輯後存檔會存成 prompt_templates 的新版本。設定頁改選版本時會替換內容，因此變更時要發 PropertyChanged 讓編輯框更新。</summary>
    [JsonIgnore]
    public string SystemPromptTemplate
    {
        get => _systemPromptTemplate;
        set
        {
            _systemPromptTemplate = value;
            OnPropertyChanged();
        }
    }

    private string _systemPromptTemplate = PromptBuilder.DefaultSystemTemplate;

    /// <summary>直播 system prompt 選用的版本（prompt_templates.id）；null 表示跟著最新的程式預設。</summary>
    public long? SystemPromptTemplateId { get; set; }

    /// <summary>單次翻譯的逾時（毫秒）。0 或負數表示不啟用逾時。</summary>
    public int TimeoutMs { get; set; } = 1500;

    /// <summary>多輪對話模式（每句字幕一輪），可大幅降低延遲；false 時使用單輪模式。</summary>
    public bool UseConversationMode { get; set; } = true;

    /// <summary>對話歷史達到此輪數時重置。</summary>
    public int ConversationMaxTurns { get; set; } = 12;

    /// <summary>重置時保留的最近輪數。</summary>
    public int ConversationKeepTurns { get; set; } = 4;

    /// <summary>超過此秒數沒有新的對話就清空歷史（換影片、暫停）。</summary>
    public int ConversationIdleResetSeconds { get; set; } = 90;

    /// <summary>幾秒內的相似字幕視為同一句的修正版；0 代表關閉。</summary>
    public int RevisionWindowSeconds { get; set; } = 8;

    /// <summary>字幕聽錯字探勘：定期請知識庫模型從最近字幕找出 Whisper 聽錯的名詞寫法（存成待審核修正）。</summary>
    public bool MishearingMiningEnabled { get; set; } = true;

    /// <summary>聽錯字探勘的間隔（秒），也是取最近字幕的時間範圍。</summary>
    public int MishearingMiningIntervalSeconds { get; set; } = 300;

    /// <summary>距離上一次探勘至少要有幾句新字幕才執行（字幕太少不浪費 tokens）。</summary>
    public int MishearingMiningMinLines { get; set; } = 10;

    /// <summary>思考強度（none／low／medium／high／max）：只有知識庫模型且為 GLM 時使用，探勘重視速度故預設 low。</summary>
    public string ReasoningEffort { get; set; } = "low";

    /// <summary>思考上限（tokens）：只有知識庫模型且不是 GLM 時使用（例如本地 Qwen 的 reasoning_budget_tokens）；0 或負數代表不送。</summary>
    public int ReasoningBudgetTokens { get; set; } = 4000;

    /// <summary>Model 變更時通知繫結更新（設定頁依模型名稱切換 GLM／非 GLM 的思考參數欄位）。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
