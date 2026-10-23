using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>貼上資料整理：假的 LLM 依序回傳腳本化的 JSON（或擲例外），驗證切段、驗證正規化與錯誤處理。</summary>
public class TermImportServiceTests
{
    private static string TermsJson(params string[] terms) => "{\"terms\":[" + string.Join(",", terms) + "]}";

    private static string TermJson(string source, string target, string aliases = "", string category = "", string note = "") =>
        $"{{\"source\":\"{source}\",\"target\":\"{target}\",\"aliases\":[{aliases}],\"category\":\"{category}\",\"note\":\"{note}\"}}";

    private sealed class Harness : IDisposable
    {
        public ImportLlmFake Llm { get; } = new();

        public TermImportService Sut { get; }

        public Harness() => Sut = new TermImportService(Llm, NullLogger<TermImportService>.Instance);

        public void Dispose()
        {
        }
    }

    /// <summary>依序回傳排入的腳本（字串＝回覆內容、Exception＝擲出），並記下收到的 user 訊息。</summary>
    internal sealed class ImportLlmFake : ILorebookLlm
    {
        /// <summary>腳本步驟：字串為回覆內容、Exception 為要擲出的例外；沒有步驟時回傳空 JSON。</summary>
        public Queue<object> Script { get; } = new();

        public List<string> UserPrompts { get; } = [];

        public Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            UserPrompts.Add(userPrompt);
            cancellationToken.ThrowIfCancellationRequested();
            if (Script.Count > 0 && Script.Dequeue() is { } step)
            {
                if (step is Exception ex)
                {
                    throw ex;
                }

                return Task.FromResult(new LlmJsonResult((string)step, 10, 5, 20, "fake-glm"));
            }

            return Task.FromResult(new LlmJsonResult("{}", 10, 5, 20, "fake-glm"));
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }

