using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>回溯紀錄的過期清除：程式啟動時依設定的保存天數清除 llm_calls 與兩種快照（使用者決定：不定時、只在啟動時執行一次）。</summary>
public sealed class AuditRetentionService
{
    private readonly IAuditStore _store;
    private readonly ISettingsService _settings;
    private readonly ILogger<AuditRetentionService> _logger;
    private readonly TimeProvider _timeProvider;

    public AuditRetentionService(IAuditStore store, ISettingsService settings,
        ILogger<AuditRetentionService> logger, TimeProvider? timeProvider = null)
    {
        _store = store;
        _settings = settings;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>執行一次過期清除：保存天數 0（永不刪除）時不做任何事；回傳清除結果，未清除時回傳 null。</summary>
    public AuditPurgeResult? RunOnce()
    {
        var days = _settings.Current.Audit.RetentionDays;
        if (days <= 0)
        {
            _logger.LogInformation("回溯紀錄保存天數為 0，不清除");
            return null;
        }

        var cutoff = _timeProvider.GetUtcNow() - TimeSpan.FromDays(days);
        var result = _store.PurgeAudit(cutoff);
        _logger.LogInformation(
            "已清除 {Days} 天前的回溯紀錄：模型呼叫 {LlmCalls} 筆、prompt 快照 {PromptSnapshots} 筆、設定快照 {SettingsSnapshots} 筆",
            days, result.LlmCalls, result.PromptSnapshots, result.SettingsSnapshots);
        return result;
    }
}
