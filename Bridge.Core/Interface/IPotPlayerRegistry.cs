namespace Translation.Bridge.Core.Interface;

/// <summary>PotPlayer 的登錄檔設定（HKCU\Software\DAUM\PotPlayerMini64\Settings）；PotPlayer 只在啟動時讀取。</summary>
public interface IPotPlayerRegistry
{
    /// <summary>開啟自動翻譯並選用指定的翻譯腳本（CaptionTranslateCond = 1、CaptionTranslateEngine2 = scriptFileName）。</summary>
    void ApplyTranslation(string scriptFileName);

    /// <summary>設定 Whisper 開關（WhisperDllUse）；languageIndex 不是 null 時也設定語言（WhisperDllLang）。</summary>
    void ApplyWhisper(bool enabled, int? languageIndex);
}
