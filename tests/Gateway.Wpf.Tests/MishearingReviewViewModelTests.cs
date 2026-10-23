using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;
using Translation.Gateway.ViewModels;

namespace Translation.Gateway.Wpf.Tests;

/// <summary>聽錯字修正審核頁 ViewModel：用假的 IGlossaryStore 測篩選、勾選、核准與駁回。</summary>
public sealed class MishearingReviewViewModelTests
{
    /// <summary>記憶體版的 IGlossaryStore：只實作審核頁會用到的讀寫行為，其餘throw。</summary>
    private sealed class FakeGlossaryStore : IGlossaryStore
    {
        public List<GlossaryCard> Cards { get; } = [];

        public List<GlossaryTerm> Terms { get; } = [];

        public List<MishearingFix> Fixes { get; } = [];

        /// <summary>true 時讀取方法都擲例外（模擬資料庫失敗）。</summary>
        public bool ThrowOnRead { get; set; }

        public List<long> ApprovedIds { get; } = [];

        public List<long> DeletedIds { get; } = [];

        public IReadOnlyList<GlossaryCard> GetGlossaryCards() => Cards;

        public IReadOnlyList<GlossaryTerm> GetGlossaryTerms() => Terms;

        public IReadOnlyList<GlossaryCard> GetAllGlossaryCards() =>
            ThrowOnRead ? throw new InvalidOperationException("資料庫讀取失敗") : Cards;

        public IReadOnlyList<GlossaryTerm> GetAllGlossaryTerms() =>
            ThrowOnRead ? throw new InvalidOperationException("資料庫讀取失敗") : Terms;

        public void SaveGlossaryChanges(IReadOnlyList<GlossaryCard> cards, IReadOnlyList<GlossaryTerm> terms) =>
            throw new NotSupportedException();

        public void SaveGlossaryTermEdits(IReadOnlyList<GlossaryTerm> updated, IReadOnlyList<GlossaryTerm> inserted) =>
            throw new NotSupportedException();

        public long CreateGlossaryCard(GlossaryCard card, IReadOnlyList<GlossaryTerm> terms) =>
            throw new NotSupportedException();

        public GlossaryMergeResult MergeGlossaryCards(long sourceId, long targetId) =>
            throw new NotSupportedException();

        public void DeleteGlossaryCard(long cardId) =>
            throw new NotSupportedException();

        public void DeleteGlossaryTerms(IReadOnlyList<long> termIds) =>
            throw new NotSupportedException();

        public void UpsertTermCandidates(IReadOnlyList<TermCandidate> candidates) =>
            throw new NotSupportedException();

        public IReadOnlyList<MishearingFix> GetMishearingFixes() =>
            ThrowOnRead
                ? throw new InvalidOperationException("資料庫讀取失敗")
                : Fixes.OrderBy(f => f.CardId).ThenBy(f => f.TermId).ThenBy(f => f.Wrong, StringComparer.Ordinal).ToList();

        public int UpsertMishearingFixes(IReadOnlyList<MishearingProposal> proposals)
        {
            var inserted = 0;
            foreach (var proposal in proposals)
            {
                var wrong = proposal.Wrong.Trim();
                if (wrong.Length < 2 || wrong == proposal.Right.Trim() || string.IsNullOrWhiteSpace(proposal.Evidence))
                {
                    continue;
                }

                var existing = Fixes.FirstOrDefault(f =>
                    f.TermId == proposal.TermId && f.Kind == proposal.Kind && f.Wrong == wrong);
                if (existing is not null)
                {
                    existing.Occurrences++;
                    existing.Evidence = proposal.Evidence;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    Fixes.Add(new MishearingFix
                    {
                        Id = Fixes.Count + 1,
                        CardId = proposal.CardId,
                        TermId = proposal.TermId,
                        Kind = proposal.Kind,
                        Wrong = wrong,
                        Right = proposal.Right,
                        Status = MishearingStatus.Pending,
                        Evidence = proposal.Evidence,
                        Occurrences = 1,
                        CreatedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
                    });
                    inserted++;
                }
            }

            return inserted;
        }

        public void ApproveMishearingFixes(IReadOnlyList<long> ids)
        {
            ApprovedIds.AddRange(ids);
            foreach (var fix in Fixes.Where(f => ids.Contains(f.Id)))
            {
                fix.Status = MishearingStatus.Approved;
                fix.UpdatedAt = DateTime.UtcNow;
            }
        }

        public void DeleteMishearingFixes(IReadOnlyList<long> ids)
        {
            DeletedIds.AddRange(ids);
            Fixes.RemoveAll(f => ids.Contains(f.Id));
        }
    }

    private static MishearingFix Fix(long id, long cardId, long termId, string wrong, string status) => new()
    {
        Id = id,
        CardId = cardId,
        TermId = termId,
        Kind = MishearingKind.Pre,
        Wrong = wrong,
        Right = "ヴァンダル",
        Status = status,
        Evidence = $"出現 {wrong} 的句子",
        Occurrences = 1,
        CreatedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
    };

