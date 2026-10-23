using CommunityToolkit.Mvvm.ComponentModel;

namespace Translation.Gateway.ViewModels;

/// <summary>詞彙卡抽屜「成員」區塊的一列：團體卡的人名名詞與其對應的角色卡；
/// 有卡可勾選生效／取消，沒有卡可一鍵探勘。</summary>
public partial class GlossaryDrawerMemberItem : ObservableObject
{
    public GlossaryDrawerMemberItem(string name, long? cardId, string? cardName, bool isActive)
    {
        Name = name;
        CardId = cardId;
        CardName = cardName;
        _isActive = isActive;
    }

    /// <summary>成員名（人名名詞的 Source；沒對到名詞的角色卡用卡名）。</summary>
    public string Name { get; }

    /// <summary>對應的角色卡 Id；null 代表還沒有角色卡（可探勘）。</summary>
    public long? CardId { get; }

    /// <summary>對應角色卡的卡名。</summary>
    public string? CardName { get; }

    public bool HasCard => CardId is not null;

    /// <summary>角色卡是否生效中（勾選框；使用者切換時由抽屜呼叫 toggleCard）。</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>顯示文字：有卡而卡名與成員名不同時附卡名；沒有卡時標示「沒有角色卡」。</summary>
    public string DisplayText => !HasCard
        ? $"{Name}（沒有角色卡）"
        : CardName != Name ? $"{Name}（{CardName}）"
        : Name;
}
