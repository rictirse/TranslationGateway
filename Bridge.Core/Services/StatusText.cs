namespace Translation.Bridge.Core.Services;

/// <summary>狀態列文字的組合規則：同一件事的進度更新（前處理逐批進度）換掉上一行，不一直往下堆。</summary>
public static class StatusText
{
    /// <summary>前處理狀態行的開頭（這類訊息在同一行更新）。</summary>
    private const string PreprocessPrefix = "生成式字幕前處理：";

    /// <summary>把 message 加到 status：message 與 status 的最後一行都是前處理進度時，換掉最後一行；
    /// 其他情況附加新的一行。status 是空字串時直接回傳 message。換行一律用 \n。</summary>
    public static string AppendOrReplace(string status, string message)
    {
        if (status.Length == 0)
        {
            return message;
        }

        var lastLineStart = status.LastIndexOf('\n') + 1;
        var lastLine = status[lastLineStart..];
        if (message.StartsWith(PreprocessPrefix, StringComparison.Ordinal) &&
            lastLine.StartsWith(PreprocessPrefix, StringComparison.Ordinal))
        {
            return status[..lastLineStart] + message;
        }

        return status + "\n" + message;
    }
}
