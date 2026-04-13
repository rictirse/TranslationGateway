using ModelContextProtocol.Server;
using System.ComponentModel;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Tools;

public class TranslationTool
{
    private readonly TranslationManager _manager;

    public TranslationTool(TranslationManager manager)
    {
        _manager = manager;
    }

    [McpServerTool(Name = "translate_text")]
    [Description("呼叫本地翻譯引擎將日文轉換為繁體中文，支援語境分析。")]
    public async Task<string> TranslateText(
        [Description("目前要翻譯的文字")] string current,
        [Description("上下文內容，可空白")] string? context = null)
    {

        PotplayerRequest request = new PotplayerRequest
        {
            Context = context ?? string.Empty,
            Current = current
        };

        return await _manager.TranslateAsync(request);
    }
}