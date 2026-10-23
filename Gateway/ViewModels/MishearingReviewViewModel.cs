using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Gateway.ViewModels;

/// <summary>聽錯字修正審核頁：檢視字幕探勘提出的 Whisper 聽錯字修正；核准改 Status，駁回直接刪除。</summary>
public partial class MishearingReviewViewModel : ObservableObject
{
    private readonly IGlossaryStore _store;
    private readonly MishearingFixState _fixState;
    private readonly ReviewCountSignal _reviewSignal;

    /// <summary>全部項目（未篩選），依卡名、名詞原文、錯字排序。</summary>
    private List<MishearingReviewItem> _allItems = [];

    public MishearingReviewViewModel(IGlossaryStore store, MishearingFixState fixState, ReviewCountSignal reviewSignal)
    {
        _store = store;
        _fixState = fixState;
        _reviewSignal = reviewSignal;
        Load();
    }

    /// <summary>篩選後的清單；從頭到尾同一個實例，只在載入、篩選或搜尋改變時以 Clear + Add 重填。</summary>
    public ObservableCollection<MishearingReviewItem> Items { get; } = [];

    /// <summary>狀態篩選選項；預設「待審核」。</summary>
    public IReadOnlyList<string> StatusFilterOptions { get; } = ["待審核", "已核准", "全部"];

    [ObservableProperty]
    private string _statusFilter = "待審核";

    /// <summary>搜尋文字：比對卡名、名詞原文與譯名、錯字。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>筆數統計，例如「待審核 5・已核准 12」。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>資料庫讀取或寫入失敗時的錯誤文字（沒有錯誤時為空字串）。</summary>
    [ObservableProperty]
    private string _errorText = string.Empty;

    /// <summary>是／否確認對話框的進入點；測試可替換。回傳 true 表示「是」。</summary>
    public Func<string, bool> ConfirmDialog { get; set; } = _ => true;

