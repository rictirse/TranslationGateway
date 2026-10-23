using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Gateway.ViewModels;

/// <summary>ParentId 下拉的選項：「（無）」或一張團體卡。</summary>
public sealed record GlossaryParentOption(long? Id, string Name);

/// <summary>詞彙卡審核頁：檢視、編輯資料庫裡的詞彙卡與名詞，核准／拒絕後按儲存寫回。</summary>
public partial class GlossaryReviewViewModel : ObservableObject
{
    private readonly IGlossaryStore _store;
    private readonly DashboardViewModel _dashboard;
    private readonly TermImportService _termImport;
    private readonly ReviewCountSignal _reviewSignal;

    /// <summary>全部卡片項目（未篩選）。</summary>
    private List<GlossaryReviewCardItem> _allCards = [];

    /// <summary>全部卡片依顯示順序排好（上層卡在前、子卡緊接在後；同層依種類、卡名）。</summary>
    private List<GlossaryReviewCardItem> _orderedCards = [];

    private readonly Dictionary<long, GlossaryReviewCardItem> _cardsById = [];

    public GlossaryReviewViewModel(IGlossaryStore store, DashboardViewModel dashboard, TermImportService termImport,
        ReviewCountSignal reviewSignal)
    {
        _store = store;
        _dashboard = dashboard;
        _termImport = termImport;
        _reviewSignal = reviewSignal;
        Load();
    }

    /// <summary>篩選後的卡片清單。</summary>
    public ObservableCollection<GlossaryReviewCardItem> Cards { get; } = [];

    /// <summary>ParentId 下拉的選項：所有 Group 卡，前面加一個「（無）」。</summary>
    public ObservableCollection<GlossaryParentOption> ParentOptions { get; } = [];

    public IReadOnlyList<string> KindFilterOptions { get; } = ["全部", "角色", "團體", "遊戲", "共通用語"];

    /// <summary>卡片狀態篩選選項；預設「有待審名詞」。</summary>
    public IReadOnlyList<string> CardStatusFilterOptions { get; } = ["有待審名詞", "全部", "已核准", "已拒絕"];

    public IReadOnlyList<string> TermStatusFilterOptions { get; } = ["全部", "待審", "核准", "拒絕"];

    /// <summary>左欄搜尋文字：比對卡名或卡內任一名詞的 Source／Target／Aliases。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _kindFilter = "全部";

    [ObservableProperty]
    private string _cardStatusFilter = "有待審名詞";

    [ObservableProperty]
    private GlossaryReviewCardItem? _selectedCard;

    /// <summary>名詞表的狀態篩選。</summary>
    [ObservableProperty]
    private string _termStatusFilter = "全部";

    /// <summary>名詞表的搜尋文字。</summary>
    [ObservableProperty]
    private string _termSearchText = string.Empty;

    /// <summary>名詞表目前選取的那一列（沒有勾選名詞時，命令作用在這一列）。</summary>
    [ObservableProperty]
    private GlossaryReviewTermItem? _selectedTerm;

    /// <summary>最近一次操作的結果訊息（儲存成功／失敗等）。</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>是／否確認對話框的進入點；測試可替換。回傳 true 表示「是」。</summary>
    public Func<string, bool> ConfirmDialog { get; set; } = _ => true;

    /// <summary>儲存的卡有在 Dashboard 生效中，需要按「套用到翻譯」才會反映到 prompt。</summary>
    [ObservableProperty]
    private bool _needsApplyToTranslation;

    /// <summary>右側名詞表：依狀態與搜尋篩選 SelectedCard.Terms。
    /// 從頭到尾同一個實例，只在換卡、篩選或搜尋改變時以 Clear + Add 重填（依 Id 由小到大）。</summary>
    public ObservableCollection<GlossaryReviewTermItem> VisibleTerms { get; } = [];

