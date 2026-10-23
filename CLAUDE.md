# TranslationGateway — 專案守則

PotPlayer 字幕即時翻譯閘道：PotPlayer 的 AngelScript 把 `{"current": "...", "context": [...]}` POST 到本機
`http://127.0.0.1:5000/translate`，Gateway 組 prompt 後透過 OpenAI SDK 呼叫本地 LLM，再以 OpenCC 轉為繁體中文回傳。

## 技術棧
- .NET 10（`net10.0`／`net10.0-windows`），C# nullable 啟用
- WPF + CommunityToolkit.Mvvm（Gateway）、ASP.NET Core Kestrel（監聽 5000）
- OpenAI SDK 2.x、Dapper + Microsoft.Data.Sqlite／Npgsql（翻譯快取、詞彙卡）、OpenCCNET（繁簡轉換）、Serilog
- 測試：xunit.v3 + Microsoft.Testing.Platform（`dotnet test --project <csproj>`）

## 專案結構（維持現狀，不重新拆分）
| 專案 | 類型 | 內容 |
|---|---|---|
| `Bridge.Core` | net10.0 類別庫 | TranslationManager、OpenAiClient、TextProcessingService、ITranslationStore（SQLite／PostgreSQL）、SettingsService、Models |
| `Bridge` | net10.0 主控台 | MCP stdio 伺服器（`translate_text` 工具） |
| `Gateway` | net10.0-windows WPF | GUI、Views／ViewModels、App 啟動、`/translate` 端點 |
| `tests/Bridge.Core.Tests` | net10.0 | Bridge.Core 單元測試（Linux／Windows 皆可跑） |
| `tests/Gateway.Wpf.Tests` | net10.0-windows | WPF 煙霧測試：載入 App.xaml 資源、切換每一頁、走 Dashboard 編輯流程（只在 Windows 跑） |

## 慣例
- 分支為 `master`（沒有 `main`）。任務 ID 區段 `T-2xxx`，派工流程依 `/opt/ai-dev/orchestrator/docs/dispatch-guide.md`。
- 命名空間沿用現有：`Translation.Bridge.Core.*`、`Translation.Gateway.*`。
- 新增相依套件、資料庫結構變更、設計取捨：先問使用者。
- 不得變更 `PotplayerRequest` 的 JSON 欄位（`current`、`context`），否則 PotPlayer 端腳本會壞。
- 程式碼註解與 UI 文字使用繁體中文。

## 效能行為（即時字幕，PotPlayer 同步等待）
- `TranslationSettings.TimeoutMs`（預設 1500）：模型逾時就回傳原文、不寫快取（Job 的 `SystemPrompt` 為 `TIMEOUT`）；0 表示不啟用。
- 模型輸出上限 = `clamp(Current 長度 × 3, 64, 256)` token。
- `ShouldSkipTranslation`：空白、無文字字元、整句同一字元重複 ≥ 8 次，直接原文回傳，不送模型。
- 快取 key 為 `Current` 原文（不含 context）。
- 譯名修正 `AppSettings.ReplacementRules`（每行 `錯誤=正確`）：快取存**替換前**譯文，只在輸出時套用一次（`ReplacementRules.Apply`），避免重複替換。
- 原文修正 `AppSettings.SourceReplacementRules`（每行 `聽錯=正確`，修正 Whisper 聽錯）：probe 判斷之後、`ShouldSkipTranslation` 之前套用到 Current 與 Context；
  之後的快取 key、Job、對話歷史一律用修正後的原文。`ReplacementRules` 會快取多份編譯結果（兩組規則交替使用）。
- 崩壞偵測 `CollapseDetector`：長句只回一兩個字視為崩壞，不寫快取、不進歷史，多輪模式下清空歷史重試一次。

## 資料庫（`AppSettings.Database`）
- `ITranslationStore` 有 `SqliteTranslationStore`（預設，`%LOCALAPPDATA%\Translation.Gateway\translation_cache.db`）與 `PostgresTranslationStore`，
  由 `TranslationStoreFactory` 依 `UsePostgres` 擇一；切換要重開程式，兩邊資料不同步。設定必須在解析 `TranslationManager` 前載入。
- 所有資料表放同一個資料庫。SQLite 用 PascalCase（`TranslationCache`、`GlossaryCards`、`GlossaryTerms`），PostgreSQL 用 snake_case（`translation_cache`…）。
  新增資料表時兩邊都要建，啟動時以 IF NOT EXISTS 建立，不刪資料。
- 建表 SQL 只在 `Bridge.Core/Sql/sqlite-schema.sql`／`postgres-schema.sql`（嵌入資源，啟動時整份執行，含欄位註解）；兩份同步維護、只能 IF NOT EXISTS 新增。
- 字幕紀錄 `SubtitleLog`：每個 TranslationJob 一列（含 Whisper 中間版本，`RevisionOfId` 指向被取代的列），Dashboard 編輯／刪除只更新或標記，不刪列。
  由 `SubtitleLogWriter` 背景佇列寫入，不得在翻譯執行緒寫資料庫。一鍵開台新增 `Streams` 並設定 `StreamSession.CurrentStreamId`。
