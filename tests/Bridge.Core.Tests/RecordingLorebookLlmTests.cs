using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>RecordingLorebookLlm 裝飾器：成功記 Ok（purpose 取自 scope）、LorebookLlmException 記 Error 後重擲、
/// 取消不記、TestConnectionAsync 不記（真 LlmCallRecorder 搭配記憶體 SpyAuditStore）。</summary>
public class RecordingLorebookLlmTests
{
    private static (RecordingLorebookLlm Sut, SpyAuditStore Store, LlmCallRecorder Recorder) Create(
        LlmJsonResult? result = null, Exception? exception = null)
    {
        var store = new SpyAuditStore();
        var recorder = new LlmCallRecorder(
            store, new FakeSettingsService(), registry: null, new StreamSession(), NullLogger<LlmCallRecorder>.Instance);
        var inner = new FakeLorebookLlm { Result = result, Exception = exception };
        return (new RecordingLorebookLlm(inner, recorder), store, recorder);
    }

    [Fact]
    public async Task CompleteJsonAsync_Success_RecordsOkWithPurposeFromScope()
    {
        var (sut, store, recorder) = Create(result: new LlmJsonResult("{\"ok\":true}", 11, 22, 1234, "glm-4.7"));

        using (LlmCallPurposeScope.Enter(LlmCallPurposes.Mining))
        {
            var result = await sut.CompleteJsonAsync("探勘 system", "探勘 user", TestContext.Current.CancellationToken);
            Assert.Equal("{\"ok\":true}", result.Content);
        }
        await recorder.FlushAsync();

        var call = Assert.Single(store.Calls);
        Assert.Equal(LlmCallPurposes.Mining, call.Purpose);
        Assert.Equal(LlmCallOutcomes.Ok, call.Outcome);
        Assert.Equal("探勘 system", store.PromptSnapshots.Single());
        Assert.Equal("glm-4.7", call.Model);
        Assert.Equal(11, call.PromptTokens);
        Assert.Equal(22, call.CompletionTokens);
        Assert.Equal(1234, call.LatencyMs);
    }

    [Fact]
    public async Task CompleteJsonAsync_LorebookLlmException_RecordsErrorAndRethrows()
    {
        var (sut, store, recorder) = Create(exception: new LorebookLlmException("HTTP 500"));

        using (var scope = LlmCallPurposeScope.Enter(LlmCallPurposes.TermImport))
        {
            await Assert.ThrowsAsync<LorebookLlmException>(
                () => sut.CompleteJsonAsync("匯入 system", "匯入 user", TestContext.Current.CancellationToken));
        }
        await recorder.FlushAsync();

        var call = Assert.Single(store.Calls);
        Assert.Equal(LlmCallPurposes.TermImport, call.Purpose);
        Assert.Equal(LlmCallOutcomes.Error, call.Outcome);
        Assert.NotNull(call.LatencyMs);
        Assert.Null(call.ResponseRaw);
    }

    [Fact]
    public async Task CompleteJsonAsync_Cancelled_NotRecorded()
    {
        var (sut, store, recorder) = Create(exception: new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.CompleteJsonAsync("system", "user", TestContext.Current.CancellationToken));
        await recorder.FlushAsync();

        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task TestConnectionAsync_NotRecorded()
    {
        var (sut, store, recorder) = Create();

        var (success, _) = await sut.TestConnectionAsync(TestContext.Current.CancellationToken);
        await recorder.FlushAsync();

        Assert.True(success);
        Assert.Empty(store.Calls);
    }
}
