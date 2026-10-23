using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

/// <summary>翻譯快取儲存後端（SQLite 或 PostgreSQL），同一時間只會使用一種。</summary>
public interface ITranslationStore
{
    /// <summary>給人看的描述，例如「SQLite：C:\...\translation_cache.db」「PostgreSQL：192.168.5.4:5432/LocalLab（youtube_history）」。不得包含密碼。</summary>
    string Description { get; }

    string? GetCache(string source);

    void SaveCache(string source, string target, bool isManual = false);

    /// <summary>寫入快取（is_manual = false）；同原文已存在而且是手動修正時不覆蓋。</summary>
    void SaveCacheUnlessManual(string source, string target);

    /// <summary>刪除指定原文的快取（預設不含手動修正，includeManual 為 true 時一併刪除）；回傳是否有刪除。</summary>
    bool DeleteCache(string source, bool includeManual = false);

    /// <summary>依最後使用時間新到舊，最多 500 筆。</summary>
    IEnumerable<TranslationCacheItem> GetAllCache();

    /// <summary>指定原文的快取 Id；沒有或失敗時回傳 null。</summary>
    long? GetCacheId(string source) => null;
}
