using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Gateway.ViewModels;

/// <summary>抽屜的分頁：一張卡（本卡或上層卡）與其名詞清單；切換分頁時未儲存的修改保留在清單中。</summary>
public sealed partial class GlossaryDrawerTab : ObservableObject
{
    public GlossaryDrawerTab(long cardId, string titlePrefix, string cardName, string kindText, string statusText)
    {
        CardId = cardId;
        TitlePrefix = titlePrefix;
        CardName = cardName;
        KindText = kindText;
        StatusText = statusText;
        Terms = [];
        // 新增名詞時分頁標題的（名詞數）要跟著更新
        Terms.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public long CardId { get; }
    /// <summary>標題前綴：「本卡」或「上層：{上層卡名}」。</summary>
    public string TitlePrefix { get; }
    public string CardName { get; }
    /// <summary>角色／團體／遊戲／共通用語（未知種類原樣顯示）。</summary>
    public string KindText { get; }
    /// <summary>待審核／已核准。</summary>
    public string StatusText { get; }
    public ObservableCollection<GlossaryReviewTermItem> Terms { get; }

    public string Title => $"{TitlePrefix}（{Terms.Count}）";
}

/// <summary>字幕監看頁右側抽屜：編輯一張詞彙卡（與其上層卡）的名詞，按「存檔到詞彙卡」寫回資料庫。
/// 由 Dashboard 建立（不用 DI），存檔後由 Dashboard 重新載入型錄並呼叫 <see cref="Reload"/>。</summary>
public partial class GlossaryDrawerViewModel : ObservableObject
{
    private readonly IGlossaryStore _store;
    private readonly Action<IReadOnlyCollection<long>> _onSaved;
    private readonly ReviewCountSignal? _reviewSignal;
    private readonly Action<long>? _toggleCard;
    private readonly Func<string, Task>? _mineMember;
    private GlossaryCatalog _catalog;
    private IReadOnlyCollection<long> _activeIds;
    private readonly long _cardId;

    /// <summary>程式重建成員清單（重設 IsActive）時不觸發 toggleCard；只有使用者勾選才觸發。</summary>
    private bool _syncingMembers;

    /// <summary>reviewSignal：存檔成功後通知側邊欄重算待審數量；null 時略過（單元測試不傳）。
    /// toggleCard／mineMember：成員區塊的切換生效與一鍵探勘進入點（由 Dashboard 提供）；
    /// 兩者都是 null 時成員區塊不顯示（單元測試不傳）。</summary>
    public GlossaryDrawerViewModel(IGlossaryStore store, GlossaryCatalog catalog, long cardId,
        IReadOnlyCollection<long> activeIds, Action<IReadOnlyCollection<long>> onSaved,
        ReviewCountSignal? reviewSignal = null,
        Action<long>? toggleCard = null, Func<string, Task>? mineMember = null)
    {
        _store = store;
        _catalog = catalog;
        _cardId = cardId;
        _activeIds = activeIds;
        _onSaved = onSaved;
        _reviewSignal = reviewSignal;
        _toggleCard = toggleCard;
        _mineMember = mineMember;

        BuildTabs();
        SelectedTab = Tabs.FirstOrDefault();
    }