    /// <summary>一張卡（VALORANT）、一個名詞（ヴァンダル）與三筆修正（2 待審、1 已核准）。</summary>
    private static FakeGlossaryStore CreateSeededStore()
    {
        var store = new FakeGlossaryStore();
        store.Cards.Add(new GlossaryCard { Id = 1, Kind = "Game", Name = "VALORANT", Status = "Approved" });
        store.Terms.Add(new GlossaryTerm { Id = 11, CardId = 1, Source = "ヴァンダル", Target = "暴徒", Status = "Approved" });
        store.Fixes.Add(Fix(1, 1, 11, "作り", MishearingStatus.Pending));
        store.Fixes.Add(Fix(2, 1, 11, "ツクリー", MishearingStatus.Pending));
        store.Fixes.Add(Fix(3, 1, 11, "バンダル", MishearingStatus.Approved));
        return store;
    }

    [Fact]
    public void DefaultFilter_ShowsPendingOnly_AndAllSelectedByDefault()
    {
        var store = CreateSeededStore();
        var vm = new MishearingReviewViewModel(store, new MishearingFixState(), new ReviewCountSignal());

        // 預設「待審核」：已核准的不顯示
        Assert.Equal(["ツクリー", "作り"], vm.Items.Select(i => i.Wrong).ToList());
        // 載入時預設全選
        Assert.True(vm.AllVisibleSelected);
        Assert.All(vm.Items, i => Assert.True(i.IsSelected));
        Assert.Equal("待審核 2・已核准 1", vm.StatusText);

        // 對出來的卡名與名詞欄位
        var item = vm.Items[0];
        Assert.Equal("VALORANT", item.CardName);
        Assert.Equal("ヴァンダル", item.TermSource);
        Assert.Equal("暴徒", item.TermTarget);
        Assert.Equal("待審核", item.StatusText);
        Assert.Equal("03/04 05:06", item.UpdatedText);
    }

    [Fact]
    public void FilterAndSearch_KeepSameCollectionInstance()
    {
        var store = CreateSeededStore();
        var vm = new MishearingReviewViewModel(store, new MishearingFixState(), new ReviewCountSignal());
        var items = vm.Items;

        vm.StatusFilter = "全部";
        Assert.Same(items, vm.Items);
        Assert.Equal(3, vm.Items.Count);

        vm.StatusFilter = "已核准";
        Assert.Same(items, vm.Items);
        Assert.Equal(["バンダル"], vm.Items.Select(i => i.Wrong).ToList());

        // 搜尋比對卡名、名詞、錯字（平假名也找得到片假名）
        vm.StatusFilter = "全部";
        vm.SearchText = "つくりー";
        Assert.Same(items, vm.Items);
        Assert.Equal(["ツクリー"], vm.Items.Select(i => i.Wrong).ToList());

        vm.SearchText = "暴徒";
        Assert.Equal(3, vm.Items.Count);

        vm.SearchText = "valorant";
        Assert.Equal(3, vm.Items.Count);

        vm.SearchText = "找不到的字";
        Assert.Empty(vm.Items);
        Assert.False(vm.HasItems);
    }

    [Fact]
    public void ApproveSelected_OnlyCheckedRows_WriteToStoreThenReload()
    {
        var store = CreateSeededStore();
        var vm = new MishearingReviewViewModel(store, new MishearingFixState(), new ReviewCountSignal());

        // 取消勾選作り：只核准ツクリー
        vm.Items.Single(i => i.Wrong == "作り").IsSelected = false;
        vm.ApproveSelectedCommand.Execute(null);

        Assert.Equal([2L], store.ApprovedIds);
        // 重新載入後：待審核只剩作り、ツクリー變成已核准（預設篩選看不到）
        Assert.Equal(["作り"], vm.Items.Select(i => i.Wrong).ToList());
        Assert.Equal(MishearingStatus.Approved, store.Fixes.Single(f => f.Id == 2).Status);
        Assert.Equal("待審核 1・已核准 2", vm.StatusText);
    }

    [Fact]
    public void DeleteSelected_ConfirmFalse_DoesNotDelete()
    {
        var store = CreateSeededStore();
        var vm = new MishearingReviewViewModel(store, new MishearingFixState(), new ReviewCountSignal());
        var confirmMessages = new List<string>();
        vm.ConfirmDialog = message =>
        {
            confirmMessages.Add(message);
            return false;
        };

        vm.Items.Single(i => i.Wrong == "作り").IsSelected = false;
        vm.DeleteSelectedCommand.Execute(null);

        // 確認訊息只提勾選的筆數；回 false 時完全不動資料庫
        var message = Assert.Single(confirmMessages);
        Assert.Equal("確定要刪除 1 筆聽錯字修正嗎？", message);
        Assert.Empty(store.DeletedIds);
        Assert.Equal(3, store.Fixes.Count);
        Assert.Equal(["ツクリー", "作り"], vm.Items.Select(i => i.Wrong).ToList());
    }

