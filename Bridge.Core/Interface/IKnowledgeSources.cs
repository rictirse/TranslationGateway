using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

/// <summary>資料探勘用的知識來源查詢（Wikidata、Wikipedia、Fandom；皆為 MediaWiki API，不需 API Key）。
/// 查詢失敗一律回傳空清單或 null，不擲例外；使用者主動取消時照常拋 OperationCanceledException。</summary>
public interface IKnowledgeSources
{
    Task<IReadOnlyList<WikidataHit>> SearchWikidataAsync(string text, string language = "ja", int limit = 3, CancellationToken ct = default);
    Task<WikidataEntity?> GetWikidataEntityAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SearchWikipediaAsync(string lang, string text, int limit = 3, CancellationToken ct = default);
    Task<WikiPage?> GetWikipediaPageAsync(string lang, string title, CancellationToken ct = default);
    Task<bool> FandomExistsAsync(string subdomain, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SearchFandomAsync(string subdomain, string text, int limit = 3, CancellationToken ct = default);
    Task<WikiPage?> GetFandomPageAsync(string subdomain, string title, CancellationToken ct = default);
}
