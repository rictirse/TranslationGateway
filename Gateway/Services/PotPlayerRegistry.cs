using Microsoft.Win32;
using Translation.Bridge.Core.Interface;

namespace Translation.Gateway.Services;

/// <summary>PotPlayer 的登錄檔設定（HKCU\Software\DAUM\PotPlayerMini64\Settings）。
/// PotPlayer 安裝目錄的複本也讀寫同一個機碼，因此設定對原版與複本同時生效。</summary>
public sealed class PotPlayerRegistry : IPotPlayerRegistry
{
    private const string SettingsKeyPath = @"Software\DAUM\PotPlayerMini64\Settings";

    /// <summary>找不到翻譯腳本時的錯誤訊息前綴（理論上不會發生：CreateSubKey 會自動建立）。</summary>
    private const string KeyMissingMessage = "無法建立登錄檔機碼 " + SettingsKeyPath;

    public void ApplyTranslation(string scriptFileName)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath)
            ?? throw new InvalidOperationException(KeyMissingMessage);
        // 開啟自動翻譯，並選用 Gateway 寫入的翻譯腳本
        key.SetValue("CaptionTranslateCond", 1, RegistryValueKind.DWord);
        key.SetValue("CaptionTranslateEngine2", scriptFileName, RegistryValueKind.String);
    }

    public void ApplyWhisper(bool enabled, int? languageIndex)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath)
            ?? throw new InvalidOperationException(KeyMissingMessage);
        key.SetValue("WhisperDllUse", enabled ? 1 : 0, RegistryValueKind.DWord);
        if (languageIndex is { } index)
        {
            key.SetValue("WhisperDllLang", index, RegistryValueKind.DWord);
        }
    }
}
