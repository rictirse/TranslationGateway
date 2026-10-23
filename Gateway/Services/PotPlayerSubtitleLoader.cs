using System.Diagnostics;
using System.IO;
using Translation.Bridge.Core.Interface;

namespace Translation.Gateway.Services;

/// <summary>把字幕檔交給正在播放的 PotPlayer：找到正在執行的 PotPlayer 之後，以「/current 字幕檔」
/// 再啟動一次同一個 exe（PotPlayer 是單一實例：正在播放的那個會直接換上這份字幕，不重開、進度不重置）。
/// 沒有正在執行的 PotPlayer 時回傳 false（不自己啟動 PotPlayer）；所有例外都 catch，不擲例外。</summary>
public sealed class PotPlayerSubtitleLoader : IPotPlayerSubtitleLoader
{
    private readonly ISettingsService _settings;

    public PotPlayerSubtitleLoader(ISettingsService settings)
    {
        _settings = settings;
    }

    public bool Load(string subtitlePath)
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        // 比照 PotPlayerClock.GetMainWindowHandle：ProcessName 以 PotPlayer 開頭（不分大小寫）、有主視窗
                        if (!process.ProcessName.StartsWith("PotPlayer", StringComparison.OrdinalIgnoreCase) ||
                            process.MainWindowHandle == IntPtr.Zero)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        // 行程剛離開時 ProcessName／MainWindowHandle 可能擲例外：換下一個
                        continue;
                    }

                    string? exePath;
                    try
                    {
                        exePath = process.MainModule?.FileName;
                    }
                    catch
                    {
                        exePath = null;
                    }

                    // 取不到 exe 路徑時改用設定裡的播放器路徑，但檔案要存在才用
                    if (string.IsNullOrEmpty(exePath))
                    {
                        var playerPath = _settings.Current.Streamlink.PlayerPath;
                        exePath = File.Exists(playerPath) ? playerPath : null;
                    }

                    if (exePath is null)
                    {
                        continue;
                    }

                    try
                    {
                        Process.Start(new ProcessStartInfo(exePath)
                        {
                            ArgumentList = { "/current", subtitlePath },
                            UseShellExecute = false,
                        });
                        return true;
                    }
                    catch
                    {
                        // 啟動失敗：換下一個 PotPlayer 行程再試
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
