using System.Diagnostics;
using System.Runtime.InteropServices;
using Translation.Bridge.Core.Interface;

namespace Translation.Gateway.Services;

/// <summary>
/// 向 PotPlayer 主視窗詢問目前播放位置（毫秒）：送 WM_USER(0x0400)、wParam 0x5004
/// （使用者實測快轉後也正確）。用 SendMessageTimeout 限時等待，PotPlayer 卡住時不會拖住翻譯執行緒；
/// 找不到視窗、送訊息失敗或回傳值不合理時一律回傳 null，絕不擲例外。
/// </summary>
public sealed class PotPlayerClock : IPlaybackClock, IPlaybackSeeker
{
    private const uint WM_USER = 0x0400;

    /// <summary>查詢播放位置的自訂訊息參數（PotPlayer 擴充訊息）。</summary>
    private const long GetPositionCommand = 0x5004;

    /// <summary>跳到指定位置的自訂訊息參數（PotPlayer 擴充訊息；0x5005 尚待使用者實機確認）。</summary>
    private const long SeekCommand = 0x5005;

    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SMTO_BLOCK = 0x0001;
    private const uint SendMessageTimeoutMs = 200;

    /// <summary>找到的視窗代碼快取 5 秒，避免每句字幕都掃描行程。</summary>
    private static readonly TimeSpan HandleCacheDuration = TimeSpan.FromSeconds(5);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    private readonly object _lock = new();
    private IntPtr _cachedHandle;
    private long _scannedTicks;

    public long? GetPositionMs()
    {
        try
        {
            var handle = GetMainWindowHandle();
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            // lParam 為 0：PotPlayer 以 lpdwResult 回傳目前播放毫秒數
            var sent = SendMessageTimeout(
                handle, WM_USER, (IntPtr)GetPositionCommand, IntPtr.Zero,
                SMTO_ABORTIFHUNG | SMTO_BLOCK, SendMessageTimeoutMs, out var result);
            if (sent == IntPtr.Zero)
            {
                // 送訊息失敗（逾時或對方沒回應）：清掉快取，下次重新找視窗
                ClearCache();
                return null;
            }

            var position = (long)result;
            return position > 0 ? position : null;
        }
        catch
        {
            // 任何例外（取得行程資訊等）都不影響翻譯流程
            return null;
        }
    }

    /// <summary>讓 PotPlayer 跳到指定位置：送 WM_USER(0x0400)、wParam 0x5005、lParam 為毫秒數
    ///（與 0x5004 同一族的外部控制訊息，但 0x5005 尚未經使用者實測，實機確認前先做成可測版本）。
    /// positionMs &lt; 0、找不到視窗或送訊息失敗回 false，絕不擲例外。
    /// SendMessageTimeout 最多等 200ms，呼叫端不得在 UI 執行緒使用。</summary>
    public bool SeekTo(long positionMs)
    {
        if (positionMs < 0)
        {
            return false;
        }

        try
        {
            var handle = GetMainWindowHandle();
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var sent = SendMessageTimeout(
                handle, WM_USER, (IntPtr)SeekCommand, (IntPtr)positionMs,
                SMTO_ABORTIFHUNG | SMTO_BLOCK, SendMessageTimeoutMs, out _);
            if (sent == IntPtr.Zero)
            {
                // 送訊息失敗（逾時或對方沒回應）：清掉快取，下次重新找視窗
                ClearCache();
                return false;
            }

            return true;
        }
        catch
        {
            // 任何例外（取得行程資訊等）都不影響呼叫端
            return false;
        }
    }

    /// <summary>找 PotPlayer 主視窗（掃描結果快取 5 秒，含沒找到的情況）：ProcessName 以 PotPlayer
    /// 開頭（不分大小寫）、有主視窗代碼的第一個行程。不得用 FindWindow("PotPlayer64")，
    /// 實測它會找到另一個同類別的隱藏視窗。</summary>
    private IntPtr GetMainWindowHandle()
    {
        lock (_lock)
        {
            if (Environment.TickCount64 - _scannedTicks < HandleCacheDuration.TotalMilliseconds)
            {
                return _cachedHandle;
            }
        }

        IntPtr found = IntPtr.Zero;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.StartsWith("PotPlayer", StringComparison.OrdinalIgnoreCase) &&
                        process.MainWindowHandle != IntPtr.Zero)
                    {
                        found = process.MainWindowHandle;
                        break;
                    }
                }
                catch
                {
                    // 行程剛離開時 ProcessName／MainWindowHandle 可能擲例外：換下一個
                }
            }
        }

        lock (_lock)
        {
            _cachedHandle = found;
            _scannedTicks = Environment.TickCount64;
        }
        return found;
    }

    private void ClearCache()
    {
        lock (_lock)
        {
            _cachedHandle = IntPtr.Zero;
            _scannedTicks = 0;
        }
    }
}
