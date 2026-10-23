namespace Translation.Bridge.Core.Interface;

/// <summary>PotPlayer 程序的偵測與關閉：PotPlayer 只在啟動時讀設定、關閉時又會把設定寫回登錄檔，改設定前必須先讓它全部結束。</summary>
public interface IPotPlayerProcesses
{
    /// <summary>有沒有 ProcessName 以 PotPlayer 開頭的程序正在執行。</summary>
    bool AnyRunning();

    /// <summary>等所有 PotPlayer 程序結束，逾時回傳 false。</summary>
    Task<bool> WaitAllExitedAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>對每個 PotPlayer 送 CloseMainWindow（讓它正常存檔結束），再等全部結束；逾時回傳 false，不強制結束。</summary>
    Task<bool> CloseAllAsync(TimeSpan timeout, CancellationToken ct = default);
}
