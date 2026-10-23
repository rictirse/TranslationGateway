namespace Translation.Bridge.Core.Services;

/// <summary>
/// 目前生效詞彙卡的狀態（DI singleton）：Dashboard 寫入 prompt 段落、型錄與生效清單，
/// TranslationManager 讀取後附加到 system prompt，KeywordGlossary 讀取型錄與生效清單比對字幕。
/// </summary>
public sealed class GlossaryState
{
    private readonly object _lock = new();
    private string _promptSection = string.Empty;
    private GlossaryCatalog _catalog = new([], []);
    private IReadOnlyList<long> _activeIds = [];

    /// <summary>目前要附加到 system prompt 的詞彙表段落；預設空字串。</summary>
    public string PromptSection
    {
        get { lock (_lock) return _promptSection; }
    }

    /// <summary>目前要附加到 system prompt 的詞彙表段落；null 視為空字串。</summary>
    public void SetPromptSection(string? section)
    {
        lock (_lock) _promptSection = section ?? string.Empty;
    }

    /// <summary>目前的詞彙卡型錄（Dashboard 讀取資料庫後寫入）；預設空型錄。</summary>
    public GlossaryCatalog Catalog
    {
        get { lock (_lock) return _catalog; }
    }

    /// <summary>更新詞彙卡型錄；null 視為空型錄。</summary>
    public void SetCatalog(GlossaryCatalog? catalog)
    {
        lock (_lock) _catalog = catalog ?? new GlossaryCatalog([], []);
    }

    /// <summary>目前生效中的詞彙卡 Id 快照（不含動態生效）。</summary>
    public IReadOnlyList<long> ActiveIds
    {
        get { lock (_lock) return _activeIds; }
    }

    /// <summary>更新生效清單（存快照副本）。</summary>
    public void SetActiveIds(IReadOnlyList<long>? ids)
    {
        lock (_lock) _activeIds = ids is null ? [] : ids.ToList();
    }
}
