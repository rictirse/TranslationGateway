using CommunityToolkit.Mvvm.ComponentModel;
using Translation.Bridge.Core.Models;

namespace Translation.Gateway.ViewModels;

/// <summary>審核頁的名詞列：包住一個 <see cref="GlossaryTerm"/>，可編輯欄位與 dirty 追蹤。</summary>
public partial class GlossaryReviewTermItem : ObservableObject
{
    /// <summary>名詞狀態字串（與資料庫一致）。</summary>
    public const string ApprovedStatus = "Approved";
    public const string RejectedStatus = "Rejected";
    public const string PendingStatus = "Pending";

    /// <summary>載入時的原始值（比對 dirty 用）。</summary>
    private readonly GlossaryTerm _original;

    public GlossaryReviewTermItem(GlossaryTerm term)
    {
        Model = term;
        _original = Clone(term);
        _source = term.Source;
        _target = term.Target;
        _aliases = term.Aliases;
        _category = term.Category;
        _note = term.Note;
        _status = term.Status;
    }

    /// <summary>包住的原始名詞（唯讀）。</summary>
    public GlossaryTerm Model { get; }

    public long Id => Model.Id;

    public long CardId => Model.CardId;

    /// <summary>新增尚未存檔的名詞（抽屜的「＋ 新增名詞」）：Id 為 0。</summary>
    public bool IsNew => Model.Id == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _source;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _target;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string? _aliases;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string? _category;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string? _note;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _status;

    /// <summary>勾選用（批次核准／拒絕）；預設勾選。</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>待審／核准／拒絕。</summary>
    public string StatusText => Status switch
    {
        ApprovedStatus => "核准",
        RejectedStatus => "拒絕",
        _ => "待審",
    };

    /// <summary>任一可編輯屬性和載入時不同就為 true，改回原值就變回 false。
    /// 新增的名詞（IsNew）只要 Source 與 Target 修剪後都不是空的就算 dirty（還沒存進資料庫）。</summary>
    public bool IsDirty => IsNew
        ? Source.Trim().Length > 0 && Target.Trim().Length > 0
        : Source != _original.Source ||
          Target != _original.Target ||
          NullIfEmpty(Aliases) != _original.Aliases ||
          NullIfEmpty(Category) != _original.Category ||
          NullIfEmpty(Note) != _original.Note ||
          Status != _original.Status;

    /// <summary>回傳套用目前值的新 <see cref="GlossaryTerm"/>（Id、CardId 不變）。</summary>
    public GlossaryTerm ToModel() => new()
    {
        Id = Model.Id,
        CardId = Model.CardId,
        Source = Source,
        Target = Target,
        Aliases = NullIfEmpty(Aliases),
        Category = NullIfEmpty(Category),
        Note = NullIfEmpty(Note),
        Status = Status,
    };

    /// <summary>TextBox 清空後是空字串；與資料庫的 NULL 視為相同，存回去也存 NULL。</summary>
    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static GlossaryTerm Clone(GlossaryTerm term) => new()
    {
        Id = term.Id,
        CardId = term.CardId,
        Source = term.Source,
        Target = term.Target,
        Aliases = NullIfEmpty(term.Aliases),
        Category = NullIfEmpty(term.Category),
        Note = NullIfEmpty(term.Note),
        Status = term.Status,
    };
}
