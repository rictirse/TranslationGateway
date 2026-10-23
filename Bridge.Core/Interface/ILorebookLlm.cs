namespace Translation.Bridge.Core.Interface;

/// <summary>知識庫模型的 JSON 回覆結果：模型輸出的文字與用量資訊。</summary>
public sealed record LlmJsonResult(string Content, int PromptTokens, int CompletionTokens, long ElapsedMs, string Model);

/// <summary>知識庫模型（OpenAI 相容 API）的呼叫介面：供資料探勘建立詞彙卡使用。</summary>
public interface ILorebookLlm
{
    /// <summary>送出 system ＋ user 兩則訊息，要求 JSON 物件回覆；回傳模型輸出的文字（應為 JSON，但呼叫端自行解析與驗證）。
    /// 設定不完整、HTTP 非 2xx、逾時或回應格式不對時擲 LorebookLlmException（訊息含 HTTP 狀態與回應開頭 300 字）。</summary>
    Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);

    /// <summary>送一個極短的請求確認連線與模型可用；不擲例外。</summary>
    Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>知識庫模型呼叫失敗（設定不完整、HTTP 錯誤、逾時或回應格式不正確）。</summary>
/// <param name="isTransient">是否為暫時性錯誤（逾時、連線失敗、HTTP 429／5xx）：連續發生時知識庫工作暫時改用翻譯模型。</param>
public sealed class LorebookLlmException(string message, Exception? inner = null, bool isTransient = false)
    : Exception(message, inner)
{
    /// <summary>是否為暫時性錯誤（逾時、連線失敗、HTTP 429／5xx）。</summary>
    public bool IsTransient { get; } = isTransient;
}