- PostgreSQL 連線失敗時視為沒有快取、30 秒內不再連線，不得拖住即時翻譯。
- 使用者的 PostgreSQL：`192.168.5.4:5432`、資料庫 `LocalLab`、schema `youtube_history`；架構師與使用者在這裡看資料。
- 測試不得連真實 PostgreSQL；整合測試需設定 `TG_TEST_POSTGRES`（連線字串），只在自建的 `tg_test_*` schema 操作並刪除。

## 詞彙卡（`GlossaryCards`／`GlossaryTerms`）
- 一種卡涵蓋角色、團體、遊戲；只放名詞（原文=譯名）。`Status = Rejected` 不讀，Pending 目前照用（尚無審核頁）。
- 生效中的卡（`AppSettings.ActiveGlossaryCardIds`）的全部名詞寫進 system prompt（`GlossaryState.PromptSection`），
  只在生效清單變動時改變並重置＋預熱；**不得**每句依內容增減名詞（會破壞多輪的伺服器快取）。
- Dashboard 底部為詞彙卡區（記憶體搜尋 `GlossaryCatalog.Search`，平假名／片假名、全半形互通）；Prompt 詳細內容預設隱藏。
- 一鍵開台依網址 `@handle` 或頻道名稱找角色卡（`FindForStream`），換掉角色／團體卡、保留遊戲卡；角色卡生效時連同上層團體卡。

## 多輪對話模式（`UseConversationMode`，預設開）
- 實測模型（qwen3.8-flash-next，混合架構）只能「接續上一次請求」重用 KV，無法只重用共同的 system 開頭；
  因此每句字幕當一輪 user/assistant，延遲約 650ms → 100～200ms。
- system = `PromptBuilder.BuildSystemPrompt`（模板 + 直播情境 + 角色表）；user 只放 Current，不放 PotPlayer 的 context。
- `ConversationSession`：system 變更、閒置超過 90 秒、達 12 輪（保留 4 輪）時重置。歷史存**模型原始輸出**才能接續伺服器快取。
- 預熱 `WarmUpAsync`：啟動、情境／角色表變更、手動重置時在背景送一次 system，避免第一句冷啟動（約 750ms）。
- 連線測試請求（`probe: true` 或固定測試句）直接回傳，不進模型、快取、歷史。

## 字幕監看頁（Dashboard）
- 字幕表格：雙擊原文／翻譯編輯（模板欄；**不可**用單向綁定的 DataGridTextColumn，DataGrid 會把它視為唯讀）。
  編輯中鎖定，新字幕進暫存；「暫停捲動」是另一個獨立的暫存開關。改翻譯 → 手動快取；改原文 → `RetranslateAsync` 重翻，快取 key 仍是聽錯的原文。
- 右鍵選單：複製、重新翻譯、刪除、「把原文的修改加入原文修正規則」（`TextDiff.ChangedSegment` 取被改的詞，擴展到片假名／漢字／英數字的詞界）。
- 直播設定檔 `AppSettings.StreamProfiles`（存在 settings.json）：情境、角色表、兩組修正規則；套用時重置對話並預熱。
- 統計列 `TranslationStats.Compute`：近 5 分鐘句數、模型平均／P95、快取命中率、逾時數；有逾時或 P95 > 逾時 80% 時變色。
- 全域樣式在 `Gateway/App.xaml`（色票、按鈕、表格、GroupBox 卡片、側邊欄 `NavButton`）。新畫面優先沿用這些資源。
- 離開設定頁時會存檔並預熱（system prompt 可能改了）。
- 重複字幕判斷（`RevisionDetector`、`RevisionWindowSeconds` 預設 8 秒）：Whisper 滑動視窗會在幾秒內送來同一句的多個版本（實測約 31%）。
  只差標點／變短 → 沿用上一句譯文（`TranslationSource.Reused`，不呼叫模型）；延伸／改字／部分重疊 → 照常翻譯，但多輪歷史以 Replace 取代上一輪、Dashboard 以 `RevisionOf` 取代舊列。
- 一鍵開台（側邊欄「開啟直播」）：`StreamlinkLauncher` 讀剪貼簿網址 → `streamlink --json` 取標題與頻道 → 啟動 streamlink 帶起播放器；頻道名稱對上設定檔時自動套用（`StreamProfiles.FindForChannel`）。
  外部程式一律經 `IProcessRunner`（`ArgumentList`，不經 shell）；路徑在設定頁「直播播放」。

## 驗證
```bash
bash scripts/verify.sh          # build → test → format（格式檢查目前只含測試專案）
```
Windows：`/opt/ai-dev/orchestrator/worker/verify-on-windows.sh <路徑>`（Release build + test，含 `tests/Gateway.Wpf.Tests`）。
WPF 煙霧測試抓得到執行期 XAML 錯誤（找不到資源等）與編輯流程退化；外觀與滑鼠操作手感仍需要人確認。
注意：派工關卡只在容器內跑 Linux 測試，改 XAML 的任務合併前一定要跑 Windows 驗證。

## 已知問題
- README 的 AngelScript 範例仍把 `context` 當字串送出，但 `PotplayerRequest.Context` 是 `string[]`。
- 既有程式碼尚未通過 `dotnet format`（換行／縮排），尚未開啟嚴格警告設定（`TreatWarningsAsErrors` 等）。
