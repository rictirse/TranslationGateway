using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Gateway.ViewModels;

/// <summary>審核頁的卡片項目：包住一張 <see cref="GlossaryCard"/> 與其名詞，追蹤編輯與 dirty 狀態。</summary>
public partial class GlossaryReviewCardItem : ObservableObject
{
    /// <summary>載入時的原始值（比對 dirty 用）。</summary>
    private readonly GlossaryCard _original;

    public GlossaryReviewCardItem(GlossaryCard card)
    {
        Model = card;
        _original = Clone(card);
        _name = card.Name;
        _parentId = card.ParentId;
        _channelHandle = card.ChannelHandle;
        _aliases = card.Aliases;
        _description = card.Description;
        _sourceUrl = card.SourceUrl;
        _status = card.Status;
        KindText = card.Kind switch
        {
            GlossaryKind.Character => "角色",
            GlossaryKind.Group => "團體",
            GlossaryKind.Game => "遊戲",
            GlossaryKind.Common => "共通用語",
            _ => card.Kind
        };

        Terms = new ObservableCollection<GlossaryReviewTermItem>();
        Terms.CollectionChanged += OnTermsChanged;
    }

    /// <summary>包住的原始卡片（唯讀）。</summary>
    public GlossaryCard Model { get; }

    public long Id => Model.Id;

    /// <summary>角色／團體／遊戲／共通用語（未知種類原樣顯示）。</summary>
    public string KindText { get; }

    /// <summary>這張卡的名詞（載入後由 ViewModel 填入）。</summary>
    public ObservableCollection<GlossaryReviewTermItem> Terms { get; }

    /// <summary>子卡縮排顯示用：有上層卡就為 true。</summary>
    public bool Indent { get; set; }

    /// <summary>目前在 Dashboard 生效中。</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private long? _parentId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string? _channelHandle;

    /// <summary>別名（一行一個）：所有卡種都可編輯；目前開台比對標題只用遊戲卡，情境比對所有卡種都會用。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string? _aliases;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string? _description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string? _sourceUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _status;

    /// <summary>卡片狀態文字（顯示用）。</summary>
    public string StatusText => Status switch
    {
        GlossaryReviewTermItem.ApprovedStatus => "已核准",
        GlossaryReviewTermItem.RejectedStatus => "已拒絕",
        _ => "待審",
    };

    public int ApprovedCount => Terms.Count(t => t.Status == GlossaryReviewTermItem.ApprovedStatus);

    public int TotalCount => Terms.Count;

    /// <summary>清單第二行顯示的「種類・已核准/全部」。</summary>
    public string Subtitle => $"{KindText}・{ApprovedCount}/{TotalCount}";

    /// <summary>有任何一個名詞是 Pending。</summary>
    public bool HasPending => Terms.Any(t => t.Status == GlossaryReviewTermItem.PendingStatus);

    /// <summary>卡片本身或任一名詞 dirty。</summary>
    public bool IsDirty => IsCardFieldsDirty || Terms.Any(t => t.IsDirty);

    /// <summary>只有卡片自身欄位 dirty（不含名詞）。</summary>
    public bool IsCardFieldsDirty =>
        Name != _original.Name ||
        ParentId != _original.ParentId ||
        NullIfEmpty(ChannelHandle) != _original.ChannelHandle ||
        NullIfEmpty(Aliases) != _original.Aliases ||
        NullIfEmpty(Description) != _original.Description ||
        NullIfEmpty(SourceUrl) != _original.SourceUrl ||
        Status != _original.Status;

    /// <summary>回傳套用目前值的新 <see cref="GlossaryCard"/>（Id、Kind、ChannelId 不變）；別名正規化後存入。</summary>
    public GlossaryCard ToModel() => new()
    {
        Id = Model.Id,
        Kind = Model.Kind,
        Name = Name,
        ParentId = ParentId,
        ChannelId = Model.ChannelId,
        ChannelHandle = NullIfEmpty(ChannelHandle),
        Aliases = GlossaryCatalog.NormalizeCardAliases(Aliases, Name),
        Description = NullIfEmpty(Description),
        SourceUrl = NullIfEmpty(SourceUrl),
        Status = Status,
    };

    private void OnTermsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (GlossaryReviewTermItem term in e.NewItems)
            {
                term.PropertyChanged += OnTermPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (GlossaryReviewTermItem term in e.OldItems)
            {
                term.PropertyChanged -= OnTermPropertyChanged;
            }
        }
    }

    /// <summary>名詞狀態或 dirty 變動時，同步更新核准數、待審狀態與整卡 dirty。</summary>
    private void OnTermPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(GlossaryReviewTermItem.Status) or nameof(GlossaryReviewTermItem.IsDirty)))
        {
            return;
        }

        if (e.PropertyName == nameof(GlossaryReviewTermItem.Status))
        {
            OnPropertyChanged(nameof(ApprovedCount));
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(Subtitle));
        }

        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>TextBox 清空後是空字串；與資料庫的 NULL 視為相同，存回去也存 NULL。</summary>
    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static GlossaryCard Clone(GlossaryCard card) => new()
    {
        Id = card.Id,
        Kind = card.Kind,
        Name = card.Name,
        ParentId = card.ParentId,
        ChannelId = card.ChannelId,
        ChannelHandle = NullIfEmpty(card.ChannelHandle),
        Aliases = NullIfEmpty(card.Aliases),
        Description = NullIfEmpty(card.Description),
        SourceUrl = NullIfEmpty(card.SourceUrl),
        Status = card.Status,
    };
}
