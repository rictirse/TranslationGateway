using System.Collections.Specialized;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;
using Translation.Gateway.ViewModels;

namespace Translation.Gateway.Wpf.Tests;

/// <summary>字幕監看頁的詞彙卡抽屜：用暫存 SQLite 加真的 store 測分頁、搜尋、新增名詞與存檔流程。</summary>
public sealed class GlossaryDrawerViewModelTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public void Tabs_WithParentCardInCatalog_HasParentTab_WithoutParentSingleTab()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute(@"
                INSERT INTO GlossaryCards (Id, Kind, Name, ParentId, Status) VALUES
                    (1, 'Group',     'ぶいすぽっ!', NULL, 'Approved'),
                    (2, 'Character', '橘ひなの',   1,    'Pending')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '花芽すみれ', '花芽澄伶', 'Approved'),
                    (21, 2, '橘ひなの',   '橘雛乃',   'Approved')");
        });

        var drawer = NewDrawer(ctx, cardId: 2);

        // 有上層卡而且上層卡在型錄中：兩個分頁，預設選本卡
        Assert.Equal(["本卡（1）", "上層：ぶいすぽっ!（1）"], drawer.Tabs.Select(t => t.Title).ToList());
        Assert.Same(drawer.Tabs[0], drawer.SelectedTab);
        Assert.Equal("橘ひなの", drawer.CardName);
        Assert.Equal("角色", drawer.CardKindText);
        Assert.Equal("待審核", drawer.CardStatusText);

        // 切到上層分頁：抬頭換成上層卡
        drawer.SelectedTab = drawer.Tabs[1];
        Assert.Equal("ぶいすぽっ!", drawer.CardName);
        Assert.Equal("團體", drawer.CardKindText);
        Assert.Equal("已核准", drawer.CardStatusText);

        // 沒有上層卡的卡：只有一個分頁
        var single = NewDrawer(ctx, cardId: 1);
        Assert.Equal(["本卡（1）"], single.Tabs.Select(t => t.Title).ToList());
    }

    [Fact]
    public void EditOnParentTab_SwitchTabs_BackAndForth_ChangeKept()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute(@"
                INSERT INTO GlossaryCards (Id, Kind, Name, ParentId, Status) VALUES
                    (1, 'Group',     'ぶいすぽっ!', NULL, 'Approved'),
                    (2, 'Character', '橘ひなの',   1,    'Approved')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '花芽すみれ', '花芽澄伶', 'Approved'),
                    (21, 2, '橘ひなの',   '橘雛乃',   'Approved')");
        });

        var drawer = NewDrawer(ctx, cardId: 2);

        // 在上層分頁改一個名詞
        drawer.SelectedTab = drawer.Tabs[1];
        drawer.VisibleTerms.Single(t => t.Source == "花芽すみれ").Target = "花芽澄";
        Assert.Equal(1, drawer.DirtyCount);
        Assert.True(drawer.HasChanges);

        // 切回本卡再切回上層：修改還在、dirty 還在（各分頁保留一份清單）
        drawer.SelectedTab = drawer.Tabs[0];
        Assert.DoesNotContain(drawer.VisibleTerms, t => t.Source == "花芽すみれ");
        Assert.Equal(1, drawer.DirtyCount);

        drawer.SelectedTab = drawer.Tabs[1];
        Assert.Equal("花芽澄", drawer.VisibleTerms.Single(t => t.Source == "花芽すみれ").Target);
        Assert.Equal(1, drawer.DirtyCount);
    }

    [Fact]
    public void TermTargetChange_DoesNotRebuildVisibleTerms()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'ヴァンダル', '暴徒', 'Approved')");
        });

        var drawer = NewDrawer(ctx, cardId: 1);
        var visible = drawer.VisibleTerms;
        var collectionChanged = 0;
        ((INotifyCollectionChanged)visible).CollectionChanged += (_, _) => collectionChanged++;

        visible[0].Target = "暴徒步槍";

        // 同一個實例、沒有 CollectionChanged（DataGrid 編輯不能被中斷）
        Assert.Same(visible, drawer.VisibleTerms);
        Assert.Equal(0, collectionChanged);
        Assert.Equal(1, drawer.DirtyCount);
    }

    [Fact]
    public void SearchText_FiltersVisibleTerms_KanaAndWidthInterchangeable()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Aliases, Status) VALUES
                    (11, 1, 'ジェット', '捷風', 'Jett', 'Approved'),
                    (12, 1, 'ヴァンダル', '暴徒', NULL, 'Approved')");
        });

        var drawer = NewDrawer(ctx, cardId: 1);
        Assert.Equal(2, drawer.VisibleTerms.Count);

        // 平假名搜尋得到片假名原文
        drawer.SearchText = "じぇっと";
        Assert.Equal([11L], drawer.VisibleTerms.Select(t => t.Id).ToList());

        // 別名不分大小寫
        drawer.SearchText = "jett";
        Assert.Equal([11L], drawer.VisibleTerms.Select(t => t.Id).ToList());

        // 譯名也可以搜
        drawer.SearchText = "暴徒";
        Assert.Equal([12L], drawer.VisibleTerms.Select(t => t.Id).ToList());

        // 清空回全部
        drawer.SearchText = string.Empty;
        Assert.Equal(2, drawer.VisibleTerms.Count);
    }

    [Fact]
    public void AddTerm_AppendsToCurrentTab_DirtyOnlyWhenSourceAndTargetFilled()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'ヴァンダル', '暴徒', 'Approved')");
        });

        var drawer = NewDrawer(ctx, cardId: 1);
        drawer.AddTermCommand.Execute(null);

        var added = drawer.VisibleTerms[^1];
        Assert.NotSame(drawer.VisibleTerms[0], added);
        Assert.Equal(2, drawer.VisibleTerms.Count);
        Assert.Equal(2, drawer.SelectedTab!.Terms.Count);
        Assert.Same(added, drawer.SelectedTerm);
        Assert.True(added.IsNew);
        Assert.Equal(0L, added.Id);
        Assert.Equal(1L, added.CardId);
        Assert.Equal("Pending", added.Status);

        // 只填 Source 不算 dirty；Source 和 Target 都填才算
        added.Source = "スペクター";
        Assert.False(added.IsDirty);
        Assert.Equal(0, drawer.DirtyCount);

        added.Target = "幽靈";
        Assert.True(added.IsDirty);
        Assert.Equal(1, drawer.DirtyCount);
    }

    [Fact]
    public void Save_WritesToDatabase_AndCallsOnSavedWithCardIds()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute(@"
                INSERT INTO GlossaryCards (Id, Kind, Name, ParentId, Status) VALUES
                    (1, 'Group',     'ぶいすぽっ!', NULL, 'Approved'),
                    (2, 'Character', '橘ひなの',   1,    'Approved')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '花芽すみれ', '花芽澄伶', 'Approved'),
                    (21, 2, '橘ひなの',   '橘雛乃',   'Approved')");
        });

        var savedIds = new List<IReadOnlyCollection<long>>();
        GlossaryDrawerViewModel? created = null;
        var drawer = new GlossaryDrawerViewModel(ctx.Db, ctx.LoadCatalog(), cardId: 2, activeIds: [], onSaved: ids =>
        {
            savedIds.Add(ids);
            // 比照 Dashboard：存檔成功後用新的型錄重載抽屜
            created?.Reload(ctx.LoadCatalog(), []);
        });
        created = drawer;

        // 本卡改一個名詞，上層分頁加一個新名詞：兩張卡都有寫入
        drawer.VisibleTerms.Single(t => t.Source == "橘ひなの").Target = "橘雛乃（改名）";
        drawer.SelectedTab = drawer.Tabs[1];
        drawer.AddTermCommand.Execute(null);
        var added = drawer.VisibleTerms[^1];
        added.Source = "すみれ";
        added.Target = "澄伶";
        Assert.Equal(2, drawer.DirtyCount);

        drawer.SaveCommand.Execute(null);

        Assert.Equal("已存檔", drawer.StatusMessage);
        Assert.Equal(0, drawer.DirtyCount);
        Assert.False(drawer.HasChanges);

        // onSaved 收到有寫入的卡片 Id（本卡與上層卡）
        var cardIds = Assert.Single(savedIds);
        Assert.Equal([1L, 2L], cardIds.OrderBy(id => id).ToList());

        // 資料庫正確：既有名詞更新、新名詞寫入（Status Pending）
        var terms = ctx.Db.GetGlossaryTerms().ToList();
        Assert.Equal("橘雛乃（改名）", terms.Single(t => t.Source == "橘ひなの").Target);
        Assert.Equal("澄伶", terms.Single(t => t.Source == "すみれ").Target);
        Assert.Equal(1L, terms.Single(t => t.Source == "すみれ").CardId);
        Assert.Equal("Pending", terms.Single(t => t.Source == "すみれ").Status);
    }

    [Fact]
    public void Save_DuplicateSourceInSameCard_ShowsErrorAndKeepsChanges()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, 'ヴァンダル', '暴徒', 'Approved'),
                    (12, 1, 'オペ', '玩家', 'Approved')");
        });

        var drawer = NewDrawer(ctx, cardId: 1);

        // 把名詞 11 的 Source 改成與名詞 12 相同（違反 UNIQUE(CardId, Source)）
        drawer.VisibleTerms.Single(t => t.Id == 11).Source = "オペ";
        Assert.Equal(1, drawer.DirtyCount);

        drawer.SaveCommand.Execute(null);

        Assert.Contains("存檔失敗", drawer.StatusMessage);
        Assert.Equal(1, drawer.DirtyCount);
        Assert.True(drawer.HasChanges);

        // 交易回復：資料庫沒有被更新
        var terms = ctx.Db.GetGlossaryTerms().OrderBy(t => t.Id).ToList();
        Assert.Equal("ヴァンダル", terms[0].Source);
        Assert.Equal("暴徒", terms[0].Target);
    }

    [Fact]
    public void FooterText_NoChange_ShowsHint_ActiveCardMentionsPromptUpdate()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'ヴァンダル', '暴徒', 'Approved')");
        });

        var drawer = NewDrawer(ctx, cardId: 1, activeIds: [1]);
        Assert.Equal("在表格中直接修改原文、譯名或分類，按「存檔到詞彙卡」寫回。", drawer.FooterText);

        // 卡片生效中：提示會同時更新 prompt
        drawer.VisibleTerms[0].Target = "暴徒步槍";
        Assert.Contains("1 筆變更", drawer.FooterText);
        Assert.Contains("VALORANT", drawer.FooterText);
        Assert.Contains("生效中，會同時更新 prompt（重置對話一次）", drawer.FooterText);
        Assert.EndsWith("。", drawer.FooterText);

        // 不是生效中的卡：不提示 prompt
        var inactive = NewDrawer(ctx, cardId: 1, activeIds: []);
        inactive.VisibleTerms[0].Target = "暴徒步槍";
        Assert.DoesNotContain("更新 prompt", inactive.FooterText);
    }

    [Fact]
    public void Close_WithChanges_ConfirmFalseKeepsDrawer_ConfirmTrueRaisesCloseRequested()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'ヴァンダル', '暴徒', 'Approved')");
        });

        var drawer = NewDrawer(ctx, cardId: 1);
        var closed = 0;
        drawer.CloseRequested += () => closed++;

        // 沒有變更時直接關，不詢問
        var asked = 0;
        drawer.ConfirmDiscard = _ =>
        {
            asked++;
            return false;
        };
        drawer.CloseCommand.Execute(null);
        Assert.Equal(0, asked);
        Assert.Equal(1, closed);

        // 有變更而且不同意放棄：不觸發 CloseRequested、修改保留
        drawer.VisibleTerms[0].Target = "暴徒步槍";
        drawer.ConfirmDiscard = _ => false;
        drawer.CloseCommand.Execute(null);
        Assert.Equal(1, closed);
        Assert.Equal(1, drawer.DirtyCount);

        // 同意放棄：觸發 CloseRequested
        drawer.ConfirmDiscard = _ => true;
        drawer.CloseCommand.Execute(null);
        Assert.Equal(2, closed);
    }

    [Fact]
    public void Discard_ReloadsFromCatalog_AndDropsChanges()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'ヴァンダル', '暴徒', 'Approved')");
        });

        var drawer = NewDrawer(ctx, cardId: 1);
        drawer.VisibleTerms[0].Target = "暴徒步槍";
        drawer.AddTermCommand.Execute(null);
        drawer.VisibleTerms[^1].Source = "スペクター";
        drawer.VisibleTerms[^1].Target = "幽靈";
        Assert.Equal(2, drawer.DirtyCount);

        drawer.DiscardCommand.Execute(null);

        Assert.Equal("已放棄變更", drawer.StatusMessage);
        Assert.Equal(0, drawer.DirtyCount);
        var remaining = Assert.Single(drawer.VisibleTerms);
        Assert.Equal("暴徒", remaining.Target);
    }

    // ===== 成員區塊（團體卡分頁） =====

    /// <summary>成員區塊的測試資料：團體卡的人名名詞（花芽すみれ→卡2、橘ひなの→卡3、藍沢エマ沒有卡）、
    /// 非人名與 Rejected 名詞（不列）、名詞沒對到的角色卡（猫汰つな，卡4）。</summary>
    private static void SeedMembers(Context ctx)
    {
        using var conn = new SqliteConnection($"Data Source={Path.Combine(ctx.Dir, "cache.db")}");
        conn.Execute(@"
            INSERT INTO GlossaryCards (Id, Kind, Name, ParentId, Status) VALUES
                (1, 'Group',     'ぶいすぽっ!', NULL, 'Approved'),
                (2, 'Character', '花芽すみれ',   1,    'Approved'),
                (3, 'Character', '橘ひなの',   NULL, 'Approved'),
                (4, 'Character', '猫汰つな',   1,    'Approved')");
        conn.Execute(@"
            INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Category, Status) VALUES
                (11, 1, '花芽すみれ', '花芽澄伶', '人名', 'Approved'),
                (12, 1, '橘ひなの',   '橘Hinano', '人名', 'Approved'),
                (13, 1, '藍沢エマ',   '藍澤Ema',   '人名', 'Approved'),
                (14, 1, 'ぶいすぽっ!', 'VSPO!',   NULL,   'Approved'),
                (15, 1, '除外名詞',   '除外',     '人名', 'Rejected'),
                (21, 2, '花芽すみれ', '花芽澄伶', '人名', 'Approved')");
    }

    [Fact]
    public void Members_OnGroupTab_ListsPersonTermsWithCardState()
    {
        using var ctx = Create(_ => { });
        SeedMembers(ctx);

        var drawer = NewDrawerWithCallbacks(ctx, cardId: 1, activeIds: [2]);

        // 成員＝人名名詞（非 Rejected）＋名詞沒對到的角色卡（猫汰つな）；非人名（ぶいすぽっ!）與 Rejected 不列
        Assert.True(drawer.HasMembers);
        Assert.Equal(["花芽すみれ", "橘ひなの", "藍沢エマ", "猫汰つな"], drawer.Members.Select(m => m.Name).ToList());
        Assert.Equal([2L, 3L, null, 4L], drawer.Members.Select(m => m.CardId).ToList());

        // 有卡：IsActive 依生效清單（卡 2 生效中）；卡名與成員名相同時顯示文字只有名字
        var sumire = drawer.Members[0];
        Assert.True(sumire.HasCard);
        Assert.True(sumire.IsActive);
        Assert.Equal("花芽すみれ", sumire.DisplayText);
        Assert.False(drawer.Members[1].IsActive);

        // 沒有卡：HasCard false、顯示「沒有角色卡」
        var ema = drawer.Members[2];
        Assert.False(ema.HasCard);
        Assert.Equal("藍沢エマ（沒有角色卡）", ema.DisplayText);
    }

    [Fact]
    public void Members_OnNonGroupTab_HasMembersFalse_ParentGroupTabTrue()
    {
        using var ctx = Create(_ => { });
        SeedMembers(ctx);

        // 本卡是角色卡：沒有成員
        var drawer = NewDrawerWithCallbacks(ctx, cardId: 2, activeIds: []);
        Assert.False(drawer.HasMembers);
        Assert.Empty(drawer.Members);

        // 切到上層團體分頁：有成員
        drawer.SelectedTab = drawer.Tabs[1];
        Assert.True(drawer.HasMembers);
        Assert.NotEmpty(drawer.Members);

        // 切回本卡分頁：成員清空
        drawer.SelectedTab = drawer.Tabs[0];
        Assert.False(drawer.HasMembers);
        Assert.Empty(drawer.Members);
    }

    [Fact]
    public void MemberCheckbox_UserToggleCallsToggleCard_RefreshMembersResetDoesNot()
    {
        using var ctx = Create(_ => { });
        SeedMembers(ctx);
        var toggled = new List<long>();
        var drawer = NewDrawerWithCallbacks(ctx, cardId: 1, activeIds: [2], toggled);

        // 使用者取消勾選：呼叫 toggleCard，參數是那個成員的角色卡 Id
        drawer.Members.Single(m => m.CardId == 2).IsActive = false;
        Assert.Equal([2L], toggled);

        // 沒有卡的成員：IsActive 變動不觸發 toggle
        drawer.Members.Single(m => !m.HasCard).IsActive = true;
        Assert.Equal([2L], toggled);

        // RefreshMembers 重設 IsActive（卡 2 不再生效）：不觸發 toggle
        drawer.RefreshMembers(ctx.LoadCatalog(), []);
        Assert.False(drawer.Members.Single(m => m.CardId == 2).IsActive);
        Assert.Equal([2L], toggled);
    }

    [Fact]
    public void RefreshMembers_KeepsUnsavedTermEdits()
    {
        using var ctx = Create(_ => { });
        SeedMembers(ctx);
        var drawer = NewDrawerWithCallbacks(ctx, cardId: 1, activeIds: []);

        drawer.VisibleTerms.Single(t => t.Source == "花芽すみれ").Target = "花芽澄";
        Assert.Equal(1, drawer.DirtyCount);

        // 生效清單變動只重建成員區塊：名詞分頁（含未存修改）不動
        drawer.RefreshMembers(ctx.LoadCatalog(), [2]);

        Assert.Equal(1, drawer.DirtyCount);
        Assert.Equal("花芽澄", drawer.VisibleTerms.Single(t => t.Source == "花芽すみれ").Target);
        Assert.True(drawer.Members.Single(m => m.CardId == 2).IsActive);
    }

    [Fact]
    public async Task MineMemberCommand_EnabledOnlyWithoutCardAndNotBusy_ExecutesWithMemberName()
    {
        using var ctx = Create(_ => { });
        SeedMembers(ctx);
        var mined = new List<string>();
        var drawer = NewDrawerWithCallbacks(ctx, cardId: 1, activeIds: [], mineMember: name =>
        {
            mined.Add(name);
            return Task.CompletedTask;
        });

        var cardless = drawer.Members.Single(m => !m.HasCard);
        var withCard = drawer.Members.Single(m => m.CardId == 2);

        // IsMiningBusy 是 true 時不能執行；有卡的成員一律不能探勘
        drawer.IsMiningBusy = true;
        Assert.False(drawer.MineMemberCommand.CanExecute(cardless));
        Assert.False(drawer.MineMemberCommand.CanExecute(withCard));

        // 沒有在探勘時：沒有卡的成員可以探勘，名字傳給 mineMember
        drawer.IsMiningBusy = false;
        Assert.True(drawer.MineMemberCommand.CanExecute(cardless));
        Assert.False(drawer.MineMemberCommand.CanExecute(withCard));
        await drawer.MineMemberCommand.ExecuteAsync(cardless);
        Assert.Equal(["藍沢エマ"], mined);
    }

    [Fact]
    public void Members_WithoutCallbacks_EmptyEvenOnGroupCard()
    {
        using var ctx = Create(_ => { });
        SeedMembers(ctx);

        // 沒有傳 toggleCard／mineMember（既有測試的建法）：成員區塊不顯示
        var drawer = NewDrawer(ctx, cardId: 1);
        Assert.False(drawer.HasMembers);
        Assert.Empty(drawer.Members);
    }

    // ===== 輔助 =====

    private GlossaryDrawerViewModel NewDrawer(Context ctx, long cardId, IReadOnlyCollection<long>? activeIds = null) =>
        new(ctx.Db, ctx.LoadCatalog(), cardId, activeIds ?? [], _ => { });

    /// <summary>帶成員區塊回呼的建法：toggleCard 一定有（toggled 未傳時為 no-op），成員區塊才會顯示。</summary>
    private GlossaryDrawerViewModel NewDrawerWithCallbacks(Context ctx, long cardId,
        IReadOnlyCollection<long>? activeIds = null, List<long>? toggled = null, Func<string, Task>? mineMember = null) =>
        new(ctx.Db, ctx.LoadCatalog(), cardId, activeIds ?? [],
            onSaved: _ => { },
            toggleCard: id => toggled?.Add(id),
            mineMember: mineMember);

    private sealed class Context : IDisposable
    {
        public required string Dir { get; init; }
        public required SqliteTranslationStore Db { get; init; }

        public GlossaryCatalog LoadCatalog() => new(Db.GetGlossaryCards(), Db.GetGlossaryTerms());

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { /* SQLite 連線池可能還握著檔案 */ }
        }
    }

    private Context Create(Action<SqliteConnection> seed)
    {
        var dir = Directory.CreateTempSubdirectory("glossary-drawer-");
        _tempDirs.Add(dir.FullName);
        var db = new SqliteTranslationStore(
            NullLogger<SqliteTranslationStore>.Instance, Path.Combine(dir.FullName, "cache.db"));
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(dir.FullName, "cache.db")}"))
        {
            seed(conn);
        }

        return new Context { Dir = dir.FullName, Db = db };
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* SQLite 連線池可能還握著檔案 */ }
        }
    }
}