    [Fact]
    public void DeleteSelected_ConfirmTrue_DeletesOnlyCheckedRows()
    {
        var store = CreateSeededStore();
        var vm = new MishearingReviewViewModel(store, new MishearingFixState(), new ReviewCountSignal());
        var confirmMessages = new List<string>();
        vm.ConfirmDialog = message =>
        {
            confirmMessages.Add(message);
            return true;
        };

        vm.Items.Single(i => i.Wrong == "作り").IsSelected = false;
        vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal("確定要刪除 1 筆聽錯字修正嗎？", Assert.Single(confirmMessages));
        Assert.Equal([2L], store.DeletedIds);
        // 刪除後重新載入：ツクリー消失
        Assert.Equal(["作り"], vm.Items.Select(i => i.Wrong).ToList());
        Assert.Equal(2, store.Fixes.Count);
    }

    [Fact]
    public void Commands_DisabledWhenNothingSelected()
    {
        var store = CreateSeededStore();
        var vm = new MishearingReviewViewModel(store, new MishearingFixState(), new ReviewCountSignal());

        Assert.True(vm.ApproveSelectedCommand.CanExecute(null));
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));

        vm.AllVisibleSelected = false;
        Assert.False(vm.ApproveSelectedCommand.CanExecute(null));
        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));

        // 再勾一列回來就恢復可用
        vm.Items[0].IsSelected = true;
        Assert.True(vm.ApproveSelectedCommand.CanExecute(null));
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void ToggleSelectAll_SwitchesAllVisibleRows()
    {
        var store = CreateSeededStore();
        var vm = new MishearingReviewViewModel(store, new MishearingFixState(), new ReviewCountSignal());

        vm.ToggleSelectAllCommand.Execute(null);
        Assert.False(vm.AllVisibleSelected);

        vm.ToggleSelectAllCommand.Execute(null);
        Assert.True(vm.AllVisibleSelected);
    }

    [Fact]
    public void Load_StoreFails_ShowsErrorTextWithoutThrowing()
    {
        var store = CreateSeededStore();
        var vm = new MishearingReviewViewModel(store, new MishearingFixState(), new ReviewCountSignal());

        store.ThrowOnRead = true;
        var ex = Record.Exception(() => vm.Load());

        Assert.Null(ex);
        Assert.NotEqual(string.Empty, vm.ErrorText);
        // 資料庫掛掉時保留最後一次成功載入的清單
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public void Load_MissingCardOrTerm_ShowsPlaceholderWithoutThrowing()
    {
        var store = new FakeGlossaryStore();
        store.Fixes.Add(Fix(9, 404, 405, "作り", MishearingStatus.Pending));

        var vm = new MishearingReviewViewModel(store, new MishearingFixState(), new ReviewCountSignal());

        var item = Assert.Single(vm.Items);
        Assert.Contains("404", item.CardName);
        Assert.Equal(string.Empty, item.TermSource);
        Assert.Equal(string.Empty, item.TermTarget);
    }

    [Fact]
    public void ApproveSelected_ReloadsMishearingFixState()
    {
        var store = CreateSeededStore();
        var fixState = new MishearingFixState();
        var vm = new MishearingReviewViewModel(store, fixState, new ReviewCountSignal());

        // 核准前還沒載入任何修正
        Assert.Equal(0, fixState.Count);

        // 預設「待審核」全選：核准作り與ツクリー後，三筆都是 Approved + Pre
        vm.ApproveSelectedCommand.Execute(null);

        Assert.Equal(3, fixState.Count);
    }

    [Fact]
    public void ApproveAndDelete_NotifyReviewCountChanged()
    {
        var store = CreateSeededStore();
        var signal = new ReviewCountSignal();
        var vm = new MishearingReviewViewModel(store, new MishearingFixState(), signal);
        var raised = 0;
        signal.Changed += (_, _) => raised++;

        // 核准勾選的列（ツクリー）→ 寫進資料庫後通知一次
        vm.Items.Single(i => i.Wrong == "作り").IsSelected = false;
        vm.ApproveSelectedCommand.Execute(null);
        Assert.Equal(1, raised);

        // 刪除（駁回）已核准的列 → 再通知一次
        vm.StatusFilter = "已核准";
        vm.ConfirmDialog = _ => true;
        vm.DeleteSelectedCommand.Execute(null);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void DeleteSelected_AfterApprove_ReloadsMishearingFixState()
    {
        var store = CreateSeededStore();
        var fixState = new MishearingFixState();
        var vm = new MishearingReviewViewModel(store, fixState, new ReviewCountSignal());

        vm.ApproveSelectedCommand.Execute(null);
        Assert.Equal(3, fixState.Count);

        // 切成「已核准」全選刪除：生效清單跟著清空
        vm.StatusFilter = "已核准";
        vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal(0, fixState.Count);
        Assert.Empty(store.Fixes);
    }
}
