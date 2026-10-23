namespace Translation.Bridge.Core.Models;

/// <summary>Wikidata 搜尋的單筆結果。</summary>
public sealed record WikidataHit(string Id, string Label, string? Description);

/// <summary>Wikidata 實體的詳細內容（只取日／中／英相關語言與 wiki 站連結）。</summary>
public sealed record WikidataEntity(string Id, IReadOnlyDictionary<string, string> Labels, IReadOnlyDictionary<string, string> Descriptions,
    IReadOnlyDictionary<string, string> Aliases /* 語言 → 以換行串起的別名 */, string? JaWikiTitle, string? ZhWikiTitle, string? EnWikiTitle)
{
    public string Url => $"https://www.wikidata.org/wiki/{Id}";
}

/// <summary>MediaWiki 系網站的頁面內容（維基百科或 Fandom 的導言）。</summary>
/// <remarks>Site：例如 "ja.wikipedia.org"、"virtualyoutuber.fandom.com"。</remarks>
public sealed record WikiPage(string Site, string Title, string Url, string? Extract, string? ZhTitle, string? WikidataId);
