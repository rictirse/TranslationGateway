using System.Reflection;
using System.Text;

namespace Translation.Bridge.Core.Services;

/// <summary>把章節掛勾（ChaptersHook.as）接進 PotPlayer 內建的 YouTube 解析腳本（MediaPlayParse - YouTube.as）：
/// 原本的 PlayitemParse 改名為 GatewayOriginal_PlayitemParse，掛勾裡新的 PlayitemParse 先呼叫原本的，
/// 說明欄沒有章節時才向 Gateway 的 /chapters 要章節（架構師已實機確認這種包法有效）。
/// 已接過的腳本先還原成原始腳本再重接（冪等），換 port 重 Build 只改 URL 那一行。</summary>
public static class ChaptersHookBuilder
{
    /// <summary>掛勾要接上的腳本檔名（PotPlayer 內建的 YouTube 解析腳本）。</summary>
    public const string TargetScriptFileName = "MediaPlayParse - YouTube.as";

    /// <summary>掛勾區塊的開頭行（模板第一行）：已接過的腳本從這行（含）之後全部是掛勾，還原時砍掉。</summary>
    private const string HookHeader = "// ==== Translation Gateway chapters hook ====";

    /// <summary>原始腳本裡要改名的函式簽章（必須恰好出現一次）。</summary>
    private const string OriginalSignature =
        "string PlayitemParse(const string &in path, dictionary &MetaData, array<dictionary> &QualityList)";

    /// <summary><see cref="OriginalSignature"/> 改名後的簽章（掛勾裡新的 PlayitemParse 呼叫它）。</summary>
    private const string RenamedSignature =
        "string GatewayOriginal_PlayitemParse(const string &in path, dictionary &MetaData, array<dictionary> &QualityList)";

    /// <summary>架構師 2026-10-26 實機實驗用的包裝開頭（使用者可能沒換回原檔）：還原時從這行起一併砍掉。</summary>
    private const string SpikeHeader = "// ---- Translation Gateway 章節實驗";

    /// <summary>還原時把改名（含呼叫處）換回原本的名字。</summary>
    private const string RenamedCall = "GatewayOriginal_PlayitemParse(";

    /// <summary>模板中待取代的 Gateway URL 標記行（恰好出現一次）。</summary>
    private const string ChaptersUrlMarker = "const string FixedGatewayChaptersUrl = \"\";";

    /// <summary>讀取內嵌資源裡的章節掛勾模板；讀不到時擲 <see cref="InvalidOperationException"/>。</summary>
    public static string LoadTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Translation.Bridge.Core.ChaptersHook.as")
            ?? throw new InvalidOperationException("找不到嵌入資源 Translation.Bridge.Core.ChaptersHook.as");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>把掛勾接到 <paramref name="script"/>（可能已經接過）並寫入 <paramref name="chaptersUrl"/>，回傳新的腳本內容。
    /// 已接過時先還原成原始腳本（砍掉掛勾區塊、把改名換回來）再重接，同一份輸入重複 Build 結果完全相同。
    /// URL 含引號或反斜線時擲 <see cref="ArgumentException"/>（會破壞 AngelScript 的字串常數）；
    /// 簽章或模板標記出現次數不是恰好一次時擲 <see cref="InvalidOperationException"/>（訊息含實際次數）。</summary>
    public static string Build(string script, string hookTemplate, string chaptersUrl)
    {
        if (chaptersUrl.Contains('"') || chaptersUrl.Contains('\\'))
        {
            throw new ArgumentException("章節 URL 不得包含引號或反斜線", nameof(chaptersUrl));
        }

        var hook = ReplaceMarker(
            hookTemplate,
            ChaptersUrlMarker,
            $"const string FixedGatewayChaptersUrl = \"{chaptersUrl}\";");

        // 還原成原始腳本：砍掉掛勾區塊（開頭行含）之後的全部內容；架構師實機實驗版（spike）的包裝也一併砍掉，
        // 最後把改名換回原本的名字。實驗版也把原本的 PlayitemParse 改了名，只砍掛勾會留下兩個 GatewayOriginal_PlayitemParse，
        // PotPlayer 編譯失敗（同名函式重複）、整支 YouTube 解析腳本不能用。
        var original = script;
        foreach (var header in (string[])[HookHeader, SpikeHeader])
        {
            var headerIndex = original.IndexOf(header, StringComparison.Ordinal);
            if (headerIndex >= 0)
            {
                original = original[..headerIndex];
            }
        }

        // 不是自己接的改名（其他工具改過）也換回來：簽章因此變成兩個時，下面的次數檢查會擋下，不寫檔
        original = original.Replace(RenamedCall, "PlayitemParse(", StringComparison.Ordinal);

        // 結尾的空白行（含換行）一律去掉，接掛勾時一律補回「一個空白行」，兩邊 normalize 才能冪等
        original = TrimTrailingBlankLines(original);

        var count = CountOccurrences(original, OriginalSignature);
        if (count != 1)
        {
            throw new InvalidOperationException(
                $"YouTube 解析腳本裡的 PlayitemParse 簽章應恰好出現一次，實際 {count} 次：{TargetScriptFileName}");
        }

        // 換行符號跟原始腳本一致：原始腳本含 \r\n 時，模板的換行全部轉成 \r\n
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (newline != "\n")
        {
            hook = hook.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        return original.Replace(OriginalSignature, RenamedSignature, StringComparison.Ordinal) + newline + newline + hook;
    }

    /// <summary>去掉結尾的空白行與換行（只有空白的行也算空白行）；回傳結果不以換行結尾。</summary>
    private static string TrimTrailingBlankLines(string text)
    {
        var end = text.Length;
        while (end > 0)
        {
            // 先越過緊接在結尾的換行字元，再找這一行的開頭
            var contentEnd = end;
            while (contentEnd > 0 && text[contentEnd - 1] is '\r' or '\n')
            {
                contentEnd--;
            }

            var lineStart = contentEnd;
            while (lineStart > 0 && text[lineStart - 1] is not ('\r' or '\n'))
            {
                lineStart--;
            }

            // 有內容的行（非純空白）：到這裡為止
            if (contentEnd > lineStart && text[lineStart..contentEnd].Trim().Length > 0)
            {
                return text[..contentEnd];
            }

            // 空白行：整行（含換行）去掉，繼續往前
            end = lineStart;
        }

        return string.Empty;
    }

    /// <summary>把標記行換成實際值；標記出現次數不是剛好 1 次時擲 <see cref="InvalidOperationException"/>。</summary>
    private static string ReplaceMarker(string template, string marker, string replacement)
    {
        var count = CountOccurrences(template, marker);
        if (count != 1)
        {
            throw new InvalidOperationException($"章節掛勾模板的標記行應恰好出現一次，實際 {count} 次：{marker}");
        }

        return template.Replace(marker, replacement, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
