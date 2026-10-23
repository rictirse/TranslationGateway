using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>開台資料探勘用哪個模型。</summary>
public enum StreamOpenMiningModel
{
    None,
    Lorebook,
    Translation
}

/// <summary>開台資料探勘的模型分流：知識庫模型有設定就用它；否則翻譯模型可用時借用翻譯模型（只設定翻譯模型的使用情境）。</summary>
public static class StreamOpenMiningMode
{
    /// <summary>知識庫模型的 ApiHost 與 Model 都有填寫。</summary>
    public static bool LorebookConfigured(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.Lorebook.ApiHost) &&
        !string.IsNullOrWhiteSpace(settings.Lorebook.Model);

    /// <summary>翻譯模型可用：Model 有填寫，而且 UseLocalModel 或 ApiHost 有填寫。</summary>
    public static bool TranslationConfigured(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.Translation.Model) &&
        (settings.Translation.UseLocalModel || !string.IsNullOrWhiteSpace(settings.Translation.ApiHost));

    /// <summary>知識庫模型有設定 → Lorebook；否則翻譯模型可用 → Translation；都沒有 → None。</summary>
    public static StreamOpenMiningModel Decide(AppSettings settings) =>
        LorebookConfigured(settings) ? StreamOpenMiningModel.Lorebook :
        TranslationConfigured(settings) ? StreamOpenMiningModel.Translation : StreamOpenMiningModel.None;
}
