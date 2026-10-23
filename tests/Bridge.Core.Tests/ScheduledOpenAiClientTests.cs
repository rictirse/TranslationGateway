using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests.Lanes;

/// <summary>ScheduledOpenAiClient：經 LlmLane 排隊；沒有 scope 時 SendAsync／SendChatAsync 是 Realtime、
/// WarmUpAsync 是 Background，有 scope 時照 scope 的優先度。</summary>
public class ScheduledOpenAiClientTests
{
    private static LlmLane CreateLane() =>
        new("翻譯模型", () => 100, new ManualTimeProvider(), NullLogger<LlmLane>.Instance);

    /// <summary>記錄執行順序與當下 LlmPriorityScope 優先度的假翻譯模型。</summary>
    private sealed class RecordingClient : IOpenAiClient
    {
        /// <summary>執行順序（依執行當下記錄）：“send:user”、“chat:user” 或 “warmup”。</summary>
        public List<string> Events { get; } = [];

        public List<LlmPriority?> Priorities { get; } = [];

        public Task<ChatResult> SendAsync(string userText, string systemText, CancellationToken cancellationToken, int? maxOutputTokens = null)
        {
            Events.Add($"send:{userText}");
            Priorities.Add(LlmPriorityScope.Current?.Priority);
            return Task.FromResult(new ChatResult($"回 {userText}", 1, 1, 2, "fake"));
        }

        public Task<ChatResult> SendChatAsync(string systemText, IReadOnlyList<ChatTurn> history, string userText, CancellationToken cancellationToken, int? maxOutputTokens = null)
        {
            Events.Add($"chat:{userText}");
            Priorities.Add(LlmPriorityScope.Current?.Priority);
            return Task.FromResult(new ChatResult($"回 {userText}", 1, 1, 2, "fake"));
        }

        public Task WarmUpAsync(string systemText, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken)
        {
            Events.Add("warmup");
            Priorities.Add(LlmPriorityScope.Current?.Priority);
            return Task.CompletedTask;
        }
    }

    // 沒有 scope 時 SendAsync 是 Realtime：排在 Prefetch 之前。
    [Fact]
    public async Task SendAsync_WithoutScope_RunsBeforePrefetch()
    {
        var inner = new RecordingClient();
        var lane = CreateLane();
        var sut = new ScheduledOpenAiClient(inner, lane);
        var releaseOccupier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 先以 Prefetch 占住執行位置
        var occupier = lane.RunAsync<string>(LlmPriority.Prefetch, async ct =>
        {
            await releaseOccupier.Task;
            return "occupier";
        }, CancellationToken.None);

        Task<ChatResult> withoutScope, withPrefetch;
        withoutScope = sut.SendAsync("x", "sys", CancellationToken.None); // 沒有 scope → Realtime
        using (LlmPriorityScope.Enter(LlmPriority.Prefetch))
        {
            withPrefetch = sut.SendAsync("y", "sys", CancellationToken.None);
        }

        releaseOccupier.SetResult();
        await Task.WhenAll(occupier, withoutScope, withPrefetch);

        // x（Realtime）先於 y（Prefetch）
        Assert.Equal(["send:x", "send:y"], inner.Events);
    }

    // 在 Enter(Prefetch) 裡呼叫時：以 Prefetch 優先度執行。
    [Fact]
    public async Task SendAsync_InsidePrefetchScope_UsesPrefetchPriority()
    {
        var inner = new RecordingClient();
        var sut = new ScheduledOpenAiClient(inner, CreateLane());

        using (LlmPriorityScope.Enter(LlmPriority.Prefetch))
        {
            await sut.SendAsync("x", "sys", CancellationToken.None);
        }

        Assert.Equal([LlmPriority.Prefetch], inner.Priorities);
    }

    // WarmUpAsync 沒有 scope 時是 Background：排在 Prefetch 的對話請求之後。
    [Fact]
    public async Task WarmUpAsync_WithoutScope_RunsAfterPrefetch()
    {
        var inner = new RecordingClient();
        var lane = CreateLane();
        var sut = new ScheduledOpenAiClient(inner, lane);
        var releaseOccupier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 先以 Prefetch 占住執行位置
        var occupier = lane.RunAsync<string>(LlmPriority.Prefetch, async ct =>
        {
            await releaseOccupier.Task;
            return "occupier";
        }, CancellationToken.None);

        var warmUp = sut.WarmUpAsync("sys", [], CancellationToken.None); // 沒有 scope → Background

        Task<ChatResult> chat;
        using (LlmPriorityScope.Enter(LlmPriority.Prefetch))
        {
            chat = sut.SendChatAsync("sys", [], "chat", CancellationToken.None);
        }

        releaseOccupier.SetResult();
        await Task.WhenAll(occupier, warmUp, chat);

        // chat（Prefetch）先執行，WarmUp（Background）後執行
        Assert.Equal(["chat:chat", "warmup"], inner.Events);
    }
}
