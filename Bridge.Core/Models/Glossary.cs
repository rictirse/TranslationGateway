namespace Translation.Bridge.Core.Models;

/// <summary>詞彙卡種類。</summary>
public static class GlossaryKind
{
    public const string Character = "Character";
    public const string Group = "Group";
    public const string Game = "Game";

    /// <summary>共通用語（例如 VTuber、FPS 用語）：不會被開台或情境比對生效，名詞只在字幕提到時以關鍵字提示放進 user prompt。</summary>
    public const string Common = "Common";
}

/// <summary>詞彙卡（角色／團體／遊戲），一張卡含多個名詞。</summary>
public sealed class GlossaryCard
{
    public long Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long? ParentId { get; set; }
    public string? ChannelId { get; set; }
    public string? ChannelHandle { get; set; }

    /// <summary>別名：簡稱與其他寫法（例如 APEX、エペ），每行一個；開台時用來比對直播標題。</summary>
    public string? Aliases { get; set; }

    public string? Description { get; set; }
    public string? SourceUrl { get; set; }
    public string Status { get; set; } = "Pending";
}

/// <summary>StreamCards.Reason 的值。</summary>
public static class StreamCardReason
{
    public const string Host = "Host";
    public const string HostGroup = "HostGroup";
    public const string Title = "Title";

    /// <summary>直播標題比對到的遊戲卡（卡名或別名）。</summary>
    public const string TitleGame = "TitleGame";
    public const string Parent = "Parent";
    public const string Carried = "Carried";
    public const string Manual = "Manual";
    public const string Dynamic = "Dynamic";
    public const string Situation = "Situation";
    public const string Mining = "Mining";
}

/// <summary>開台自動套用比對到的一張卡與原因（StreamCardReason）。</summary>
public sealed record GlossaryCardMatch(GlossaryCard Card, string Reason);

/// <summary>詞彙卡的單一名詞（原文=譯名）。</summary>
public sealed class GlossaryTerm
{
    public long Id { get; set; }
    public long CardId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string? Aliases { get; set; }
    public string? Category { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = "Pending";
}

/// <summary>合併結果：搬過去的名詞數、因衝突丟掉的名詞數、加進既有卡的別名數。</summary>
public sealed record GlossaryMergeResult(int MovedTerms, int DroppedTerms, int AddedAliases);
