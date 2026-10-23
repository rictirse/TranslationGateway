using System.Collections.Specialized;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;
using Translation.Gateway.Services;
using Translation.Gateway.ViewModels;

namespace Translation.Gateway.Wpf.Tests;

/// <summary>詞彙卡審核頁 ViewModel：用暫存 SQLite 檔加上 DI 組出的 DashboardViewModel 測試。</summary>
public sealed class GlossaryReviewViewModelTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public void DefaultFilter_ShowsOnlyCardsWithPendingTerms_AndKeepsParents()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute(@"
                INSERT INTO GlossaryCards (Id, Kind, Name, ParentId, Status) VALUES
                    (1, 'Group',     'G團體', NULL, 'Approved'),
                    (2, 'Character', 'C角色', 1,    'Pending'),
                    (3, 'Game',      'G遊戲', NULL, 'Approved')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '済み', '甲', 'Approved'),
                    (12, 2, '未1',  '乙', 'Pending'),
                    (13, 3, '却下', '丙', 'Rejected')");
        });

        var names = ctx.Review.Cards.Select(c => c.Name).ToList();

        // 沒有待審名詞的卡不列出；子卡在清單中時，上層卡也要在（分組完整）
        Assert.Contains("C角色", names);
        Assert.Contains("G團體", names);
        Assert.DoesNotContain("G遊戲", names);
        // 上層卡在前、子卡緊接其後
        Assert.True(names.IndexOf("G團體") < names.IndexOf("C角色"));
        // 篩選後自動選到第一張卡
        Assert.NotNull(ctx.Review.SelectedCard);
    }

    [Fact]
    public void SearchText_HiraganaFindsKatakanaTerm()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'ジェット', '捷風', 'Pending')");
        });

        ctx.Review.SearchText = "じぇっと";

        var card = Assert.Single(ctx.Review.Cards);
        Assert.Equal("VALORANT", card.Name);
    }

    [Fact]
    public void KindFilter_Common_ShowsOnlyCommonCards()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute(@"
                INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES
                    (1, 'Game',   'VALORANT', 'Pending'),
                    (2, 'Common', 'VTuber用語', 'Pending')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, 'ヴァンダル', '暴徒', 'Pending'),
                    (12, 2, 'ガチ恋',   '真愛粉', 'Pending')");
        });

        ctx.Review.KindFilter = "共通用語";

        var card = Assert.Single(ctx.Review.Cards);
        Assert.Equal("VTuber用語", card.Name);
        Assert.Equal("共通用語", card.KindText);
    }

    [Fact]
    public void ApproveSelectedTerms_ApprovesCheckedTerms_AndDirtyFollowsValueChanges()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', '英リサ', 'Pending')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '英リサ', '英莉莎', 'Pending'),
                    (12, 1, 'りさ',   '莉莎',   'Pending')");
        });

        var review = ctx.Review;
        var terms = review.SelectedCard!.Terms.ToList();
        terms[0].IsSelected = true;
        terms[1].IsSelected = true;

        review.ApproveSelectedTermsCommand.Execute(null);

        Assert.All(review.SelectedCard.Terms, t => Assert.Equal("Approved", t.Status));
        Assert.Equal(2, review.DirtyCount);
        Assert.True(review.HasUnsavedChanges);

        // 改回原值（Pending）就不再 dirty
        foreach (var term in review.SelectedCard.Terms)
        {
            term.Status = "Pending";
        }

        Assert.Equal(0, review.DirtyCount);
        Assert.False(review.HasUnsavedChanges);
    }

    [Fact]
    public void ApproveCard_OnlyPendingTermsBecomeApproved_RejectedUntouched()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', '英リサ', 'Pending')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '未1', '乙', 'Pending'),
                    (12, 1, '未2', '丙', 'Pending'),
                    (13, 1, '却',  '丁', 'Rejected')");
        });

        var review = ctx.Review;
        review.ApproveCardCommand.Execute(null);

        Assert.Equal("Approved", review.SelectedCard!.Status);
        Assert.Equal("Approved", review.SelectedCard.Terms.Single(t => t.Source == "未1").Status);
        Assert.Equal("Approved", review.SelectedCard.Terms.Single(t => t.Source == "未2").Status);
        Assert.Equal("Rejected", review.SelectedCard.Terms.Single(t => t.Source == "却").Status);
    }

    [Fact]
    public void Save_WritesChangesToDatabase_AndClearsUnsavedState()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, ChannelHandle, Status) VALUES (1, 'Game', 'VALORANT', '@valorant', 'Pending')");
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'ヴァンダル', '暴徒', 'Pending')");
        });

        var review = ctx.Review;
        var card = Assert.Single(review.Cards);
        card.ChannelHandle = "@valorant_jp";
        card.Terms[0].Target = "暴徒（步槍）";
        review.ToggleSelectAllCommand.Execute(null);  // 名詞預設全選：先全不選，才會走選取列的分歧
        review.SelectedTerm = card.Terms[0];
        review.ApproveSelectedTermsCommand.Execute(null);  // 沒有勾選時作用在選取的那一列（SelectedTerm）

        review.SaveCommand.Execute(null);

        Assert.False(review.HasUnsavedChanges);
        Assert.Contains("已儲存", review.StatusMessage);

        // 資料庫內容：卡片欄位與名詞的 Target、Status 都要更新
        var savedCard = Assert.Single(ctx.Db.GetAllGlossaryCards());
        Assert.Equal("@valorant_jp", savedCard.ChannelHandle);
        Assert.Equal("Pending", savedCard.Status);

        var savedTerm = Assert.Single(ctx.Db.GetAllGlossaryTerms());
        Assert.Equal("暴徒（步槍）", savedTerm.Target);
        Assert.Equal("Approved", savedTerm.Status);
    }

    [Fact]
    public void Save_WritesDatabase_NotifiesReviewCount_InMemoryStatusChangeDoesNot()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Pending')");
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'ヴァンダル', '暴徒', 'Pending')");
        });

        var signal = ctx.Provider.GetRequiredService<ReviewCountSignal>();
        var raised = 0;
        signal.Changed += (_, _) => raised++;

        var review = ctx.Review;

        // 只在記憶體裡切換狀態（核准名詞／核准卡）：還沒寫進資料庫，不通知
        review.ApproveSelectedTermsCommand.Execute(null);
        review.ApproveCardCommand.Execute(null);
        Assert.Equal(0, raised);

        // 存檔成功寫進資料庫：通知一次
        review.SaveCommand.Execute(null);
        Assert.Contains("已儲存", review.StatusMessage);
        Assert.Equal(1, raised);

        // 沒有變更的存檔不會再通知
        review.SaveCommand.Execute(null);
        Assert.Equal("沒有未儲存的變更", review.StatusMessage);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Save_ActiveCard_SetsNeedsApply_PromptSectionUnchangedUntilApplied()
    {
        using var ctx = Create(
            conn =>
            {
                conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
                conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'ヴァンダル', '暴徒', 'Approved')");
            },
            settings => settings.ActiveGlossaryCardIds = [1]);

        var review = ctx.Review;
        Assert.Contains("暴徒", ctx.GlossaryState.PromptSection);

        // 卡片與名詞都已是 Approved（沒有待審名詞）：切到「全部」才看得到
        review.CardStatusFilter = "全部";
        review.SelectedCard!.Terms[0].Target = "新譯名";
        review.SaveCommand.Execute(null);

        // 儲存後：需要按「套用到翻譯」，prompt 段落還沒變
        Assert.True(review.NeedsApplyToTranslation);
        Assert.Contains("暴徒", ctx.GlossaryState.PromptSection);
        Assert.DoesNotContain("新譯名", ctx.GlossaryState.PromptSection);

        // 套用後 prompt 才包含新譯名
        review.ApplyToTranslationCommand.Execute(null);
        Assert.False(review.NeedsApplyToTranslation);
        Assert.Contains("新譯名", ctx.GlossaryState.PromptSection);
        Assert.DoesNotContain("暴徒", ctx.GlossaryState.PromptSection);
    }

    [Fact]
    public void NeedsApply_StaysTrue_WhenLaterSaveOnlyTouchesInactiveCard()
    {
        using var ctx = Create(
            conn =>
            {
                conn.Execute(@"
                    INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES
                        (1, 'Game', 'VALORANT', 'Approved'),
                        (2, 'Game', 'APEX', 'Approved')");
                conn.Execute(@"
                    INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                        (11, 1, 'ヴァンダル', '暴徒', 'Approved'),
                        (21, 2, 'レイス', '惡靈', 'Approved')");
            },
            settings => settings.ActiveGlossaryCardIds = [1]);

        var review = ctx.Review;
        review.CardStatusFilter = "全部";
        review.SelectedCard = review.Cards.Single(c => c.Name == "VALORANT");
        review.SelectedCard.Terms[0].Target = "新譯名";
        review.SaveCommand.Execute(null);
        Assert.True(review.NeedsApplyToTranslation);

        // 第二次只改非生效卡：前一次的變更還沒套用，提示不能消失
        review.SelectedCard = review.Cards.Single(c => c.Name == "APEX");
        review.SelectedCard.Terms[0].Target = "惡靈2";
        review.SaveCommand.Execute(null);
        Assert.True(review.NeedsApplyToTranslation);
    }

    [Fact]
    public void ClearingNullFieldToEmpty_IsNotDirty_AndEditingDoesNotRebuildVisibleTerms()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Pending')");
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'ヴァンダル', '暴徒', 'Pending')");
        });

        var review = ctx.Review;
        var card = review.SelectedCard!;
        var visibleTermsRaised = 0;
        review.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GlossaryReviewViewModel.VisibleTerms)) visibleTermsRaised++;
        };
        var statusTextRaised = 0;
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GlossaryReviewCardItem.StatusText)) statusTextRaised++;
        };

        // TextBox 清空後是空字串，與資料庫的 NULL 視為相同
        card.Terms[0].Aliases = string.Empty;
        card.Description = string.Empty;
        Assert.Equal(0, review.DirtyCount);

        // 編輯名詞內容或狀態時不重建 VisibleTerms（DataGrid 編輯中換 ItemsSource 會中斷編輯）
        card.Terms[0].Target = "暴徒步槍";
        card.Terms[0].Status = "Approved";
        Assert.Equal(0, visibleTermsRaised);

        // 卡片狀態改變時，狀態文字要通知畫面更新
        review.ApproveCardCommand.Execute(null);
        Assert.Equal("已核准", card.StatusText);
        Assert.True(statusTextRaised > 0);
    }

    [Fact]
    public void Load_TermsDefaultAllSelected_AndVisibleTermsOrderedById()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', '英リサ', 'Pending')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (12, 1, 'りさ',   '莉莎',   'Pending'),
                    (11, 1, '英リサ', '英莉莎', 'Pending'),
                    (13, 1, 'リサ',   '莉莎',   'Pending')");
        });

        var review = ctx.Review;

        Assert.NotEmpty(review.VisibleTerms);
        Assert.All(review.VisibleTerms, t => Assert.True(t.IsSelected));
        Assert.True(review.AllVisibleSelected);
        Assert.Equal([11L, 12L, 13L], review.VisibleTerms.Select(t => t.Id).ToList());
    }

    [Fact]
    public void ToggleSelectAll_UnchecksAllThenChecksAll_FilteredOutTermsUntouched()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', '英リサ', 'Pending')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '未1', '甲', 'Pending'),
                    (12, 1, '未2', '乙', 'Approved'),
                    (13, 1, '未3', '丙', 'Rejected')");
        });

        var review = ctx.Review;
        var card = review.SelectedCard!;

        review.ToggleSelectAllCommand.Execute(null);
        Assert.All(review.VisibleTerms, t => Assert.False(t.IsSelected));
        Assert.False(review.AllVisibleSelected);

        // 名詞狀態篩選「待審」：切換只作用在 VisibleTerms，被篩選掉的名詞勾選狀態不變
        review.TermStatusFilter = "待審";
        Assert.Equal([11L], review.VisibleTerms.Select(t => t.Id).ToList());
        review.ToggleSelectAllCommand.Execute(null);
        Assert.All(review.VisibleTerms, t => Assert.True(t.IsSelected));
        Assert.True(card.Terms.Single(t => t.Id == 11).IsSelected);
        Assert.False(card.Terms.Single(t => t.Id == 12).IsSelected);
        Assert.False(card.Terms.Single(t => t.Id == 13).IsSelected);

        // 再執行一次又全部取消勾選（仍只作用在 VisibleTerms）
        review.ToggleSelectAllCommand.Execute(null);
        Assert.False(card.Terms.Single(t => t.Id == 11).IsSelected);
        Assert.False(card.Terms.Single(t => t.Id == 12).IsSelected);
        Assert.False(card.Terms.Single(t => t.Id == 13).IsSelected);
    }

    [Fact]
    public void TermSelectionStatusTargetChanges_DoNotRebuildOrReorderVisibleTerms()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', '英リサ', 'Pending')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '未1', '甲', 'Pending'),
                    (12, 1, '未2', '乙', 'Pending'),
                    (13, 1, '未3', '丙', 'Pending')");
        });

        var review = ctx.Review;
        var visible = review.VisibleTerms;
        var collectionChanged = 0;
        ((INotifyCollectionChanged)visible).CollectionChanged += (_, _) => collectionChanged++;

        visible[1].IsSelected = false;
        visible[0].Status = "Approved";
        visible[2].Target = "新譯名";

        Assert.Same(visible, review.VisibleTerms);
        Assert.Equal([11L, 12L, 13L], review.VisibleTerms.Select(t => t.Id).ToList());
        Assert.Equal(0, collectionChanged);
    }

    [Fact]
    public void AllVisibleSelected_FollowsTermToggle_AndRaisesPropertyChanged()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', '英リサ', 'Pending')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '未1', '甲', 'Pending'),
                    (12, 1, '未2', '乙', 'Pending')");
        });

        var review = ctx.Review;
        Assert.True(review.AllVisibleSelected);

        var raised = 0;
        review.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GlossaryReviewViewModel.AllVisibleSelected)) raised++;
        };

        review.VisibleTerms[0].IsSelected = false;
        Assert.False(review.AllVisibleSelected);
        Assert.Equal(1, raised);

        review.VisibleTerms[0].IsSelected = true;
        Assert.True(review.AllVisibleSelected);
        Assert.Equal(2, raised);

        // 標頭 CheckBox 以 TwoWay 綁定直接設定：全部取消／全部勾選
        review.AllVisibleSelected = false;
        Assert.All(review.VisibleTerms, t => Assert.False(t.IsSelected));
        review.AllVisibleSelected = true;
        Assert.All(review.VisibleTerms, t => Assert.True(t.IsSelected));
    }

    [Fact]
    public void Save_DuplicateSources_ShowsErrorAndKeepsUnsavedChanges()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', '英リサ', 'Pending')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '甲', '甲譯', 'Pending'),
                    (12, 1, '乙', '乙譯', 'Pending')");
        });

        var review = ctx.Review;
        review.SelectedCard!.Terms[0].Source = "同名";
        review.SelectedCard.Terms[1].Source = "同名";

        review.SaveCommand.Execute(null);

        Assert.Contains("儲存失敗", review.StatusMessage);
        Assert.True(review.HasUnsavedChanges);
        Assert.Equal(2, review.DirtyCount);

        // 交易回復：資料庫兩筆都沒有被更新
        var terms = ctx.Db.GetAllGlossaryTerms().OrderBy(t => t.Id).ToList();
        Assert.Equal("甲", terms[0].Source);
        Assert.Equal("乙", terms[1].Source);
    }

    [Fact]
    public void EditAliases_MakesCardDirty_AndSavesNormalizedValue()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'Apex Legends', 'Pending')");
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'エペ', '艾佩', 'Pending')");
        });

        var review = ctx.Review;
        var card = review.SelectedCard!;
        Assert.False(card.IsDirty);

        card.Aliases = " APEX \nApex Legends\napex\nエペ";
        Assert.True(card.IsDirty);
        Assert.Equal(1, review.DirtyCount);

        review.SaveCommand.Execute(null);

        // 存檔後 store 裡是正規化後的值
        var saved = Assert.Single(ctx.Db.GetAllGlossaryCards());
        Assert.Equal("APEX\nエペ", saved.Aliases);
        Assert.False(review.HasUnsavedChanges);
    }

    [Fact]
    public void Revert_DiscardsChanges_AndRestoresDatabaseContents()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Pending')");
            conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, 'ヴァンダル', '暴徒', 'Pending')");
        });

        var review = ctx.Review;
        review.SelectedCard!.Name = "改過的名字";
        review.SelectedCard.Terms[0].Target = "改過的譯名";
        Assert.True(review.HasUnsavedChanges);

        review.RevertCommand.Execute(null);

        Assert.False(review.HasUnsavedChanges);
        Assert.Equal("VALORANT", Assert.Single(review.Cards).Name);
        Assert.Equal("暴徒", review.SelectedCard!.Terms[0].Target);
        Assert.Equal("Pending", review.SelectedCard.Status);
    }

    [Fact]
    public async Task ImportTerms_AddsUnsavedPendingTerms_StoreNotWrittenUntilSave()
    {
        using var ctx = Create(
            conn =>
            {
                conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'LoL', 'Pending')");
                conn.Execute("INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, '既存名詞', '既有譯名', 'Approved')");
            },
            settings =>
            {
                settings.Lorebook.ApiHost = "http://127.0.0.1:1234";
                settings.Lorebook.Model = "glm-test";
            });
        ctx.Llm.JsonReplies.Enqueue(
            "{\"terms\":[" +
            "{\"source\":\"アーリ\",\"target\":\"阿璃\",\"aliases\":[\"Ahri\"],\"category\":\"英雄\",\"note\":\"台服譯名\"}," +
            "{\"source\":\"ヤスオ\",\"target\":\"疾風壁\",\"aliases\":[],\"category\":\"英雄\",\"note\":\"\"}]}");
        Assert.NotNull(ctx.Provider.GetRequiredService<TermImportService>());

        var review = ctx.Review;
        // 預設篩選「有待審名詞」不會列出這張卡（只有已核准的名詞）：改成全部再選取
        review.CardStatusFilter = "全部";
        review.SelectedCard = Assert.Single(review.Cards);
        review.ImportInstruction = "左邊英文、右邊台服譯名";
        review.ImportText = "Ahri 阿璃\nヤスオ 疾風壁";
        Assert.True(review.ImportTermsCommand.CanExecute(null));

        await review.ImportTermsCommand.ExecuteAsync(null);

        // 以未存檔的新列加進選取卡片與目前顯示清單（Id 0、Pending、dirty）
        var card = review.SelectedCard!;
        var ahri = Assert.Single(card.Terms, t => t.Source == "アーリ");
        var yasuo = Assert.Single(card.Terms, t => t.Source == "ヤスオ");
        Assert.True(ahri.IsNew);
        Assert.Equal("Pending", ahri.Status);
        Assert.True(ahri.IsDirty);
        Assert.Equal(1, ahri.CardId);
        Assert.Contains(ahri, review.VisibleTerms);
        Assert.Contains(yasuo, review.VisibleTerms);
        Assert.True(review.HasUnsavedChanges);

        // 資料裡沒有日文「アーリ」：保留但註記推測；「ヤスオ」在資料裡：不加註
        Assert.StartsWith("日文由模型推測", ahri.Note, StringComparison.Ordinal);
        Assert.Contains("台服譯名", ahri.Note, StringComparison.Ordinal);
        Assert.Null(yasuo.Note);

        // prompt 帶卡名、種類與說明；既有名詞在「已有名詞」清單中
        var prompt = Assert.Single(ctx.Llm.UserPrompts);
        Assert.Contains("卡名：LoL（遊戲）", prompt, StringComparison.Ordinal);
        Assert.Contains("左邊英文、右邊台服譯名", prompt, StringComparison.Ordinal);
        Assert.Contains("既存名詞", prompt, StringComparison.Ordinal);

        // 存檔前資料庫完全不動；成功後清空貼上內容、保留說明
        Assert.Single(ctx.Db.GetAllGlossaryTerms());
        Assert.Equal(string.Empty, review.ImportText);
        Assert.Equal("左邊英文、右邊台服譯名", review.ImportInstruction);

        // 按儲存才寫進資料庫（inserted terms）
        review.SaveCommand.Execute(null);
        Assert.False(review.HasUnsavedChanges);
        var savedAhri = ctx.Db.GetAllGlossaryTerms().Single(t => t.Source == "アーリ");
        var savedYasuo = ctx.Db.GetAllGlossaryTerms().Single(t => t.Source == "ヤスオ");
        Assert.Equal("阿璃", savedAhri.Target);
        Assert.Equal("Pending", savedAhri.Status);
        Assert.Equal(1, savedAhri.CardId);
        Assert.Equal("疾風壁", savedYasuo.Target);
    }

    [Fact]
    public void ImportTerms_CanExecute_RequiresCardTextLimitAndMiningConfig()
    {
        using var ctx = Create(
            conn => conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'LoL', 'Pending')"),
            settings =>
            {
                settings.Lorebook.ApiHost = "http://127.0.0.1:1234";
                settings.Lorebook.Model = "glm-test";
            });

        var review = ctx.Review;
        // 預設篩選「有待審名詞」不會列出沒有名詞的卡：改成全部再選取
        review.CardStatusFilter = "全部";
        review.SelectedCard = Assert.Single(review.Cards);

        // 沒有文字／空白文字
        Assert.False(review.ImportTermsCommand.CanExecute(null));
        review.ImportText = "   ";
        Assert.False(review.ImportTermsCommand.CanExecute(null));

        review.ImportText = "Ahri 阿璃";
        Assert.True(review.ImportTermsCommand.CanExecute(null));

        // 超過長度上限
        review.ImportText = new string('a', TermImportService.MaxInputLength + 1);
        Assert.False(review.ImportTermsCommand.CanExecute(null));

        // 沒有選取的卡（狀態篩選把卡濾掉）
        review.ImportText = "Ahri 阿璃";
        review.CardStatusFilter = "已拒絕";
        Assert.Null(review.SelectedCard);
        Assert.False(review.ImportTermsCommand.CanExecute(null));
    }

    [Fact]
    public void ImportTerms_CanExecute_FalseWithoutMiningConfig()
    {
        using var ctx = Create(
            conn => conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'LoL', 'Pending')"));

        var review = ctx.Review;
        review.ImportText = "Ahri 阿璃";

        // 知識庫模型未設定（Lorebook 空）
        Assert.False(review.ImportTermsCommand.CanExecute(null));
    }

    // ===== 合併到既有卡／刪除整張卡 =====

    /// <summary>共用種子：うるは先輩（探勘建的，生效中）、一ノ瀬Uruha（既有卡，名詞與來源卡衝突一個）、APEX（不同種類）。</summary>
    private static void SeedMergeCards(SqliteConnection conn)
    {
        conn.Execute(@"
            INSERT INTO GlossaryCards (Id, Kind, Name, Aliases, Status) VALUES
                (1, 'Character', 'うるは先輩', NULL, 'Pending'),
                (2, 'Character', '一ノ瀬Uruha', 'ウルハ', 'Approved'),
                (3, 'Game',      'APEX', NULL, 'Approved')");
        conn.Execute(@"
            INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                (11, 1, 'うるは先輩', '宇留葉前輩', 'Pending'),
                (12, 1, 'セナ',       '瀨名',       'Approved'),
                (21, 2, 'うるは先輩', '一之瀨宇留葉', 'Approved'),
                (31, 3, 'エペ',       '艾佩',       'Approved')");
    }

    [Fact]
    public void MergeCandidates_ExcludeSelected_EmptySearchListsSameKindOnly()
    {
        using var ctx = Create(SeedMergeCards);

        var review = ctx.Review;
        review.CardStatusFilter = "全部";
        review.SelectedCard = review.Cards.Single(c => c.Name == "うるは先輩");

        review.ToggleMergePanelCommand.Execute(null);
        Assert.True(review.IsMergePanelOpen);

        // 搜尋空白：只列同種類（角色）的卡，不含選取的卡本身
        Assert.Equal(["一ノ瀬Uruha"], review.MergeCandidates.Select(c => c.Name).ToList());
    }

    [Fact]
    public void MergeCandidates_SearchFindsByCardAliasAndTermSource()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute(@"
                INSERT INTO GlossaryCards (Id, Kind, Name, Aliases, Status) VALUES
                    (1, 'Character', 'うるは先輩', NULL, 'Pending'),
                    (2, 'Character', '一ノ瀬Uruha', 'ウルハ', 'Approved'),
                    (3, 'Group',     'クライム', 'crimz.', 'Approved'),
                    (4, 'Game',      'APEX', NULL, 'Approved')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, 'うるは先輩', '宇留葉前輩', 'Pending'),
                    (31, 3, 'せんぱい',   '前輩',       'Approved'),
                    (41, 4, 'エペ',       '艾佩',       'Approved')");
        });

        var review = ctx.Review;
        review.CardStatusFilter = "全部";
        review.SelectedCard = review.Cards.Single(c => c.Name == "うるは先輩");
        review.IsMergePanelOpen = true;

        // 卡片別名（片假名寫法）
        review.MergeSearchText = "ウルハ";
        Assert.Equal(["一ノ瀬Uruha"], review.MergeCandidates.Select(c => c.Name).ToList());

        // 名詞的 Source（不同種類的卡也找得到）
        review.MergeSearchText = "せんぱい";
        Assert.Equal(["クライム"], review.MergeCandidates.Select(c => c.Name).ToList());

        // 找不到時清單是空的
        review.MergeSearchText = "找不到的字";
        Assert.Empty(review.MergeCandidates);
    }

    [Fact]
    public void MergeCard_CallsStoreWithIds_ReloadsAndSelectsTarget()
    {
        using var ctx = Create(SeedMergeCards, settings => settings.ActiveGlossaryCardIds = [1], recordStoreCalls: true);

        var review = ctx.Review;
        review.CardStatusFilter = "全部";
        review.SelectedCard = review.Cards.Single(c => c.Name == "うるは先輩");
        review.IsMergePanelOpen = true;
        review.MergeSearchText = "一ノ瀬";
        review.MergeTarget = Assert.Single(review.MergeCandidates);
        Assert.True(review.MergeCardCommand.CanExecute(null));

        var confirmMessages = new List<string>();
        review.ConfirmDialog = message =>
        {
            confirmMessages.Add(message);
            return true;
        };

        review.MergeCardCommand.Execute(null);

        // 呼叫 store 時參數正確（來源、目標）
        Assert.Equal([(1L, 2L)], ctx.Recorder!.MergeCalls);

        // 確認訊息用計畫算好的數字（1 個搬過去、來源卡名會變別名）
        var message = Assert.Single(confirmMessages);
        Assert.Contains("把「うるは先輩」併進「一ノ瀬Uruha」", message);
        Assert.Contains("其他 1 個搬過去並改成待審", message);
        Assert.Contains("合併後「うるは先輩」會被刪除", message);

        // 重新載入後選取的是目標卡，面板收起、搜尋與目標清空
        Assert.Equal("一ノ瀬Uruha", review.SelectedCard!.Name);
        Assert.False(review.IsMergePanelOpen);
        Assert.Null(review.MergeTarget);
        Assert.Equal(string.Empty, review.MergeSearchText);

        // 結果訊息含搬過去／丟掉／別名的數量；來源卡生效中 → 要套用到翻譯
        Assert.Equal("已把「うるは先輩」併進「一ノ瀬Uruha」：搬過去 1 個名詞（待審）、重複丟掉 1 個、新增 1 個別名",
            review.StatusMessage);
        Assert.True(review.NeedsApplyToTranslation);

        // 資料庫：來源卡刪掉、卡名變別名、衝突名詞丟掉、其他名詞搬到目標卡改 Pending
        var cards = ctx.Db.GetAllGlossaryCards();
        Assert.DoesNotContain(cards, c => c.Id == 1);
        Assert.Equal("ウルハ\nうるは先輩", cards.Single(c => c.Id == 2).Aliases);
        var terms = ctx.Db.GetAllGlossaryTerms();
        Assert.DoesNotContain(terms, t => t.Id == 11);
        Assert.Equal(2L, terms.Single(t => t.Id == 12).CardId);
        Assert.Equal("Pending", terms.Single(t => t.Id == 12).Status);
        Assert.Equal("Approved", terms.Single(t => t.Id == 21).Status);
    }

    [Fact]
    public void MergeAndDelete_UnsavedChanges_DoNotCallStore()
    {
        using var ctx = Create(SeedMergeCards, recordStoreCalls: true);

        var review = ctx.Review;
        review.CardStatusFilter = "全部";
        review.SelectedCard = review.Cards.Single(c => c.Name == "うるは先輩");
        review.SelectedCard.Terms[0].Target = "改過的譯名";
        Assert.True(review.HasUnsavedChanges);
        review.IsMergePanelOpen = true;
        review.MergeSearchText = "一ノ瀬";
        review.MergeTarget = Assert.Single(review.MergeCandidates);

        review.MergeCardCommand.Execute(null);
        Assert.Equal("請先儲存或放棄未儲存的變更，再合併", review.StatusMessage);

        review.DeleteCardCommand.Execute(null);
        Assert.Equal("請先儲存或放棄未儲存的變更，再刪除", review.StatusMessage);

        // 沒有呼叫 store，資料庫也沒有變
        Assert.Empty(ctx.Recorder!.MergeCalls);
        Assert.Empty(ctx.Recorder!.DeleteCalls);
        Assert.Equal(3, ctx.Db.GetAllGlossaryCards().Count);
    }

    [Fact]
    public void MergeAndDelete_ConfirmDialogFalse_DoNotCallStore()
    {
        using var ctx = Create(SeedMergeCards, recordStoreCalls: true);

        var review = ctx.Review;
        review.CardStatusFilter = "全部";
        review.SelectedCard = review.Cards.Single(c => c.Name == "うるは先輩");
        review.ConfirmDialog = _ => false;
        review.IsMergePanelOpen = true;
        review.MergeSearchText = "一ノ瀬";
        review.MergeTarget = Assert.Single(review.MergeCandidates);

        review.MergeCardCommand.Execute(null);
        Assert.True(review.IsMergePanelOpen);

        review.DeleteCardCommand.Execute(null);

        Assert.Empty(ctx.Recorder!.MergeCalls);
        Assert.Empty(ctx.Recorder!.DeleteCalls);
        Assert.Equal(3, ctx.Db.GetAllGlossaryCards().Count);
        Assert.Equal(3, review.Cards.Count);
    }

    [Fact]
    public void DeleteCard_RemovesCardAndTerms_SetsNeedsApplyWhenActive()
    {
        using var ctx = Create(
            conn =>
            {
                conn.Execute(@"
                    INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES
                        (1, 'Character', '英リサ', 'Pending'),
                        (2, 'Group', 'にじさんじ', 'Approved')");
                conn.Execute(@"
                    INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                        (11, 1, '英リサ', '英莉莎', 'Pending'),
                        (21, 2, 'さんじ', '彩虹社', 'Approved')");
            },
            settings => settings.ActiveGlossaryCardIds = [1],
            recordStoreCalls: true);

        var review = ctx.Review;
        review.CardStatusFilter = "全部";
        review.SelectedCard = review.Cards.Single(c => c.Name == "英リサ");
        review.ConfirmDialog = _ => true;

        review.DeleteCardCommand.Execute(null);

        Assert.Equal([(1L)], ctx.Recorder!.DeleteCalls);
        Assert.Equal("已刪除「英リサ」", review.StatusMessage);

        // 資料庫：卡與它的名詞都刪掉；清單重新載入後那張卡不見了
        Assert.DoesNotContain(ctx.Db.GetAllGlossaryCards(), c => c.Id == 1);
        Assert.DoesNotContain(ctx.Db.GetAllGlossaryTerms(), t => t.CardId == 1);
        Assert.DoesNotContain(review.Cards, c => c.Name == "英リサ");
        Assert.NotNull(review.SelectedCard);

        // 被刪的卡生效中 → 要套用到翻譯
        Assert.True(review.NeedsApplyToTranslation);
    }

    [Fact]
    public void DeleteCard_InactiveCard_DoesNotNeedApply()
    {
        using var ctx = Create(
            conn =>
            {
                conn.Execute(@"
                    INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES
                        (1, 'Character', '英リサ', 'Pending'),
                        (2, 'Group', 'にじさんじ', 'Approved')");
                conn.Execute(
                    "INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES (11, 1, '英リサ', '英莉莎', 'Pending')");
            });

        var review = ctx.Review;
        review.CardStatusFilter = "全部";
        review.SelectedCard = review.Cards.Single(c => c.Name == "英リサ");
        review.ConfirmDialog = _ => true;

        review.DeleteCardCommand.Execute(null);

        Assert.Equal("已刪除「英リサ」", review.StatusMessage);
        Assert.False(review.NeedsApplyToTranslation);
    }

    [Fact]
    public void MergeCard_StoreThrows_ShowsFailureAndKeepsData()
    {
        using var ctx = Create(SeedMergeCards, recordStoreCalls: true);
        ctx.Recorder!.ThrowOnMerge = true;

        var review = ctx.Review;
        review.CardStatusFilter = "全部";
        review.SelectedCard = review.Cards.Single(c => c.Name == "うるは先輩");
        review.IsMergePanelOpen = true;
        review.MergeSearchText = "一ノ瀬";
        review.MergeTarget = Assert.Single(review.MergeCandidates);
        review.ConfirmDialog = _ => true;

        review.MergeCardCommand.Execute(null);

        Assert.Equal("合併失敗：合併測試錯誤", review.StatusMessage);

        // 資料不變：三張卡都還在，清單沒有重新載入，面板也沒收
        Assert.Equal(3, ctx.Db.GetAllGlossaryCards().Count);
        Assert.Equal(3, review.Cards.Count);
        Assert.True(review.IsMergePanelOpen);
    }

    [Fact]
    public void DeleteCard_StoreThrows_ShowsFailureAndKeepsData()
    {
        using var ctx = Create(SeedMergeCards, recordStoreCalls: true);
        ctx.Recorder!.ThrowOnDelete = true;

        var review = ctx.Review;
        review.CardStatusFilter = "全部";
        review.SelectedCard = review.Cards.Single(c => c.Name == "うるは先輩");
        review.ConfirmDialog = _ => true;

        review.DeleteCardCommand.Execute(null);

        Assert.Equal("刪除失敗：刪除測試錯誤", review.StatusMessage);

        // 資料不變：卡還在資料庫與清單中
        Assert.Single(ctx.Db.GetAllGlossaryCards(), c => c.Id == 1);
        Assert.Contains(review.Cards, c => c.Name == "うるは先輩");
    }

    // ===== 刪除名詞 =====

    /// <summary>刪除名詞測試共用的種子：角色卡「英リサ」帶三個待審名詞。</summary>
    private static void SeedTermDeletion(SqliteConnection conn)
    {
        conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Character', '英リサ', 'Pending')");
        conn.Execute(@"
            INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                (11, 1, '英リサ', '英莉莎', 'Pending'),
                (12, 1, 'りさ',   '莉莎',   'Pending'),
                (13, 1, 'リサ',   '莉莎',   'Pending')");
    }

    [Fact]
    public void DeleteSelectedTerms_CheckedTerms_CallsStoreRemovesRowsAndShowsMessage()
    {
        using var ctx = Create(SeedTermDeletion, recordStoreCalls: true);

        var review = ctx.Review;
        var card = review.SelectedCard!;
        review.ToggleSelectAllCommand.Execute(null);  // 全不選
        card.Terms.Single(t => t.Id == 11).IsSelected = true;
        card.Terms.Single(t => t.Id == 12).IsSelected = true;

        var confirmMessages = new List<string>();
        review.ConfirmDialog = message =>
        {
            confirmMessages.Add(message);
            return true;
        };
        var subtitleRaised = 0;
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GlossaryReviewCardItem.Subtitle)) subtitleRaised++;
        };

        review.DeleteSelectedTermsCommand.Execute(null);

        // store 收到這 2 個 Id；確認訊息列出要刪的名詞
        Assert.Equal([[11L, 12L]], ctx.Recorder!.DeleteTermCalls.Select(c => c.ToList()).ToList());
        var message = Assert.Single(confirmMessages);
        Assert.Contains("刪除 2 個名詞？", message);
        Assert.Contains("英リサ → 英莉莎", message);
        Assert.Contains("會從資料庫刪除（含聽錯字修正），無法復原。", message);

        // 畫面、卡片與資料庫都少了這 2 個；沒有重新載入（另一列的物件原封不動）
        Assert.Equal([13L], review.VisibleTerms.Select(t => t.Id).ToList());
        Assert.Equal([13L], card.Terms.Select(t => t.Id).ToList());
        Assert.Equal([13L], ctx.Db.GetAllGlossaryTerms().Select(t => t.Id).ToList());

        Assert.Equal("已刪除 2 個名詞", review.StatusMessage);
        Assert.False(review.HasUnsavedChanges);
        Assert.False(review.NeedsApplyToTranslation);

        // 左欄卡片的 Subtitle 計數（計算值與通知）都更新
        Assert.Equal("角色・0/1", card.Subtitle);
        Assert.True(subtitleRaised > 0);
    }

    [Fact]
    public void DeleteSelectedTerms_NoCheckBox_DeletesSelectedRow()
    {
        using var ctx = Create(SeedTermDeletion, recordStoreCalls: true);

        var review = ctx.Review;
        var card = review.SelectedCard!;
        review.ToggleSelectAllCommand.Execute(null);  // 全不選
        review.SelectedTerm = card.Terms.Single(t => t.Id == 12);

        review.DeleteSelectedTermsCommand.Execute(null);

        // 沒有勾選時作用在選取的那一列
        Assert.Equal([[12L]], ctx.Recorder!.DeleteTermCalls.Select(c => c.ToList()).ToList());
        Assert.Equal([11L, 13L], review.VisibleTerms.Select(t => t.Id).ToList());
        Assert.Equal([11L, 13L], card.Terms.Select(t => t.Id).ToList());
        Assert.Equal("已刪除 1 個名詞", review.StatusMessage);
        Assert.Null(review.SelectedTerm);
    }

    [Fact]
    public void DeleteSelectedTerms_ConfirmDialogFalse_KeepsEverything()
    {
        using var ctx = Create(SeedTermDeletion, recordStoreCalls: true);

        var review = ctx.Review;
        var card = review.SelectedCard!;
        review.ConfirmDialog = _ => false;

        review.DeleteSelectedTermsCommand.Execute(null);

        // 不呼叫 store，畫面與資料庫都不變
        Assert.Empty(ctx.Recorder!.DeleteTermCalls);
        Assert.Equal([11L, 12L, 13L], review.VisibleTerms.Select(t => t.Id).ToList());
        Assert.Equal(3, card.Terms.Count);
        Assert.Equal(3, ctx.Db.GetAllGlossaryTerms().Count);
        Assert.Equal(string.Empty, review.StatusMessage);
    }

    [Fact]
    public void DeleteSelectedTerms_NewRows_SkipStoreAndRemoveFromView()
    {
        using var ctx = Create(SeedTermDeletion, recordStoreCalls: true);

        var review = ctx.Review;
        var card = review.SelectedCard!;
        // 還沒存檔的新列（Id 0，例如貼上整理加進來的）：直接加進卡片，再觸發重建進 VisibleTerms
        card.Terms.Add(new GlossaryReviewTermItem(new GlossaryTerm
        {
            Id = 0,
            CardId = 1,
            Source = "貼上新列",
            Target = "新列譯名",
            Status = GlossaryReviewTermItem.PendingStatus,
        }));
        review.TermStatusFilter = "待審";
        review.TermStatusFilter = "全部";
        Assert.Equal(4, review.VisibleTerms.Count(t => t.IsSelected));
        Assert.True(review.HasUnsavedChanges);

        review.DeleteSelectedTermsCommand.Execute(null);

        // Id 0 的列不送 store；既有的照送；兩列都從畫面移除
        Assert.Equal([[11L, 12L, 13L]], ctx.Recorder!.DeleteTermCalls.Select(c => c.ToList()).ToList());
        Assert.Empty(review.VisibleTerms);
        Assert.Empty(card.Terms);
        Assert.Empty(ctx.Db.GetAllGlossaryTerms());
        Assert.Equal("已刪除 4 個名詞", review.StatusMessage);
        Assert.False(review.HasUnsavedChanges);
    }

    [Fact]
    public void DeleteSelectedTerms_KeepsUnsavedChangesOnOtherCards()
    {
        using var ctx = Create(conn =>
        {
            conn.Execute(@"
                INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES
                    (1, 'Character', '英リサ', 'Pending'),
                    (2, 'Group', 'ぶいすぽっ!', 'Pending')");
            conn.Execute(@"
                INSERT INTO GlossaryTerms (Id, CardId, Source, Target, Status) VALUES
                    (11, 1, '英リサ',   '英莉莎', 'Pending'),
                    (12, 1, 'りさ',     '莉莎',   'Pending'),
                    (21, 2, 'もかちゃん', 'Moka醬', 'Pending')");
        });

        var review = ctx.Review;
        // 另一張卡有未儲存的修改
        var otherCard = review.Cards.Single(c => c.Id == 2);
        otherCard.Terms[0].Target = "改過的譯名";
        Assert.True(review.HasUnsavedChanges);

        // 刪除選取卡（英リサ）的名詞：預設全選
        review.DeleteSelectedTermsCommand.Execute(null);
        Assert.Equal("已刪除 2 個名詞", review.StatusMessage);

        // 不重新載入：另一張卡的修改還在，未儲存狀態也還在
        Assert.Equal("改過的譯名", otherCard.Terms[0].Target);
        Assert.True(otherCard.Terms[0].IsDirty);
        Assert.True(review.HasUnsavedChanges);
        Assert.Equal(1, review.DirtyCount);
    }

    [Fact]
    public void DeleteSelectedTerms_StoreThrows_ShowsFailureAndKeepsRows()
    {
        using var ctx = Create(SeedTermDeletion, recordStoreCalls: true);
        ctx.Recorder!.ThrowOnDeleteTerms = true;

        var review = ctx.Review;
        var card = review.SelectedCard!;
        // 混一個 Id 0 的新列：失敗時連它都不能從畫面移除
        card.Terms.Add(new GlossaryReviewTermItem(new GlossaryTerm
        {
            Id = 0,
            CardId = 1,
            Source = "貼上新列",
            Target = "新列譯名",
            Status = GlossaryReviewTermItem.PendingStatus,
        }));
        review.TermStatusFilter = "待審";
        review.TermStatusFilter = "全部";

        review.DeleteSelectedTermsCommand.Execute(null);

        Assert.Equal("刪除失敗：名詞刪除測試錯誤", review.StatusMessage);

        // 畫面不動（含 Id 0 的列）、資料庫不變
        Assert.Equal(4, review.VisibleTerms.Count);
        Assert.Equal(4, card.Terms.Count);
        Assert.Equal(3, ctx.Db.GetAllGlossaryTerms().Count);
        Assert.True(review.HasUnsavedChanges);
    }

    [Fact]
    public void DeleteSelectedTerms_ActiveCard_SetsNeedsApply()
    {
        using var ctx = Create(SeedTermDeletion, settings => settings.ActiveGlossaryCardIds = [1]);

        var review = ctx.Review;
        review.ConfirmDialog = _ => true;

        review.DeleteSelectedTermsCommand.Execute(null);

        // 生效中卡片的名詞被刪：prompt 還引用舊名詞，要再按「套用到翻譯」
        Assert.Equal("已刪除 3 個名詞", review.StatusMessage);
        Assert.True(review.NeedsApplyToTranslation);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* SQLite 連線池可能還握著檔案 */ }
        }
    }

    // ===== 輔助 =====

    private sealed class Context : IDisposable
    {
        public required string Dir { get; init; }
        public required SqliteTranslationStore Db { get; init; }
        public required StubLorebookLlm Llm { get; init; }
        public GlossaryStoreRecorder? Recorder { get; init; }
        public required ServiceProvider Provider { get; init; }

        public GlossaryReviewViewModel Review => Provider.GetRequiredService<GlossaryReviewViewModel>();
        public DashboardViewModel Dashboard => Provider.GetRequiredService<DashboardViewModel>();
        public GlossaryState GlossaryState => Provider.GetRequiredService<GlossaryState>();

        public void Dispose() => Provider.Dispose();
    }

    private Context Create(Action<SqliteConnection> seed, Action<AppSettings>? configureSettings = null,
        bool recordStoreCalls = false)
    {
        var dir = Directory.CreateTempSubdirectory("glossary-review-");
        _tempDirs.Add(dir.FullName);
        var dbPath = Path.Combine(dir.FullName, "cache.db");
        var db = new SqliteTranslationStore(NullLogger<SqliteTranslationStore>.Instance, dbPath);
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            seed(conn);
        }

        var settings = new SettingsService(
            NullLogger<SettingsService>.Instance, Path.Combine(dir.FullName, "settings.json"));
        configureSettings?.Invoke(settings.Current);
        var llm = new StubLorebookLlm();
        var recorder = recordStoreCalls ? new GlossaryStoreRecorder(db) : null;

        return new Context
        {
            Dir = dir.FullName,
            Db = db,
            Llm = llm,
            Recorder = recorder,
            Provider = BuildProvider(settings, db, llm, recorder),
        };
    }

    /// <summary>不連網的假影片資訊查詢：一律回 null（查不到）。</summary>
    private sealed class StubVideoInfoProvider : IVideoInfoProvider
    {
        public Task<StreamMetadata?> GetYouTubeInfoAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult<StreamMetadata?>(null);
    }

    /// <summary>UiSmokeTests.BuildProvider 的精簡版：只註冊審核頁與 DashboardViewModel 需要的服務。</summary>
    private static ServiceProvider BuildProvider(
        SettingsService settings, SqliteTranslationStore db, StubLorebookLlm llm, GlossaryStoreRecorder? recorder)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton<ITranslationStore>(db);
        services.AddSingleton<IGlossaryStore>(recorder is not null ? recorder : db);
        services.AddSingleton<ISubtitleLogStore>(db);
        services.AddSingleton<StreamSession>();
        services.AddSingleton<SubtitleLogWriter>();
        services.AddSingleton<StreamCardRecorder>();
        services.AddSingleton<GlossaryState>();
        services.AddSingleton<KeywordGlossary>();
        services.AddSingleton<MishearingFixState>();
        services.AddSingleton<IOpenAiClient, StubOpenAiClient>();
        services.AddSingleton<ITextProcessingService, TextProcessingService>();
        services.AddSingleton(sp => new TranslationManager(
            sp.GetRequiredService<IOpenAiClient>(),
            sp.GetRequiredService<ITextProcessingService>(),
            sp.GetRequiredService<ITranslationStore>(),
            sp.GetRequiredService<ISettingsService>(),
            glossary: sp.GetRequiredService<GlossaryState>(),
            keywordGlossary: sp.GetRequiredService<KeywordGlossary>()));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IVideoInfoProvider>(new StubVideoInfoProvider());
        services.AddSingleton<ILorebookLlm>(llm);
        services.AddSingleton<IKnowledgeSources>(new StubKnowledgeSources());
        services.AddSingleton<MiningService>();
        services.AddSingleton<MishearingMiningService>();
        services.AddSingleton<GameTermDiscoveryService>();
        services.AddSingleton<TermImportService>();
        services.AddSingleton<ReviewCountSignal>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<GlossaryReviewViewModel>();
        return services.BuildServiceProvider();
    }

    /// <summary>不連網的知識庫模型替身：依序回傳排入的腳本 JSON（沒有排入時回傳空物件），並記下 user 訊息。</summary>
    private sealed class StubLorebookLlm : ILorebookLlm
    {
        /// <summary>貼上資料整理的腳本回覆（依序 dequeue）。</summary>
        public Queue<string> JsonReplies { get; } = new();

        public List<string> UserPrompts { get; } = [];

        public Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            UserPrompts.Add(userPrompt);
            return Task.FromResult(new LlmJsonResult(
                JsonReplies.Count > 0 ? JsonReplies.Dequeue() : "{}", 10, 5, 20, "stub-glm"));
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }

    /// <summary>把呼叫轉給真的 SQLite store，並記錄合併／刪除整張卡的呼叫；可設定成擲例外（測試失敗處理）。</summary>
    private sealed class GlossaryStoreRecorder(SqliteTranslationStore inner) : IGlossaryStore
    {
        /// <summary>true 時 MergeGlossaryCards 擲例外（通話仍會記錄）。</summary>
        public bool ThrowOnMerge { get; set; }

        /// <summary>true 時 DeleteGlossaryCard 擲例外（通話仍會記錄）。</summary>
        public bool ThrowOnDelete { get; set; }

        /// <summary>true 時 DeleteGlossaryTerms 擲例外（通話仍會記錄）。</summary>
        public bool ThrowOnDeleteTerms { get; set; }

        public List<(long SourceId, long TargetId)> MergeCalls { get; } = [];

        public List<long> DeleteCalls { get; } = [];

        public List<IReadOnlyList<long>> DeleteTermCalls { get; } = [];

        public IReadOnlyList<GlossaryCard> GetGlossaryCards() => inner.GetGlossaryCards();

        public IReadOnlyList<GlossaryTerm> GetGlossaryTerms() => inner.GetGlossaryTerms();

        public IReadOnlyList<GlossaryCard> GetAllGlossaryCards() => inner.GetAllGlossaryCards();

        public IReadOnlyList<GlossaryTerm> GetAllGlossaryTerms() => inner.GetAllGlossaryTerms();

        public void SaveGlossaryChanges(IReadOnlyList<GlossaryCard> cards, IReadOnlyList<GlossaryTerm> terms) =>
            inner.SaveGlossaryChanges(cards, terms);

        public void SaveGlossaryTermEdits(IReadOnlyList<GlossaryTerm> updated, IReadOnlyList<GlossaryTerm> inserted) =>
            inner.SaveGlossaryTermEdits(updated, inserted);

        public long CreateGlossaryCard(GlossaryCard card, IReadOnlyList<GlossaryTerm> terms) =>
            inner.CreateGlossaryCard(card, terms);

        public void UpsertTermCandidates(IReadOnlyList<TermCandidate> candidates) =>
            inner.UpsertTermCandidates(candidates);

        public GlossaryMergeResult MergeGlossaryCards(long sourceId, long targetId)
        {
            MergeCalls.Add((sourceId, targetId));
            if (ThrowOnMerge) throw new InvalidOperationException("合併測試錯誤");
            return inner.MergeGlossaryCards(sourceId, targetId);
        }

        public void DeleteGlossaryCard(long cardId)
        {
            DeleteCalls.Add(cardId);
            if (ThrowOnDelete) throw new InvalidOperationException("刪除測試錯誤");
            inner.DeleteGlossaryCard(cardId);
        }

        public void DeleteGlossaryTerms(IReadOnlyList<long> termIds)
        {
            DeleteTermCalls.Add(termIds);
            if (ThrowOnDeleteTerms) throw new InvalidOperationException("名詞刪除測試錯誤");
            inner.DeleteGlossaryTerms(termIds);
        }

        public IReadOnlyList<MishearingFix> GetMishearingFixes() => inner.GetMishearingFixes();

        public int UpsertMishearingFixes(IReadOnlyList<MishearingProposal> proposals) =>
            inner.UpsertMishearingFixes(proposals);

        public void ApproveMishearingFixes(IReadOnlyList<long> ids) => inner.ApproveMishearingFixes(ids);

        public void DeleteMishearingFixes(IReadOnlyList<long> ids) => inner.DeleteMishearingFixes(ids);
    }

    /// <summary>不連網的知識來源替身：全部查不到（這些測試不跑資料探勘）。</summary>
    private sealed class StubKnowledgeSources : IKnowledgeSources
    {
        public Task<IReadOnlyList<WikidataHit>> SearchWikidataAsync(string text, string language = "ja", int limit = 3, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WikidataHit>>([]);

        public Task<WikidataEntity?> GetWikidataEntityAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<WikidataEntity?>(null);

        public Task<IReadOnlyList<string>> SearchWikipediaAsync(string lang, string text, int limit = 3, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<WikiPage?> GetWikipediaPageAsync(string lang, string title, CancellationToken ct = default) =>
            Task.FromResult<WikiPage?>(null);

        public Task<bool> FandomExistsAsync(string subdomain, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> SearchFandomAsync(string subdomain, string text, int limit = 3, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<WikiPage?> GetFandomPageAsync(string subdomain, string title, CancellationToken ct = default) =>
            Task.FromResult<WikiPage?>(null);
    }
}
