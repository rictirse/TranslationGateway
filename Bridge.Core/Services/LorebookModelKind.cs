namespace Translation.Bridge.Core.Services;

/// <summary>知識庫模型的種類判斷與思考參數正規化。</summary>
public static class LorebookModelKind
{
    /// <summary>模型名稱含 "glm"（OrdinalIgnoreCase）就當成 Z.AI GLM。null 或空白回傳 false。</summary>
    public static bool IsGlm(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Contains("glm", StringComparison.OrdinalIgnoreCase);

    /// <summary>正規化思考強度：去頭尾空白、轉小寫；不在 none/low/medium/high/max 內時回傳 "low"。</summary>
    public static string NormalizeReasoningEffort(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "none" or "low" or "medium" or "high" or "max" ? normalized : "low";
    }
}
