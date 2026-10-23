using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>直播設定檔管理：儲存、套用、刪除 <see cref="StreamProfile"/>（存在 settings.json，不動資料庫）。</summary>
public static class StreamProfiles
{
    /// <summary>所有設定檔名稱，以 <see cref="StringComparer.CurrentCultureIgnoreCase"/> 排序。</summary>
    public static IReadOnlyList<string> Names(AppSettings settings)
    {
        return settings.StreamProfiles
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 把 settings 目前的情境、角色表、譯名修正與原文修正存成指定名稱的設定檔；
    /// 同名（Ordinal）設定檔會被覆寫，並將 <see cref="AppSettings.ActiveProfileName"/> 設為修剪後的名稱。
    /// </summary>
    /// <exception cref="ArgumentException">name 修剪後為空。</exception>
    public static void SaveCurrent(AppSettings settings, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("設定檔名稱不可為空。", nameof(name));
        }

        var profile = settings.StreamProfiles
            .FirstOrDefault(p => string.Equals(p.Name, trimmed, StringComparison.Ordinal));

        if (profile is null)
        {
            profile = new StreamProfile { Name = trimmed };
            settings.StreamProfiles.Add(profile);
        }

        profile.StreamSituation = settings.StreamSituation;
        profile.CharacterTable = settings.CharacterTable;
        profile.ReplacementRules = settings.ReplacementRules;
        profile.SourceReplacementRules = settings.SourceReplacementRules;
        profile.UpdatedAt = DateTime.Now;
        settings.ActiveProfileName = trimmed;
    }

    /// <summary>
    /// 套用指定名稱的設定檔：把四項內容複製回 settings、更新
    /// <see cref="AppSettings.StreamInfoUpdatedAt"/> 與 <see cref="AppSettings.ActiveProfileName"/>；
    /// 找不到（Ordinal，name 先修剪）時回傳 false 且 settings 不變。
    /// </summary>
    public static bool Apply(AppSettings settings, string name)
    {
        var profile = settings.StreamProfiles
            .FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.Ordinal));

        if (profile is null) return false;

        settings.StreamSituation = profile.StreamSituation;
        settings.CharacterTable = profile.CharacterTable;
        settings.ReplacementRules = profile.ReplacementRules;
        settings.SourceReplacementRules = profile.SourceReplacementRules;
        settings.StreamInfoUpdatedAt = DateTime.Now;
        settings.ActiveProfileName = profile.Name;
        return true;
    }

    /// <summary>
    /// 依頻道名稱（streamlink 查到的 author）找對應的設定檔：
    /// 名稱完全相同（忽略大小寫）優先；否則以包含關係（互為包含、OrdinalIgnoreCase）比對，恰好一個命中才回傳。
    /// </summary>
    public static string? FindForChannel(AppSettings settings, string? author)
    {
        var target = author?.Trim();
        if (string.IsNullOrEmpty(target)) return null;

        var exact = settings.StreamProfiles
            .FirstOrDefault(p => string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Name;

        var candidates = settings.StreamProfiles
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Where(p => target.Contains(p.Name, StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains(target, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return candidates.Count == 1 ? candidates[0].Name : null;
    }

    /// <summary>移除指定名稱的設定檔；若刪的是目前套用中的設定檔，會把 <see cref="AppSettings.ActiveProfileName"/> 清成空字串。回傳是否有移除。</summary>
    public static bool Delete(AppSettings settings, string name)
    {
        var removed = settings.StreamProfiles
            .RemoveAll(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        if (removed > 0 && string.Equals(settings.ActiveProfileName, name, StringComparison.Ordinal))
        {
            settings.ActiveProfileName = string.Empty;
        }

        return removed > 0;
    }
}
