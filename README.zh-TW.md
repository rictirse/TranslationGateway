# Translation Gateway

[English](README.md) | 繁體中文

用你自己的 LLM，替 PotPlayer 做日文 → 繁體中文的即時字幕翻譯。

PotPlayer 用語音辨識（Whisper）或讀取 YouTube 字幕，把每一句送到 Translation Gateway。Gateway 是 Windows 桌面程式：它組好 prompt，呼叫任何 OpenAI 相容的 LLM 端點，用 OpenCC 轉成繁體中文後寫入快取，再把譯文交回 PotPlayer 顯示成字幕。

## 功能

- **一鍵開片**：複製 YouTube／Twitch 網址，按「開啟影片／直播」即可。
  - 直播經由 streamlink 播放。
  - YouTube 錄影直接用 PotPlayer 開啟。
  - PotPlayer 的翻譯腳本與設定會自動安裝。
- **低延遲直播翻譯**：多輪對話模式會重用模型的 KV 快取，每句約 100～200 ms。
  - 模型太慢時直接回傳原文，字幕不會卡住。
- **錄影預先翻譯**：有自動字幕的 YouTube 影片，會在播放前分批翻好。
- **詞彙卡**：角色、團體、遊戲的專有名詞譯名。
  - 開台時自動套用對應的卡。
  - 知識庫模型可以從 wiki 或貼上的資料建卡。
- **聽錯字修正**：自動找出 Whisper 常聽錯的詞，你核准後就會在翻譯前修正。
- **字幕監看**：即時檢視、編輯、重翻字幕。每位主播可以存一份設定檔，內容包括情境、角色表和修正規則。
- **資料庫**：SQLite（預設）或 PostgreSQL，存翻譯快取、詞彙卡、字幕紀錄，以及選用的完整回溯紀錄。
- **YouTube 被擋處理**：匿名存取被擋時會提示你。也可以設定 MikroTik 路由器：被擋時換一組 IPv6 前綴，再自動重試一次。

## 系統需求

