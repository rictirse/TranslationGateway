namespace Translation.Bridge.Core.Models;

/// <summary>GLM 整理出的一個名詞提案（尚未存檔）。</summary>
public sealed record TermImportProposal(string Source, string Target, string? Aliases, string? Category, string? Note, bool JapaneseGuessed);

/// <summary>整理結果：提案、每一種丟棄的數量、分段數、tokens、耗時、錯誤（某一段失敗時記錄該段錯誤，其他段照常）。</summary>
public sealed record TermImportResult(
    IReadOnlyList<TermImportProposal> Proposals,
    int Duplicates,
    int Dropped,
    int Chunks,
    int TotalTokens,
    long ElapsedMs,
    IReadOnlyList<string> Errors);
