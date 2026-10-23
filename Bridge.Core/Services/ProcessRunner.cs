using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;

namespace Translation.Bridge.Core.Services;

/// <summary>以 <see cref="Process"/> 執行外部程式（streamlink、播放器）；參數一律走 ArgumentList，不經過 shell。</summary>
public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    public bool FileExists(string path) => File.Exists(path);

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var process = CreateProcess(fileName, arguments);
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 逾時：砍掉整個行程樹再回報，避免留下孤兒程序占住檔案或連線
            KillTree(process);
            await Task.WhenAny(stdoutTask, stderrTask);
            return new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            // 外部取消：一樣要清掉行程樹，再把取消往回丟
            KillTree(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr, TimedOut: false);
    }

    public IStartedProcess Start(string fileName, IReadOnlyList<string> arguments, Action<string> onOutput)
    {
        var process = CreateProcess(fileName, arguments);
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line) onOutput(line);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line) onOutput(line);
        };

        var started = new StartedProcess(process, logger);
        process.Exited += (_, _) =>
        {
            // 先標記再 Dispose：KillTree 才能在 Dispose 之後安全地以 HasExited 判斷
            started.MarkExited();
            process.Dispose();
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return started;
    }

    /// <summary>Start 回傳的程序控制物件；HasExited 由 Exited 事件維護，Dispose 後呼叫 KillTree 也安全。</summary>
    private sealed class StartedProcess(Process process, ILogger<ProcessRunner> logger) : IStartedProcess
    {
        private volatile bool _hasExited;

        public bool HasExited => _hasExited;

        public void MarkExited() => _hasExited = true;

        public void KillTree()
        {
            if (_hasExited) return;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 行程已經結束（含 Process 已被 Dispose）：不用再砍
                _hasExited = true;
            }
            catch (Win32Exception ex)
            {
                // 行程正在關閉中：忽略
                logger.LogWarning(ex, "結束行程樹失敗（可能正在關閉中）");
            }
        }
    }

    private static Process CreateProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // streamlink 是 Python 程式：強制 UTF-8，避免中文標題變成問號
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = info };
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 行程已經結束：不用再砍
        }
        catch (Win32Exception)
        {
            // 行程正在關閉中：忽略
        }
    }
}