    /// <summary>分頁：第一個為本卡，有上層卡（且在型錄中）時第二個為上層卡。</summary>
    public ObservableCollection<GlossaryDrawerTab> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardName), nameof(CardKindText), nameof(CardStatusText))]
    private GlossaryDrawerTab? _selectedTab;

    /// <summary>名詞搜尋文字：比對 Source／Target／Aliases（同 GlossaryCatalog：不分大小寫、全半形、平假名片假名互通）。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private GlossaryReviewTermItem? _selectedTerm;

    /// <summary>最近一次操作的結果訊息（存檔成功／失敗等）。</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>目前分頁篩選後的名詞；從頭到尾同一個實例，只在切換分頁或搜尋文字改變時以 Clear + Add 重填
    /// （名詞內容改變時不重填，避免中斷 DataGrid 編輯）。</summary>
    public ObservableCollection<GlossaryReviewTermItem> VisibleTerms { get; } = [];

    /// <summary>團體卡分頁的成員清單：人名名詞（非 Rejected）加上沒被名詞對到的角色卡；
    /// 非團體分頁或沒有 toggleCard／mineMember 進入點時為空。</summary>
    public ObservableCollection<GlossaryDrawerMemberItem> Members { get; } = [];

    /// <summary>成員區塊是否顯示（目前在團體卡分頁而且清單有內容）。</summary>
    public bool HasMembers => Members.Count > 0;

    /// <summary>資料探勘執行中或未設定知識庫模型（成員「探勘」按鈕的停用條件）；由 Dashboard 設定。</summary>
    [ObservableProperty]
    private bool _isMiningBusy;

    partial void OnIsMiningBusyChanged(bool value)
    {
        MineMemberCommand.NotifyCanExecuteChanged();
    }

    /// <summary>目前分頁的卡名（切換分頁時更新）。</summary>
    public string CardName => SelectedTab?.CardName ?? string.Empty;

    /// <summary>目前分頁的種類文字（角色／團體／遊戲）。</summary>
    public string CardKindText => SelectedTab?.KindText ?? string.Empty;

    /// <summary>目前分頁的卡片狀態文字（待審核／已核准）。</summary>
    public string CardStatusText => SelectedTab?.StatusText ?? string.Empty;

    /// <summary>所有分頁 dirty 名詞的數量。</summary>
    public int DirtyCount => Tabs.Sum(tab => tab.Terms.Count(term => term.IsDirty));

    public bool HasChanges => DirtyCount > 0;

    /// <summary>底部說明文字：變更數、會寫回哪些卡、生效中時提示會同時更新 prompt。</summary>
    public string FooterText
    {
        get
        {
            if (!HasChanges)
            {
                return "在表格中直接修改原文、譯名或分類，按「存檔到詞彙卡」寫回。";
            }

            var changedCards = Tabs
                .Where(tab => tab.Terms.Any(term => term.IsDirty))
                .Select(tab => tab.CardName)
                .ToList();
            var text = $"{DirtyCount} 筆變更。存檔後寫回「{string.Join("、", changedCards)}」卡";
            if (Tabs.Any(tab => tab.Terms.Any(term => term.IsDirty) && _activeIds.Contains(tab.CardId)))
            {
                text += "；生效中，會同時更新 prompt（重置對話一次）";
            }
            return text + "。";
        }
    }

    /// <summary>是／否確認對話框的進入點；測試可替換。回傳 true 表示「是」。</summary>
    public Func<string, bool> ConfirmDiscard { get; set; } = message =>
        MessageBox.Show(message, "編輯名詞", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>要求關閉抽屜（有未存變更且使用者不同意放棄時不會觸發）；由 Dashboard 處理。</summary>
    public event Action? CloseRequested;

    partial void OnSelectedTabChanged(GlossaryDrawerTab? value)
    {
        RebuildVisibleTerms();
        RebuildMembers();
    }

    partial void OnSearchTextChanged(string value)
    {
        RebuildVisibleTerms();
    }

    /// <summary>在目前分頁的名詞清單最後面加一個新名詞（Id 0、Status Pending），並選取它。</summary>
    [RelayCommand]
    private void AddTerm()
    {
        if (SelectedTab is not { } tab)
        {
            return;
        }

        var term = new GlossaryReviewTermItem(new GlossaryTerm
        {
            Id = 0,
            CardId = tab.CardId,
            Source = string.Empty,
            Target = string.Empty,
            Category = string.Empty,
            Status = GlossaryReviewTermItem.PendingStatus,
        });
        term.PropertyChanged += OnTermPropertyChanged;
        tab.Terms.Add(term);
        VisibleTerms.Add(term);
        SelectedTerm = term;
    }

    /// <summary>成員探勘：用成員名（沒有角色卡的成員）跑資料探勘建立角色卡，完成後由 Dashboard 自動生效。</summary>
    [RelayCommand(CanExecute = nameof(CanMineMember))]
    private async Task MineMemberAsync(GlossaryDrawerMemberItem member)
    {
        if (_mineMember is null)
        {
            return;
        }

        await _mineMember(member.Name);
    }

    /// <summary>成員探勘可執行：成員沒有角色卡、有探勘進入點、沒有探勘正在執行。</summary>
    private bool CanMineMember(GlossaryDrawerMemberItem? member) =>
        member is { HasCard: false } && _mineMember is not null && !IsMiningBusy;

    /// <summary>把所有分頁的變更一次寫回資料庫（同一個交易）：dirty 的既有名詞更新、新名詞新增。
    /// 成功後呼叫 onSaved（有寫入的卡片 Id），再由 Dashboard 以新型錄呼叫 <see cref="Reload"/>。</summary>
    [RelayCommand]
    private void Save()
    {
        var updated = new List<GlossaryTerm>();
        var inserted = new List<GlossaryTerm>();
        var savedCardIds = new HashSet<long>();

        foreach (var tab in Tabs)
        {
            foreach (var term in tab.Terms.Where(term => term.IsDirty))
            {
                if (term.IsNew)
                {
                    inserted.Add(term.ToModel());
                }
                else
                {
                    updated.Add(term.ToModel());
                }
                savedCardIds.Add(tab.CardId);
            }
        }

        if (updated.Count == 0 && inserted.Count == 0)
        {
            StatusMessage = "沒有未儲存的變更";
            return;
        }

        try
        {
            _store.SaveGlossaryTermEdits(updated, inserted);
        }
        catch (Exception ex)
        {
            StatusMessage = $"存檔失敗：{ex.Message}";
            return;
        }

        _onSaved(savedCardIds);
        _reviewSignal?.Notify();
        StatusMessage = "已存檔";
    }

    /// <summary>用目前的型錄重新載入，放棄所有修改。</summary>
    [RelayCommand]
    private void Discard()
    {
        Reload(_catalog, _activeIds);
        StatusMessage = "已放棄變更";
    }

    /// <summary>關閉抽屜：有變更時先詢問 ConfirmDiscard，不同意就不關。</summary>
    [RelayCommand]
    private void Close()
    {
        if (HasChanges && !ConfirmDiscard($"有 {DirtyCount} 筆變更尚未存檔，要放棄嗎？"))
        {
            return;
        }

        CloseRequested?.Invoke();
    }

    /// <summary>以新的型錄與生效清單重新載入（存檔後由 Dashboard 呼叫）；保留目前分頁與搜尋文字。</summary>
    public void Reload(GlossaryCatalog newCatalog, IReadOnlyCollection<long> activeIds)
    {
        _catalog = newCatalog;
        _activeIds = activeIds;

        var selectedCardId = SelectedTab?.CardId;
        foreach (var tab in Tabs)
        {
            foreach (var term in tab.Terms)
            {
                term.PropertyChanged -= OnTermPropertyChanged;
            }
        }

        BuildTabs();
        SelectedTab = Tabs.FirstOrDefault(tab => tab.CardId == selectedCardId) ?? Tabs.FirstOrDefault();
        if (SelectedTab is null)
        {
            RebuildVisibleTerms();
        }

        RefreshDirtyState();
        RebuildMembers();
    }

    /// <summary>生效清單（或型錄）變動後由 Dashboard 呼叫：更新型錄與生效清單並重建成員區塊。
    /// 不動名詞分頁——使用者可能有未存的名詞編輯。</summary>
    public void RefreshMembers(GlossaryCatalog catalog, IReadOnlyCollection<long> activeIds)
    {
        _catalog = catalog;
        _activeIds = activeIds;
        RebuildMembers();
    }

    /// <summary>重建成員清單：目前分頁的卡是團體卡（本卡或上層分頁）時，列出這張團體卡的人名名詞
    /// （Category = 人名、非 Rejected，依 Id 排序）與其角色卡，再加上 ParentId 是這個團體、
    /// 但沒被名詞對到的角色卡（Name 用卡名）；同一張卡只出現一次。</summary>
    private void RebuildMembers()
    {
        _syncingMembers = true;
        try
        {
            foreach (var member in Members)
            {
                member.PropertyChanged -= OnMemberPropertyChanged;
            }
            Members.Clear();

            if ((_toggleCard is not null || _mineMember is not null) && CurrentGroupCard() is { } group)
            {
                var seenCardIds = new HashSet<long>();
                foreach (var term in _catalog.TermsOf(group.Id).Where(term =>
                             term.Category == "人名" && term.Status != GlossaryReviewTermItem.RejectedStatus))
                {
                    var card = _catalog.FindMemberCard(group.Id, term.Source);
                    if (card is not null && !seenCardIds.Add(card.Id))
                    {
                        continue;
                    }
                    Members.Add(CreateMember(term.Source, card));
                }

                foreach (var card in _catalog.Cards.Where(card => card.ParentId == group.Id))
                {
                    if (!seenCardIds.Add(card.Id))
                    {
                        continue;
                    }
                    Members.Add(CreateMember(card.Name, card));
                }
            }
        }
        finally
        {
            _syncingMembers = false;
        }
        OnPropertyChanged(nameof(HasMembers));
    }

    private GlossaryDrawerMemberItem CreateMember(string name, GlossaryCard? card)
    {
        var item = new GlossaryDrawerMemberItem(name, card?.Id, card?.Name, card is not null && _activeIds.Contains(card.Id));
        item.PropertyChanged += OnMemberPropertyChanged;
        return item;
    }

    /// <summary>目前分頁對應的團體卡（本卡或上層分頁都算）；不是團體卡時為 null。</summary>
    private GlossaryCard? CurrentGroupCard() =>
        SelectedTab is { } tab && _catalog.FindCard(tab.CardId) is { Kind: GlossaryKind.Group } card ? card : null;

    /// <summary>使用者勾選／取消成員的角色卡時切換那張卡的生效狀態（程式重建成員時不觸發）。</summary>
    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GlossaryDrawerMemberItem.IsActive) ||
            _syncingMembers ||
            sender is not GlossaryDrawerMemberItem { HasCard: true, CardId: { } cardId })
        {
            return;
        }

        _toggleCard?.Invoke(cardId);
    }

    /// <summary>重建分頁與各分頁的名詞清單（來自型錄，不含 Rejected 的名詞）。</summary>
    private void BuildTabs()
    {
        Tabs.Clear();
        if (_catalog.FindCard(_cardId) is not { } card)
        {
            return;
        }

        AddTab(card, "本卡");
        if (card.ParentId is { } parentId && _catalog.FindCard(parentId) is { } parent)
        {
            AddTab(parent, $"上層：{parent.Name}");
        }
    }

    private void AddTab(GlossaryCard card, string titlePrefix)
    {
        var tab = new GlossaryDrawerTab(
            card.Id,
            titlePrefix,
            card.Name,
            card.Kind switch
            {
                GlossaryKind.Character => "角色",
                GlossaryKind.Group => "團體",
                GlossaryKind.Game => "遊戲",
                GlossaryKind.Common => "共通用語",
                _ => card.Kind,
            },
            card.Status == GlossaryReviewTermItem.ApprovedStatus ? "已核准" : "待審核");
        foreach (var term in _catalog.TermsOf(card.Id))
        {
            var item = new GlossaryReviewTermItem(term);
            item.PropertyChanged += OnTermPropertyChanged;
            tab.Terms.Add(item);
        }
        Tabs.Add(tab);
    }

    /// <summary>以 Clear + Add 重填 VisibleTerms（目前分頁＋搜尋文字）；名詞內容改變時不呼叫。</summary>
    private void RebuildVisibleTerms()
    {
        VisibleTerms.Clear();
        if (SelectedTab is null)
        {
            return;
        }

        var query = (SearchText ?? string.Empty).Trim();
        foreach (var term in SelectedTab.Terms)
        {
            if (query.Length == 0 || TermMatches(term, query))
            {
                VisibleTerms.Add(term);
            }
        }
    }

    /// <summary>比對 Source／Target／Aliases（別名逐行）。</summary>
    private static bool TermMatches(GlossaryReviewTermItem term, string query) =>
        LooseContains(term.Source, query) ||
        LooseContains(term.Target, query) ||
        AliasLines(term.Aliases).Any(alias => LooseContains(alias, query));

    /// <summary>名詞 dirty 變動時只更新計數與說明文字，不重建 VisibleTerms。</summary>
    private void OnTermPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GlossaryReviewTermItem.IsDirty))
        {
            RefreshDirtyState();
        }
    }

    private void RefreshDirtyState()
    {
        OnPropertyChanged(nameof(DirtyCount));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(FooterText));
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
}
