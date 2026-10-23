using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>固定時間的 TimeProvider：cutoff 斷言用（AuditRetentionService 不需要計時器）。</summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

/// <summary>記錄 PurgeAudit 呼叫的假 IAuditStore。</summary>
internal sealed class FakeAuditStore : IAuditStore
{
    public List<DateTimeOffset> Cutoffs { get; } = [];

    public AuditPurgeResult NextResult { get; set; } = new(1, 2, 3);

    public long? EnsurePromptTemplate(string kind, string content, string origin, string? appVersion) => 1;

    public IReadOnlyList<PromptTemplate> GetPromptTemplates(string kind) => [];

    public PromptTemplate? GetPromptTemplate(long id) => null;

    public long? EnsurePromptSnapshot(long? templateId, string content) => 1;

    public long? EnsureSettingsSnapshot(string content) => 1;

    public long? InsertLlmCall(LlmCallRecord record) => 1;

    public AuditPurgeResult PurgeAudit(DateTimeOffset cutoffUtc)
    {
        Cutoffs.Add(cutoffUtc);
        return NextResult;
    }
}

public class AuditRetentionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RunOnce_Default30Days_PurgesWithCutoff30DaysAgo()
    {
        var store = new FakeAuditStore();
        var settings = new AppSettings();
        var sut = new AuditRetentionService(store, new FakeSettingsService(settings),
            NullLogger<AuditRetentionService>.Instance, new FixedTimeProvider(Now));

        var result = sut.RunOnce();

        Assert.NotNull(result);
        Assert.Equal(store.NextResult, result);
        var cutoff = Assert.Single(store.Cutoffs);
        Assert.Equal(Now - TimeSpan.FromDays(30), cutoff);
    }

    [Fact]
    public void RunOnce_RetentionDays7_PurgesWithCutoff7DaysAgo()
    {
        var store = new FakeAuditStore();
        var settings = new AppSettings { Audit = new AuditSettings { RetentionDays = 7 } };
        var sut = new AuditRetentionService(store, new FakeSettingsService(settings),
            NullLogger<AuditRetentionService>.Instance, new FixedTimeProvider(Now));

        sut.RunOnce();

        Assert.Equal(Now - TimeSpan.FromDays(7), Assert.Single(store.Cutoffs));
    }

    [Fact]
    public void RunOnce_RetentionDays0_DoesNotPurgeAndReturnsNull()
    {
        var store = new FakeAuditStore();
        var settings = new AppSettings { Audit = new AuditSettings { RetentionDays = 0 } };
        var sut = new AuditRetentionService(store, new FakeSettingsService(settings),
            NullLogger<AuditRetentionService>.Instance, new FixedTimeProvider(Now));

        var result = sut.RunOnce();

        Assert.Null(result);
        Assert.Empty(store.Cutoffs);
    }

    [Fact]
    public void NewAppSettings_AuditDefaults_FullRecordOffRetention30()
    {
        var audit = new AppSettings().Audit;

        Assert.False(audit.FullRecordEnabled);
        Assert.Equal(30, audit.RetentionDays);
    }
}
