using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

/// <summary>詞彙卡讀取（與 ITranslationStore 同一個資料庫後端一起實作）。</summary>
public interface IGlossaryStore
{
    /// <summary>所有 Status 不是 Rejected 的詞彙卡，依 Id 排序。</summary>
    IReadOnlyList<GlossaryCard> GetGlossaryCards();

    /// <summary>所有 Status 不是 Rejected 的名詞，依 CardId、Id 排序。</summary>
    IReadOnlyList<GlossaryTerm> GetGlossaryTerms();

    /// <summary>所有詞彙卡（含 Rejected），依 Id 排序；審核頁用。</summary>
    IReadOnlyList<GlossaryCard> GetAllGlossaryCards();

    /// <summary>所有名詞（含 Rejected），依 CardId、Id 排序；審核頁用。</summary>
    IReadOnlyList<GlossaryTerm> GetAllGlossaryTerms();

    /// <summary>在同一個交易中更新卡片與名詞（依 Id），並把 UpdatedAt 設為現在（UTC）。
    /// 任何一筆失敗（例如違反 UNIQUE）就整批回復並擲出例外；不吞例外、不回傳預設值。</summary>
    void SaveGlossaryChanges(IReadOnlyList<GlossaryCard> cards, IReadOnlyList<GlossaryTerm> terms);

    /// <summary>在同一個交易中：依 Id 更新既有名詞（Source、Target、Aliases、Category、Note、Status、UpdatedAt = 現在），
    /// 並新增名詞（CardId、Source、Target、Aliases、Category、Note、Status，CreatedAt 用預設值）。
    /// 任何一筆失敗（例如違反 UNIQUE(CardId, Source)）就整批回復並擲出例外；不吞例外。</summary>
    void SaveGlossaryTermEdits(IReadOnlyList<GlossaryTerm> updated, IReadOnlyList<GlossaryTerm> inserted);

    /// <summary>同一個交易中新增一張卡與它的名詞，回傳新卡 Id（寫回 card.Id 與各名詞的 CardId 不在此限）。
    /// 任一筆失敗就整批回復並擲出例外；不吞例外。</summary>
    long CreateGlossaryCard(GlossaryCard card, IReadOnlyList<GlossaryTerm> terms);

    /// <summary>新增或累加名詞候選：(Surface, Kind) 已存在時 Occurrences + 1、更新 LastSeenAt、StreamId、Context、Importance 取較大值；
    /// 不存在時新增。寫入失敗時擲例外。</summary>
    void UpsertTermCandidates(IReadOnlyList<TermCandidate> candidates);

    /// <summary>把 sourceId 卡併進 targetId 卡，再刪掉 sourceId 卡；規則見 GlossaryMerger。
    /// 同一個交易：任一步失敗就整批回復並擲例外。兩個 Id 相同或任一張不存在時擲 ArgumentException。</summary>
    GlossaryMergeResult MergeGlossaryCards(long sourceId, long targetId);

    /// <summary>刪除一張卡與它的名詞、聽錯字修正、開台生效紀錄；子卡的上層與直播的主播卡改成空。
    /// 同一個交易，失敗擲例外。卡不存在時不動作。</summary>
    void DeleteGlossaryCard(long cardId);

    /// <summary>刪除指定的名詞與它們的聽錯字修正（同一個交易，失敗擲例外）；不存在的 Id 略過。</summary>
    void DeleteGlossaryTerms(IReadOnlyList<long> termIds);

    /// <summary>全部聽錯字修正（Pending 與 Approved），依 CardId、TermId、Wrong 排序。</summary>
    IReadOnlyList<MishearingFix> GetMishearingFixes();

    /// <summary>新增或累加聽錯字修正：同一個 (TermId, Kind, Wrong) 已存在時 Occurrences + 1、Evidence 換成新的、UpdatedAt = 現在，Status 不變；
    /// 不存在時新增為 Pending。Wrong 去頭尾空白後少於 2 個字、Wrong 等於 Right、Evidence 是空的，這三種都略過不存。
    /// 回傳實際新增的筆數（不含累加）。整批在一個交易裡，失敗擲例外並全部回復。</summary>
    int UpsertMishearingFixes(IReadOnlyList<MishearingProposal> proposals);

    /// <summary>把指定的 Id 改成 Approved、UpdatedAt = 現在。</summary>
    void ApproveMishearingFixes(IReadOnlyList<long> ids);

    /// <summary>刪除指定的 Id（駁回）。</summary>
    void DeleteMishearingFixes(IReadOnlyList<long> ids);
}
