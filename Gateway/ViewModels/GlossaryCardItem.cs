using CommunityToolkit.Mvvm.ComponentModel;
using Translation.Bridge.Core.Models;

namespace Translation.Gateway.ViewModels;

/// <summary>Dashboard 詞彙卡清單的項目：卡片的顯示資訊與生效狀態。</summary>
public partial class GlossaryCardItem : ObservableObject
{
    public GlossaryCardItem(GlossaryCard card, int termCount, string? parentName)
    {
        Card = card;
        Id = card.Id;
        Name = card.Name;
        KindText = card.Kind switch
        {
            GlossaryKind.Character => "角色",
            GlossaryKind.Group => "團體",
            GlossaryKind.Game => "遊戲",
            GlossaryKind.Common => "共通用語",
            _ => card.Kind
        };
        TermCount = termCount;
        ParentName = parentName;
        Summary = ParentName is null
            ? $"{KindText}・{TermCount} 個名詞"
            : $"{KindText}・{TermCount} 個名詞・{ParentName}";
    }

    public GlossaryCard Card { get; }
    public long Id { get; }
    public string Name { get; }
    /// <summary>角色／團體／遊戲／共通用語（未知種類原樣顯示）。</summary>
    public string KindText { get; }
    public int TermCount { get; }
    public string? ParentName { get; }
    /// <summary>清單第二行顯示的說明。</summary>
    public string Summary { get; }

    [ObservableProperty] private bool _isActive;

    /// <summary>生效原因的顯示文字（主播／所屬團體／連動／上層／沿用／手動／情境）；沒有原因時為種類文字。</summary>
    [ObservableProperty] private string _reasonText = string.Empty;

    /// <summary>上層卡也在清單中的子卡：左邊縮排並加灰色 └。</summary>
    [ObservableProperty] private bool _indent;
}