    /// <summary>Items 非空且全部勾選時為 true；設定時把 Items 全部勾選或取消。
    /// 可寫是給標頭 CheckBox 用 TwoWay 綁定：CheckBox 被點擊時會在本機設定 IsChecked，OneWay 綁定會因此失效。</summary>
    public bool AllVisibleSelected
    {
        get => Items.Count > 0 && Items.All(i => i.IsSelected);
        set
        {
            foreach (var item in Items)
            {
                item.IsSelected = value;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>目前顯示的列是否有任何勾選（核准／駁回命令的 CanExecute）。</summary>
    public bool HasCheckedItems => Items.Any(i => i.IsSelected);

    /// <summary>有資料時為 false（空狀態提示的反向）。</summary>
    public bool HasItems => Items.Count > 0;

    partial void OnStatusFilterChanged(string value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>（重新）從資料庫讀取全部修正，對上卡名與名詞後重建清單；讀取失敗時顯示錯誤文字，不擲例外。</summary>
    public void Load()
    {
        List<MishearingFix> fixes;
        IReadOnlyList<GlossaryCard> cards;
        IReadOnlyList<GlossaryTerm> terms;
        try
        {
            fixes = _store.GetMishearingFixes().ToList();
            cards = _store.GetAllGlossaryCards();
            terms = _store.GetAllGlossaryTerms();
        }
        catch (Exception)
        {
            // 讀取失敗不擲例外：保留目前清單，只顯示錯誤文字
            ErrorText = "資料庫讀取失敗，無法載入聽錯字修正。";
            return;
        }

        ErrorText = string.Empty;
        var cardNames = cards.ToDictionary(c => c.Id, c => c.Name);
        var termsById = terms.ToDictionary(t => t.Id, t => t);

        _allItems = fixes
            .Select(fix =>
            {
                var cardName = cardNames.TryGetValue(fix.CardId, out var name)
                    ? name
                    : $"（卡片 #{fix.CardId}）";
                var (termSource, termTarget) = termsById.TryGetValue(fix.TermId, out var term)
                    ? (term.Source, term.Target)
                    : (string.Empty, string.Empty);
                var item = new MishearingReviewItem(fix, cardName, termSource, termTarget);
                item.PropertyChanged += OnItemPropertyChanged;
                return item;
            })
            .OrderBy(i => i.CardName, StringComparer.Ordinal)
            .ThenBy(i => i.TermSource, StringComparer.Ordinal)
            .ThenBy(i => i.Wrong, StringComparer.Ordinal)
            .ToList();

        RefreshCounts();
        ApplyFilter();
    }

    /// <summary>統計列：全部修正的待審／已核准筆數。</summary>
    private void RefreshCounts()
    {
        var pending = _allItems.Count(i => i.Fix.Status != MishearingStatus.Approved);
        var approved = _allItems.Count(i => i.Fix.Status == MishearingStatus.Approved);
        StatusText = $"待審核 {pending}・已核准 {approved}";
    }

    /// <summary>依狀態篩選與搜尋文字重填 Items（同一個集合實例，Clear + Add）。</summary>
    private void ApplyFilter()
    {
        var query = (SearchText ?? string.Empty).Trim();
        IEnumerable<MishearingReviewItem> filtered = StatusFilter switch
        {
            "已核准" => _allItems.Where(i => i.Fix.Status == MishearingStatus.Approved),
            "全部" => _allItems,
            _ => _allItems.Where(i => i.Fix.Status != MishearingStatus.Approved),
        };

        if (query.Length > 0)
        {
            filtered = filtered.Where(i =>
                LooseContains(i.CardName, query) ||
                LooseContains(i.TermSource, query) ||
                LooseContains(i.TermTarget, query) ||
                LooseContains(i.Wrong, query));
        }

        Items.Clear();
        foreach (var item in filtered)
        {
            Items.Add(item);
        }

        RefreshSelectionState();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MishearingReviewItem.IsSelected) &&
            sender is MishearingReviewItem item && Items.Contains(item))
        {
            RefreshSelectionState();
        }
    }

    /// <summary>勾選狀態變更後更新表頭核選與兩個命令的可用狀態。</summary>
    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(AllVisibleSelected));
        OnPropertyChanged(nameof(HasCheckedItems));
        OnPropertyChanged(nameof(HasItems));
        ApproveSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>比對方式與詞彙卡頁相同：不分大小寫、全形半形互通、平假名片假名互通。</summary>
    private static bool LooseContains(string text, string query) =>
        query.Length > 0 &&
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType) >= 0;

    // ===== 命令 =====

    /// <summary>全選／全不選：Items 全部已勾選時全部取消，否則全部勾選。</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        AllVisibleSelected = !AllVisibleSelected;
    }

    /// <summary>把勾選的列改成 Approved，立即寫進資料庫，再重新載入。</summary>
    [RelayCommand(CanExecute = nameof(HasCheckedItems))]
    private void ApproveSelected()
    {
        var ids = Items.Where(i => i.IsSelected).Select(i => i.Fix.Id).ToList();
        if (ids.Count == 0) return;

        try
        {
            _store.ApproveMishearingFixes(ids);
        }
        catch (Exception ex)
        {
            ErrorText = $"核准失敗：{ex.Message}";
            return;
        }

        // 核准後立即重新載入生效清單（Approved 的修正在下一次翻譯前就會套用）
        _fixState.Reload(_store);
        _reviewSignal.Notify();
        Load();
    }

    /// <summary>把勾選的列從資料庫刪除（駁回）；先經 ConfirmDialog 確認。</summary>
    [RelayCommand(CanExecute = nameof(HasCheckedItems))]
    private void DeleteSelected()
    {
        var targets = Items.Where(i => i.IsSelected).ToList();
        if (targets.Count == 0) return;

        if (!ConfirmDialog($"確定要刪除 {targets.Count} 筆聽錯字修正嗎？")) return;

        try
        {
            _store.DeleteMishearingFixes(targets.Select(i => i.Fix.Id).ToList());
        }
        catch (Exception ex)
        {
            ErrorText = $"刪除失敗：{ex.Message}";
            return;
        }

        // 刪除後立即重新載入生效清單（被駁回的修正不再套用）
        _fixState.Reload(_store);
        _reviewSignal.Notify();
        Load();
    }
}