    /// <summary>VisibleTerms 非空且全部勾選時為 true；設定時把 VisibleTerms 全部勾選或取消。
    /// 可寫是給標頭 CheckBox 用 TwoWay 綁定：CheckBox 被點擊時會在本機設定 IsChecked，OneWay 綁定會因此失效。</summary>
    public bool AllVisibleSelected
    {
        get => VisibleTerms.Count > 0 && VisibleTerms.All(t => t.IsSelected);
        set
        {
            foreach (var term in VisibleTerms)
            {
                term.IsSelected = value;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>dirty 的卡片數加名詞數。</summary>
    public int DirtyCount =>
        _allCards.Count(c => c.IsCardFieldsDirty) +
        _allCards.Sum(c => c.Terms.Count(t => t.IsDirty));

    public bool HasUnsavedChanges => DirtyCount > 0;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnKindFilterChanged(string value) => ApplyFilter();

    partial void OnCardStatusFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedCardChanged(GlossaryReviewCardItem? oldValue, GlossaryReviewCardItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnCardPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnCardPropertyChanged;
        }

        RebuildVisibleTerms();
        RebuildMergeCandidates();
        ImportTermsCommand.NotifyCanExecuteChanged();
        ToggleMergePanelCommand.NotifyCanExecuteChanged();
        MergeCardCommand.NotifyCanExecuteChanged();
        DeleteCardCommand.NotifyCanExecuteChanged();
    }

    partial void OnTermStatusFilterChanged(string value) => RebuildVisibleTerms();

    partial void OnTermSearchTextChanged(string value) => RebuildVisibleTerms();

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GlossaryReviewCardItem.IsDirty))
        {
            RefreshDirty();
        }
    }

    /// <summary>名詞內容或狀態改變時只更新 dirty 計數，不重建 VisibleTerms：
    /// DataGrid 正在編輯儲存格時換掉 ItemsSource 會中斷編輯（甚至擲例外），也會遺失選取列。
    /// 篩選結果要到篩選條件改變、換卡或重新載入時才更新。</summary>
    private void OnTermPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GlossaryReviewTermItem.IsDirty))
        {
            RefreshDirty();
        }
        else if (e.PropertyName == nameof(GlossaryReviewTermItem.IsSelected) &&
                 sender is GlossaryReviewTermItem term && VisibleTerms.Contains(term))
        {
            OnPropertyChanged(nameof(AllVisibleSelected));
        }
    }

    private void RefreshDirty()
    {
        OnPropertyChanged(nameof(DirtyCount));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /// <summary>以 Clear + Add 重填 VisibleTerms（依 Id 由小到大）；名詞的勾選、狀態、內容改變時不呼叫。</summary>
    private void RebuildVisibleTerms()
    {
        VisibleTerms.Clear();
        if (SelectedCard is not null)
        {
            foreach (var term in FilterTerms(SelectedCard.Terms))
            {
                VisibleTerms.Add(term);
            }
        }

        OnPropertyChanged(nameof(AllVisibleSelected));
    }

    // ===== 載入 =====

    /// <summary>（重新）從資料庫讀取所有卡片與名詞，重建清單；未儲存的變更會被放棄。</summary>
    public void Load()
    {
        var selectedId = SelectedCard?.Id;

        var cards = _store.GetAllGlossaryCards();
        var terms = _store.GetAllGlossaryTerms();
        var termsByCard = terms
            .GroupBy(t => t.CardId)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Id).ToList());

        var activeIds = _dashboard.ActiveGlossaryIds;
        _allCards = cards.Select(card =>
        {
            GlossaryReviewCardItem item = new NotifyingGlossaryReviewCardItem(card)
            {
                IsActive = activeIds.Contains(card.Id),
            };
            if (termsByCard.TryGetValue(card.Id, out var cardTerms))
            {
                foreach (var term in cardTerms)
                {
                    var reviewTerm = new GlossaryReviewTermItem(term);
                    reviewTerm.PropertyChanged += OnTermPropertyChanged;
                    item.Terms.Add(reviewTerm);
                }
            }

            item.PropertyChanged += OnCardPropertyChanged;
            return item;
        }).ToList();

        _cardsById.Clear();
        foreach (var card in _allCards)
        {
            _cardsById[card.Id] = card;
        }

        foreach (var card in _allCards)
        {
            card.Indent = card.Model.ParentId is { } parent && _cardsById.ContainsKey(parent);
        }

        _orderedCards = OrderCards(_allCards);

        ParentOptions.Clear();
        ParentOptions.Add(new GlossaryParentOption(null, "（無）"));
        foreach (var card in _orderedCards.Where(c => c.Model.Kind == GlossaryKind.Group))
        {
            ParentOptions.Add(new GlossaryParentOption(card.Id, card.Name));
        }

        ApplyFilter();
        if (selectedId is { } id && Cards.FirstOrDefault(c => c.Id == id) is { } restored)
        {
            SelectedCard = restored;
        }

        RefreshDirty();
    }

    /// <summary>上層卡在前、子卡緊接在其上層卡之後；同層依種類（角色→團體→遊戲→共通用語→其他）再依卡名。</summary>
    private static List<GlossaryReviewCardItem> OrderCards(List<GlossaryReviewCardItem> cards)
    {
        var childrenByParent = cards
            .Where(c => c.Model.ParentId is not null)
            .GroupBy(c => c.Model.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var ordered = new List<GlossaryReviewCardItem>(cards.Count);
        var roots = cards.Where(c => c.Model.ParentId is not { } parent || !childrenByParent.ContainsKey(parent));
        foreach (var root in roots.OrderBy(SortKindRank).ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            AppendWithChildren(ordered, root, childrenByParent);
        }

        return ordered;
    }

    private static void AppendWithChildren(
        List<GlossaryReviewCardItem> ordered,
        GlossaryReviewCardItem card,
        Dictionary<long, List<GlossaryReviewCardItem>> childrenByParent)
    {
        ordered.Add(card);
        if (!childrenByParent.TryGetValue(card.Id, out var children))
        {
            return;
        }

        foreach (var child in children.OrderBy(SortKindRank).ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            AppendWithChildren(ordered, child, childrenByParent);
        }
    }

    private static int SortKindRank(GlossaryReviewCardItem card) => card.Model.Kind switch
    {
        GlossaryKind.Character => 0,
        GlossaryKind.Group => 1,
        GlossaryKind.Game => 2,
        GlossaryKind.Common => 3,
        _ => 4,
    };

    // ===== 篩選 =====

    private void ApplyFilter()
    {
        var selectedId = SelectedCard?.Id;
        var query = (SearchText ?? string.Empty).Trim();
        var kind = KindFilter switch
        {
            "角色" => GlossaryKind.Character,
            "團體" => GlossaryKind.Group,
            "遊戲" => GlossaryKind.Game,
            "共通用語" => GlossaryKind.Common,
            _ => null,
        };

        var keep = _allCards
            .Where(card => query.Length == 0 || CardMatches(card, query))
            .Where(card => kind is null || card.Model.Kind == kind)
            .Where(card => CardStatusFilter switch
            {
                "全部" => true,
                "已核准" => card.Status == GlossaryReviewTermItem.ApprovedStatus,
                "已拒絕" => card.Status == GlossaryReviewTermItem.RejectedStatus,
                _ => card.HasPending,
            })
            .Select(card => card.Id)
            .ToHashSet();

        // 上層卡被篩掉、子卡留在清單裡時，上層卡也要一起顯示，讓分組完整
        foreach (var card in _allCards)
        {
            if (!keep.Contains(card.Id))
            {
                continue;
            }

            var parent = card.Model.ParentId;
            while (parent is { } parentId && _cardsById.TryGetValue(parentId, out var parentCard))
            {
                keep.Add(parentCard.Id);
                parent = parentCard.Model.ParentId;
            }
        }

        Cards.Clear();
        foreach (var card in _orderedCards)
        {
            if (keep.Contains(card.Id))
            {
                Cards.Add(card);
            }
        }

        SelectedCard = selectedId is { } id && Cards.FirstOrDefault(c => c.Id == id) is { } same
            ? same
            : Cards.FirstOrDefault();
    }

    /// <summary>比對卡名或卡內任一名詞的 Source／Target／Aliases（別名逐行）。</summary>
    private bool CardMatches(GlossaryReviewCardItem card, string query) =>
        LooseContains(card.Name, query) ||
        AliasLines(card.Aliases).Any(alias => LooseContains(alias, query)) ||
        card.Terms.Any(term =>
            LooseContains(term.Source, query) ||
            LooseContains(term.Target, query) ||
            AliasLines(term.Aliases).Any(alias => LooseContains(alias, query)));

    /// <summary>名詞表篩選：狀態（全部／待審／核准／拒絕）＋搜尋（Source／Target／Aliases）。</summary>
    private List<GlossaryReviewTermItem> FilterTerms(
        ObservableCollection<GlossaryReviewTermItem> terms)
    {
        var query = (TermSearchText ?? string.Empty).Trim();
        IEnumerable<GlossaryReviewTermItem> filtered = TermStatusFilter switch
        {
            "待審" => terms.Where(t => t.Status == GlossaryReviewTermItem.PendingStatus),
            "核准" => terms.Where(t => t.Status == GlossaryReviewTermItem.ApprovedStatus),
            "拒絕" => terms.Where(t => t.Status == GlossaryReviewTermItem.RejectedStatus),
            _ => terms,
        };

        if (query.Length > 0)
        {
            filtered = filtered.Where(term =>
                LooseContains(term.Source, query) ||
                LooseContains(term.Target, query) ||
                AliasLines(term.Aliases).Any(alias => LooseContains(alias, query)));
        }

        return filtered.OrderBy(t => t.Id).ToList();
    }

    /// <summary>比對方式與 GlossaryCatalog 相同：不分大小寫、全形半形互通、平假名片假名互通。</summary>
    private static bool LooseContains(string text, string query) =>
        query.Length > 0 &&
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType) >= 0;

    /// <summary>Aliases 以行分隔，逐行比對；null 或空行略過。</summary>
    private static IEnumerable<string> AliasLines(string? aliases)
    {
        if (string.IsNullOrEmpty(aliases))
        {
            return [];
        }

        return aliases
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
    }

    // ===== 名詞狀態命令 =====

    /// <summary>全選／全不選：VisibleTerms 全部已勾選時全部取消，否則全部勾選；只作用在 VisibleTerms。</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        AllVisibleSelected = !AllVisibleSelected;
    }

    /// <summary>把 VisibleTerms 中 IsSelected 的名詞改成 Approved；沒有勾選時改目前選取的那一列。</summary>
    [RelayCommand]
    private void ApproveSelectedTerms() => SetSelectedTermsStatus(GlossaryReviewTermItem.ApprovedStatus);

    /// <summary>把 VisibleTerms 中 IsSelected 的名詞改成 Rejected；沒有勾選時改目前選取的那一列。</summary>
    [RelayCommand]
    private void RejectSelectedTerms() => SetSelectedTermsStatus(GlossaryReviewTermItem.RejectedStatus);

    /// <summary>把 VisibleTerms 中 IsSelected 的名詞改回 Pending；沒有勾選時改目前選取的那一列。</summary>
    [RelayCommand]
    private void ResetSelectedTerms() => SetSelectedTermsStatus(GlossaryReviewTermItem.PendingStatus);

    private void SetSelectedTermsStatus(string status)
    {
        var targets = VisibleTerms.Where(t => t.IsSelected).ToList();
        if (targets.Count == 0 && SelectedTerm is { } selected && VisibleTerms.Contains(selected))
        {
            targets.Add(selected);
        }

        foreach (var term in targets)
        {
            term.Status = status;
        }
    }

    // ===== 刪除名詞（立即寫入資料庫，先經 ConfirmDialog 確認） =====

    /// <summary>從資料庫刪除勾選的名詞（沒有勾選時刪除目前選取的那一列）與它們的聽錯字修正；
    /// 確認後立即刪除，無法復原。未存檔的新列（Id 0）只從畫面移除，不送資料庫。</summary>
    [RelayCommand]
    private void DeleteSelectedTerms()
    {
        var targets = VisibleTerms.Where(t => t.IsSelected).ToList();
        if (targets.Count == 0 && SelectedTerm is { } selected && VisibleTerms.Contains(selected))
        {
            targets.Add(selected);
        }

        if (targets.Count == 0)
        {
            return;
        }

        // 確認訊息最多列 5 個（原文 → 譯名），超過時最後加「…等 N 個」
        var preview = string.Join("\n", targets.Take(5).Select(t => $"{t.Source} → {t.Target}"));
        if (targets.Count > 5)
        {
            preview += $"\n…等 {targets.Count} 個";
        }

        if (!ConfirmDialog(
                $"刪除 {targets.Count} 個名詞？\n{preview}\n會從資料庫刪除（含聽錯字修正），無法復原。"))
        {
            return;
        }

        // Id 0 的列還不在資料庫，直接從畫面移除；其餘先寫進資料庫，失敗時什麼都不動（連 Id 0 的列也不移除）
        var existingIds = targets.Where(t => t.Id != 0).Select(t => t.Id).ToList();
        if (existingIds.Count > 0)
        {
            try
            {
                _store.DeleteGlossaryTerms(existingIds);
            }
            catch (Exception ex)
            {
                StatusMessage = $"刪除失敗：{ex.Message}";
                return;
            }
        }

        // 只移除被刪的列，不重新載入：其他未儲存的修改要保留
        // targets 來自 VisibleTerms（＝選取卡的名詞），SelectedCard 一定有值
        var fallbackCard = SelectedCard!;
        var changedCards = new HashSet<GlossaryReviewCardItem>();
        foreach (var term in targets)
        {
            var card = _cardsById.TryGetValue(term.CardId, out var owner) ? owner : fallbackCard;
            card.Terms.Remove(term);  // 卡片自己的訂閱由 CollectionChanged 取消
            term.PropertyChanged -= OnTermPropertyChanged;
            changedCards.Add(card);
        }

        if (SelectedTerm is not null && targets.Contains(SelectedTerm))
        {
            SelectedTerm = null;
        }

        RebuildVisibleTerms();
        RefreshDirty();
        foreach (var card in changedCards.OfType<NotifyingGlossaryReviewCardItem>())
        {
            card.RaiseTermCountsChanged();
        }

        if (existingIds.Count > 0)
        {
            // 有寫進資料庫：待審數量可能變了、型錄內容變了
            _reviewSignal.Notify();
            _dashboard.ReloadGlossaryCatalogOnly();
            NeedsApplyToTranslation |= changedCards.Any(c => _dashboard.ActiveGlossaryIds.Contains(c.Id));
        }

        StatusMessage = $"已刪除 {targets.Count} 個名詞";
    }

    // ===== 卡片狀態命令 =====

    /// <summary>核准整張卡：Pending 名詞一起改成 Approved，Rejected 的名詞不動。</summary>
    [RelayCommand]
    private void ApproveCard()
    {
        if (SelectedCard is not { } card)
        {
            return;
        }

        card.Status = GlossaryReviewTermItem.ApprovedStatus;
        foreach (var term in card.Terms.Where(t => t.Status == GlossaryReviewTermItem.PendingStatus))
        {
            term.Status = GlossaryReviewTermItem.ApprovedStatus;
        }
    }

    /// <summary>拒絕整張卡（只改卡片 Status，名詞不動）。</summary>
    [RelayCommand]
    private void RejectCard()
    {
        if (SelectedCard is not { } card)
        {
            return;
        }

        card.Status = GlossaryReviewTermItem.RejectedStatus;
    }

    // ===== 合併到既有卡／刪除整張卡（立即寫入資料庫，先經 ConfirmDialog 確認） =====

    /// <summary>合併面板的開關。</summary>
    [ObservableProperty]
    private bool _isMergePanelOpen;

    /// <summary>合併面板的搜尋文字（比對卡名、卡片別名、名詞的 Source／Target）。</summary>
    [ObservableProperty]
    private string _mergeSearchText = string.Empty;

    /// <summary>合併面板選定的目標卡。</summary>
    [ObservableProperty]
    private GlossaryReviewCardItem? _mergeTarget;

    /// <summary>合併面板的候選卡：從 _allCards 排除選取的卡，依搜尋與種類篩選（最多 50 筆）。</summary>
    public ObservableCollection<GlossaryReviewCardItem> MergeCandidates { get; } = [];

    partial void OnMergeSearchTextChanged(string value) => RebuildMergeCandidates();

    partial void OnIsMergePanelOpenChanged(bool value) => ToggleMergePanelCommand.NotifyCanExecuteChanged();

    partial void OnMergeTargetChanged(GlossaryReviewCardItem? oldValue, GlossaryReviewCardItem? newValue)
        => MergeCardCommand.NotifyCanExecuteChanged();

    /// <summary>開關合併面板；沒有選取的卡時不能開。</summary>
    [RelayCommand(CanExecute = nameof(CanToggleMergePanel))]
    private void ToggleMergePanel()
    {
        if (IsMergePanelOpen)
        {
            IsMergePanelOpen = false;
        }
        else if (SelectedCard is not null)
        {
            IsMergePanelOpen = true;
        }
    }

    private bool CanToggleMergePanel() => IsMergePanelOpen || SelectedCard is not null;

    /// <summary>重建合併候選清單：搜尋文字空白時列出與選取卡同種類的卡，否則沿用左欄搜尋的比對方式；
    /// 依種類、卡名排序，最多 50 筆。選取卡或搜尋文字改變、重新載入後都會重建。</summary>
    private void RebuildMergeCandidates()
    {
        MergeCandidates.Clear();
        if (SelectedCard is not { } selected)
        {
            MergeTarget = null;
            return;
        }

        var query = (MergeSearchText ?? string.Empty).Trim();
        foreach (var card in _allCards
                     .Where(c => !ReferenceEquals(c, selected))
                     .Where(c => query.Length == 0
                         ? c.Model.Kind == selected.Model.Kind
                         : CardMatches(c, query))
                     .OrderBy(SortKindRank)
                     .ThenBy(c => c.Name, StringComparer.Ordinal)
                     .Take(50))
        {
            MergeCandidates.Add(card);
        }

        if (MergeTarget is not null && !MergeCandidates.Contains(MergeTarget))
        {
            MergeTarget = null;
        }
    }

    /// <summary>把選取的卡併進 MergeTarget：名詞衝突以目標卡為主、搬過去的改待審、來源卡名變成別名，來源卡刪除。</summary>
    [RelayCommand(CanExecute = nameof(CanMergeCard))]
    private void MergeCard()
    {
        if (SelectedCard is not { } source || MergeTarget is not { } target || source.Id == target.Id)
        {
            return;
        }

        if (HasUnsavedChanges)
        {
            StatusMessage = "請先儲存或放棄未儲存的變更，再合併";
            return;
        }

        // 先用兩張卡現在的 Model 與名詞算好計畫，確認訊息裡的數字才會跟實際合併一致
        var plan = GlossaryMerger.Plan(
            source.ToModel(), source.Terms.Select(t => t.ToModel()).ToList(),
            target.ToModel(), target.Terms.Select(t => t.ToModel()).ToList());
        if (!ConfirmDialog(
                $"把「{source.Name}」併進「{target.Name}」？\n" +
                $"・名詞：原文和「{target.Name}」重複的以既有的為主，其他 {plan.TermsToMove.Count} 個搬過去並改成待審\n" +
                $"・「{source.Name}」會加進「{target.Name}」的別名\n" +
                $"・開台紀錄改成指向「{target.Name}」\n" +
                $"・合併後「{source.Name}」會被刪除，無法復原"))
        {
            return;
        }

        GlossaryMergeResult result;
        try
        {
            result = _store.MergeGlossaryCards(source.Id, target.Id);
        }
        catch (Exception ex)
        {
            StatusMessage = $"合併失敗：{ex.Message}";
            return;
        }

        _reviewSignal.Notify();
        Load();
        // 選取改成目標卡（Load 會先試圖還原來源卡，但它已經不存在）
        if (Cards.FirstOrDefault(c => c.Id == target.Id) is { } merged)
        {
            SelectedCard = merged;
        }

        _dashboard.ReloadGlossaryCatalogOnly();
        IsMergePanelOpen = false;
        MergeSearchText = string.Empty;
        MergeTarget = null;
        StatusMessage =
            $"已把「{source.Name}」併進「{target.Name}」：搬過去 {result.MovedTerms} 個名詞（待審）、" +
            $"重複丟掉 {result.DroppedTerms} 個、新增 {result.AddedAliases} 個別名";

        // 來源或目標卡生效中：prompt 還是舊的內容，要再按「套用到翻譯」
        var activeIds = _dashboard.ActiveGlossaryIds;
        NeedsApplyToTranslation |= activeIds.Contains(source.Id) || activeIds.Contains(target.Id);
    }

    private bool CanMergeCard() =>
        SelectedCard is not null && MergeTarget is not null && SelectedCard.Id != MergeTarget.Id;

    /// <summary>從資料庫刪除選取的卡與它的名詞、聽錯字修正、開台生效紀錄；不想讓探勘再建卡請改用「拒絕整張卡」。</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteCard))]
    private void DeleteCard()
    {
        if (SelectedCard is not { } card)
        {
            return;
        }

        if (HasUnsavedChanges)
        {
            StatusMessage = "請先儲存或放棄未儲存的變更，再刪除";
            return;
        }

        if (!ConfirmDialog(
                $"刪除「{card.Name}」？\n" +
                $"卡片、{card.Terms.Count} 個名詞和它的聽錯字修正會從資料庫刪除，開台紀錄裡的這張卡也會一起刪掉，無法復原。\n" +
                $"只是不想讓資料探勘再建這張卡的話，請改用「拒絕整張卡」。"))
        {
            return;
        }

        try
        {
            _store.DeleteGlossaryCard(card.Id);
        }
        catch (Exception ex)
        {
            StatusMessage = $"刪除失敗：{ex.Message}";
            return;
        }

        _reviewSignal.Notify();
        Load();
        _dashboard.ReloadGlossaryCatalogOnly();
        StatusMessage = $"已刪除「{card.Name}」";

        // 被刪的卡生效中：prompt 還引用它的名詞，要再按「套用到翻譯」
        NeedsApplyToTranslation |= _dashboard.ActiveGlossaryIds.Contains(card.Id);
    }

    private bool CanDeleteCard() => SelectedCard is not null;

    // ===== 貼上資料整理 =====

    /// <summary>貼上的網路資料內容（整理成功後清空）。</summary>
    [ObservableProperty]
    private string _importText = string.Empty;

    /// <summary>給模型的補充說明（例如資料格式）；成功後保留，下次貼同類資料可沿用。</summary>
    [ObservableProperty]
    private string _importInstruction = string.Empty;

    /// <summary>整理執行中。</summary>
    [ObservableProperty]
    private bool _isImporting;

    /// <summary>整理的進度與結果訊息。</summary>
    [ObservableProperty]
    private string _importStatusText = string.Empty;

    /// <summary>貼上資料整理面板的開關。</summary>
    [ObservableProperty]
    private bool _isImportPanelOpen;

    private CancellationTokenSource? _importCts;

    partial void OnImportTextChanged(string value) => ImportTermsCommand.NotifyCanExecuteChanged();

    partial void OnIsImportingChanged(bool value)
    {
        ImportTermsCommand.NotifyCanExecuteChanged();
        CancelImportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>開關貼上資料整理面板。</summary>
    [RelayCommand]
    private void ToggleImportPanel() => IsImportPanelOpen = !IsImportPanelOpen;

    /// <summary>把貼上的資料交給知識庫模型整理成名詞，以未存檔的新列（Id 0、Pending）加進選取的卡。</summary>
    [RelayCommand(CanExecute = nameof(CanImportTerms))]
    private async Task ImportTermsAsync()
    {
        if (SelectedCard is not { } card || IsImporting || !_dashboard.MiningConfigured)
        {
            return;
        }

        // 執行中可能切換選取的卡：記住開始時的那張，結果一律加到它
        var text = ImportText;
        var instruction = ImportInstruction;
        var existingSources = card.Terms.Select(t => t.Source).ToList();
        _importCts?.Dispose();
        _importCts = new CancellationTokenSource();
        var ct = _importCts.Token;
        IsImporting = true;
        ImportStatusText = "整理中…";
        try
        {
            var progress = new Progress<string>(message => ImportStatusText = message);
            var result = await Task.Run(
                () => _termImport.ImportAsync(card.Name, card.Model.Kind, instruction, existingSources, text, progress, ct),
                ct);

            foreach (var proposal in result.Proposals)
            {
                var term = new GlossaryReviewTermItem(new GlossaryTerm
                {
                    Id = 0,
                    CardId = card.Id,
                    Source = proposal.Source,
                    Target = proposal.Target,
                    Aliases = proposal.Aliases,
                    Category = proposal.Category,
                    Note = proposal.Note,
                    Status = GlossaryReviewTermItem.PendingStatus,
                });
                card.Terms.Add(term);
                if (ReferenceEquals(SelectedCard, card))
                {
                    VisibleTerms.Add(term);
                }
            }

            RefreshDirty();
            OnPropertyChanged(nameof(AllVisibleSelected));
            var guessed = result.Proposals.Count(p => p.JapaneseGuessed);
            ImportStatusText = $"新增 {result.Proposals.Count} 個（推測日文 {guessed} 個，請特別確認）、" +
                $"重複略過 {result.Duplicates} 個、丟棄 {result.Dropped} 個；請檢查後按「儲存」";
            if (result.Errors.Count > 0)
            {
                ImportStatusText += $"\n部分失敗：{string.Join("；", result.Errors)}";
            }

            ImportText = string.Empty;
        }
        catch (OperationCanceledException)
        {
            ImportStatusText = "已取消";
        }
        catch (ArgumentException ex)
        {
            ImportStatusText = ex.Message;
        }
        catch (Exception ex)
        {
            ImportStatusText = $"整理失敗：{ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    private bool CanImportTerms() =>
        SelectedCard is not null &&
        !IsImporting &&
        _dashboard.MiningConfigured &&
        !string.IsNullOrWhiteSpace(ImportText) &&
        ImportText.Length <= TermImportService.MaxInputLength;

    /// <summary>取消進行中的整理（已整理好的提案不會加進名詞表）。</summary>
    [RelayCommand(CanExecute = nameof(CanCancelImport))]
    private void CancelImport() => _importCts?.Cancel();

    private bool CanCancelImport() => IsImporting;

    // ===== 儲存／復原／套用 =====

    /// <summary>把所有 dirty 的卡與名詞一次寫回資料庫（同一個交易）；成功後重載並刷新 Dashboard 型錄。</summary>
    [RelayCommand]
    private void Save()
    {
        var cardsToSave = new List<GlossaryCard>();
        var termsToSave = new List<GlossaryTerm>();
        var savedCardIds = new HashSet<long>();

        foreach (var card in _allCards)
        {
            if (card.IsCardFieldsDirty)
            {
                cardsToSave.Add(card.ToModel());
                savedCardIds.Add(card.Id);
            }

            foreach (var term in card.Terms.Where(t => t.IsDirty))
            {
                termsToSave.Add(term.ToModel());
                savedCardIds.Add(card.Id);
            }
        }

        if (cardsToSave.Count == 0 && termsToSave.Count == 0)
        {
            StatusMessage = "沒有未儲存的變更";
            return;
        }

        // 依 Id 排序：同一批更新的執行順序穩定，UNIQUE 衝突的行為才可預期
        var orderedCards = cardsToSave.OrderBy(c => c.Id).ToList();
        var orderedTerms = termsToSave.OrderBy(t => t.CardId).ThenBy(t => t.Id).ToList();
        // 新增（Id 0，例如貼上整理加進來的列）走 SaveGlossaryTermEdits 的 inserted 部分（與抽屜新增名詞同一個機制）；
        // 既有名詞仍與卡片同一個交易更新
        var updatedTerms = orderedTerms.Where(t => t.Id != 0).ToList();
        var insertedTerms = orderedTerms.Where(t => t.Id == 0).ToList();

        try
        {
            _store.SaveGlossaryChanges(orderedCards, updatedTerms);
            if (insertedTerms.Count > 0)
            {
                _store.SaveGlossaryTermEdits([], insertedTerms);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"儲存失敗：{ex.Message}";
            return;
        }

        // 儲存的卡（包含名詞所屬的卡）有生效中時，提示要再按「套用到翻譯」
        // 用 |=：前一次儲存還沒套用時，這次只改非生效卡也不能把提示蓋掉
        NeedsApplyToTranslation |= savedCardIds.Overlaps(_dashboard.ActiveGlossaryIds);

        // 有寫進資料庫（例如核准／拒絕名詞或卡片）：待審數量可能變了
        _reviewSignal.Notify();

        Load();
        _dashboard.ReloadGlossaryCatalogOnly();
        StatusMessage = $"已儲存 {orderedCards.Count} 張卡、{orderedTerms.Count} 個名詞";
    }

    /// <summary>依 Dashboard 目前的生效清單更新各卡的 IsActive（切換到本頁時呼叫）。</summary>
    public void RefreshActiveFlags()
    {
        var activeIds = _dashboard.ActiveGlossaryIds;
        foreach (var card in _allCards)
        {
            card.IsActive = activeIds.Contains(card.Id);
        }

        // 知識庫模型設定可能在設定頁變過，回到本頁時重新評估整理命令
        ImportTermsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>重新載入，放棄所有未儲存的變更。</summary>
    [RelayCommand]
    private void Revert()
    {
        Load();
        StatusMessage = "已放棄未儲存的變更";
    }

    /// <summary>把儲存的內容套用到翻譯：重建 prompt、重置對話並預熱（沿用 Dashboard 的 ReloadGlossary）。</summary>
    [RelayCommand]
    private void ApplyToTranslation()
    {
        _dashboard.ReloadGlossaryCommand.Execute(null);
        NeedsApplyToTranslation = false;
        StatusMessage = "已套用到翻譯";
    }
}

/// <summary>審核頁實際使用的卡片項目：核准數與 Subtitle 是計算屬性，集合變動（刪除名詞）不會自動通知，
/// 這裡開放一個方法讓 ViewModel 在移除名詞後主動通知畫面更新。</summary>
internal sealed class NotifyingGlossaryReviewCardItem(GlossaryCard card) : GlossaryReviewCardItem(card)
{
    /// <summary>名詞集合變動後呼叫：通知核准數、待審狀態、Subtitle 與整卡 dirty。</summary>
    public void RaiseTermCountsChanged()
    {
        OnPropertyChanged(nameof(ApprovedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(IsDirty));
    }
}
