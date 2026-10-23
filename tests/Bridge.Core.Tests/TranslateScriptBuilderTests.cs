using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class TranslateScriptBuilderTests
{
    private const string GatewayUrlMarker = "const string FixedGatewayUrl = \"\";";

    private const string ContextCountMarker = "const int FixedContextCount = 0;";

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

    [Fact]
    public void LoadTemplate_ContainsBothMarkersExactlyOnce()
    {
        var template = TranslateScriptBuilder.LoadTemplate();

        Assert.Equal(1, CountOccurrences(template, GatewayUrlMarker));
        Assert.Equal(1, CountOccurrences(template, ContextCountMarker));
    }

    [Fact]
    public void Build_ReplacesMarkersWithUrlAndContextCount()
    {
        var template = TranslateScriptBuilder.LoadTemplate();

        var result = TranslateScriptBuilder.Build(template, "http://127.0.0.1:5003/translate", 5);

        Assert.Contains("const string FixedGatewayUrl = \"http://127.0.0.1:5003/translate\";", result);
        Assert.Contains("const int FixedContextCount = 5;", result);
        // 原本的標記行（空值）不該留下
        Assert.DoesNotContain(GatewayUrlMarker, result);
        Assert.DoesNotContain(ContextCountMarker, result);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate-url")]
    [InlineData("duplicate-context")]
    public void Build_MarkerMissingOrDuplicated_ThrowsInvalidOperationException(string kind)
    {
        var template = TranslateScriptBuilder.LoadTemplate();
        template = kind switch
        {
            "missing" => template.Replace(GatewayUrlMarker, string.Empty),
            "duplicate-url" => template + "\n" + GatewayUrlMarker,
            _ => template + "\n" + ContextCountMarker,
        };

        Assert.Throws<InvalidOperationException>(() => TranslateScriptBuilder.Build(template, "http://127.0.0.1:5000/translate", 5));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/\"translate")]
    [InlineData(@"http://127.0.0.1:5000\translate")]
    public void Build_UrlContainsQuoteOrBackslash_ThrowsArgumentException(string gatewayUrl)
    {
        var template = TranslateScriptBuilder.LoadTemplate();

        Assert.Throws<ArgumentException>(() => TranslateScriptBuilder.Build(template, gatewayUrl, 5));
    }
}
