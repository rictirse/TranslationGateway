using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>prompt 版本化的中樞：啟動時把每一種預設 prompt 寫進 prompt_templates（只增不改），
/// 直播 system prompt 改由 DB 裡的版本提供（設定只存選用的版本 Id），設定頁編輯後存檔則存成新的 User 版本。
/// DB 不可用時退回程式預設，不得影響翻譯。</summary>
public sealed class PromptTemplateRegistry
{
    /// <summary>每種預設 prompt：kind → 程式常數內容（雜湊預先算好，FindTemplateId 比對用）。</summary>
    private static readonly (string Kind, string Content, string Hash)[] Defaults =
    [
        (PromptTemplateKinds.LiveSystem, PromptBuilder.DefaultSystemTemplate, AuditHash.Of(PromptBuilder.DefaultSystemTemplate)),
        (PromptTemplateKinds.VodBatch, PromptBuilder.BatchSystemTemplate, AuditHash.Of(PromptBuilder.BatchSystemTemplate)),
        (PromptTemplateKinds.MiningExtract, MiningPrompts.ExtractSystem, AuditHash.Of(MiningPrompts.ExtractSystem)),
        (PromptTemplateKinds.MiningCompose, MiningPrompts.ComposeSystem, AuditHash.Of(MiningPrompts.ComposeSystem)),
        (PromptTemplateKinds.Mishearing, MishearingPrompts.System, AuditHash.Of(MishearingPrompts.System)),
        (PromptTemplateKinds.TermDiscovery, TermDiscoveryPrompts.System, AuditHash.Of(TermDiscoveryPrompts.System)),
        (PromptTemplateKinds.TermImport, TermImportPrompts.System, AuditHash.Of(TermImportPrompts.System)),
        (PromptTemplateKinds.CaptionPreprocess, CaptionPreprocessPrompts.System, AuditHash.Of(CaptionPreprocessPrompts.System)),
    ];

    private readonly IAuditStore _store;
    private readonly ISettingsService _settings;
    private readonly ILogger<PromptTemplateRegistry> _logger;
    private readonly string? _appVersion;
    private readonly object _lock = new();
    private readonly Dictionary<string, long?> _defaultIds = new();
    private long? _liveTemplateId;

    public PromptTemplateRegistry(IAuditStore store, ISettingsService settings,
        ILogger<PromptTemplateRegistry> logger, string? appVersion = null)
    {
        _store = store;
        _settings = settings;
        _logger = logger;
        _appVersion = appVersion;
    }

    /// <summary>直播 system prompt 目前使用的版本 Id；DB 不可用時為 null。</summary>
    public long? LiveTemplateId
    {
        get { lock (_lock) return _liveTemplateId; }
    }

    /// <summary>指定種類的程式預設版本 Id（Initialize 時寫入的那一筆）；沒有時為 null。</summary>
    public long? DefaultTemplateId(string kind)
    {
        lock (_lock) return _defaultIds.GetValueOrDefault(kind);
    }

    /// <summary>直播 system 的全部版本（依 Id 由大到小）。</summary>
    public IReadOnlyList<PromptTemplate> GetLiveVersions() =>
        _store.GetPromptTemplates(PromptTemplateKinds.LiveSystem);

    /// <summary>system prompt 全文剛好等於某個預設 prompt 時，回傳那個版本的 Id，否則為 null（用 AuditHash 比對）。
    /// 直播 system 另外也比對 LiveTemplateId 的內容。</summary>
    public long? FindTemplateId(string content)
    {
        if (string.IsNullOrEmpty(content)) return null;

        var hash = AuditHash.Of(content);
        lock (_lock)
        {
            foreach (var (kind, _, defaultHash) in Defaults)
            {
                if (defaultHash == hash && _defaultIds.GetValueOrDefault(kind) is { } id)
                {
                    return id;
                }
            }

            if (_liveTemplateId is { } liveId)
            {
                var template = _store.GetPromptTemplate(liveId);
                if (template is not null && template.ContentHash == hash)
                {
                    return liveId;
                }
            }
        }

        return null;
    }

    /// <summary>啟動時呼叫一次：把每種預設 prompt 寫進 prompt_templates，再依設定選用的版本套用直播 system prompt。
    /// DB 不可用時記成沒有 Id、退回程式預設，不擲例外。</summary>
    public void Initialize()
    {
        lock (_lock)
        {
            foreach (var (kind, content, _) in Defaults)
            {
                _defaultIds[kind] = _store.EnsurePromptTemplate(kind, content, PromptTemplateOrigins.Default, _appVersion);
            }

            ApplyLiveSelection();

            var followingDefault = _liveTemplateId is null || _liveTemplateId == _defaultIds[PromptTemplateKinds.LiveSystem];
            _logger.LogInformation(
                "prompt 版本已載入：{Count} 種預設，直播 system 使用 #{Id}（{Source}）",
                _defaultIds.Values.Count(id => id is not null),
                _liveTemplateId,
                followingDefault ? "跟著預設" : "自訂");
        }
    }

    /// <summary>設定頁存檔時呼叫：依目前選用的版本與編輯後的內容，決定要存的版本 Id，回傳後由呼叫端寫回 SystemPromptTemplateId。</summary>
    public long? CommitLiveTemplate(long? selectedId, string content)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                // 清空內容視為跟著預設
                selectedId = null;
            }
            else
            {
                var baseline = selectedId is { } id
                    ? _store.GetPromptTemplate(id)?.Content
                    : PromptBuilder.DefaultSystemTemplate;
                if (content != baseline)
                {
                    var newId = _store.EnsurePromptTemplate(
                        PromptTemplateKinds.LiveSystem, content, PromptTemplateOrigins.User, _appVersion);
                    if (newId is null)
                    {
                        _logger.LogWarning("無法寫入新的直播 system prompt 版本，沿用原本的選擇 #{Id}", selectedId);
                    }
                    else
                    {
                        selectedId = newId;
                    }
                }
            }

            _settings.Current.Translation.SystemPromptTemplateId = selectedId;
            ApplyLiveSelection();
            return selectedId;
        }
    }

    /// <summary>依設定選用的版本決定直播 system prompt 的執行期內容與 LiveTemplateId（呼叫端須持有 _lock）。
    /// 讀不到選用的版本（不存在、種類不符、DB 不可用）時改用程式預設，但不改設定裡的 Id，DB 恢復後下次啟動還能用。</summary>
    private void ApplyLiveSelection()
    {
        var selectedId = _settings.Current.Translation.SystemPromptTemplateId;
        var content = PromptBuilder.DefaultSystemTemplate;
        var liveId = _defaultIds.GetValueOrDefault(PromptTemplateKinds.LiveSystem);

        if (selectedId is { } id)
        {
            var template = _store.GetPromptTemplate(id);
            if (template is not null && template.Kind == PromptTemplateKinds.LiveSystem)
            {
                content = template.Content;
                liveId = id;
            }
            else
            {
                _logger.LogWarning(
                    "選用的直播 system prompt 版本 #{Id} 讀不到（不存在、種類不符或資料庫不可用），改用程式預設", id);
            }
        }

        _liveTemplateId = liveId;
        _settings.Current.Translation.SystemPromptTemplate = content;
    }
}
