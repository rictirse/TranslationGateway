namespace Translation.Bridge.Core.Interface;

/// <summary>外部程序的執行結果；逾時時 ExitCode 為 -1 且 TimedOut = true。</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Start 啟動的背景程序。</summary>
public interface IStartedProcess
{
    /// <summary>程序已結束（包含被 KillTree 結束）。</summary>
    bool HasExited { get; }

    /// <summary>結束整個行程樹（包含子程序，例如 streamlink 帶起的 PotPlayer）；已結束時不做事、不擲例外。</summary>
    void KillTree();
}

public interface IProcessRunner
{
    bool FileExists(string path);

    /// <summary>執行並等待結束；逾時則終止整個行程樹並回傳 TimedOut = true。</summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>啟動後不等待；標準輸出與錯誤的每一行交給 onOutput。</summary>
    IStartedProcess Start(string fileName, IReadOnlyList<string> arguments, Action<string> onOutput);
}
