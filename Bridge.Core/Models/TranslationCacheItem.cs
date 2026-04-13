namespace Translation.Bridge.Core.Models;

public class TranslationCacheItem
{
    public string SourceText { get; set; } = string.Empty;
    public string TargetText { get; set; } = string.Empty;
    public bool IsManual { get; set; }
    public DateTime LastUsed { get; set; }
}