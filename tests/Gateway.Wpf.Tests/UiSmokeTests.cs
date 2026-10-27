using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;
using Translation.Gateway.Logging;
using Translation.Gateway.Services;
using Translation.Gateway.ViewModels;
using Translation.Gateway.Views;
using Translation.Gateway.Views.Controls;

namespace Translation.Gateway.Wpf.Tests;

/// <summary>
/// WPF 煙霧測試：載入 App.xaml 的全域資源，以真實 ViewModel 建出主視窗並切換每一頁、強制版面配置，
/// 抓出只會在執行期才出錯的 XAML 問題（找不到資源、樣式型別不符、範本錯誤等），
/// 並實際走一次 Dashboard 的編輯、鎖定、設定檔、重翻與刪除流程。
/// WPF 的 Application 一個行程只能有一個，所以整個情境放在同一個 STA 測試裡依序執行。
/// </summary>
public class UiSmokeTests
{
    private const string StreamMetadataJson =
        """
        {"plugin":"twitch","metadata":{"id":"12345","author":"Sumire Ch. 花芽すみれ","category":null,"title":"【APEX】w/ 花芽すみれ・橘ひなの"},"streams":{"best":{"url":"hls://example"}}}
        """;

    [Fact]
    public void MainWindow_AllPagesLoad_AndDashboardWorkflowsWork()
    {
        RunOnSta(Scenario);
    }

    /// <summary>PotPlayer 還開著而且使用者回答「否」：開台流程在啟動任何程式前取消。</summary>
    [Fact]
    public void OpenStream_PotPlayerRunning_UserDeclines_CancelsBeforeLaunch()
    {
        RunOnSta(PotPlayerDeclinedScenario);
    }

