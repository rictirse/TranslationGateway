using System.Reflection;
using System.Text;

namespace Translation.Bridge.Core.Services;

/// <summary>產生寫入 PotPlayer 的翻譯腳本：把 Gateway 的 URL 與上下文行數寫死進內嵌的 SubtitleTranslate.as 模板。</summary>
public static class TranslateScriptBuilder
{
    /// <summary>寫入 PotPlayer 的 Extension\Subtitle\Translate\ 時沿用的檔名（bing 腳本的位置，PotPlayer 才找得到）。</summary>
    public const string ScriptFileName = "SubtitleTranslate - bing.as";

    /// <summary>寫死進腳本的上下文行數。</summary>
    public const int ContextCount = 5;

    /// <summary>模板中待取代的 Gateway URL 標記行（SubtitleTranslate.as v2.7 起，恰好出現一次）。</summary>
    private const string GatewayUrlMarker = "const string FixedGatewayUrl = \"\";";

    /// <summary>模板中待取代的上下文行數標記行（恰好出現一次）。</summary>
    private const string ContextCountMarker = "const int FixedContextCount = 0;";

    /// <summary>讀取內嵌資源裡的翻譯腳本模板；讀不到時擲 <see cref="InvalidOperationException"/>。</summary>
    public static string LoadTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Translation.Bridge.Core.SubtitleTranslate.as")
            ?? throw new InvalidOperationException("找不到嵌入資源 Translation.Bridge.Core.SubtitleTranslate.as");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>把 <paramref name="gatewayUrl"/> 與 <paramref name="contextCount"/> 寫死進模板。
    /// 兩行標記在模板中必須都恰好出現一次，否則擲 <see cref="InvalidOperationException"/>；
    /// URL 含引號或反斜線時擲 <see cref="ArgumentException"/>（會破壞 AngelScript 的字串常數）。</summary>
    public static string Build(string template, string gatewayUrl, int contextCount)
    {
        if (gatewayUrl.Contains('"') || gatewayUrl.Contains('\\'))
        {
            throw new ArgumentException("Gateway URL 不得包含引號或反斜線", nameof(gatewayUrl));
        }

        var withUrl = ReplaceMarker(template, GatewayUrlMarker, $"const string FixedGatewayUrl = \"{gatewayUrl}\";");
        return ReplaceMarker(withUrl, ContextCountMarker, $"const int FixedContextCount = {contextCount};");
    }

    /// <summary>把標記行換成實際值；標記出現次數不是剛好 1 次時擲 <see cref="InvalidOperationException"/>。</summary>
    private static string ReplaceMarker(string template, string marker, string replacement)
    {
        var count = 0;
        var index = 0;
        while ((index = template.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        if (count != 1)
        {
            throw new InvalidOperationException($"翻譯腳本模板的標記行應恰好出現一次，實際 {count} 次：{marker}");
        }

        return template.Replace(marker, replacement);
    }
}
