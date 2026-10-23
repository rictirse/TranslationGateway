using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>合併計畫（<see cref="GlossaryMerger.Plan"/> 的結果）：兩個 store 共用的純邏輯，不碰資料庫。</summary>
public sealed record GlossaryMergePlan(
    IReadOnlyList<GlossaryTerm> TermsToMove,
    IReadOnlyList<(long DroppedTermId, long KeptTermId)> DroppedTerms,
    string? NewTargetAliases,
    int AddedAliases,
    long? NewTargetParentId,
    string? NewTargetChannelId,
    string? NewTargetChannelHandle,
    string? NewTargetDescription,
    string? NewTargetSourceUrl);

/// <summary>詞彙卡合併規則（純邏輯、不碰資料庫）：名詞衝突以既有卡為主、搬過去的名詞改待審、別名與空欄位補進既有卡。</summary>
public static class GlossaryMerger
{
    /// <summary>把 source 卡併進 target 卡的計畫：名詞衝突（SameName 或 Trim 後相同）以 target 為主，
    /// source 內部對到同一個新寫法時第二個對應到第一個；不衝突的名詞搬過去（保留原 Id、Status 改 Pending）；
    /// source 卡名與別名併進 target 別名（正規化、去重複、去掉與 target 卡名相同的行）。</summary>
    public static GlossaryMergePlan Plan(GlossaryCard source, IReadOnlyList<GlossaryTerm> sourceTerms,
        GlossaryCard target, IReadOnlyList<GlossaryTerm> targetTerms)
    {
        var termsToMove = new List<GlossaryTerm>();
        var droppedTerms = new List<(long DroppedTermId, long KeptTermId)>();

        foreach (var term in sourceTerms)
        {
            // 與 target 既有名詞衝突：丟掉 source 的，記下對應的既有名詞 Id
            var targetConflict = targetTerms.FirstOrDefault(existing => Conflicts(term, existing));
            if (targetConflict is not null)
            {
                droppedTerms.Add((term.Id, targetConflict.Id));
                continue;
            }

            // source 卡內部衝突：對到同一個新寫法時，第二個對應到第一個（搬過去後的同一個 Id）
            var innerConflict = termsToMove.FirstOrDefault(moved => Conflicts(term, moved));
            if (innerConflict is not null)
            {
                droppedTerms.Add((term.Id, innerConflict.Id));
                continue;
            }

            // 不衝突的搬過去：保留原 Id，Status 一律改成 Pending，其他欄位不動
            termsToMove.Add(new GlossaryTerm
            {
                Id = term.Id,
                CardId = target.Id,
                Source = term.Source,
                Target = term.Target,
                Aliases = term.Aliases,
                Category = term.Category,
                Note = term.Note,
                Status = "Pending",
            });
        }

        var oldAliases = GlossaryCatalog.NormalizeCardAliases(target.Aliases, target.Name);
        var newAliases = GlossaryCatalog.NormalizeCardAliases(
            $"{target.Aliases ?? string.Empty}\n{source.Name}\n{source.Aliases ?? string.Empty}", target.Name);

        return new GlossaryMergePlan(
            termsToMove,
            droppedTerms,
            newAliases,
            LineCount(newAliases) - LineCount(oldAliases),
            PickParentId(source, target),
            PickValue(target.ChannelId, source.ChannelId),
            PickValue(target.ChannelHandle, source.ChannelHandle),
            PickValue(target.Description, source.Description),
            PickValue(target.SourceUrl, source.SourceUrl));
    }

    /// <summary>名詞衝突：Source 經 <see cref="GlossaryNameNormalizer.SameName"/>（全形半形、平假名片假名互通）視為相同，
    /// 或 Trim 後完全相同（UNIQUE(CardId, Source)）。</summary>
    private static bool Conflicts(GlossaryTerm sourceTerm, GlossaryTerm otherTerm) =>
        GlossaryNameNormalizer.SameName(sourceTerm.Source, otherTerm.Source) ||
        string.Equals(sourceTerm.Source.Trim(), otherTerm.Source.Trim(), StringComparison.Ordinal);

    /// <summary>別名行數（正規化後的 null 視為 0 行）。</summary>
    private static int LineCount(string? aliases) =>
        string.IsNullOrEmpty(aliases) ? 0 : aliases.Split('\n').Length;

    /// <summary>卡片欄位以 target 為主：target 是 null 或空白時才用 source 的值。</summary>
    private static string? PickValue(string? targetValue, string? sourceValue) =>
        string.IsNullOrWhiteSpace(targetValue) ? sourceValue : targetValue;

    /// <summary>ParentId 以 target 為主（target 沒有才用 source 的）；指到 target 或 source 自己時視為 null。</summary>
    private static long? PickParentId(GlossaryCard source, GlossaryCard target)
    {
        var parentId = target.ParentId ?? source.ParentId;
        return parentId == target.Id || parentId == source.Id ? null : parentId;
    }
}
