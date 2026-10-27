using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>直播標題翻譯：假的知識庫模型回腳本化的 JSON（或擲例外、或等到取消），驗證解析、失敗處理與用途標記。</summary>
public class StreamTitleTranslatorTests
{
    private static StreamTitleTranslator Create(ILorebookLlm llm, GlossaryState? glossary = null) => new(
        llm, glossary ?? new GlossaryState(), new TextProcessingService(), NullLogger<StreamTitleTranslator>.Instance);

    [Fact]
    public async Task Translate_ReturnsParsedTranslation()
    {
        var llm = new ScriptedLlm { Reply = """{"translation": "【APEX】一起來玩"}""" };
        var sut = Create(llm);

        var result = await sut.TranslateAsync("【APEX】雑談やりましょう", TestContext.Current.CancellationToken);

        Assert.Equal("【APEX】一起來玩", result);
        Assert.Equal(1, llm.CallCount);
        Assert.Equal("【APEX】雑談やりましょう", llm.LastUserPrompt);
        Assert.Contains("繁體中文", llm.LastSystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Translate_StripsCodeFence()
    {
        var llm = new ScriptedLlm { Reply = "```json\n{\"translation\": \"雜談直播\"}\n```" };
        var sut = Create(llm);

        var result = await sut.TranslateAsync("雑談配信", TestContext.Current.CancellationToken);

        Assert.Equal("雜談直播", result);
    }

    [Fact]
    public async Task Translate_LlmThrows_ReturnsNull()
    {
        var llm = new ScriptedLlm { Exception = new LorebookLlmException("HTTP 500：內部錯誤") };
        var sut = Create(llm);

        var result = await sut.TranslateAsync("タイトル", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Translate_BadJson_ReturnsNull()
    {
        var llm = new ScriptedLlm { Reply = "這不是 JSON" };
        var sut = Create(llm);

        var result = await sut.TranslateAsync("タイトル", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Translate_EmptyTranslation_ReturnsNull()
    {
        var llm = new ScriptedLlm { Reply = """{"translation": "   "}""" };
        var sut = Create(llm);

        var result = await sut.TranslateAsync("タイトル", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Translate_BlankTitle_ReturnsNull_WithoutCallingLlm()
    {
        var llm = new ScriptedLlm { Reply = """{"translation": "譯文"}""" };
        var sut = Create(llm);

        Assert.Null(await sut.TranslateAsync("   ", TestContext.Current.CancellationToken));
        Assert.Null(await sut.TranslateAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task Translate_UsesTitleTranslationPurpose()
    {
        var llm = new ScriptedLlm { Reply = """{"translation": "譯文"}""" };
        var sut = Create(llm);

        await sut.TranslateAsync("タイトル", TestContext.Current.CancellationToken);

        Assert.Equal(LlmCallPurposes.TitleTranslation, llm.PurposeAtCall);
    }

    [Fact]
    public void BuildSystemPrompt_IncludesGlossarySection()
    {
        var glossary = new GlossaryState();
        glossary.SetPromptSection("ぺこら=佩克拉\nリサ=Lisa");
        var sut = Create(new ScriptedLlm(), glossary);

        var prompt = sut.BuildSystemPrompt();

        Assert.Contains("## 詞彙表", prompt, StringComparison.Ordinal);
        Assert.Contains("ぺこら=佩克拉", prompt, StringComparison.Ordinal);
        Assert.EndsWith("リサ=Lisa", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSystemPrompt_WithoutGlossary()
    {
        var sut = Create(new ScriptedLlm(), new GlossaryState());

        var prompt = sut.BuildSystemPrompt();

        Assert.DoesNotContain("## 詞彙表", prompt, StringComparison.Ordinal);
        Assert.Contains("""{"translation": "譯文"}""", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Translate_CallerCancel_Throws()
    {
        var llm = new ScriptedLlm { WaitForever = true };
        var sut = Create(llm);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.TranslateAsync("タイトル", cts.Token));
    }

    /// <summary>可腳本化的假知識庫模型：記下 prompt 與用途，回 Reply、擲 Exception，或等到取消。</summary>
    private sealed class ScriptedLlm : ILorebookLlm
    {
        public string? Reply { get; set; }
        public Exception? Exception { get; set; }

        /// <summary>true 時不等回覆、直到取消（呼叫端取消的測試用）。</summary>
        public bool WaitForever { get; set; }

        public int CallCount { get; private set; }
        public string? LastSystemPrompt { get; private set; }
        public string? LastUserPrompt { get; private set; }

        /// <summary>呼叫當下讀到的 LlmCallPurposeScope.Current。</summary>
        public string? PurposeAtCall { get; private set; }

        public async Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            PurposeAtCall = LlmCallPurposeScope.Current;
            if (Exception is not null)
            {
                throw Exception;
            }
            if (WaitForever)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new LlmJsonResult(Reply ?? "{}", 1, 1, 1, "fake-glm");
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }
}
