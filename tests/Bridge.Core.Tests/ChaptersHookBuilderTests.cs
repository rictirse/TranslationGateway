using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class ChaptersHookBuilderTests
{
    private const string HookHeader = "// ==== Translation Gateway chapters hook ====";

    private const string ChaptersUrlMarker = "const string FixedGatewayChaptersUrl = \"\";";

    private const string OriginalSignature =
        "string PlayitemParse(const string &in path, dictionary &MetaData, array<dictionary> &QualityList)";

    private const string RenamedSignature =
        "string GatewayOriginal_PlayitemParse(const string &in path, dictionary &MetaData, array<dictionary> &QualityList)";

    /// <summary>長得像 PotPlayer 內建 YouTube 解析腳本的最小樣本（含日文與 PlayitemParse 簽章）。</summary>
    private static string SampleScript(string newline = "\n") => string.Join(newline,
    [
        "// MediaSpliter - YouTube",
        OriginalSignature,
        "{",
        "    string api = \"https://www.youtube.com/watch?v=\";",
        "    HostPrintUTF8(\"日本語のテスト\");",
        "    return 0;",
        "}",
        ""
    ]);

    [Fact]
    public void Build_RenamesOriginalAndAppendsHookWithUrl()
    {
        var script = SampleScript();

        var result = ChaptersHookBuilder.Build(script, ChaptersHookBuilder.LoadTemplate(), "http://127.0.0.1:5003/chapters");

        // 原本的 PlayitemParse 改名，簽章只出現一次的新名字（掛勾裡新的 PlayitemParse 在掛勾區塊）
        Assert.Contains(RenamedSignature, result);
        var headerAt = result.IndexOf(HookHeader, StringComparison.Ordinal);
        Assert.True(headerAt > 0);
        var originalPart = result[..headerAt];
        Assert.Contains(RenamedSignature, originalPart);
        Assert.DoesNotContain(OriginalSignature, originalPart);
        Assert.Contains(OriginalSignature, result[(headerAt + HookHeader.Length)..]);
        // 掛勾的 URL 寫進標記行，原本的空值標記不留下
        Assert.Contains("const string FixedGatewayChaptersUrl = \"http://127.0.0.1:5003/chapters\";", result);
        Assert.DoesNotContain(ChaptersUrlMarker, result);
        // 原始腳本的內容還在，掛勾接在最後（原始腳本在前）
        Assert.Contains("日本語のテスト", result);
        Assert.Contains(HookHeader, result);
        Assert.True(
            result.IndexOf(RenamedSignature, StringComparison.Ordinal) < result.IndexOf(HookHeader, StringComparison.Ordinal),
            "原始腳本應在掛勾前面");
        // 掛勾裡新的 PlayitemParse 先呼叫原本的
        Assert.Contains("GatewayOriginal_PlayitemParse(path, MetaData, QualityList)", result);
    }

    [Fact]
    public void Build_IsIdempotent()
    {
        var template = ChaptersHookBuilder.LoadTemplate();

        var first = ChaptersHookBuilder.Build(SampleScript(), template, "http://127.0.0.1:5003/chapters");
        var second = ChaptersHookBuilder.Build(first, template, "http://127.0.0.1:5003/chapters");

        Assert.Equal(first, second);
    }

    /// <summary>架構師實機實驗版：原本的 PlayitemParse 已改名，最後接一段同簽章的測試包裝（沒有掛勾開頭行）。</summary>
    private static string SpikeScript() =>
        SampleScript().Replace(OriginalSignature, RenamedSignature, StringComparison.Ordinal) +
        "\n// ---- Translation Gateway 章節實驗（spike）：原本的 PlayitemParse 跑完後，沒有章節時補上測試章節 ----\n" +
        OriginalSignature + "\n{\n    string result = GatewayOriginal_PlayitemParse(path, MetaData, QualityList);\n    return result;\n}\n";

    [Fact]
    public void Build_SpikeScript_RestoresOriginalBeforeHooking()
    {
        var template = ChaptersHookBuilder.LoadTemplate();

        var result = ChaptersHookBuilder.Build(SpikeScript(), template, "http://127.0.0.1:5003/chapters");

        // 實驗版的包裝被砍掉：結果與從原檔接掛勾完全相同（只有一個改名後的函式）
        Assert.Equal(ChaptersHookBuilder.Build(SampleScript(), template, "http://127.0.0.1:5003/chapters"), result);
        Assert.DoesNotContain("章節實驗", result);
    }

    [Fact]
    public void Build_SpikeThenHookedScript_Recovers()
    {
        // 使用者實際遇到的檔案：實驗版沒換回原檔，又被舊版 Build 接了掛勾 → 兩個 GatewayOriginal_PlayitemParse
        var template = ChaptersHookBuilder.LoadTemplate();
        var broken = SpikeScript().Replace(OriginalSignature, RenamedSignature, StringComparison.Ordinal) +
            "\n" + template.Replace(ChaptersUrlMarker, "const string FixedGatewayChaptersUrl = \"http://127.0.0.1:5000/chapters\";", StringComparison.Ordinal);

        var result = ChaptersHookBuilder.Build(broken, template, "http://127.0.0.1:5003/chapters");

        Assert.Equal(ChaptersHookBuilder.Build(SampleScript(), template, "http://127.0.0.1:5003/chapters"), result);
    }

    [Fact]
    public void Build_ForeignRenameWithoutHeader_Throws()
    {
        // 別的工具也包過一層（有改名、有同簽章的新函式，但不是我們的開頭行）：換回後簽章兩個 → 擋下，不寫檔
        var foreign = SampleScript().Replace(OriginalSignature, RenamedSignature, StringComparison.Ordinal) +
            "\n// someone else's wrapper\n" + OriginalSignature + "\n{\n    return GatewayOriginal_PlayitemParse(path, MetaData, QualityList);\n}\n";

        Assert.Throws<InvalidOperationException>(() =>
            ChaptersHookBuilder.Build(foreign, ChaptersHookBuilder.LoadTemplate(), "http://127.0.0.1:5003/chapters"));
    }

    [Fact]
    public void Build_RebuildWithNewPort_OnlyUrlChanges()
    {
        var template = ChaptersHookBuilder.LoadTemplate();

        var first = ChaptersHookBuilder.Build(SampleScript(), template, "http://127.0.0.1:5003/chapters");
        var second = ChaptersHookBuilder.Build(first, template, "http://127.0.0.1:5007/chapters");

        Assert.Contains("const string FixedGatewayChaptersUrl = \"http://127.0.0.1:5007/chapters\";", second);
        // 把第一次輸出的 URL 換成新 port 就與第二次完全相同：換 port 只改 URL 那一行
        Assert.Equal(
            first.Replace("http://127.0.0.1:5003/chapters", "http://127.0.0.1:5007/chapters", StringComparison.Ordinal),
            second);
    }

    [Fact]
    public void Build_KeepsCrLf()
    {
        var script = SampleScript("\r\n");

        var result = ChaptersHookBuilder.Build(script, ChaptersHookBuilder.LoadTemplate(), "http://127.0.0.1:5003/chapters");

        // 輸出裡沒有單獨的 \n（每個 \n 前面都是 \r）
        for (var i = 0; i < result.Length; i++)
        {
            if (result[i] == '\n')
            {
                Assert.True(i > 0 && result[i - 1] == '\r', $"位置 {i} 的 \\n 前面沒有 \\r");
            }
        }

        Assert.Contains(RenamedSignature, result);
        Assert.Contains("const string FixedGatewayChaptersUrl = \"http://127.0.0.1:5003/chapters\";", result);
        // CRLF 版也要冪等
        Assert.Equal(result, ChaptersHookBuilder.Build(result, ChaptersHookBuilder.LoadTemplate(), "http://127.0.0.1:5003/chapters"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Build_SignatureMissingOrDuplicated_Throws(int signatureCount)
    {
        var script = signatureCount == 0
            ? "// 沒有 PlayitemParse 的腳本\r\nint Other() { return 0; }\r\n"
            : SampleScript() + "\n" + OriginalSignature + "\r\n{ return 1; }\r\n";

        var exception = Assert.Throws<InvalidOperationException>(
            () => ChaptersHookBuilder.Build(script, ChaptersHookBuilder.LoadTemplate(), "http://127.0.0.1:5003/chapters"));

        // 訊息含實際次數
        Assert.Contains(signatureCount.ToString(), exception.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/\"chapters")]
    [InlineData(@"http://127.0.0.1:5000\chapters")]
    public void Build_UrlWithQuote_Throws(string chaptersUrl)
    {
        Assert.Throws<ArgumentException>(
            () => ChaptersHookBuilder.Build(SampleScript(), ChaptersHookBuilder.LoadTemplate(), chaptersUrl));
    }

    [Fact]
    public void LoadTemplate_ContainsMarkerAndHeader()
    {
        var template = ChaptersHookBuilder.LoadTemplate();

        // 掛勾區塊的開頭行是模板第一行
        Assert.StartsWith(HookHeader, template);
        Assert.Contains(ChaptersUrlMarker, template);
    }
}
