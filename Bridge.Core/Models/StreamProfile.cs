namespace Translation.Bridge.Core.Models;

/// <summary>直播設定檔：依主播／直播儲存的情境、角色表與修正規則。</summary>
public class StreamProfile
{
    /// <summary>設定檔名稱。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>直播情境（例如誰在直播、在做什麼、玩什麼遊戲）。</summary>
    public string StreamSituation { get; set; } = string.Empty;
    /// <summary>角色表，每行「原名=譯名」，可加備註。</summary>
    public string CharacterTable { get; set; } = string.Empty;
    /// <summary>譯名修正規則（翻譯後套用），每行「錯誤譯名=正確譯名」。</summary>
    public string ReplacementRules { get; set; } = string.Empty;
    /// <summary>原文修正規則（翻譯前套用），每行「聽錯=正確」。</summary>
    public string SourceReplacementRules { get; set; } = string.Empty;
    /// <summary>設定檔最後儲存時間。</summary>
    public DateTime UpdatedAt { get; set; }
}
