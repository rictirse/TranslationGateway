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
    public TranslationSettings Translation { get; set; } = new();
}