    private static void PotPlayerDeclinedScenario()
    {
        // 與主情境一樣裝 Dispatcher 同步內容，開台指令的接續才會留在這條 UI 執行緒
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var dir = Directory.CreateTempSubdirectory("gateway-potplayer-");
        try
        {
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, Path.Combine(dir.FullName, "settings.json"));
            var db = new SqliteTranslationStore(NullLogger<SqliteTranslationStore>.Instance, Path.Combine(dir.FullName, "cache.db"));
            // 假的行程執行器：不會真的啟動 streamlink 或 PotPlayer
            var fakeRunner = new FakeProcessRunner();
            fakeRunner.ExistingFiles.Add(settings.Current.Streamlink.StreamlinkPath);
            fakeRunner.ExistingFiles.Add(settings.Current.Streamlink.PlayerPath);
            // PotPlayer 正在執行：開台流程要先詢問關閉
            var processes = new StubPotPlayerProcesses(anyRunning: true);
            using var provider = BuildServices(settings, db, fakeRunner, potPlayerProcesses: processes);
            var mainVm = provider.GetRequiredService<MainViewModel>();

            mainVm.ClipboardReader = () => "https://www.twitch.tv/someone";
            var confirmMessages = new List<string>();
            mainVm.ConfirmDialog = message =>
            {
                confirmMessages.Add(message);
                return false;
            };
            mainVm.OpenStreamCommand.Execute(null);
            DoEvents();

            Assert.Equal("已取消：PotPlayer 還開著", mainVm.StreamStatus);
            var message = Assert.Single(confirmMessages);
            Assert.Contains("PotPlayer 正在執行", message);
            // 取消時不啟動任何程式、不關閉 PotPlayer
            Assert.Empty(fakeRunner.Runs);
            Assert.Empty(fakeRunner.Starts);
            Assert.Equal(0, processes.CloseAllCount);
        }
        finally
        {
            WeakReferenceMessenger.Default.Reset();
            try { dir.Delete(recursive: true); } catch (IOException) { /* SQLite 連線池可能還握著檔案 */ }
        }
    }

    private static void Scenario()
    {
        // 與真正的 WPF 程式一樣裝上 Dispatcher 的同步內容：非同步指令（開台）的 await 之後才會回到這條 UI 執行緒，
        // 否則接續會跑到背景執行緒去更新按鈕而讓測試程式崩潰
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        // SkipStartup：只要 App.xaml 的資源，不執行真正的啟動流程（建主機、開視窗、寫 log）
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, SkipStartup = true };
        app.InitializeComponent();

        var dir = Directory.CreateTempSubdirectory("gateway-ui-");
        try
        {
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, Path.Combine(dir.FullName, "settings.json"));
            settings.Current.Translation.SystemPromptTemplate = "你是日翻中字幕翻譯員";
            settings.Current.Translation.UseConversationMode = true;
            settings.Current.StreamProfiles.Add(new StreamProfile
            {
                Name = "佩克拉",
                StreamSituation = "兔田佩克拉在打麻將",
                CharacterTable = "ぺこら=佩克拉",
                SourceReplacementRules = "ペコラ=ぺこら",
            });
            var db = new SqliteTranslationStore(NullLogger<SqliteTranslationStore>.Instance, Path.Combine(dir.FullName, "cache.db"));
            // 假的行程執行器：開台流程可以成功，但不會真的啟動 streamlink 或 PotPlayer
            var fakeRunner = new FakeProcessRunner();

            // 在暫存 SQLite 插入一張遊戲卡與一個名詞，加上 VSPO 團體卡（人名：花芽すみれ）
            // 與其角色卡橘ひなの（Dashboard 建構時會讀成詞彙卡型錄）
            using (var conn = new SqliteConnection($"Data Source={Path.Combine(dir.FullName, "cache.db")}"))
            {
                conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
                conn.Execute("INSERT INTO GlossaryTerms (CardId, Source, Target, Status) VALUES (1, 'ヴァンダル', '暴徒', 'Approved')");
                conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, ParentId, Status) VALUES (2, 'Group', 'ぶいすぽっ!', NULL, 'Approved')");
                conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, ParentId, Status) VALUES (3, 'Character', '橘ひなの', 2, 'Approved')");
                conn.Execute("INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (2, '花芽すみれ', '花芽澄伶', '人名', 'Approved')");
                conn.Execute("INSERT INTO GlossaryTerms (CardId, Source, Target, Category, Status) VALUES (3, '橘ひなの', '橘雛乃', '人名', 'Approved')");
                // 側邊欄待審數量的種子：ぶいすぽっ! 卡有一個待審名詞（非人名，不影響開台比對）
                conn.Execute("INSERT INTO GlossaryTerms (CardId, Source, Target, Status) VALUES (2, 'ぶいすぽ', 'VSPO', 'Pending')");
            }

            var stubLlm = new StubLorebookLlm();
            using var provider = BuildServices(settings, db, fakeRunner, stubLlm, new StubKnowledgeSources());
            // 字幕紀錄：比照 App 啟動註冊，開始接收 TranslationJob 訊息（背景寫入資料庫）
            var writer = provider.GetRequiredService<SubtitleLogWriter>();
            writer.StartListening();
            var mainVm = provider.GetRequiredService<MainViewModel>();
            var glossaryReviewVm = provider.GetRequiredService<GlossaryReviewViewModel>();
            var window = new MainWindow(mainVm, settings, glossaryReviewVm) { ShowActivated = false };
            window.Show();
            DoEvents();

            // ===== 側邊欄待審數量：啟動時算一次（種子有一個待審名詞 ぶいすぽ）=====
            var pendingCardIds = db.GetGlossaryCards().Select(c => c.Id).ToHashSet();
            var expectedPendingTerms = db.GetGlossaryTerms()
                .Count(t => t.Status == "Pending" && pendingCardIds.Contains(t.CardId));
            PumpUntil(() => mainVm.GlossaryBadgeText == $"({expectedPendingTerms})");
            Assert.Equal(1, expectedPendingTerms);
            // 沒有待審的聽錯字修正：不顯示
            Assert.Equal(string.Empty, mainVm.MishearingBadgeText);

            // ===== Dashboard：收到字幕後表格要有對應的列 =====
            Assert.True(mainVm.IsDashboardPage);
            var vm = provider.GetRequiredService<DashboardViewModel>();
            var grid = FindVisual<DataGrid>(window);
            Assert.NotNull(grid);

            Publish("こんにちは", "你好", TranslationSource.Model, modelLatency: 150);
            Publish("ありがとう", "謝謝", TranslationSource.Cache, modelLatency: 0);
            Publish("なるほどね", "なるほどね", TranslationSource.Timeout, modelLatency: 1500);
            DoEvents();

            Assert.Equal(3, grid.Items.Count);
            Assert.NotNull(grid.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Contains("3 句", vm.StatsText);
            Assert.True(vm.StatsWarning); // 有逾時

            // ===== 雙擊編輯翻譯：進入編輯會鎖定，新字幕先暫存；Enter 寫回 DB 並解鎖 =====
            var job = vm.Jobs.Single(j => j.Current == "こんにちは");
            var translationColumn = grid.Columns.Single(c => c.SortMemberPath == "TranslatedText");
            grid.SelectedItem = job;
            grid.CurrentCell = new DataGridCellInfo(job, translationColumn);
            Assert.True(grid.BeginEdit());
            DoEvents();
            Assert.True(vm.IsLocked);

            Publish("新しい字幕", "新的字幕", TranslationSource.Model, modelLatency: 120);
            DoEvents();
            Assert.Equal(1, vm.PendingCount);
            Assert.Equal(3, vm.Jobs.Count);

            var editBox = FindVisual<TextBox>(translationColumn.GetCellContent(job)!);
            Assert.NotNull(editBox);
            Assert.Equal("你好", editBox.Text);
            editBox.Text = "哈囉";
            Assert.True(grid.CommitEdit(DataGridEditingUnit.Cell, true));
            DoEvents();

            Assert.False(vm.IsLocked);
            Assert.Equal(4, vm.Jobs.Count);
            Assert.Equal("新しい字幕", vm.Jobs[0].Current);
            Assert.Equal("哈囉", job.TranslatedText);
            Assert.Equal("哈囉", db.GetCache("こんにちは"));

            // Esc：放棄修改也要解鎖
            grid.CurrentCell = new DataGridCellInfo(job, translationColumn);
            Assert.True(grid.BeginEdit());
            DoEvents();
            Assert.True(vm.IsLocked);
            grid.CancelEdit(DataGridEditingUnit.Cell);
            DoEvents();
            Assert.False(vm.IsLocked);
            Assert.Equal("哈囉", job.TranslatedText);

            // ===== 右鍵：重新翻譯這句 =====
            vm.SelectedJob = job;
            vm.RetranslateSelectedCommand.Execute(null);
            DoEvents();
            Assert.Equal(StubOpenAiClient.Reply, job.TranslatedText);

            // ===== 右鍵選單：每個項目都要繫結到命令（ContextMenu 不在視覺樹裡，DataContext 要靠 PlacementTarget） =====
            var menu = grid.ContextMenu;
            Assert.NotNull(menu);
            menu.PlacementTarget = grid;
            menu.IsOpen = true;
            DoEvents();
            Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.NotNull(item.Command));
            menu.IsOpen = false;

            // ===== 暫停捲動：新字幕進暫存；編輯結束也不會自動補上，按「繼續捲動」才補 =====
            vm.TogglePauseCommand.Execute(null);
            Assert.True(vm.IsPaused);
            Publish("カーレスターどこ?", "卡", TranslationSource.Model, modelLatency: 100);
            DoEvents();
            Assert.Equal(1, vm.PendingCount);

            var sourceColumn = grid.Columns.Single(c => c.SortMemberPath == "Current");
            var misheard = vm.Jobs.Single(j => j.Current == "ありがとう");
            grid.CurrentCell = new DataGridCellInfo(misheard, sourceColumn);
            Assert.True(grid.BeginEdit());
            DoEvents();
            grid.CancelEdit(DataGridEditingUnit.Cell);
            DoEvents();
            Assert.False(vm.IsLocked);
            Assert.True(vm.IsPaused);
            Assert.Equal(1, vm.PendingCount);

            vm.TogglePauseCommand.Execute(null);
            DoEvents();
            Assert.False(vm.IsPaused);
            Assert.Equal(0, vm.PendingCount);
            var carlester = vm.Jobs[0];
            Assert.Equal("カーレスターどこ?", carlester.Current);

            // ===== 雙擊原文修正聽錯 → 重翻 → 右鍵把修改轉成原文修正規則 =====
            grid.CurrentCell = new DataGridCellInfo(carlester, sourceColumn);
            Assert.True(grid.BeginEdit());
            DoEvents();
            var sourceBox = FindVisual<TextBox>(sourceColumn.GetCellContent(carlester)!);
            Assert.NotNull(sourceBox);
            sourceBox.Text = "カレスタどこ?";
            Assert.True(grid.CommitEdit(DataGridEditingUnit.Cell, true));
            DoEvents();
            Assert.Equal("カレスタどこ?", carlester.Current);
            Assert.Equal("カーレスターどこ?", carlester.CacheKey);
            Assert.Equal(StubOpenAiClient.Reply, carlester.TranslatedText);
            Assert.Equal(StubOpenAiClient.Reply, db.GetCache("カーレスターどこ?"));

            vm.SelectedJob = carlester;
            vm.AddSourceRuleFromEditCommand.Execute(null);
            Assert.Contains("カーレスター=カレスタ", vm.SourceReplacementRules);
            Assert.Equal(vm.SourceReplacementRules, settings.Current.SourceReplacementRules);

            // ===== 刪除：畫面與 DB 都移除 =====
            vm.SelectedJob = job;
            vm.DeleteSelectedCommand.Execute(null);
            DoEvents();
            Assert.DoesNotContain(job, vm.Jobs);
            Assert.Null(db.GetCache("こんにちは"));

            // ===== 直播設定檔：套用後四項內容與標題列都更新 =====
            vm.ProfileName = "佩克拉";
            vm.ApplyProfileCommand.Execute(null);
            DoEvents();
            Assert.Equal("兔田佩克拉在打麻將", vm.StreamSituation);
            Assert.Equal("ペコラ=ぺこら", vm.SourceReplacementRules);
            Assert.Contains("佩克拉", vm.ActiveProfileText);

            vm.ProfileName = "瑪琳";
            vm.SaveProfileCommand.Execute(null);
            Assert.Contains("瑪琳", vm.ProfileNames);

            // ===== 詞彙卡：Prompt 詳細內容預設隱藏，切換鈕才顯示；搜尋與生效都要能動 =====
            var promptBox = FindAllVisual<GroupBox>(window)
                .FirstOrDefault(g => (g.Header as string)?.Contains("User Prompt") == true);
            Assert.NotNull(promptBox);
            Assert.False(promptBox.IsVisible);  // 實際是否可見（含上層容器的 Visibility）
            vm.ShowPromptDetails = true;
            DoEvents();
            Assert.True(promptBox.IsVisible);
            vm.ShowPromptDetails = false;
            DoEvents();

            vm.GlossarySearchText = "暴徒";
            DoEvents();
            var glossaryItem = Assert.Single(vm.GlossaryResults);
            Assert.Equal("VALORANT", glossaryItem.Name);
            Assert.False(glossaryItem.IsActive);

            vm.ToggleGlossaryCardCommand.Execute(glossaryItem);
            DoEvents();
            Assert.Single(vm.ActiveGlossaryCards);
            var glossaryState = provider.GetRequiredService<GlossaryState>();
            Assert.Contains("ヴァンダル=暴徒", glossaryState.PromptSection);
            Assert.Equal([1L], settings.Current.ActiveGlossaryCardIds);
            Assert.Contains("生效 1 張", vm.GlossaryStatus);

            // 移除生效後段落清空
            vm.RemoveGlossaryCardCommand.Execute(vm.ActiveGlossaryCards[0]);
            DoEvents();
            Assert.Empty(vm.ActiveGlossaryCards);
            Assert.Equal(string.Empty, glossaryState.PromptSection);

            // ===== 字幕監看頁改版：直播資訊右欄是詞彙卡面板，舊的下方「生效中」區塊已移除 =====
            Assert.DoesNotContain(FindAllVisual<TextBlock>(window), t => t.Text == "生效中");
            // 「直播資訊」Expander 預設收合，收合時內容不在視覺樹中：先全部展開再找面板
            foreach (var expander in FindAllVisual<Expander>(window))
            {
                expander.IsExpanded = true;
            }
            DoEvents();
            window.UpdateLayout();
            Assert.NotNull(FindAllVisual<FrameworkElement>(window).Single(e => e.Name == "GlossaryPanel"));

            // ===== 直播資訊左欄改分頁［情境｜歌曲清單］：沒有錄影時歌曲清單分頁收合，看起來跟原本一樣 =====
            var infoTabs = Assert.Single(FindAllVisual<TabControl>(window));
            Assert.Equal(2, infoTabs.Items.Count);
            Assert.Equal(0, vm.InfoTabIndex);
            Assert.False(vm.ShowSongList);
            var songTab = Assert.IsType<TabItem>(infoTabs.Items[1]);
            Assert.Equal(Visibility.Collapsed, songTab.Visibility);
            var situationTab = Assert.IsType<TabItem>(infoTabs.Items[0]);
            Assert.Equal("情境", situationTab.Header);

            // ===== 詞彙卡抽屜：點卡開右側抽屜編輯名詞，存檔寫回資料庫並更新 prompt =====
            vm.GlossarySearchText = "暴徒";
            DoEvents();
            vm.ToggleGlossaryCardCommand.Execute(Assert.Single(vm.GlossaryResults));
            DoEvents();
            Assert.Single(vm.ActiveGlossaryCards);

            vm.OpenGlossaryDrawerCommand.Execute(vm.ActiveGlossaryCards[0]);
            DoEvents();
            Assert.True(vm.IsGlossaryDrawerOpen);
            var drawerVm = vm.GlossaryDrawer!;
            Assert.Equal("本卡（1）", drawerVm.Tabs.Single().Title);
            Assert.Equal("VALORANT", drawerVm.CardName);
            Assert.Equal("遊戲", drawerVm.CardKindText);
            Assert.Equal("已核准", drawerVm.CardStatusText);

            // 抽屜的名詞表格要載入（VisibleTerms 綁到 ItemsSource）
            var drawerGrid = FindAllVisual<DataGrid>(window)
                .Single(g => ReferenceEquals(g.ItemsSource, drawerVm.VisibleTerms));
            Assert.Single(drawerGrid.Items);

            var vandal = drawerVm.VisibleTerms.Single(t => t.Source == "ヴァンダル");
            vandal.Target = "暴徒步槍";
            DoEvents();
            Assert.Equal(1, drawerVm.DirtyCount);
            Assert.Contains("更新 prompt", drawerVm.FooterText);

            drawerVm.SaveCommand.Execute(null);
            DoEvents();
            Assert.Equal("已存檔", drawerVm.StatusMessage);
            Assert.Equal(0, drawerVm.DirtyCount);
            // 卡片生效中：存檔同時更新 prompt 段落，資料庫也要是新值
            Assert.Contains("ヴァンダル=暴徒步槍", glossaryState.PromptSection);
            Assert.Equal("暴徒步槍", db.GetGlossaryTerms().Single(t => t.Source == "ヴァンダル").Target);

            // 關閉抽屜（無變更時不詢問）
            drawerVm.CloseCommand.Execute(null);
            DoEvents();
            Assert.False(vm.IsGlossaryDrawerOpen);
            Assert.Null(vm.GlossaryDrawer);

            // 還原：移除 VALORANT 生效，讓後續開台流程的生效清單回到原始預期
            vm.RemoveGlossaryCardCommand.Execute(vm.ActiveGlossaryCards[0]);
            DoEvents();
            Assert.Empty(vm.ActiveGlossaryCards);
            Assert.Equal(string.Empty, glossaryState.PromptSection);

            // ===== 情境存檔時比對詞彙卡：VALORANT 生效（原因：情境）、顯示已比對標籤 =====
            vm.StreamSituation = "今天玩 VALORANT";
            vm.ProfileName = "情境測試";
            vm.SaveProfileCommand.Execute(null);
            DoEvents();
            Assert.Contains(vm.MiningResultTags, t => t.Text == "已比對：VALORANT");
            var situational = Assert.Single(vm.ActiveGlossaryCards, c => c.Name == "VALORANT");
            Assert.Equal("情境", situational.ReasonText);
            Assert.Contains("VALORANT", vm.GlossarySummaryText);

            // ===== 探勘結果標籤可點擊複製：CopyText 是名詞本身（不含「已比對：」），命令 CanExecute 不擲例外（不真的執行複製）=====
            var matchedTag = Assert.Single(vm.MiningResultTags, t => t.Text == "已比對：VALORANT");
            Assert.True(matchedTag.CanCopy);
            Assert.Equal("VALORANT", matchedTag.CopyText);
            Assert.DoesNotContain("已比對", matchedTag.CopyText);
            Assert.True(vm.CopyMiningTagCommand.CanExecute(matchedTag));
            Assert.False(new MiningTag("候選：3 個", nameof(MiningOutcome.Candidate)).CanCopy);
            Assert.True(new MiningTag("查不到：ほの", nameof(MiningOutcome.NotFound), "ほの").CanCopy);
            _ = vm.CopyMiningTagCommand.CanExecute(null);
            _ = vm.CopyMiningTagCommand.CanExecute(new MiningTag("候選：3 個", nameof(MiningOutcome.Candidate)));

            // ===== 關鍵字詞彙提示：先移除生效，未生效卡的名詞出現在本句時以關鍵字提示列出（user prompt 段落）=====
            vm.RemoveGlossaryCardCommand.Execute(situational);
            DoEvents();
            Assert.Empty(vm.ActiveGlossaryCards);
            var keyword = provider.GetRequiredService<KeywordGlossary>();
            Assert.Contains("ヴァンダル=暴徒", keyword.BuildUserPromptSection("ヴァンダル強いね", []));

            // 還原：生效清單已清空，讓後續開台流程回到原始預期
            Assert.Equal(string.Empty, glossaryState.PromptSection);

            // ===== 資料探勘：還沒設定知識庫模型時按鈕停用 =====
            Assert.False(vm.CanStartMining);
            Assert.Equal("請先到 設定 → 知識庫模型 填寫並測試連線", vm.MiningDisabledToolTip);

            // ===== 資料探勘：設定 Lorebook、輸入情境後執行，用假的 GLM 與知識來源建一張新的遊戲卡 =====
            settings.Current.Lorebook.ApiHost = "http://127.0.0.1:8300/v1";
            settings.Current.Lorebook.Model = "glm-test";
            vm.StreamSituation = "今天玩 ほの暮しの庭";
            DoEvents();
            Assert.True(vm.CanStartMining);
            Assert.Null(vm.MiningDisabledToolTip);

            stubLlm.JsonReplies.Enqueue(
                "{\"nouns\":[{\"surface\":\"ほの暮しの庭\",\"kind\":\"game\",\"importance\":5}]}");
            stubLlm.JsonReplies.Enqueue(
                "{\"decision\":\"new\",\"kind\":\"Game\",\"name\":\"ほの暮らしの庭\"," +
                "\"summary\":\"任天堂推出的生活模擬遊戲，玩家在悠閒的小鎮打理庭園、種花與佈置，療癒的日常系作品。\"," +
                "\"parentGroup\":null,\"terms\":[{\"source\":\"ほの暮しの庭\",\"target\":\"恬靜庭園\",\"category\":\"遊戲\"}]}");

            vm.MineSituationCommand.Execute(null);
            PumpUntil(() => !vm.IsMining);
            DoEvents();

            // 新的 Game 卡建立並立即生效（原因：探勘）
            var mined = Assert.Single(vm.ActiveGlossaryCards, c => c.Name == "ほの暮らしの庭");
            Assert.Equal("探勘", mined.ReasonText);
            Assert.Equal([mined.Id], settings.Current.ActiveGlossaryCardIds);
            Assert.Contains(vm.MiningResultTags, t => t.Text.StartsWith("新增：", StringComparison.Ordinal));
            // 新增標籤點一下複製的是卡名（CopyText），不是整句標籤文字
            var createdTag = Assert.Single(vm.MiningResultTags, t => t.Text.StartsWith("新增：", StringComparison.Ordinal));
            Assert.True(createdTag.CanCopy);
            Assert.Equal("ほの暮らしの庭", createdTag.CopyText);
            var toasts = provider.GetRequiredService<ToastService>();
            Assert.Single(toasts.Items);
            Assert.Equal("資料探勘完成", toasts.Items[0].Title);
            // 遊戲卡的簡介放進 system prompt
            Assert.Contains("簡介：", glossaryState.PromptSection);
            Assert.Contains("ほの暮しの庭=恬靜庭園", glossaryState.PromptSection);

            // 還原：移除探勘到的卡，讓後續開台流程的生效清單回到原始預期
            vm.RemoveGlossaryCardCommand.Execute(mined);
            DoEvents();
            Assert.Empty(vm.ActiveGlossaryCards);
            Assert.Equal(string.Empty, glossaryState.PromptSection);

            // ===== 重複字幕：修正版取代舊列並移到最上面；Reused 列也要能顯示 =====
            var draft = new TranslationJob { Current = "普段、引きこもりなんで全然", CacheKey = "普段、引きこもりなんで全然", TranslatedText = "平常都宅在家" };
            WeakReferenceMessenger.Default.Send(draft);
            DoEvents();
            var countBefore = vm.Jobs.Count;
            WeakReferenceMessenger.Default.Send(new TranslationJob
            {
                Current = "普段、引きこもりなんで全然ご飯行かないし",
                CacheKey = "普段、引きこもりなんで全然ご飯行かないし",
                TranslatedText = "平常都宅在家，完全不出去吃飯",
                Source = TranslationSource.Reused,
                RevisionOf = draft.Id,
            });
            DoEvents();
            Assert.Equal(countBefore, vm.Jobs.Count);
            Assert.DoesNotContain(draft, vm.Jobs);
            Assert.Equal("普段、引きこもりなんで全然ご飯行かないし", vm.Jobs[0].Current);
            Assert.NotNull(grid.ItemContainerGenerator.ContainerFromIndex(0));

            // ===== 字幕紀錄：收到的字幕與編輯／刪除都寫進暫存 SQLite 的 SubtitleLog =====
            Assert.True(writer.FlushAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
            using (var conn = new SqliteConnection($"Data Source={Path.Combine(dir.FullName, "cache.db")}"))
            {
                var logged = conn.Query<SubtitleLogRow>(
                    "SELECT JobId, Source, Translation, IsEdited, IsDeleted FROM SubtitleLog ORDER BY Id").ToList();
                // 7 次新增（含暫存期間的字幕與重複字幕的中間版本）
                Assert.Equal(7, logged.Count);
                // 編輯（改譯文、重翻、改原文）標記在對應的列上：こんにちは 與 カーレスター 兩列
                Assert.Equal(2, logged.Count(r => r.IsEdited == 1));
                var edited = Assert.Single(logged, r => r.Source == "カレスタどこ?");
                Assert.Equal(StubOpenAiClient.Reply, edited.Translation);
                // 刪除只做標記：こんにちは 那列 IsDeleted = 1，資料列仍在
                var deleted = Assert.Single(logged, r => r.IsDeleted == 1);
                Assert.Equal("こんにちは", deleted.Source);
                Assert.Equal(StubOpenAiClient.Reply, deleted.Translation);
            }

            // ===== 一鍵開台：剪貼簿不是網址、找不到 streamlink 都只顯示訊息，不啟動任何程式 =====
            settings.Current.Streamlink.StreamlinkPath = Path.Combine(dir.FullName, "missing-streamlink.exe");
            mainVm.ClipboardReader = () => "hello";
            var confirmMessages = new List<string>();
            mainVm.ConfirmDialog = message =>
            {
                confirmMessages.Add(message);
                return false;
            };
            // 情境存檔詢問也替換成假的（不得彈出真的 MessageBox）：先一律回答「否」
            var saveSituationMessages = new List<string>();
            mainVm.SaveSituationDialog = message =>
            {
                saveSituationMessages.Add(message);
                return false;
            };
            mainVm.OpenStreamCommand.Execute(null);
            DoEvents();
            Assert.Contains("沒有 YouTube 或 Twitch", mainVm.StreamStatus);
            Assert.Empty(confirmMessages); // 沒有網址時不詢問
            Assert.Empty(saveSituationMessages);

            // 開台失敗（找不到 PotPlayer 或 streamlink，看驗證機有沒有裝；T-2111 起 PotPlayer 先檢查、streamlink 只在直播路徑檢查）：
            // 全新的網址會先詢問情境存檔，但失敗時不清情境也不清卡；狀態列下方有「前往下載」
            mainVm.ClipboardReader = () => "https://www.twitch.tv/someone";
            mainVm.OpenStreamCommand.Execute(null);
            DoEvents();
            Assert.Matches("找不到 (PotPlayer|streamlink)", mainVm.StreamStatus);
            Assert.NotNull(mainVm.StreamHelpUrl);
            Assert.Empty(confirmMessages);
            // 情境（今天玩 ほの暮しの庭）與套用中設定檔「情境測試」的內容不同 → 詢問一次，訊息含設定檔名稱
            var failSaveMessage = Assert.Single(saveSituationMessages);
            Assert.Contains("情境測試", failSaveMessage);
            Assert.Equal("今天玩 ほの暮しの庭", vm.StreamSituation); // 開台失敗：情境不變
            Assert.Empty(vm.ActiveGlossaryCards);
            saveSituationMessages.Clear();

            // ===== 重複開台：上一場還在播時先詢問；取消不砍原程序，確認才砍掉舊的再開新的 =====
            settings.Current.Streamlink.StreamlinkPath = Path.Combine(dir.FullName, "streamlink.exe");
            settings.Current.Streamlink.PlayerPath = Path.Combine(dir.FullName, "player.exe");
            fakeRunner.ExistingFiles.Add(settings.Current.Streamlink.StreamlinkPath);
            fakeRunner.ExistingFiles.Add(settings.Current.Streamlink.PlayerPath);
            fakeRunner.Results.Enqueue(new ProcessResult(0, StreamMetadataJson, string.Empty, false));
            // 主播 花芽すみれ 沒有角色卡、Lorebook 已設定：開台會自動資料探勘，先給空的拆名詞回覆
            stubLlm.JsonReplies.Enqueue("{\"nouns\":[]}");
            mainVm.OpenStreamCommand.Execute(null);
            PumpUntil(() => fakeRunner.StartedProcesses.Count == 1);
            // 等整個開台指令跑完（含背景建立直播紀錄）：測試結束後才完成的話，指令會在非 UI 執行緒更新按鈕而讓測試程式崩潰
            PumpUntil(() => !mainVm.OpenStreamCommand.IsRunning);
            // 第一次開台（全新的網址）：情境有內容而且沒存成設定檔 → 詢問一次（回答「否」）
            var firstSaveMessage = Assert.Single(saveSituationMessages);
            Assert.Contains("情境測試", firstSaveMessage);
            saveSituationMessages.Clear();
            // 全新的直播：開台成功後情境被清空（回答「否」不存檔）
            AssertStreamAutoMining(vm, mainVm, stubLlm, toasts, string.Empty);

            var launcher = provider.GetRequiredService<StreamlinkLauncher>();
            Assert.True(launcher.IsRunning);
            var firstStream = fakeRunner.StartedProcesses[0];

            // 為這場新直播輸入新的情境（未存檔），驗證同一個網址重開時不會被清掉
            vm.StreamSituation = "すみれのAPEX";
            DoEvents();

            // 取消：不砍程序、沿用原直播（取消時不詢問情境存檔、不清空）
            mainVm.OpenStreamCommand.Execute(null);
            DoEvents();
            var confirmMessage = Assert.Single(confirmMessages);
            Assert.Contains("目前正在播", confirmMessage);
            Assert.Contains("Sumire Ch. 花芽すみれ", confirmMessage);
            Assert.Equal(0, firstStream.KillTreeCount);
            Assert.Contains("已取消", mainVm.StreamStatus);
            Assert.True(launcher.IsRunning);
            Assert.Empty(saveSituationMessages);
            Assert.Equal("すみれのAPEX", vm.StreamSituation);

            // 確認：砍掉上一場（含 PotPlayer 的行程樹）再開新的
            fakeRunner.Results.Enqueue(new ProcessResult(0, StreamMetadataJson, string.Empty, false));
            stubLlm.JsonReplies.Enqueue("{\"nouns\":[]}");
            mainVm.ConfirmDialog = _ => true;
            mainVm.OpenStreamCommand.Execute(null);
            PumpUntil(() => fakeRunner.StartedProcesses.Count == 2);
            // 等整個開台指令跑完（含背景建立直播紀錄）：測試結束後才完成的話，指令會在非 UI 執行緒更新按鈕而讓測試程式崩潰
            PumpUntil(() => !mainVm.OpenStreamCommand.IsRunning);
            // 同一個網址再開：不是全新的直播 → 不再詢問情境存檔，情境沿用
            Assert.Empty(saveSituationMessages);
            AssertStreamAutoMining(vm, mainVm, stubLlm, toasts, "すみれのAPEX");

            Assert.Equal(1, firstStream.KillTreeCount);
            Assert.True(launcher.IsRunning);
            Assert.Contains("花芽すみれ", launcher.CurrentDescription);

            // ===== 開台自動套用詞彙卡：主播（花芽すみれ）沒有角色卡 → VSPO 團體卡（人名比對）；
            // 標題裡的橘ひなの → 其角色卡（連動），上層團體卡不重複顯示 =====
            Assert.Contains("已套用詞彙卡：ぶいすぽっ!、橘ひなの（連動）", mainVm.StreamStatus);
            Assert.Equal(["ぶいすぽっ!", "橘ひなの"], vm.ActiveGlossaryCards.Select(c => c.Name).ToList());

            // StreamCards：最新一場直播生效過的每張卡各一筆（HostGroup 與 Title）
            PumpUntil(() =>
            {
                using var conn = new SqliteConnection($"Data Source={Path.Combine(dir.FullName, "cache.db")}");
                var reasons = conn.Query<string>(
                    "SELECT Reason FROM StreamCards WHERE StreamId = (SELECT MAX(Id) FROM Streams) ORDER BY Id").ToList();
                return reasons.Count == 2 && reasons[0] == "HostGroup" && reasons[1] == "Title";
            });

            // ===== 關鍵字詞彙提示：未生效的 VALORANT 名詞出現在字幕時列出；生效卡（ぶいすぽっ!）的名詞不列 =====
            var keywordSection = keyword.BuildUserPromptSection("ヴァンダル強いね", []);
            Assert.StartsWith("# 參考詞彙（本句或前文提到，原文=譯名，譯名請照用）", keywordSection);
            Assert.Contains("ヴァンダル=暴徒", keywordSection);
            Assert.DoesNotContain("花芽すみれ", keywordSection);
            Assert.Equal(["ぶいすぽっ!", "橘ひなの"], vm.ActiveGlossaryCards.Select(c => c.Name).ToList());

            // ===== 切換每一頁都要能載入 =====
            mainVm.NavigateCommand.Execute("Settings");
            DoEvents();
            Assert.True(mainVm.IsSettingsPage);
            var settingsView = FindVisual<SettingsView>(window);
            Assert.NotNull(settingsView);
            var settingsVm = Assert.IsType<SettingsViewModel>(settingsView.DataContext);
            Assert.Contains(FindAllVisual<TextBlock>(settingsView), t => t.Text == "設定");

            // ===== 設定頁改為分頁：未選取的分頁內容不在視覺樹中，逐一選取後檢查 =====
            var tabs = FindVisual<TabControl>(settingsView);
            Assert.NotNull(tabs);
            Assert.Equal(6, tabs.Items.Count);

            // 第一頁（翻譯模型）：預設選取，連線欄位改用共用的 ModelConnectionControl；
            // 舊欄位的「進階」Expander 已移除；即時翻譯的欄位已搬到直播分頁
            Assert.Equal(0, tabs.SelectedIndex);
            Assert.NotNull(FindVisual<TranslationSettingsControl>(settingsView));
            Assert.Single(FindAllVisual<ModelConnectionControl>(settingsView));
            Assert.DoesNotContain(FindAllVisual<Expander>(settingsView),
                e => string.Equals(e.Header as string, "進階（目前程式未使用的舊欄位）"));
            Assert.DoesNotContain(FindAllVisual<GroupBox>(settingsView),
                g => string.Equals(g.Header as string, "即時翻譯"));

            // 第二頁（知識庫模型）：連線欄位綁到 settings.Current.Lorebook 同一個物件；
            // 聽錯字探勘的 CheckBox 已搬到直播分頁
            tabs.SelectedIndex = 1;
            DoEvents();
            var lorebookControl = Assert.Single(FindAllVisual<ModelConnectionControl>(settingsView));
            Assert.True(ReferenceEquals(lorebookControl.DataContext, settings.Current.Lorebook));
            Assert.DoesNotContain(FindAllVisual<CheckBox>(settingsView),
                c => (c.Content as string)?.StartsWith("每 5 分鐘從字幕找出可能的聽錯字", StringComparison.Ordinal) == true);

            // 模型參數：Lorebook.Model 是 GLM（glm-test）時「思考強度」可見（預設 low）、「思考上限」隱藏；
            // 從 UI 把模型名稱改成非 GLM 後兩者互換，測完改回原值
            var effortBox = FindByAutomationId<ComboBox>(settingsView, "LorebookReasoningEffort");
            Assert.True(effortBox.IsVisible);
            Assert.Equal("low", effortBox.SelectedValue);
            var budgetBox = FindByAutomationId<TextBox>(settingsView, "LorebookReasoningBudgetTokens");
            Assert.False(budgetBox.IsVisible);
            var modelBox = FindFieldBox(settingsView, "模型名稱");
            var originalModel = modelBox.Text;
            modelBox.Text = "qwen3-next";
            PumpUntil(() => budgetBox.IsVisible);
            DoEvents();
            Assert.False(effortBox.IsVisible);
            Assert.Equal("qwen3-next", settings.Current.Lorebook.Model);
            modelBox.Text = originalModel;
            PumpUntil(() => effortBox.IsVisible);

            var lorebookTestButton = FindAllVisual<Button>(settingsView)
                .Single(b => (b.Content as string) == "測試連線");
            Assert.NotNull(lorebookTestButton.Command);
            lorebookTestButton.Command.Execute(null);
            PumpUntil(() => settingsVm.LorebookTestSucceeded is not null);
            Assert.True(settingsVm.LorebookTestSucceeded);
            Assert.Contains("連線成功", settingsVm.LorebookTestMessage);

            // 第三頁（直播）：即時翻譯六列從翻譯模型分頁搬來，聽錯字探勘從知識庫模型分頁搬來
            tabs.SelectedIndex = 2;
            DoEvents();
            Assert.NotNull(FindVisual<LiveSettingsControl>(settingsView));
            Assert.Contains(FindAllVisual<GroupBox>(settingsView), g => string.Equals(g.Header as string, "即時翻譯"));
            Assert.Contains(FindAllVisual<GroupBox>(settingsView), g => string.Equals(g.Header as string, "聽錯字探勘"));

            // 第四頁（錄影）：DataContext 綁到 settings.Current.Vod 同一個物件，三個區塊都在
            tabs.SelectedIndex = 3;
            DoEvents();
            var vodControl = FindVisual<VodSettingsControl>(settingsView);
            Assert.NotNull(vodControl);
            Assert.True(ReferenceEquals(vodControl.DataContext, settings.Current.Vod));
            Assert.Contains(FindAllVisual<GroupBox>(settingsView), g => string.Equals(g.Header as string, "預先翻譯"));
            Assert.Contains(FindAllVisual<GroupBox>(settingsView), g => string.Equals(g.Header as string, "開片"));
            Assert.Contains(FindAllVisual<GroupBox>(settingsView), g => string.Equals(g.Header as string, "排程"));
            // 章節名稱新增「查不到原曲長度時顯示 (秒)」欄位（綁 ChapterTitleFallbackSeconds）
            Assert.Contains(FindAllVisual<TextBlock>(settingsView), t => t.Text == "查不到原曲長度時顯示 (秒)");

            // 第五頁（資料庫）：區塊載入、顯示目前使用中的後端，測試暫存 SQLite 連線成功
            tabs.SelectedIndex = 4;
            DoEvents();
            Assert.Contains(FindAllVisual<GroupBox>(settingsView), g => string.Equals(g.Header as string, "資料庫"));
            Assert.StartsWith("目前使用 SQLite：", settingsVm.ActiveDatabase);
            settingsVm.Database.SqliteFilePath = Path.Combine(dir.FullName, "cache.db");
            settingsVm.TestDbConnectionCommand.Execute(null);
            DoEvents();
            PumpUntil(() => settingsVm.ConnectionTestSucceeded is not null);
            Assert.True(settingsVm.ConnectionTestSucceeded);

            // 第六頁（播放器）：原「直播播放」分頁只改標題，區塊載入
            tabs.SelectedIndex = 5;
            DoEvents();
            Assert.Contains(FindAllVisual<GroupBox>(settingsView), g => string.Equals(g.Header as string, "直播播放（streamlink）"));

            // ===== 詞彙卡審核頁（知識庫）：標題、名詞表格與選卡後的列都要載入 =====
            mainVm.NavigateCommand.Execute("Glossary");
            DoEvents();
            Assert.True(mainVm.IsGlossaryPage);
            var reviewView = FindVisual<GlossaryReviewView>(window);
            Assert.NotNull(reviewView);
            Assert.Contains(FindAllVisual<TextBlock>(reviewView), t => t.Text == "詞彙卡");
            var reviewGrid = FindVisual<DataGrid>(reviewView);
            Assert.NotNull(reviewGrid);

            // 預設篩選「有待審名詞」看不到全核准的 VALORANT：切成「全部」後選卡，名詞表要有列
            glossaryReviewVm.CardStatusFilter = "全部";
            DoEvents();
            var valorant = Assert.Single(glossaryReviewVm.Cards, c => c.Name == "VALORANT");
            glossaryReviewVm.SelectedCard = valorant;
            DoEvents();
            Assert.True(reviewGrid.Items.Count >= 1);
            Assert.NotEmpty(glossaryReviewVm.VisibleTerms);

            // 名詞表不得排序／重排欄序；勾選欄用範本欄放 CheckBox（單擊即切換），要找得到核選方塊
            Assert.False(reviewGrid.CanUserSortColumns);
            Assert.False(reviewGrid.CanUserReorderColumns);
            var firstTerm = Assert.IsType<GlossaryReviewTermItem>(reviewGrid.Items.GetItemAt(0));
            var checkColumn = Assert.Single(reviewGrid.Columns, c =>
                c.GetCellContent(firstTerm) is { } content && FindVisual<CheckBox>(content) is not null);
            Assert.IsType<DataGridTemplateColumn>(checkColumn);

            // 貼上資料整理：工具列按鈕要找得到，開關面板後整理面板會出現
            Assert.Contains(FindAllVisual<Button>(reviewView), b => string.Equals(b.Content as string, "貼上資料整理…"));
            glossaryReviewVm.ToggleImportPanelCommand.Execute(null);
            DoEvents();
            Assert.True(glossaryReviewVm.IsImportPanelOpen);
            Assert.Contains(FindAllVisual<Button>(reviewView), b => string.Equals(b.Content as string, "整理"));
            glossaryReviewVm.ToggleImportPanelCommand.Execute(null);
            DoEvents();
            Assert.False(glossaryReviewVm.IsImportPanelOpen);

            // ===== 聽錯字修正頁：先在暫存 SQLite Upsert 一筆，導覽後 View 能建立、DataGrid 顯示 1 列 =====
            var vandalTermId = db.GetGlossaryTerms().Single(t => t.Source == "ヴァンダル").Id;
            Assert.Equal(1, db.UpsertMishearingFixes(
                [new MishearingProposal(1, vandalTermId, MishearingKind.Pre, "ヴァンダール", "ヴァンダル", "ヴァンダール強いね")]));
            mainVm.NavigateCommand.Execute("Mishearing");
            DoEvents();
            Assert.True(mainVm.IsMishearingPage);
            var mishearingView = FindVisual<MishearingReviewView>(window);
            Assert.NotNull(mishearingView);
            var mishearingVm = provider.GetRequiredService<MishearingReviewViewModel>();
            Assert.Same(mishearingVm, mishearingView.DataContext);
            var mishearingGrid = FindVisual<DataGrid>(mishearingView);
            Assert.NotNull(mishearingGrid);
            Assert.Single(mishearingGrid.Items);
            Assert.True(mishearingVm.HasItems);
            // 空狀態提示在有資料時要收合（元素仍在視覺樹中）
            var emptyHint = FindAllVisual<TextBlock>(mishearingView)
                .Single(t => t.Text?.Contains("目前沒有聽錯字修正", StringComparison.Ordinal) == true);
            Assert.Equal(Visibility.Collapsed, emptyHint.Visibility);

            // 核准後 MishearingFixState 重新載入：Approved + Pre 的修正計入 Count（下一次翻譯前生效）
            var fixState = provider.GetRequiredService<MishearingFixState>();
            Assert.Equal(0, fixState.Count);
            mishearingVm.ApproveSelectedCommand.Execute(null);
            DoEvents();
            Assert.Equal(1, fixState.Count);
            Assert.Empty(mishearingVm.Items);

            // ===== 側邊欄待審數量：加一筆 Pending 聽錯字修正 → Notify → Pump 後顯示 (1)，TextBlock 也要 Visible =====
            PumpUntil(() => mainVm.MishearingBadgeText == string.Empty); // 核准（Notify）之後收斂回 0 筆
            Assert.Equal(1, db.UpsertMishearingFixes(
                [new MishearingProposal(1, vandalTermId, MishearingKind.Pre, "ヴアンダル", "ヴァンダル", "ヴアンダル強いね")]));
            provider.GetRequiredService<ReviewCountSignal>().Notify();
            PumpUntil(() => mainVm.MishearingBadgeText == "(1)");
            PumpUntil(() => FindAllVisual<TextBlock>(window)
                .Any(t => t.Text == "(1)" && t.Visibility == Visibility.Visible));

            // 切回其他頁不能拋出 XAML 例外（測試本身會失敗）
            // 左下角版本號：測試組件也由 git repo 建置，InformationalVersion 會帶 commit
            Assert.StartsWith("v", mainVm.AppVersion);
            Assert.Equal("v1.0.0 · 07c64f4", MainViewModel.FormatVersion("1.0.0+07c64f4a0123456789"));
            Assert.Equal("v1.0.0", MainViewModel.FormatVersion("1.0.0"));

            mainVm.NavigateCommand.Execute("Log");
            DoEvents();
            Assert.True(mainVm.IsLogPage);
            Assert.NotNull(FindVisual<LogView>(window));

            mainVm.NavigateCommand.Execute("Dashboard");
            DoEvents();
            Assert.True(mainVm.IsDashboardPage);
            Assert.NotNull(FindVisual<DashboardView>(window));

            // ===== 聽錯字探勘：時間到、新字幕夠多時把情境、生效名詞與最近字幕交給 GLM，
            // 找出的聽錯字存成 Pending 並跳提示框；立刻再呼叫不會重複執行；關掉設定就不執行 =====
            var fakeTime = provider.GetRequiredService<ManualTimeProvider>();
            // 狀態膠囊：啟動時還沒探勘過 → 上次 尚未、倒數為 m:ss 後
            vm.RefreshMishearingStatus();
            Assert.Contains("上次 尚未", vm.MishearingStatusText);
            Assert.Contains("每 5 分", vm.MishearingStatusText);
            Assert.Matches(@"\d+:\d{2} 後", vm.MishearingStatusText);
            fakeTime.Advance(TimeSpan.FromSeconds(5));
            var hinanoTermId = db.GetGlossaryTerms().Single(t => t.Source == "橘ひなの").Id;
            for (var i = 0; i < 10; i++)
            {
                Publish(i == 0 ? "橘雛のがすごい" : $"適当な発言{i}", "隨便的發言", TranslationSource.Model, modelLatency: 100);
            }
            DoEvents();
            fakeTime.Advance(TimeSpan.FromSeconds(300));
            stubLlm.JsonReplies.Enqueue(
                "{\"fixes\":[{\"termId\":" + hinanoTermId + ",\"wrong\":\"橘雛の\",\"evidence\":\"橘雛のがすごい\"}]}");
            var requestsBefore = stubLlm.UserPrompts.Count;
            var miningTask = vm.RunMishearingMiningIfDueAsync();
            PumpUntil(() => miningTask.IsCompleted);
            miningTask.GetAwaiter().GetResult();
            DoEvents();

            // GLM 收到一次請求（名詞清單與字幕都在裡面）、提示框多一則、資料庫多一筆 Pending
            Assert.Equal(requestsBefore + 1, stubLlm.UserPrompts.Count);
            Assert.Contains("## 名詞", stubLlm.UserPrompts[^1]);
            Assert.Contains("橘雛のがすごい", stubLlm.UserPrompts[^1]);
            var mishearingToast = Assert.Single(toasts.Items, t => t.Title == "聽錯字修正");
            Assert.Contains("新增 1 筆待審核", mishearingToast.Message);
            var minedFix = Assert.Single(db.GetMishearingFixes(), f => f.Wrong == "橘雛の");
            Assert.Equal(MishearingStatus.Pending, minedFix.Status);
            Assert.Equal("橘ひなの", minedFix.Right);
            Assert.Equal("橘雛のがすごい", minedFix.Evidence);

            // 立刻再呼叫：上一次探勘時間已更新、新字幕數歸 0，不會再發請求
            var rerunTask = vm.RunMishearingMiningIfDueAsync();
            PumpUntil(() => rerunTask.IsCompleted);
            rerunTask.GetAwaiter().GetResult();
            Assert.Equal(requestsBefore + 1, stubLlm.UserPrompts.Count);

            // 狀態膠囊：時間推進超過間隔、但新字幕不足 → 倒數顯示等待字幕
            fakeTime.Advance(TimeSpan.FromSeconds(310));
            vm.RefreshMishearingStatus();
            Assert.Contains("等待字幕", vm.MishearingStatusText);
            Assert.DoesNotContain("尚未", vm.MishearingStatusText);

            // 關掉設定後：即使時間到、字幕夠多也不會發請求
            settings.Current.Translation.MishearingMiningEnabled = false;
            fakeTime.Advance(TimeSpan.FromSeconds(310));
            for (var i = 0; i < 10; i++)
            {
                Publish($"別の発言{i}", "別的發言", TranslationSource.Model, modelLatency: 100);
            }
            DoEvents();
            var disabledTask = vm.RunMishearingMiningIfDueAsync();
            PumpUntil(() => disabledTask.IsCompleted);
            disabledTask.GetAwaiter().GetResult();
            Assert.Equal(requestsBefore + 1, stubLlm.UserPrompts.Count);

            // 狀態膠囊：設定關閉 → 已關閉，「立即探勘」按鈕停用
            vm.RefreshMishearingStatus();
            Assert.Equal("聽錯字探勘：已關閉", vm.MishearingStatusText);
            Assert.False(vm.MineMishearingNowCommand.CanExecute(null));

            // ===== 錄影有生成式字幕（CaptionTrackState 有 cue）：到期、新字幕也夠多，
            // 仍不自動跑聽錯字探勘（它是為直播的 Whisper 字幕設計的；手動「立即探勘」不受影響）=====
            settings.Current.Translation.MishearingMiningEnabled = true;
            var captions = provider.GetRequiredService<CaptionTrackState>();
            captions.Set("aaaaaaaaaaa", [new CaptionCue(0, 1000, "こんにちは"), new CaptionCue(2000, 1000, "ありがとう")]);
            fakeTime.Advance(TimeSpan.FromSeconds(310));
            for (var i = 0; i < 10; i++)
            {
                Publish($"字幕付き録画{i}", "有字幕的錄影", TranslationSource.Model, modelLatency: 100);
            }
            DoEvents();
            var vodCaptionsTask = vm.RunMishearingMiningIfDueAsync();
            PumpUntil(() => vodCaptionsTask.IsCompleted);
            vodCaptionsTask.GetAwaiter().GetResult();
            Assert.Equal(requestsBefore + 1, stubLlm.UserPrompts.Count);

            // 沒有字幕（直播開台會清空字幕狀態）時照舊：到期就自動執行
            captions.Clear();
            fakeTime.Advance(TimeSpan.FromSeconds(310));
            for (var i = 0; i < 10; i++)
            {
                Publish($"配信の字幕{i}", "直播的字幕", TranslationSource.Model, modelLatency: 100);
            }
            DoEvents();
            stubLlm.JsonReplies.Enqueue("{\"fixes\":[]}");
            var liveTask = vm.RunMishearingMiningIfDueAsync();
            PumpUntil(() => liveTask.IsCompleted);
            liveTask.GetAwaiter().GetResult();
            Assert.Equal(requestsBefore + 2, stubLlm.UserPrompts.Count);

            // 「立即探勘」：重新開啟設定，略過間隔與新字幕下限，時間窗內 1 句就執行；
            // 沒有新增時也跳「聽錯字探勘完成」提示框，執行後狀態不再顯示「尚未」
            settings.Current.Translation.MishearingMiningEnabled = true;
            fakeTime.Advance(TimeSpan.FromSeconds(310)); // 把時間窗內的舊字幕清掉，只留下一句
            Publish("橘ひなの、また負けた", "橘雛乃又輸了", TranslationSource.Model, modelLatency: 100);
            DoEvents();
            vm.RefreshMishearingStatus();
            Assert.True(vm.MineMishearingNowCommand.CanExecute(null));
            stubLlm.JsonReplies.Enqueue("{\"fixes\":[]}");
            var manualRequestsBefore = stubLlm.UserPrompts.Count;
            var manualTask = vm.MineMishearingNowCommand.ExecuteAsync(null);
            PumpUntil(() => manualTask.IsCompleted);
            manualTask.GetAwaiter().GetResult();
            DoEvents();
            // 假 GLM 收到一次請求（字幕在裡面）、跳完成提示框、上次時間已更新
            Assert.Equal(manualRequestsBefore + 1, stubLlm.UserPrompts.Count);
            Assert.Contains("橘ひなの、また負けた", stubLlm.UserPrompts[^1]);
            var manualToast = Assert.Single(toasts.Items, t => t.Title == "聽錯字探勘完成");
            Assert.Contains("沒有新的聽錯字", manualToast.Message);
            vm.RefreshMishearingStatus();
            Assert.DoesNotContain("尚未", vm.MishearingStatusText);
            Assert.Contains("上次結果", vm.MishearingStatusToolTip);

            window.Close();

            // 測試結束：停止接收並把剩下的字幕紀錄寫完
            writer.Dispose();
        }
        finally
        {
            WeakReferenceMessenger.Default.Reset();
            try { dir.Delete(recursive: true); } catch (IOException) { /* SQLite 連線池可能還握著檔案：暫存目錄留給系統清 */ }
        }
    }

    /// <summary>直接讀 SubtitleLog 的列，供斷言欄位值。</summary>
    private sealed class SubtitleLogRow
    {
        public string JobId { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        public long IsEdited { get; set; }
        public long IsDeleted { get; set; }
    }

    /// <summary>可以控制 HasExited 並記錄 KillTree 次數的假背景程序。</summary>
    private sealed class FakeStartedProcess : IStartedProcess
    {
        public bool HasExited { get; set; }

        public int KillTreeCount { get; private set; }

        public void KillTree() => KillTreeCount++;
    }

    /// <summary>不真的執行程式的假行程執行器：RunAsync 回傳排入的結果，Start 回傳可控制的假程序。</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public HashSet<string> ExistingFiles { get; } = new();

        public Queue<ProcessResult> Results { get; } = new();

        public List<(string FileName, IReadOnlyList<string> Arguments)> Runs { get; } = [];

        public List<(string FileName, IReadOnlyList<string> Arguments)> Starts { get; } = [];

        public List<FakeStartedProcess> StartedProcesses { get; } = new();

        public bool FileExists(string path) => ExistingFiles.Contains(path);

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Runs.Add((fileName, arguments));
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new ProcessResult(0, string.Empty, string.Empty, false));
        }

        public IStartedProcess Start(string fileName, IReadOnlyList<string> arguments, Action<string> onOutput)
        {
            Starts.Add((fileName, arguments));
            var process = new FakeStartedProcess();
            StartedProcesses.Add(process);
            return process;
        }
    }

    /// <summary>不連網的假影片資訊查詢：一律回 null（查不到）。</summary>
    private sealed class StubVideoInfoProvider : IVideoInfoProvider
    {
        public Task<StreamMetadata?> GetYouTubeInfoAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult<StreamMetadata?>(null);
    }

    /// <summary>假的播放時鐘：固定回 null（沒有播放位置），不會去找 PotPlayer 行程。</summary>
    private sealed class NullPlaybackClock : IPlaybackClock
    {
        public long? GetPositionMs() => null;
    }

    /// <summary>不啟動任何程式的假 PotPlayer 字幕載入器：一律回 false（當作沒有正在執行的 PotPlayer）。</summary>
    private sealed class StubPotPlayerSubtitleLoader : IPotPlayerSubtitleLoader
    {
        public bool Load(string subtitlePath) => false;
    }

    /// <summary>不碰任何程序的假 PotPlayer 程序清單：AnyRunning 與關閉結果可控制。</summary>
    private sealed class StubPotPlayerProcesses : IPotPlayerProcesses
    {
        public StubPotPlayerProcesses(bool anyRunning = false)
        {
            AnyRunningValue = anyRunning;
        }

        public bool AnyRunningValue { get; set; }

        public int CloseAllCount { get; private set; }

        public bool AnyRunning() => AnyRunningValue;

        public Task<bool> WaitAllExitedAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> CloseAllAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            CloseAllCount++;
            AnyRunningValue = false;
            return Task.FromResult(true);
        }
    }

    /// <summary>不連網的假字幕下載：固定回傳 3 段日文字幕。</summary>
    private sealed class StubCaptionProvider : IYouTubeCaptionProvider
    {
        public Task<YouTubeCaptionResult?> GetJapaneseCaptionsAsync(string videoId, string? visitorData, CancellationToken cancellationToken = default) =>
            Task.FromResult<YouTubeCaptionResult?>(new YouTubeCaptionResult(
                "ja",
                true,
                "IOS",
                [
                    new CaptionCue(1000, 2000, "こんにちは"),
                    new CaptionCue(3000, 2000, "ありがとう"),
                    new CaptionCue(5000, 2000, "さようなら"),
                ]));
    }

    /// <summary>不連網的假熱門留言查詢：固定回 null（查不到留言），章節來源只會用說明欄。</summary>
    private sealed class StubCommentProvider : IYouTubeCommentProvider
    {
        public Task<IReadOnlyList<YouTubeComment>?> GetTopCommentsAsync(string videoId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<YouTubeComment>?>(null);
    }

    /// <summary>不連網的知識庫模型替身：測試連線一律成功；CompleteJson 依序回傳排入的腳本 JSON（沒有排入時回傳空物件）。</summary>
    private sealed class StubLorebookLlm : ILorebookLlm
    {
        /// <summary>資料探勘的腳本回覆（拆名詞與建卡各一次，依序 dequeue）。</summary>
        public Queue<string> JsonReplies { get; } = new();

        /// <summary>每次收到的 user 訊息（驗證開台探勘送出的內容用）。</summary>
        public List<string> UserPrompts { get; } = [];

        public Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            UserPrompts.Add(userPrompt);
            return Task.FromResult(new LlmJsonResult(
                JsonReplies.Count > 0 ? JsonReplies.Dequeue() : "{\"ok\": true}", 1, 1, 1, "stub-glm"));
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功：stub-glm，1 ms"));
    }

    /// <summary>不連網的知識來源替身：「ほの暮」相關的查詢回傳固定的 Wikidata 資料，其餘查不到。</summary>
    private sealed class StubKnowledgeSources : IKnowledgeSources
    {
        public Task<IReadOnlyList<WikidataHit>> SearchWikidataAsync(string text, string language = "ja", int limit = 3, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WikidataHit>>(
                text.Contains("ほの暮", StringComparison.Ordinal)
                    ? [new WikidataHit("Q1234567", "ほの暮らしの庭", "任天堂のゲームソフト")]
                    : []);

        public Task<WikidataEntity?> GetWikidataEntityAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<WikidataEntity?>(new WikidataEntity(
                id,
                new Dictionary<string, string> { ["ja"] = "ほの暮らしの庭", ["zh-tw"] = "恬靜庭園", ["en"] = "Honokamura no Niwa" },
                new Dictionary<string, string> { ["ja"] = "任天堂の生活シミュレーションゲーム", ["en"] = "life simulation game" },
                new Dictionary<string, string> { ["ja"] = "ほのぼのガーデン" },
                JaWikiTitle: null, ZhWikiTitle: "恬靜庭園", EnWikiTitle: null));

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

    private static ServiceProvider BuildServices(SettingsService settings, SqliteTranslationStore db, IProcessRunner processRunner,
        StubLorebookLlm? lorebookLlm = null, StubKnowledgeSources? knowledgeSources = null,
        StubPotPlayerProcesses? potPlayerProcesses = null, StreamTitleTranslator? titleTranslator = null,
        PlaybackLineTracker? playbackTracker = null, IPlaybackClock? playbackClock = null,
        IPlaybackSeeker? playbackSeeker = null)
    {
        lorebookLlm ??= new StubLorebookLlm();
        knowledgeSources ??= new StubKnowledgeSources();
        potPlayerProcesses ??= new StubPotPlayerProcesses();
        var fakeTime = new ManualTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton<ITranslationStore>(db);
        services.AddSingleton<IGlossaryStore>(db);
        services.AddSingleton<ISubtitleLogStore>(sp => (ISubtitleLogStore)sp.GetRequiredService<ITranslationStore>());
        services.AddSingleton<ICaptionStore>(sp => (ICaptionStore)sp.GetRequiredService<ITranslationStore>());
        // prompt 版本化：SettingsViewModel 依賴 PromptTemplateRegistry（測試不呼叫 Initialize，版本清單只含「跟著最新預設」）
        services.AddSingleton<IAuditStore>(db);
        services.AddSingleton<PromptTemplateRegistry>();
        services.AddSingleton<StreamSession>();
        services.AddSingleton<SubtitleLogWriter>();
        services.AddSingleton<StreamCardRecorder>();
        services.AddSingleton<GlossaryState>();
        services.AddSingleton<KeywordGlossary>();
        services.AddSingleton<MishearingFixState>();
        services.AddSingleton<IOpenAiClient, StubOpenAiClient>();
        services.AddSingleton<ITextProcessingService, TextProcessingService>();
        services.AddSingleton(processRunner);
        services.AddSingleton<IVideoInfoProvider>(new StubVideoInfoProvider());
        // YouTube 錄影字幕：假的下載器固定回傳 3 段（現有開台情境都是 Twitch，不會真的用到）
        services.AddSingleton<CaptionTrackState>();
        services.AddSingleton<IYouTubeCaptionProvider>(new StubCaptionProvider());
        // YouTube 字幕下載（開台與 PotPlayer 腳本通知共用）：MainViewModel 依賴它（pageInfo 沒註冊，用預設 null）
        services.AddSingleton<CaptionLoader>();
        // 章節名稱疊加：MainViewModel 開片時解析 ChapterOverlayState（假的留言查詢不連網，查不到章節）
        services.AddSingleton<IYouTubeCommentProvider>(new StubCommentProvider());
        services.AddSingleton<VideoChapterService>();
        services.AddSingleton<ChapterTitleTranslator>();
        services.AddSingleton<ChapterOverlayState>();
        // 生成式字幕前處理：MainViewModel 依賴 CaptionPreprocessor（假的播放時鐘已註冊，不會真的找 PotPlayer）
        services.AddSingleton<CaptionSentenceState>();
        // 錄影開片的詞彙卡準備門：CaptionPreprocessor、VodPrefetcher、MainViewModel 共用（開台時開關）
        services.AddSingleton<CardReadinessGate>();
        services.AddSingleton<CaptionPreprocessor>();
        // 前處理有進度時的 PotPlayer 字幕推送：假的載入器一律回 false（不啟動任何程式）
        services.AddSingleton<IPotPlayerSubtitleLoader>(new StubPotPlayerSubtitleLoader());
        // YouTube 字幕參考：假的播放時鐘固定回 null（不真的去找 PotPlayer 行程）
        services.AddSingleton<IPlaybackClock>(new NullPlaybackClock());
        // 錄影「目前播放」高亮：有傳才註冊（播放時鐘換成可控制的假時鐘；後註冊的優先）
        if (playbackTracker is not null)
        {
            services.AddSingleton(playbackTracker);
        }

        if (playbackClock is not null)
        {
            services.AddSingleton<IPlaybackClock>(playbackClock);
        }
        // 歌曲清單點歌跳轉：有傳才註冊（沒傳時 DashboardViewModel 的選用參數是 null，點歌不做事）
        if (playbackSeeker is not null)
        {
            services.AddSingleton<IPlaybackSeeker>(playbackSeeker);
        }
        services.AddSingleton<CaptionReference>();
        // 開台前詢問關閉 PotPlayer 用：假的程序清單（不掃描、不啟動或結束任何程式）
        services.AddSingleton<IPotPlayerProcesses>(potPlayerProcesses);
        services.AddSingleton<StreamlinkLauncher>();
        services.AddSingleton(sp => new TranslationManager(
            sp.GetRequiredService<IOpenAiClient>(),
            sp.GetRequiredService<ITextProcessingService>(),
            sp.GetRequiredService<ITranslationStore>(),
            sp.GetRequiredService<ISettingsService>(),
            glossary: sp.GetRequiredService<GlossaryState>(),
            keywordGlossary: sp.GetRequiredService<KeywordGlossary>(),
            mishearingFixes: sp.GetRequiredService<MishearingFixState>(),
            captionReference: sp.GetRequiredService<CaptionReference>()));
        services.AddSingleton(new UiLogStore());
        services.AddSingleton(new ApiTraceStore());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILorebookLlm>(lorebookLlm);
        services.AddSingleton<IKnowledgeSources>(knowledgeSources);
        services.AddSingleton<MiningService>();
        services.AddSingleton<MishearingMiningService>();
        services.AddSingleton<GameTermDiscoveryService>();
        services.AddSingleton<TermImportService>();
        services.AddSingleton<ReviewCountSignal>();
        services.AddSingleton<ToastService>();
        // MainViewModel 依賴：知識庫模型健康狀態（時間用已註冊的 fakeTime，不打真實模型）
        services.AddSingleton<LlmHealth>();
        // 聽錯字探勘的時間來源：可手動推進，測試不必真的等 5 分鐘
        services.AddSingleton(fakeTime);
        services.AddSingleton<TimeProvider>(fakeTime);
        // 直播標題翻譯（Dashboard 頁首）：有傳才註冊（沒傳時 DashboardViewModel 的選用參數是 null，頁首只顯示原文）
        if (titleTranslator is not null)
        {
            services.AddSingleton(titleTranslator);
        }
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<GlossaryReviewViewModel>();
        services.AddSingleton<MishearingReviewViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider();
    }

    /// <summary>可手動推進時間的 TimeProvider（聽錯字探勘的時間判斷用），避免測試真的等待。</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan time) => _utcNow += time;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private static void Publish(string current, string translated, TranslationSource source, long modelLatency) =>
        WeakReferenceMessenger.Default.Send(new TranslationJob
        {
            Current = current,
            CacheKey = current,
            TranslatedText = translated,
            Source = source,
            ModelLatency = modelLatency,
        });

    /// <summary>讓 Dispatcher 跑完排隊中的工作（版面配置、繫結更新、範本套用）。</summary>
    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>反覆打氣 Dispatcher 直到條件成立（或逾時），給非同步命令收尾用。</summary>
    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等候條件逾時");
            DoEvents();
            Thread.Sleep(10);
        }
    }

    /// <summary>開台自動資料探勘的斷言：等探勘跑完後，狀態列有提示、送給 GLM 的 user 訊息含頻道名稱、
    /// 情境欄等於 expectedSituation（全新的直播已清空 → 空字串；同網址重開 → 沿用開台前輸入的內容）、
    /// 最後一則提示框的標題是「開台資料探勘完成」。</summary>
    private static void AssertStreamAutoMining(DashboardViewModel vm, MainViewModel mainVm,
        StubLorebookLlm stubLlm, ToastService toasts, string expectedSituation)
    {
        PumpUntil(() => !vm.IsMining);
        DoEvents();
        Assert.Contains("開始資料探勘", mainVm.StreamStatus);
        Assert.Contains("Sumire Ch. 花芽すみれ", stubLlm.UserPrompts[^1]);
        Assert.Equal(expectedSituation, vm.StreamSituation);
        Assert.NotEmpty(toasts.Items);
        Assert.Equal("開台資料探勘完成", toasts.Items[^1].Title);
    }

    /// <summary>以 AutomationProperties.AutomationId 在視覺樹中尋找元素。</summary>
    private static T FindByAutomationId<T>(DependencyObject root, string automationId) where T : FrameworkElement =>
        FindAllVisual<T>(root).Single(e =>
            string.Equals(AutomationProperties.GetAutomationId(e), automationId, StringComparison.Ordinal));

    /// <summary>以欄位標籤文字找出同一列（Grid.Row 相同）的輸入框，例如 ModelConnectionControl 的「模型名稱」。</summary>
    private static TextBox FindFieldBox(DependencyObject root, string label)
    {
        var labelBlock = FindAllVisual<TextBlock>(root).Single(t => t.Text == label);
        var row = Grid.GetRow(labelBlock);
        var parent = VisualTreeHelper.GetParent(labelBlock);
        return FindAllVisual<TextBox>(parent).Single(b => Grid.GetRow(b) == row);
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            return match;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>收集視覺樹中所有型別為 <typeparamref name="T"/> 的元素。</summary>
    private static IEnumerable<T> FindAllVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            foreach (var found in FindAllVisual<T>(VisualTreeHelper.GetChild(root, i)))
            {
                yield return found;
            }
        }
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    // ===== Dashboard 單元測試（DashboardViewModelTests.cs 不存在，依任務卡寫進 UiSmokeTests）=====

    /// <summary>用暫存 SQLite 與 DI 組出 DashboardViewModel 的測試內容；釋放時清掉暫存目錄。</summary>
    private sealed class DashboardContext : IDisposable
    {
        public required string Dir { get; init; }
        public required SettingsService Settings { get; init; }
        public required ServiceProvider Provider { get; init; }

        public DashboardViewModel Dashboard => Provider.GetRequiredService<DashboardViewModel>();

        public GlossaryState GlossaryState => Provider.GetRequiredService<GlossaryState>();

        public void Dispose()
        {
            Provider.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { /* SQLite 連線池可能還握著檔案 */ }
        }
    }

    private static DashboardContext CreateDashboardContext(
        Action<SqliteConnection> seed, Action<AppSettings>? configureSettings = null,
        ILorebookLlm? titleLlm = null,
        PlaybackLineTracker? playbackTracker = null, IPlaybackClock? playbackClock = null,
        IPlaybackSeeker? playbackSeeker = null)
    {
        var dir = Directory.CreateTempSubdirectory("gateway-dashboard-");
        var dbPath = Path.Combine(dir.FullName, "cache.db");
        var db = new SqliteTranslationStore(NullLogger<SqliteTranslationStore>.Instance, dbPath);
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            seed(conn);
        }

        var settings = new SettingsService(
            NullLogger<SettingsService>.Instance, Path.Combine(dir.FullName, "settings.json"));
        configureSettings?.Invoke(settings.Current);
        // 標題翻譯用獨立的假模型（可控制完成時機）：與探勘用的 StubLorebookLlm 分開，才不會吃掉探勘的腳本回覆
        StreamTitleTranslator? titleTranslator = titleLlm is null ? null : new StreamTitleTranslator(
            titleLlm, new GlossaryState(), new TextProcessingService(), NullLogger<StreamTitleTranslator>.Instance);
        var provider = BuildServices(settings, db, new FakeProcessRunner(), titleTranslator: titleTranslator,
            playbackTracker: playbackTracker, playbackClock: playbackClock, playbackSeeker: playbackSeeker);
        return new DashboardContext { Dir = dir.FullName, Settings = settings, Provider = provider };
    }

    [Fact]
    public void SituationSaveTarget_DecidesBySituationAndProfile()
    {
        using var ctx = CreateDashboardContext(conn =>
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')"));
        var vm = ctx.Dashboard;

        // 情境空白：不需要存
        vm.StreamSituation = "  ";
        Assert.Null(vm.SituationSaveTarget("花芽すみれ"));

        // 情境與套用中設定檔的內容相同（兩邊都修剪）：不需要存
        ctx.Settings.Current.StreamProfiles.Add(new StreamProfile { Name = "佩克拉", StreamSituation = "兔田佩克拉在打麻將" });
        ctx.Settings.Current.ActiveProfileName = "佩克拉";
        vm.StreamSituation = " 兔田佩克拉在打麻將 ";
        Assert.Null(vm.SituationSaveTarget("花芽すみれ"));

        // 情境與套用中設定檔不同：存回 ActiveProfileName
        vm.StreamSituation = "兔田佩克拉在唱歌";
        Assert.Equal("佩克拉", vm.SituationSaveTarget("花芽すみれ"));

        // 沒有套用中的設定檔：用 fallbackName（上一場的頻道名稱）
        ctx.Settings.Current.ActiveProfileName = string.Empty;
        Assert.Equal("花芽すみれ", vm.SituationSaveTarget("花芽すみれ"));

        // ActiveProfileName 指向已刪除的設定檔：視為沒有，也是用 fallbackName
        ctx.Settings.Current.ActiveProfileName = "已刪除的設定檔";
        Assert.Equal("花芽すみれ", vm.SituationSaveTarget("花芽すみれ"));

        // fallbackName 空白：用畫面上的 ProfileName
        ctx.Settings.Current.ActiveProfileName = string.Empty;
        vm.ProfileName = "手上名稱";
        Assert.Equal("手上名稱", vm.SituationSaveTarget("  "));

        // 全部都沒有：回傳 null（略過詢問）
        vm.ProfileName = " ";
        Assert.Null(vm.SituationSaveTarget(null));
    }

    [Fact]
    public void ClearForNewStream_ClearsCardsAndSituation_KeepsTableAndRules()
    {
        using var ctx = CreateDashboardContext(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (2, 'Group', 'ぶいすぽっ!', 'Approved')");
            conn.Execute("INSERT INTO GlossaryTerms (CardId, Source, Target, Status) VALUES (1, 'ヴァンダル', '暴徒', 'Approved')");
            conn.Execute("INSERT INTO GlossaryTerms (CardId, Source, Target, Status) VALUES (2, '花芽すみれ', '花芽澄伶', 'Approved')");
        }, settings => settings.ActiveGlossaryCardIds = [1, 2]);
        var vm = ctx.Dashboard;
        vm.StreamSituation = "今天玩 VALORANT";
        vm.CharacterTable = "ぺこら=佩克拉";
        vm.ReplacementRules = "舊譯=新譯";
        vm.SourceReplacementRules = "聽錯=正確";

        vm.ClearForNewStream();

        // 生效卡全部移除、情境清空、設定裡的生效清單也清空
        Assert.Empty(vm.ActiveGlossaryIds);
        Assert.Empty(vm.ActiveGlossaryCards);
        Assert.Equal(string.Empty, vm.StreamSituation);
        Assert.Empty(ctx.Settings.Current.ActiveGlossaryCardIds);
        Assert.Equal(string.Empty, ctx.GlossaryState.PromptSection);
        // 角色表、譯名修正與原文修正不動
        Assert.Equal("ぺこら=佩克拉", vm.CharacterTable);
        Assert.Equal("舊譯=新譯", vm.ReplacementRules);
        Assert.Equal("聽錯=正確", vm.SourceReplacementRules);
        Assert.Equal("ぺこら=佩克拉", ctx.Settings.Current.CharacterTable);
        Assert.Equal("舊譯=新譯", ctx.Settings.Current.ReplacementRules);
        Assert.Equal("聽錯=正確", ctx.Settings.Current.SourceReplacementRules);
    }

    [Fact]
    public void SaveProfileAs_SavesCurrentContent_KeepsActiveCards()
    {
        using var ctx = CreateDashboardContext(conn =>
        {
            conn.Execute("INSERT INTO GlossaryCards (Id, Kind, Name, Status) VALUES (1, 'Game', 'VALORANT', 'Approved')");
            conn.Execute("INSERT INTO GlossaryTerms (CardId, Source, Target, Status) VALUES (1, 'ヴァンダル', '暴徒', 'Approved')");
        }, settings => settings.ActiveGlossaryCardIds = [1]);
        var vm = ctx.Dashboard;
        vm.StreamSituation = "兔田佩克拉在打麻將";
        vm.CharacterTable = "ぺこら=佩克拉";
        vm.ProfileName = "別的名稱";

        vm.SaveProfileAs(" 佩克拉 ");

        var profile = Assert.Single(ctx.Settings.Current.StreamProfiles, p => p.Name == "佩克拉");
        Assert.Equal("兔田佩克拉在打麻將", profile.StreamSituation);
        Assert.Equal("ぺこら=佩克拉", profile.CharacterTable);
        Assert.Equal("佩克拉", ctx.Settings.Current.ActiveProfileName);
        Assert.Equal("佩克拉", vm.ProfileName);
        Assert.Contains("已新增", vm.ProfileMessage);
        // 生效詞彙卡不變（SaveProfile 命令的情境比對詞彙卡不在 SaveProfileAs 裡）
        Assert.Equal([1L], ctx.Settings.Current.ActiveGlossaryCardIds);
        Assert.Single(vm.ActiveGlossaryCards);

        // 同名再存一次：覆寫內容並顯示「已覆寫」
        vm.StreamSituation = "兔田佩克拉在唱歌";
        vm.SaveProfileAs("佩克拉");
        Assert.Equal("兔田佩克拉在唱歌", profile.StreamSituation);
        Assert.Contains("已覆寫", vm.ProfileMessage);
        Assert.Single(ctx.Settings.Current.StreamProfiles);
    }

    [Fact]
    public void ExpanderState_LoadsFromSettings_AndSyncsBack()
    {
        using var ctx = CreateDashboardContext(_ => { }, settings =>
        {
            settings.StreamInfoExpanded = true;
            settings.RulesExpanded = true;
        });
        var vm = ctx.Dashboard;

        // 建構子從設定讀初值
        Assert.True(vm.StreamInfoExpanded);
        Assert.True(vm.RulesExpanded);

        // 改屬性會同步寫回設定
        vm.StreamInfoExpanded = false;
        vm.RulesExpanded = false;
        Assert.False(ctx.Settings.Current.StreamInfoExpanded);
        Assert.False(ctx.Settings.Current.RulesExpanded);

        vm.StreamInfoExpanded = true;
        vm.RulesExpanded = true;
        Assert.True(ctx.Settings.Current.StreamInfoExpanded);
        Assert.True(ctx.Settings.Current.RulesExpanded);

        // 展開狀態與直播資訊內容無關：切換時不走 OnStreamInfoChanged（StreamInfoUpdatedAt 維持 null，代表沒有預熱）
        Assert.Null(ctx.Settings.Current.StreamInfoUpdatedAt);
    }

    [Fact]
    public void SetStreamTitle_ShowsOriginalThenTranslation()
    {
        var llm = new GatedTitleLlm();
        using var ctx = CreateDashboardContext(_ => { }, titleLlm: llm);
        var vm = ctx.Dashboard;

        // 預設頁首
        Assert.Equal("字幕監看", vm.HeaderTitle);
        Assert.Null(vm.HeaderSubtitle);

        // 開台後先顯示原文
        vm.SetStreamTitle("【APEX】雑談配信");
        Assert.Equal("【APEX】雑談配信", vm.HeaderTitle);
        Assert.Null(vm.HeaderSubtitle);

        // 翻譯完成：譯文大字在上、原文小字在下
        llm.Complete(0, """{"translation": "【APEX】雜談直播"}""");
        PumpUntil(() => vm.HeaderTitle == "【APEX】雜談直播");
        Assert.Equal("【APEX】雑談配信", vm.HeaderSubtitle);

        // title 空白：回到預設
        vm.SetStreamTitle(" ");
        Assert.Equal("字幕監看", vm.HeaderTitle);
        Assert.Null(vm.HeaderSubtitle);
    }

    [Fact]
    public void SetStreamTitle_StaleResultIgnored()
    {
        var llm = new GatedTitleLlm();
        using var ctx = CreateDashboardContext(_ => { }, titleLlm: llm);
        var vm = ctx.Dashboard;

        // 第一場開台後馬上換場：舊的翻譯還在跑（假模型故意不吃取消，模擬模型慢慢回、被取代後仍送達）
        vm.SetStreamTitle("第一場標題");
        Assert.Equal(1, llm.CallCount);
        vm.SetStreamTitle("第二場標題");
        Assert.Equal(2, llm.CallCount);
        Assert.Equal("第二場標題", vm.HeaderTitle);
        Assert.Null(vm.HeaderSubtitle);

        // 舊的翻譯這時才回來：換場後的舊結果丟掉，頁首維持第二場的原文
        llm.Complete(0, """{"translation": "第一場譯文"}""");
        Thread.Sleep(200);
        Assert.Equal("第二場標題", vm.HeaderTitle);
        Assert.Null(vm.HeaderSubtitle);

        // 第二場的翻譯回來：正常更新
        llm.Complete(1, """{"translation": "第二場譯文"}""");
        PumpUntil(() => vm.HeaderTitle == "第二場譯文");
        Assert.Equal("第二場標題", vm.HeaderSubtitle);
    }

    // 錄影「目前播放」高亮：推送字幕行＋播放位置對上某句 → 該列 IsPlaying；換位置後舊列變 false、新列變 true；
    // 位置超過所有行之後高亮清掉。計時器（0.5 秒）走真實時間，用 PumpUntil 打氣 Dispatcher 等 tick。
    [Fact]
    public void PlayingHighlight_FollowsPlaybackPosition_AndClearsOutsideLines()
    {
        RunOnSta(() =>
        {
            // 與其他情境一樣裝 Dispatcher 同步內容，查詢的接續才會留在這條 UI 執行緒
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            using var playback = new PlaybackPushHarness();
            var clock = new ScriptedPlaybackClock { Position = 1500 };
            using var ctx = CreateDashboardContext(_ => { }, playbackTracker: playback.Tracker, playbackClock: clock);
            var vm = ctx.Dashboard;
            var jobA = NewPlayingJob("おはよう。");
            var jobB = NewPlayingJob("こんにちは。");
            vm.Jobs.Insert(0, jobB);
            vm.Jobs.Insert(0, jobA);

            // 兩句各佔 [1000,3000) 與 [3000,5000)（句子蓋住 StubCaptionProvider 的三段 cue）
            playback.Push(
                NewSentence(1000, 3000, "おはよう。"),
                NewSentence(3000, 5000, "こんにちは。"));

            // 位置 1500 → 第一列高亮
            PumpUntil(() => jobA.IsPlaying);
            Assert.False(jobB.IsPlaying);

            // 位置 4000 → 換第二列，第一列取消
            clock.Position = 4000;
            PumpUntil(() => jobB.IsPlaying);
            Assert.False(jobA.IsPlaying);

            // 位置 9000（超過所有行）→ 高亮清掉
            clock.Position = 9000;
            PumpUntil(() => !jobB.IsPlaying);
            Assert.False(jobA.IsPlaying);
        });
    }

    private static TranslationJob NewPlayingJob(string text) => new()
    {
        Current = text,
        CacheKey = text,
        TranslatedText = "譯文",
    };

    private static CaptionSentence NewSentence(long startMs, long endMs, string text) =>
        new(startMs, endMs, text, text, "m");

    /// <summary>可控制播放位置的假時鐘（錄影「目前播放」高亮用）。</summary>
    private sealed class ScriptedPlaybackClock : IPlaybackClock
    {
        public long? Position { get; set; }

        public long? GetPositionMs() => Position;
    }

    /// <summary>可控制結果的假跳轉器（歌曲清單點歌用）：記下每次收到的毫秒數。</summary>
    private sealed class StubPlaybackSeeker : IPlaybackSeeker
    {
        public List<long> Requests { get; } = [];

        public bool Result { get; set; } = true;

        public bool SeekTo(long positionMs)
        {
            Requests.Add(positionMs);
            return Result;
        }
    }

    // ===== 歌曲清單（直播資訊左欄分頁）=====

    /// <summary>同步等 Task 完成（歌曲清單測試用；集中在非測試方法裡，避免 xUnit1031 的封鎖呼叫警告）。</summary>
    private static void WaitFor(Task task) => task.GetAwaiter().GetResult();

    /// <summary>同步等 Task&lt;T&gt; 完成（歌曲清單測試用；集中在非測試方法裡，避免 xUnit1031 的封鎖呼叫警告）。</summary>
    private static T WaitFor<T>(Task<T> task) => task.GetAwaiter().GetResult();

    /// <summary>錄影開片後歌曲清單從 ChapterOverlayState 重建（真的狀態＋假的留言／模型）：
    /// 數量、時間範圍（同左上角章節名的結束規則）、來源文字與第一次自動切分頁；同一支影片只自動切一次。</summary>
    [Fact]
    public void SongList_BuildsFromChapterOverlay_AndAutoSwitchesOncePerVideo()
    {
        using var ctx = CreateDashboardContext(_ => { });
        var vm = ctx.Dashboard;
        var overlay = ctx.Provider.GetRequiredService<ChapterOverlayState>();

        // 尚未開片：分頁不顯示、清單空、停在情境分頁
        Assert.False(vm.ShowSongList);
        Assert.Equal(0, vm.InfoTabIndex);
        Assert.Empty(vm.SongList);

        // 錄影開片：說明欄有 3 個章節 → Changed 重建清單、顯示分頁、第一次載到章節自動切到歌曲清單分頁
        WaitFor(overlay.LoadAsync("dQw4w9WgXcQ", "0:00 オープニング\n8:35 一番好きな歌\n12:58 雑談", CancellationToken.None));
        PumpUntil(() => vm.SongList.Count == 3);

        Assert.True(vm.ShowSongList);
        Assert.Equal(1, vm.InfoTabIndex);
        Assert.Equal("來源：說明欄", vm.SongListSourceText);
        Assert.Equal("オープニング", vm.SongList[0].Title);
        // EndMs 同左上角章節名的規則：查不到原曲長度顯示 300 秒（預設）、不超過下一章開始；最後一章（videoEndMs=0）用 +60 秒
        Assert.Equal(0, vm.SongList[0].StartMs);
        Assert.Equal(300000, vm.SongList[0].EndMs);
        Assert.Equal("00:00–05:00", vm.SongList[0].TimeRangeText);
        Assert.Equal("08:35–12:58", vm.SongList[1].TimeRangeText);
        Assert.Equal("12:58–13:58", vm.SongList[2].TimeRangeText);
        // 假 GLM 的預設回覆（{"ok": true}）翻不了標題 → 譯文維持 null
        Assert.Null(vm.SongList[1].Translation);

        // 使用者切回情境分頁後，後續的 Changed（人工貼上等）不得再自動切過去：同一支影片只做一次
        vm.InfoTabIndex = 0;
        WaitFor(overlay.ApplyManualAsync("0:00 ねんね\n3:00 うた\n6:00 おわり", CancellationToken.None));
        PumpUntil(() => vm.SongList.Count == 3 && vm.SongList[2].StartMs == 360000);
        Assert.Equal(0, vm.InfoTabIndex);
        Assert.Equal("來源：人工貼上", vm.SongListSourceText);
    }

    /// <summary>直播（StreamSession.IsLive）時不顯示歌曲清單分頁：ShowSongList false、分頁索引回到情境、清單清空。</summary>
    [Fact]
    public void SongList_LiveStream_HidesSongTab()
    {
        using var ctx = CreateDashboardContext(_ => { });
        var vm = ctx.Dashboard;
        var overlay = ctx.Provider.GetRequiredService<ChapterOverlayState>();
        ctx.Provider.GetRequiredService<StreamSession>().IsLive = true;
        vm.InfoTabIndex = 1; // 假設使用者上次停在歌曲清單分頁

        WaitFor(overlay.LoadAsync("dQw4w9WgXcQ", "0:00 A\n8:35 B\n12:58 C", CancellationToken.None));

        Assert.False(vm.ShowSongList);
        Assert.Equal(0, vm.InfoTabIndex);
        Assert.Empty(vm.SongList);
        Assert.Equal(string.Empty, vm.SongListSourceText);
    }

    /// <summary>從剪貼簿貼上セトリ：空白／不合格的對應訊息；合格的三行時間軸套用成功、來源變人工、
    /// 「清除人工章節」變可用；清除後改回自動抓到的章節。</summary>
    [Fact]
    public void PasteChapters_MessagesForEmptyInvalidAndValidText()
    {
        using var ctx = CreateDashboardContext(_ => { });
        var vm = ctx.Dashboard;
        var overlay = ctx.Provider.GetRequiredService<ChapterOverlayState>();

        // 先開片（人工貼上需要有影片）；說明欄與留言都沒有章節 → 顯示貼上提示
        WaitFor(overlay.LoadAsync("dQw4w9WgXcQ", "只是說明", CancellationToken.None));
        PumpUntil(() => vm.ShowSongList && vm.SongList.Count == 0);
        Assert.Equal("沒有找到章節，請複製セトリ後按「從剪貼簿貼上」", vm.SongListSourceText);
        Assert.False(vm.ClearManualChaptersCommand.CanExecute(null));

        // 剪貼簿空白
        vm.ClipboardReader = () => "   ";
        WaitFor(vm.PasteChaptersCommand.ExecuteAsync(null));
        Assert.Equal("剪貼簿是空的", vm.SongListMessage);

        // 不合格（沒有時間軸）
        vm.ClipboardReader = () => "今天唱了很多歌";
        WaitFor(vm.PasteChaptersCommand.ExecuteAsync(null));
        Assert.Equal("剪貼簿裡找不到 3 行以上、時間遞增的時間軸", vm.SongListMessage);
        Assert.Empty(vm.SongList);

        // 合格的三行時間軸 → 套用成功、清單重建、來源變人工
        vm.ClipboardReader = () => "0:00 ねんね\n3:00 うた\n6:00 おわり";
        WaitFor(vm.PasteChaptersCommand.ExecuteAsync(null));
        Assert.Equal("已套用 3 個章節（人工貼上），PotPlayer 進度條的章節要重開影片才會更新", vm.SongListMessage);
        PumpUntil(() => vm.SongList.Count == 3);
        Assert.Equal("來源：人工貼上", vm.SongListSourceText);
        Assert.True(vm.ClearManualChaptersCommand.CanExecute(null));

        // 清除人工章節 → 改回自動抓到的（說明欄沒有 → 回到貼上提示）
        WaitFor(vm.ClearManualChaptersCommand.ExecuteAsync(null));
        Assert.Equal("已改回自動抓到的章節", vm.SongListMessage);
        PumpUntil(() => vm.SongList.Count == 0);
        Assert.Equal("沒有找到章節，請複製セトリ後按「從剪貼簿貼上」", vm.SongListSourceText);
        Assert.False(vm.ClearManualChaptersCommand.CanExecute(null));
    }

    /// <summary>點歌曲讓 PotPlayer 跳轉：跳轉器收到該章的開始毫秒；失敗時顯示提示（訊息在 UI 執行緒更新）。</summary>
    [Fact]
    public void SeekToSong_SendsStartMs_AndShowsFailureWhenSeekerFails()
    {
        var seeker = new StubPlaybackSeeker();
        using var ctx = CreateDashboardContext(_ => { }, playbackSeeker: seeker);
        var vm = ctx.Dashboard;
        var song = new SongListItem { StartMs = 515000, EndMs = 778000, Title = "一番好きな歌" };

        // 成功：收到該章的開始毫秒，沒有失敗訊息
        vm.SeekToSongCommand.Execute(song);
        PumpUntil(() => seeker.Requests.Count == 1);
        Assert.Equal(515000, seeker.Requests[0]);
        Assert.Equal(string.Empty, vm.SongListMessage);

        // 失敗：顯示提示
        seeker.Result = false;
        vm.SeekToSongCommand.Execute(song);
        PumpUntil(() => vm.SongListMessage == "PotPlayer 沒有回應，跳轉失敗");
        Assert.Equal(2, seeker.Requests.Count);

        // 沒有跳轉器時（DI 未注入）不做事：這裡用 null 參數的 ViewModel 驗證（另建一個 context 太重，
        // 直接確認命令丟 null 參數也不做事）
        vm.SeekToSongCommand.Execute(null);
        Assert.Equal(2, seeker.Requests.Count);
    }

    /// <summary>歌曲清單的「目前播放」高亮沿用 500ms 計時器：位置在某一章範圍內 → 該列 IsPlaying；
    /// 換位置後舊列取消、新列亮起；位置超出所有章節後全部取消。</summary>
    [Fact]
    public void SongPlayingHighlight_FollowsPlaybackPosition()
    {
        RunOnSta(() =>
        {
            // 與其他情境一樣裝 Dispatcher 同步內容，查詢的接續才會留在這條 UI 執行緒
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            using var playback = new PlaybackPushHarness();
            var clock = new ScriptedPlaybackClock { Position = 200000 };
            using var ctx = CreateDashboardContext(_ => { }, playbackTracker: playback.Tracker, playbackClock: clock);
            var vm = ctx.Dashboard;
            WaitFor(ctx.Provider.GetRequiredService<ChapterOverlayState>()
                .LoadAsync("dQw4w9WgXcQ", "0:00 オープニング\n8:35 一番好きな歌\n12:58 雑談", CancellationToken.None));
            PumpUntil(() => vm.SongList.Count == 3);

            // 位置 200000 在第一章 [0,300000) 內 → 第一首高亮
            PumpUntil(() => vm.SongList[0].IsPlaying);
            Assert.False(vm.SongList[1].IsPlaying);

            // 位置 600000 在第二章 [515000,778000) 內 → 換第二首，第一首取消
            clock.Position = 600000;
            PumpUntil(() => vm.SongList[1].IsPlaying);
            Assert.False(vm.SongList[0].IsPlaying);

            // 位置 900000 超過所有章節 → 高亮清掉
            clock.Position = 900000;
            PumpUntil(() => !vm.SongList[1].IsPlaying);
            Assert.False(vm.SongList[0].IsPlaying);
            Assert.False(vm.SongList[2].IsPlaying);
        });
    }

    /// <summary>錄影「目前播放」高亮的測試內容：自己的字幕狀態與推送器（沿用 PotPlayerSubtitlePusherTests 的做法，
    /// 假的 PotPlayer 載入器一律成功才推得出去），Pushed 事件由 CaptionSentenceState 的變動經真的推送觸發。</summary>
    private sealed class PlaybackPushHarness : IDisposable
    {
        private const string VideoId = "dQw4w9WgXcQ";

        public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory("gateway-playing-");

        public CaptionSentenceState Sentences { get; } = new();

        public CaptionTrackState Captions { get; } = new();

        public CaptionLoader Loader { get; }

        public PotPlayerSubtitlePusher Pusher { get; }

        public PlaybackLineTracker Tracker { get; }

        public PlaybackPushHarness()
        {
            Loader = new CaptionLoader(Captions, new StubCaptionProvider());
            Pusher = new PotPlayerSubtitlePusher(
                Sentences, Captions, Loader, new AcceptingLoader(), () => true, Dir.FullName,
                NullLogger<PotPlayerSubtitlePusher>.Instance)
            {
                Debounce = TimeSpan.FromMilliseconds(50),
            };
            Tracker = new PlaybackLineTracker(Pusher);
        }

        /// <summary>載入影片後推送句子，等到 Pushed 真的帶著這批句子觸發（比對 tracker 內容，不是只看推送次數）。</summary>
        public void Push(params CaptionSentence[] sentences)
        {
            Loader.LoadAsync(VideoId, null).GetAwaiter().GetResult();
            var last = sentences[^1];
            var expected = CaptionSubtitleBuilder.CueText(last.Text);
            Sentences.Reset(VideoId, 1);
            Sentences.Add(VideoId, sentences);
            PumpUntil(() => Tracker.FindTextAt(last.StartMs) == expected);
        }

        public void Dispose()
        {
            Tracker.Dispose();
            Pusher.Dispose();
            try { Dir.Delete(recursive: true); } catch (IOException) { /* 寫入中的檔案留給系統清 */ }
        }

        /// <summary>一律成功的假 PotPlayer 字幕載入器（推送才會成立、Pushed 才會觸發）。</summary>
        private sealed class AcceptingLoader : IPotPlayerSubtitleLoader
        {
            public bool Load(string subtitlePath) => true;
        }
    }

    /// <summary>可控制完成時機的標題翻譯假模型：每次呼叫配一個新的閘門（Complete(index, content) 才回覆）。
    /// 故意不收 CancellationToken——模擬模型慢慢回、被換場取代後舊結果仍送達。</summary>
    private sealed class GatedTitleLlm : ILorebookLlm
    {
        private readonly object _lock = new();
        private readonly List<TaskCompletionSource<string>> _gates = [];

        /// <summary>收到的 user 訊息（驗證送出的是標題原文）。</summary>
        public List<string> UserPrompts { get; } = [];

        public int CallCount
        {
            get { lock (_lock) return _gates.Count; }
        }

        public async Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<string> gate;
            lock (_lock)
            {
                gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates.Add(gate);
            }
            UserPrompts.Add(userPrompt);
            var reply = await gate.Task.ConfigureAwait(false);
            return new LlmJsonResult(reply, 1, 1, 1, "stub-title");
        }

        public void Complete(int index, string content)
        {
            lock (_lock) _gates[index].TrySetResult(content);
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "連線成功"));
    }
}