| 項目 | 說明 |
| --- | --- |
| Windows 10/11 x64 | Gateway 是 WPF 程式 |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | 從原始碼建置時需要（只執行發布版本的話，裝 Desktop Runtime 即可） |
| [PotPlayer](https://potplayer.tv/) 64 位元 | 播放器與語音辨識 |
| [streamlink](https://streamlink.github.io/) | 只有看直播需要 |
| OpenAI 相容的 LLM 端點 | 例如 llama.cpp server、vLLM、Ollama、LM Studio（`/v1/chat/completions`） |
| 選用：第二個 LLM 端點 | 知識庫模型，用於詞彙卡探勘與聽錯字修正（適合用雲端模型） |
| 選用：PostgreSQL | 取代本機的 SQLite 檔 |

## 建置與執行

```powershell
git clone <this repo>
cd TranslationGateway
dotnet publish Gateway/Gateway.csproj -c Release -o publish
.\publish\Gateway.exe
```

Gateway 啟動後會監聽 `http://127.0.0.1:5000/translate`。5000 被佔用時，會改用下一個可用的 port。

## 快速開始

1. **設定 →「翻譯模型」**：填入 API Host（例如 `http://localhost:8080/v1`）和模型名稱；伺服器需要的話也填 API Key。填好後按「測試連線」。
2. **設定 →「播放器」**：確認 `streamlink.exe` 與 PotPlayer 的路徑。
3. 複製 YouTube 或 Twitch 網址，按側邊欄的「開啟影片／直播」。
   - 第一次開片時，Gateway 會把翻譯腳本寫進 PotPlayer，並開啟 PotPlayer 的即時翻譯。
   - PotPlayer 只在啟動時讀取設定，所以它還開著時，Gateway 會先問你要不要關掉它。
   - 如果 PotPlayer 的安裝目錄不能寫入，Gateway 會改用 `%LOCALAPPDATA%\Translation.Gateway\PotPlayer` 的複本。複本和原本的 PotPlayer 共用設定與播放紀錄。
4. 譯文會出現在 PotPlayer 的字幕上，「字幕監看」頁也看得到。

> 離開設定頁時會自動存檔，並重新預熱模型（因為 prompt 可能改了）。

## 頁面說明

### 字幕監看
- **字幕表格**：雙擊原文或譯文就能編輯。
  - 改譯文：存進快取。
  - 改原文：重新翻譯。
  - 右鍵選單可以複製、重新翻譯、刪除，或把改過的原文加入原文修正規則。
- **直播資訊與設定檔**：填寫情境（誰在直播、正在做什麼）和臨時角色表，連同兩組修正規則，依主播存成設定檔。按「套用」載入設定檔並重置對話。
- **詞彙卡**：搜尋卡片並切換是否生效。生效中的卡，所有名詞都會寫進 system prompt。
- **修正規則**：每行一條。
  - 原文修正（`聽錯=正確`）在翻譯前套用。
  - 譯名修正（`錯誤=正確`）在翻譯後套用。
- **重置對話**：翻譯開始走樣，或拖動進度條後可以按。
- **統計列**：顯示近 5 分鐘的句數、模型平均與 P95 延遲、快取命中率、逾時數。

### 知識庫
- 審核知識庫模型整理的詞彙卡：核准、拒絕、編輯、合併或刪除。
- **貼上資料整理…**：貼上角色列表、譯名對照表等資料，知識庫模型會整理成名詞加進卡片。
- 被拒絕的卡與名詞不會進 prompt。

### 聽錯字修正
- 直播時，Gateway 每 5 分鐘從最近的字幕找出 Whisper 聽錯的名詞。
- 核准的修正會在翻譯前套用到原文；駁回會直接刪除。

### 設定
| 分頁 | 內容 |
| --- | --- |
| 翻譯模型 | 端點、模型、取樣參數，以及直播 system prompt 與版本紀錄 |
| 知識庫模型 | 探勘與修正用的另一個端點（選用；連續失敗時暫時改用翻譯模型） |
| 直播 | 逾時（預設 1500 ms，超過就回傳原文）、多輪對話、歷史上限、重複字幕判斷、聽錯字探勘 |
| 錄影 | 預先翻譯的每批句數、庫存秒數、逾時與排程 |
| 資料庫 | SQLite 或 PostgreSQL、完整回溯紀錄與保留天數 |
| 播放器 | streamlink 與 PotPlayer 路徑、畫質、選用的 MikroTik 路由器 |

### 系統記錄
程式 log，同時寫入 `%LOCALAPPDATA%\Translation.Gateway\logs`。

## 被 YouTube 擋住時

匿名存取 YouTube 太頻繁時，IP 可能會被擋。被擋的徵兆包括：HTTP 429、被導到「異常流量」頁面，或要求「登入以確認你不是機器人」。

Gateway 偵測到被擋時，不會開啟播放器：

- **沒設定路由器**：提示你可能是匿名存取被擋。
- **有設定路由器**：只呼叫和這台電腦同網段的那台路由器換 IPv6 前綴，然後自動重試一次。

### 選用：MikroTik 路由器設定

1. 在路由器建立名為 `ipv6-release` 的腳本，內容是 release 並重新取得 DHCPv6 client。
2. 開啟路由器的 `www-ssl` 服務。
3. 建立一個帳號，它的群組要有 `read,write,test,api,rest-api` 權限。少了 `api` 時，REST API 會回 "not allowed"。
4. 到 **設定 →「播放器」**，按「新增」，每台路由器填一列：`位址:port`、帳號、密碼。填好後按「測試連線」。

完整的 RouterOS 指令，請按該區塊的「設定說明」查看。

## 資料位置

| 內容 | 位置 |
| --- | --- |
| 設定 | Gateway 工作目錄下的 `settings.json`（一般啟動時就在 `Gateway.exe` 旁邊） |
| SQLite 資料庫、log、PotPlayer 複本 | `%LOCALAPPDATA%\Translation.Gateway\` |

> `settings.json` 以**明碼**存放 API Key 與路由器密碼，請勿分享。

## 手動搭配 PotPlayer

透過 Gateway 開片就不需要這段。不經過 Gateway 時，請參考 [tools/potplayer/README.md](tools/potplayer/README.md)。請求格式如下：

```json
POST /translate
{ "current": "今の字幕", "context": ["前の字幕", "..."] }
```

## MCP 伺服器（選用）

`Bridge` 專案是 stdio MCP 伺服器，提供 `translate_text` 工具，翻譯流程和 Gateway 相同：

```powershell
dotnet run --project Bridge/Bridge.csproj
```

## 開發

| 專案 | 內容 |
| --- | --- |
| `Bridge.Core` | 翻譯流程、LLM 用戶端、資料庫（SQLite／PostgreSQL）、設定 |
| `Gateway` | WPF 程式與 `/translate` HTTP 端點 |
| `Bridge` | MCP stdio 伺服器 |
| `tests/Bridge.Core.Tests` | 單元測試（Windows／Linux 皆可） |
| `tests/Gateway.Wpf.Tests` | WPF 煙霧測試（只在 Windows） |

```powershell
dotnet build Translation.slnx
dotnet test --project tests/Bridge.Core.Tests/Bridge.Core.Tests.csproj
```

PostgreSQL 整合測試預設略過，設定環境變數 `TG_TEST_POSTGRES`（連線字串）後才會執行。測試只會建立並刪除自己的 `tg_test_*` schema。

## 疑難排解

- **PotPlayer 顯示 "credentials are missing or invalid"**：PotPlayer 更新時把腳本換回了原版 Bing。重新經由 Gateway 開片，或依 [tools/potplayer/README.md](tools/potplayer/README.md) 手動安裝。
- **字幕回傳原文沒翻譯**：模型超過直播逾時，請看統計列的延遲。
  - 常見原因是 system prompt 太長，或共用的 GPU 太忙。
  - 多輪對話與預熱都依賴伺服器的 prompt 快取。
- **完全沒有字幕**：確認 Gateway 有在執行、「系統記錄」頁看得到收到請求，以及 PotPlayer 的即時翻譯已開啟。

## 授權

[MIT](LICENSE)
