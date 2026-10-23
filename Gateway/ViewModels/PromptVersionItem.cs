namespace Translation.Gateway.ViewModels;

/// <summary>設定頁「直播 system prompt 版本」下拉選單的一項（Id 為 null 表示跟著最新預設）。</summary>
public sealed class PromptVersionItem
{
    public long? Id { get; init; }

    /// <summary>顯示文字：「跟著最新預設」或「#Id · 預設／自訂 · 建立時間」。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>這個版本的全文（選取時填回 Translation.SystemPromptTemplate）。</summary>
    public string Content { get; init; } = string.Empty;
}