    [Fact]
    public async Task ImportAsync_DataContainsJapanese_NotGuessedAndNoteUntouched()
    {
        using var harness = new Harness();
        harness.Llm.Script.Enqueue(TermsJson(
            TermJson("アーリ", "阿璃", aliases: "\"Ahri\"", category: "英雄", note: ""),
            TermJson("ヤスオ", "疾風壁")));

        var result = await harness.Sut.ImportAsync(
            "LoL", GlossaryKind.Game, null, [], "アーリ 阿璃\nヤスオ 疾風壁", null, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Proposals.Count);
        Assert.All(result.Proposals, p => Assert.False(p.JapaneseGuessed));
        Assert.All(result.Proposals, p => Assert.DoesNotContain("日文由模型推測", p.Note, StringComparison.Ordinal));
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(0, result.Dropped);
        Assert.Equal(1, result.Chunks);
        Assert.Empty(result.Errors);

        // 一段時資料標題不帶段號；沒有說明與已有名詞時整段省略
        var prompt = Assert.Single(harness.Llm.UserPrompts);
        Assert.Contains("## 詞彙卡", prompt, StringComparison.Ordinal);
        Assert.Contains("卡名：LoL（遊戲）", prompt, StringComparison.Ordinal);
        Assert.Contains("## 資料\n", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("第 1/1 段", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## 使用者說明", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## 已有名詞", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_UserPrompt_ContainsInstructionAndExistingSources()
    {
        using var harness = new Harness();
        harness.Llm.Script.Enqueue(TermsJson());

        await harness.Sut.ImportAsync(
            "LoL", GlossaryKind.Game, " 左邊日文、右邊台服譯名 ", ["既存1", "既存2"], "アーリ 阿璃",
            null, TestContext.Current.CancellationToken);

        var prompt = Assert.Single(harness.Llm.UserPrompts);
        Assert.Contains("## 使用者說明", prompt, StringComparison.Ordinal);
        Assert.Contains("左邊日文、右邊台服譯名", prompt, StringComparison.Ordinal);
        Assert.Contains("## 已有名詞（不要再給）", prompt, StringComparison.Ordinal);
        Assert.Contains("既存1、既存2", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_EnglishChineseOnly_GuessedJapaneseMarkedInNote()
    {
        using var harness = new Harness();
        harness.Llm.Script.Enqueue(TermsJson(
            TermJson("アーリ", "阿璃", note: "台服官方譯名"),
            TermJson("ヤスオ", "疾風壁")));

        var result = await harness.Sut.ImportAsync(
            "LoL", GlossaryKind.Game, null, [], "Ahri 阿璃\nYasuo 疾風壁", null, TestContext.Current.CancellationToken);

        var ahri = Assert.Single(result.Proposals, p => p.Source == "アーリ");
        Assert.True(ahri.JapaneseGuessed);
        Assert.Equal("日文由模型推測；台服官方譯名", ahri.Note);
        var yasuo = Assert.Single(result.Proposals, p => p.Source == "ヤスオ");
        Assert.True(yasuo.JapaneseGuessed);
        Assert.Equal("日文由模型推測", yasuo.Note);
    }

    [Fact]
    public async Task ImportAsync_DuplicateSources_OrdinalIgnoreCase_CountedAndSkipped()
    {
        using var harness = new Harness();
        harness.Llm.Script.Enqueue(TermsJson(
            TermJson(" AHRI ", "阿璃"),
            TermJson("ahri", "阿璃"),
            TermJson("ヤスオ", "疾風壁")));

        var result = await harness.Sut.ImportAsync(
            "LoL", GlossaryKind.Game, null, ["Ahri"], "Ahri 阿璃\nヤスオ 疾風壁", null, TestContext.Current.CancellationToken);

        // 第一個與既有名詞重複（大小寫不同）、第二個與本次第一個重複（修剪後同源）
        Assert.Equal(2, result.Duplicates);
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal("ヤスオ", proposal.Source);
    }

    [Fact]
    public async Task ImportAsync_TwoChunks_SecondPromptListsFirstChunkSources()
    {
        using var harness = new Harness();
        var line1 = "アーリ 阿璃 " + new string('a', 3900);
        var line2 = "ヤスオ 疾風壁 " + new string('b', 3900);
        harness.Llm.Script.Enqueue(TermsJson(TermJson("アーリ", "阿璃")));
        harness.Llm.Script.Enqueue(TermsJson(
            TermJson("ヤスオ", "疾風壁"),
            TermJson("アーリ", "阿璃")));

        var progress = new List<string>();
        var result = await harness.Sut.ImportAsync(
            "LoL", GlossaryKind.Game, null, [], $"{line1}\n{line2}",
            new ProgressCollector(progress), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Chunks);
        Assert.Equal(2, harness.Llm.UserPrompts.Count);
        Assert.Contains("## 資料（第 1/2 段）", harness.Llm.UserPrompts[0], StringComparison.Ordinal);
        Assert.Contains("## 資料（第 2/2 段）", harness.Llm.UserPrompts[1], StringComparison.Ordinal);
        Assert.Contains("## 已有名詞（不要再給）", harness.Llm.UserPrompts[1], StringComparison.Ordinal);
        Assert.Contains("アーリ", harness.Llm.UserPrompts[1], StringComparison.Ordinal);
        Assert.Contains("整理中：第 1/2 段…", progress[0], StringComparison.Ordinal);
        Assert.Contains("整理中：第 2/2 段…", progress[1], StringComparison.Ordinal);

        // 第二段重複提出第一段的アーリ：算 Duplicates，不進提案
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(2, result.Proposals.Count);
        Assert.Contains(result.Proposals, p => p.Source == "アーリ");
        Assert.Contains(result.Proposals, p => p.Source == "ヤスオ");
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("badJson")]
    public async Task ImportAsync_ChunkFailure_RecordsErrorAndContinues(string kind)
    {
        using var harness = new Harness();
        var line1 = "アーリ 阿璃 " + new string('a', 3900);
        var line2 = "ヤスオ 疾風壁 " + new string('b', 3900);
        harness.Llm.Script.Enqueue(TermsJson(TermJson("アーリ", "阿璃")));
        harness.Llm.Script.Enqueue(kind == "exception"
            ? new LorebookLlmException("HTTP 500：內部錯誤")
            : "這不是 JSON");

        var result = await harness.Sut.ImportAsync(
            "LoL", GlossaryKind.Game, null, [], $"{line1}\n{line2}", null, TestContext.Current.CancellationToken);

        // 失敗的段記進 Errors（含段號），其他段的提案照樣回傳
        var error = Assert.Single(result.Errors);
        Assert.StartsWith("第 2 段：", error, StringComparison.Ordinal);
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal("アーリ", proposal.Source);
    }

    [Fact]
    public async Task ImportAsync_InvalidTerms_Dropped()
    {
        using var harness = new Harness();
        harness.Llm.Script.Enqueue(TermsJson(
            TermJson("", "譯名"),
            TermJson("アーリ", "  "),
            TermJson("Ahri", "Ahri"),
            TermJson("ヤスオ", "疾風壁")));

        var result = await harness.Sut.ImportAsync(
            "LoL", GlossaryKind.Game, null, [], "Ahri 阿璃\nヤスオ 疾風壁", null, TestContext.Current.CancellationToken);

        // 空白原文、空白譯名、ASCII 的 Source == Target 都算丟棄
        Assert.Equal(3, result.Dropped);
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal("ヤスオ", proposal.Source);
    }

    [Fact]
    public async Task ImportAsync_Aliases_NormalizedTrimmedDeduped()
    {
        using var harness = new Harness();
        harness.Llm.Script.Enqueue(TermsJson(TermJson(
            "アーリ", "阿璃", aliases: "\" Ahri \", \"\", \"ahri\", \"アーリ\", \"Ahri-九尾\", \"Ahri-九尾\"")));

        var result = await harness.Sut.ImportAsync(
            "LoL", GlossaryKind.Game, null, [], "アーリ 阿璃", null, TestContext.Current.CancellationToken);

        // 去空、去掉和 source 相同的、不分大小寫去重；用換行接起來
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal("Ahri\nAhri-九尾", proposal.Aliases);
        Assert.Equal("アーリ", proposal.Source);
    }

    [Fact]
    public async Task ImportAsync_InputTooLong_ThrowsArgumentException()
    {
        using var harness = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Sut.ImportAsync(
            "LoL", GlossaryKind.Game, null, [], new string('a', TermImportService.MaxInputLength + 1),
            null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportAsync_Canceled_ThrowsOperationCanceledException()
    {
        using var harness = new Harness();
        harness.Llm.Script.Enqueue(TermsJson(TermJson("アーリ", "阿璃")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.Sut.ImportAsync(
            "LoL", GlossaryKind.Game, null, [], "アーリ 阿璃", null, cts.Token));
    }

    [Fact]
    public void SplitChunks_CutsAtLineBoundaries_EachChunkWithinLimit()
    {
        var lineA = new string('a', 10);
        var lineB = new string('b', 10);
        var lineC = new string('c', 10);

        var chunks = TermImportService.SplitChunks($"{lineA}\r\n{lineB}\n{lineC}", 25);

        // 前兩行共 21 字（含換行）≤ 25；加上第三行會超過，在換行處切
        Assert.Equal([lineA + "\n" + lineB, lineC], chunks);
    }

    [Fact]
    public void SplitChunks_LongSingleLine_HardCut()
    {
        var chunks = TermImportService.SplitChunks(new string('あ', 25), 10);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= 10));
        Assert.Equal(new string('あ', 25), string.Join(string.Empty, chunks));
    }

    [Fact]
    public void SplitChunks_BlankLinesDoNotFormChunks()
    {
        var chunks = TermImportService.SplitChunks("\n\n   \nアーリ 阿璃\n\n", 4000);

        var chunk = Assert.Single(chunks);
        Assert.Equal("アーリ 阿璃", chunk);
    }

    [Fact]
    public void SplitChunks_DefaultChunkLengthIs4000()
    {
        var chunks = TermImportService.SplitChunks(new string('x', 8000));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(4000, chunks[0].Length);
        Assert.Equal(4000, chunks[1].Length);
    }

    [Fact]
    public void SplitChunks_CapsLinesPerChunk()
    {
        var text = string.Join("\n", Enumerable.Range(1, 65).Select(i => $"テスト{i} 測試{i}"));

        var chunks = TermImportService.SplitChunks(text);

        Assert.Equal([30, 30, 5], chunks.Select(c => c.Split('\n').Length).ToArray());
    }

    /// <summary>同步收集進度訊息的 IProgress（測試用，不經 SynchronizationContext）。</summary>
    private sealed class ProgressCollector(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}
