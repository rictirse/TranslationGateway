namespace TranslationGateway.ViewModels;

public class TranslationSettings
{
    // --- 效能參數 (影響翻譯速度與 GPU 負載) ---

    /// <summary>
    /// 同時發送給 Ollama 的請求數量。
    /// 5090 算力極強，建議設為 4-8。這能讓 GPU 並行處理多個 Batch，大幅縮短整部影片翻譯時間。
    /// </summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>
    /// 單次 Request 包含的字幕行數 (BatchCount)。
    /// 影響上下文(Context)佔用率。建議 100-150，配合 5090 的大VRAM可提高翻譯一致性。
    /// </summary>
    public int BatchSizeHardCap { get; set; } = 80;

    /// <summary>
    /// 當模型回傳格式錯誤（例如漏行、標籤未過濾）時，自動重試的次數。
    /// </summary>
    public int MaxRetries { get; set; } = 3;


    // --- Token 與 上下文 (影響顯存分配與穩定性) ---

    /// <summary>
    /// 模型總上下文窗口 (num_ctx)。
    /// 你目前的 5090 滿血版建議固定為 16384。
    /// 此值決定了模型能「看見」多少前文，也是 VRAM 佔用的主要變數。
    /// </summary>
    public int Context { get; set; } = 16384;

    /// <summary>
    /// 模型單次輸出的 Token 上限 (num_predict)。
    /// 若 BatchSize 調大，此值也必須調高（建議 4096），避免翻譯到一半被強制切斷。
    /// </summary>
    public int MaxOutput { get; set; } = 2048;


    // --- 翻譯微調 (影響翻譯品質與風格) ---

    /// <summary>
    /// 控制輸出的隨機性。
    /// 0.0 代表最精準、最穩定（適合翻譯）。
    /// 若想讓語氣更生動活潑，可微調至 0.2-0.3，但過高可能導致格式崩潰。
    /// </summary>
    public double Temperature { get; set; } = 0.0;

    /// <summary>
    /// 存放在地化專家指令的模板。
    /// 包含防幻覺標籤過濾、台灣口語風格定義等核心指令。
    /// </summary>
    public string SystemPromptTemplate { get; set; } = "";
}