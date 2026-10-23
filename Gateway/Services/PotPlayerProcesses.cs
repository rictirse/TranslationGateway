using System.Diagnostics;
using Translation.Bridge.Core.Interface;

namespace Translation.Gateway.Services;

/// <summary>PotPlayer 程序的偵測與關閉。程序名稱判斷比照 PotPlayerClock：
/// ProcessName 以 PotPlayer 開頭（不分大小寫）；等待方式為每 200 ms 檢查一次。</summary>
public sealed class PotPlayerProcesses : IPotPlayerProcesses
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    public bool AnyRunning()
    {
        var running = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.StartsWith("PotPlayer", StringComparison.OrdinalIgnoreCase))
                    {
                        running = true;
                    }
                }
                catch
                {
                    // 程序剛離開時 ProcessName 可能擲例外：略過
                }
            }
        }

        return running;
    }

    public async Task<bool> WaitAllExitedAsync(TimeSpan timeout, CancellationToken ct = default) =>
        await WaitUntilAllExitedAsync(timeout, ct).ConfigureAwait(false);

    public async Task<bool> CloseAllAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        // 送 CloseMainWindow 讓 PotPlayer 正常存檔結束（把設定寫回登錄檔），不強制結束
        foreach (var process in FindAll())
        {
            using (process)
            {
                try
                {
                    _ = process.CloseMainWindow();
                }
                catch
                {
                    // 程序剛好結束了：略過
                }
            }
        }

        return await WaitUntilAllExitedAsync(timeout, ct).ConfigureAwait(false);
    }

    private async Task<bool> WaitUntilAllExitedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (!AnyRunning())
            {
                return true;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return !AnyRunning();
    }

    /// <summary>找出所有執行中的 PotPlayer 程序（找不到時為空清單）；程序剛結束擲例外時略過。</summary>
    private static List<Process> FindAll()
    {
        var matches = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            var isMatch = false;
            try
            {
                isMatch = process.ProcessName.StartsWith("PotPlayer", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 程序剛離開時 ProcessName 可能擲例外：略過
            }

            if (isMatch)
            {
                matches.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return matches;
    }
}
