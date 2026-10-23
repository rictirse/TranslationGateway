-- TranslationGateway 資料庫結構（SQLite）
-- 程式啟動時整份執行（嵌入資源）；只能用 IF NOT EXISTS 新增，不得刪除或改動既有資料。
-- PostgreSQL 版本見 postgres-schema.sql，兩份必須同步維護（表名 PascalCase ↔ snake_case）。
-- 時間一律存 UTC，格式 yyyy-MM-dd HH:mm:ss。

-- 翻譯快取：同一句原文只翻一次
CREATE TABLE IF NOT EXISTS TranslationCache (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SourceText TEXT UNIQUE,              -- 快取索引：PotPlayer 送來的原文（原文修正之後）
    TargetText TEXT,                     -- 譯文（譯名修正之前；輸出時才套用譯名修正）
    IsManual INTEGER DEFAULT 0,          -- 1 = 使用者手動修正過，不會被自動覆寫或刪除
    LastUsed DATETIME                    -- 最後寫入或命中的時間
);

-- 詞彙卡：角色、團體、遊戲共用同一種卡
CREATE TABLE IF NOT EXISTS GlossaryCards (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Kind TEXT NOT NULL,                  -- Character（角色）| Group（團體）| Game（遊戲）
    Name TEXT NOT NULL UNIQUE,           -- 卡名：日文原名、團體名或遊戲名
    ParentId INTEGER REFERENCES GlossaryCards(Id) ON DELETE SET NULL,  -- 上層卡（角色所屬團體）；角色卡生效時一起生效
    ChannelId TEXT,                      -- YouTube 頻道 ID（UC 開頭）
    ChannelHandle TEXT,                  -- YouTube @handle；一鍵開台時用來自動套用角色卡
    Aliases TEXT,                        -- 別名：簡稱與其他寫法（例如 APEX、エペ），每行一個；開台時用來比對直播標題
    Description TEXT,                    -- 說明；遊戲卡的說明是簡介（最多 100 字），生效時會放進 system prompt，其他種類只給人看
    SourceUrl TEXT,                      -- 資料來源網址
    Status TEXT NOT NULL DEFAULT 'Pending',   -- Pending（待審核）| Approved（已核准）| Rejected（已拒絕，不使用）
    CreatedAt DATETIME NOT NULL DEFAULT (datetime('now')),
    UpdatedAt DATETIME
);

-- 詞彙卡的名詞：生效中的卡會以「Source=Target」寫進 system prompt
CREATE TABLE IF NOT EXISTS GlossaryTerms (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CardId INTEGER NOT NULL REFERENCES GlossaryCards(Id) ON DELETE CASCADE,  -- 所屬詞彙卡
    Source TEXT NOT NULL,                -- 字幕裡會出現的日文寫法
    Target TEXT NOT NULL,                -- 中文譯名
    Aliases TEXT,                        -- 其他寫法（英文、簡稱），每行一個；供搜尋用
    Category TEXT,                       -- 人名、暱稱、粉絲名、團體、特務、技能、武器、地圖、英雄、術語
    Note TEXT,                           -- 備註：資料來源、譯名是官方或建議
    Status TEXT NOT NULL DEFAULT 'Pending',   -- Pending | Approved | Rejected（Rejected 不使用）
    CreatedAt DATETIME NOT NULL DEFAULT (datetime('now')),
    UpdatedAt DATETIME,
    UNIQUE(CardId, Source)
);
CREATE INDEX IF NOT EXISTS IX_GlossaryTerms_CardId ON GlossaryTerms(CardId);
CREATE INDEX IF NOT EXISTS IX_GlossaryCards_ChannelHandle ON GlossaryCards(ChannelHandle);

-- 直播：每次一鍵開台成功新增一筆
CREATE TABLE IF NOT EXISTS Streams (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Platform TEXT NOT NULL,              -- YouTube | Twitch
    Url TEXT NOT NULL,                   -- 開台時剪貼簿裡的直播網址
    ChannelName TEXT,                    -- 頻道名稱（streamlink 的 author）
    Title TEXT,                          -- 直播標題
    Category TEXT,                       -- 遊戲分類（Twitch 才有）
    CharacterCardId INTEGER REFERENCES GlossaryCards(Id) ON DELETE SET NULL,  -- 開台時自動套用的主播角色卡
    CreatedAt DATETIME NOT NULL DEFAULT (datetime('now'))   -- 開台時間
);

-- 字幕紀錄：每一句都寫入、永不刪除，包含 Whisper 的中間版本
CREATE TABLE IF NOT EXISTS SubtitleLog (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StreamId INTEGER REFERENCES Streams(Id) ON DELETE SET NULL,  -- 所屬直播；不是用一鍵開台時為空
    JobId TEXT NOT NULL,                 -- 程式內部的句子編號（TranslationJob.Id，8 碼）；Dashboard 編輯、刪除時用它找到這一列
    RawSource TEXT NOT NULL,             -- Whisper 原始文字（原文修正之前）
    Source TEXT NOT NULL,                -- 實際送去翻譯的原文（原文修正之後；手動改原文後為修改後的內容）
    Translation TEXT NOT NULL,           -- 最後顯示的譯文（譯名修正之後；手動修正後為修正後的內容）
    TranslationSource TEXT NOT NULL,     -- 譯文來源：Model（模型）| Cache（快取）| Reused（沿用上一句）| Timeout（逾時，回原文）| ThroughPass（直通）
    RevisionOfId INTEGER REFERENCES SubtitleLog(Id),  -- 這句取代了哪一句（Whisper 修正版）；被別句的 RevisionOfId 指到的列就是中間版本
    ModelLatencyMs INTEGER,              -- 模型耗時（毫秒）；沒有呼叫模型時為 0
    IsEdited INTEGER NOT NULL DEFAULT 0, -- 1 = 使用者在 Dashboard 手動改過原文或譯文
    IsDeleted INTEGER NOT NULL DEFAULT 0,-- 1 = 使用者在 Dashboard 刪除；只做標記，資料保留
    CreatedAt DATETIME NOT NULL DEFAULT (datetime('now')),
    UpdatedAt DATETIME                   -- 最後一次編輯或刪除的時間
);
CREATE INDEX IF NOT EXISTS IX_SubtitleLog_StreamId ON SubtitleLog(StreamId);
CREATE INDEX IF NOT EXISTS IX_SubtitleLog_JobId ON SubtitleLog(JobId);
CREATE INDEX IF NOT EXISTS IX_SubtitleLog_RevisionOfId ON SubtitleLog(RevisionOfId);
CREATE INDEX IF NOT EXISTS IX_SubtitleLog_CreatedAt ON SubtitleLog(CreatedAt);

-- 直播生效過的詞彙卡：開台自動套用、直播中手動加入或移除的每一張卡都記一筆（只增不刪）
CREATE TABLE IF NOT EXISTS StreamCards (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StreamId INTEGER NOT NULL REFERENCES Streams(Id),       -- 所屬直播
    CardId INTEGER NOT NULL REFERENCES GlossaryCards(Id),   -- 生效的詞彙卡
    Reason TEXT NOT NULL,                -- Host（主播：handle 或頻道名稱比對到角色卡）| HostGroup（主播沒有角色卡，以團體卡的人名比對）| Title（直播標題比對到的連動成員）| Parent（上層卡跟著生效）| Carried（開台前就生效、沿用下來的卡，例如遊戲卡）| Manual（直播中手動加入）| Dynamic（字幕中最近被提到而動態生效）| Situation（情境存檔時比對到的卡）| Mining（資料探勘建立或比對到的卡）
    ActivatedAt DATETIME NOT NULL DEFAULT (datetime('now')),  -- 生效時間
    DeactivatedAt DATETIME               -- 直播中被移除的時間；一直生效到直播結束時為空
);
CREATE INDEX IF NOT EXISTS IX_StreamCards_StreamId ON StreamCards(StreamId);
CREATE INDEX IF NOT EXISTS IX_StreamCards_CardId ON StreamCards(CardId);

-- 名詞候選：資料探勘拆出來、但沒有建卡的專有名詞（重要性排在 5 名以後，或是活動、道具、地名等），之後給前／後處理參考；不進 prompt
CREATE TABLE IF NOT EXISTS TermCandidates (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Surface TEXT NOT NULL,               -- 出現的寫法（原文）
    Kind TEXT NOT NULL,                  -- person | group | game | event | item | place | other（AI 判斷的種類）
    Importance INTEGER NOT NULL DEFAULT 0,  -- AI 給的重要性 1～5（5 最重要）
    Origin TEXT NOT NULL,                -- Situation（情境探勘）| Subtitle（字幕探勘，之後才有）
    StreamId INTEGER REFERENCES Streams(Id),  -- 最後一次出現的直播；沒有開台時為空
    Context TEXT,                        -- 出現的上下文（最多 200 字）
    Occurrences INTEGER NOT NULL DEFAULT 1, -- 被探勘到的次數
    Status TEXT NOT NULL DEFAULT 'New',  -- New（新）| Used（已建卡或已轉成修正規則）| Ignored（忽略）
    CreatedAt DATETIME NOT NULL DEFAULT (datetime('now')),
    LastSeenAt DATETIME NOT NULL DEFAULT (datetime('now')),
    UNIQUE(Surface, Kind)
);
CREATE INDEX IF NOT EXISTS IX_TermCandidates_Status ON TermCandidates(Status);

-- 聽錯字修正：Whisper 把詞彙卡名詞聽錯的寫法 → 正確寫法；Approved 的才套用（前處理：修正原文再翻譯）
CREATE TABLE IF NOT EXISTS MishearingFixes (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,  -- 流水號
    CardId INTEGER NOT NULL REFERENCES GlossaryCards(Id) ON DELETE CASCADE,  -- 所屬詞彙卡
    TermId INTEGER NOT NULL REFERENCES GlossaryTerms(Id) ON DELETE CASCADE,  -- 對應的名詞（一個名詞可有多個錯字）
    Kind TEXT NOT NULL DEFAULT 'Pre',    -- Pre（前處理：修正原文）| Post（後處理：修正譯文，之後才有）
    Wrong TEXT NOT NULL,                 -- 聽錯的寫法（至少 2 個字）
    Right TEXT NOT NULL,                 -- 正確寫法（通常等於名詞的 Source）
    Status TEXT NOT NULL DEFAULT 'Pending',  -- Pending（待審核）| Approved（已核准，會套用）；駁回直接刪除
    Evidence TEXT,                       -- 證據：出現錯字的字幕原句（最多 200 字）
    Occurrences INTEGER NOT NULL DEFAULT 1,  -- 被探勘到的次數
    CreatedAt DATETIME NOT NULL DEFAULT (datetime('now')),  -- 建立時間
    UpdatedAt DATETIME,                  -- 最後一次累加或審核的時間
    UNIQUE(TermId, Kind, Wrong)
);
CREATE INDEX IF NOT EXISTS IX_MishearingFixes_Status ON MishearingFixes(Status);

-- YouTube 生成式字幕快取：每支影片每種語言一筆；同一支影片再開時直接載入，不再下載。查不到字幕的影片不寫入
CREATE TABLE IF NOT EXISTS YoutubeCaptions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    VideoId TEXT NOT NULL,               -- YouTube 影片 ID（11 碼）
    Language TEXT NOT NULL,              -- 字幕語言代碼，例如 ja
    IsAuto INTEGER NOT NULL,             -- 1 是 YouTube 自動產生（asr），0 是人工上傳
    ClientName TEXT,                     -- 下載時使用的用戶端身分（VISIONOS、IOS）
    CueCount INTEGER NOT NULL,           -- 字幕段數
    CuesJson TEXT NOT NULL,              -- 字幕內容 JSON 陣列 [{"s":開始毫秒,"d":長度毫秒,"t":"文字"}]，依開始時間排序
    CreatedAt DATETIME NOT NULL DEFAULT (datetime('now')),   -- 寫入時間，之後依保存期限清除時的依據
    LastUsedAt DATETIME NOT NULL DEFAULT (datetime('now')),  -- 最後一次從快取載入的時間
    UNIQUE(VideoId, Language)
);
CREATE INDEX IF NOT EXISTS IX_YoutubeCaptions_LastUsedAt ON YoutubeCaptions(LastUsedAt);

-- 生成式字幕經 AI 重新斷句、校正後的句子（前處理結果）；讀寫程式之後的卡片處理
CREATE TABLE IF NOT EXISTS CaptionSentences (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CaptionId INTEGER NOT NULL REFERENCES YoutubeCaptions(Id) ON DELETE CASCADE,  -- 所屬字幕
    StartMs INTEGER NOT NULL,            -- 句子在影片中的開始毫秒
    EndMs INTEGER NOT NULL,              -- 句子在影片中的結束毫秒
    RawText TEXT NOT NULL,               -- 對應的原始字幕文字
    Text TEXT NOT NULL,                  -- 前處理後的日文
    Model TEXT,                          -- 前處理使用的模型
    CreatedAt DATETIME NOT NULL DEFAULT (datetime('now')),  -- 寫入時間
    UNIQUE(CaptionId, StartMs)
);

-- prompt 版本（模板）：同一種 kind 的每次修改各一列，(kind, 內容雜湊) 去重；永不刪除
CREATE TABLE IF NOT EXISTS PromptTemplates (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Kind TEXT NOT NULL,                  -- 種類：LiveSystem（直播 system prompt）| VodBatch（錄影批次）| MiningExtract（探勘拆解）| MiningCompose（探勘整理）| Mishearing（聽錯字）| TermDiscovery（遊戲用語）| TermImport（貼上整理）| CaptionPreprocess（字幕前處理）
    Content TEXT NOT NULL,               -- 模板全文
    ContentHash TEXT NOT NULL,           -- 內容的 SHA-256（AuditHash.Of，小寫 hex 64 字）
    Origin TEXT NOT NULL,                -- 來源：Default（內建預設）| User（使用者修改）
    AppVersion TEXT,                     -- 第一次寫入時的程式版本
    CreatedAt DATETIME NOT NULL DEFAULT (datetime('now')),
    UNIQUE(Kind, ContentHash)
);

-- prompt 快照：組好的 system prompt 全文，內容雜湊去重；過期時一併清除
CREATE TABLE IF NOT EXISTS PromptSnapshots (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    TemplateId INTEGER REFERENCES PromptTemplates(Id) ON DELETE SET NULL,  -- 來源的 prompt 版本；查不到時為空（去重只看內容，不參與比對）
    Content TEXT NOT NULL,               -- 組好的 system prompt 全文
    ContentHash TEXT NOT NULL UNIQUE,    -- 內容的 SHA-256
    CreatedAt DATETIME NOT NULL DEFAULT (datetime('now')),
    LastUsedAt DATETIME NOT NULL DEFAULT (datetime('now'))   -- 最後一次被（Ensure 命中）使用的時間，過期清除的依據
);
CREATE INDEX IF NOT EXISTS IX_PromptSnapshots_LastUsedAt ON PromptSnapshots(LastUsedAt);

-- 設定快照：翻譯設定的 JSON 全文，內容雜湊去重；過期時一併清除
CREATE TABLE IF NOT EXISTS SettingsSnapshots (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Content TEXT NOT NULL,               -- 翻譯設定的 JSON 全文
    ContentHash TEXT NOT NULL UNIQUE,    -- 內容的 SHA-256
    CreatedAt DATETIME NOT NULL DEFAULT (datetime('now')),
    LastUsedAt DATETIME NOT NULL DEFAULT (datetime('now'))   -- 最後一次被（Ensure 命中）使用的時間，過期清除的依據
);
CREATE INDEX IF NOT EXISTS IX_SettingsSnapshots_LastUsedAt ON SettingsSnapshots(LastUsedAt);

-- 模型呼叫紀錄：每次呼叫模型一列（回溯分析用）；過期時一併清除
CREATE TABLE IF NOT EXISTS LlmCalls (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StreamId INTEGER REFERENCES Streams(Id) ON DELETE SET NULL,  -- 所屬直播或影片；直播和錄影用 Streams.IsVod 區分
    JobId TEXT,                          -- 程式內部的句子編號（TranslationJob.Id），對到 SubtitleLog.JobId
    Purpose TEXT NOT NULL,               -- 用途：LiveTranslation（即時翻譯）| Retranslate（重翻）| VodBatch（錄影批次）| CaptionPreprocess（字幕前處理）| Mining（資料探勘）| Mishearing（聽錯字探勘）| TermDiscovery（遊戲用語探勘）| TermImport（貼上整理）| Other（其他）
    TemplateId INTEGER REFERENCES PromptTemplates(Id) ON DELETE SET NULL,       -- 這次用的 prompt 版本
    SystemSnapshotId INTEGER REFERENCES PromptSnapshots(Id) ON DELETE SET NULL, -- 這次組好的 system prompt 快照
    SettingsSnapshotId INTEGER REFERENCES SettingsSnapshots(Id) ON DELETE SET NULL, -- 這次的翻譯設定快照
    Model TEXT,                          -- 模型名稱
    UserPrompt TEXT,                     -- 送出的 user prompt 全文；「保存完整紀錄」關閉時為空
    History TEXT,                        -- 對話歷史 JSON；「保存完整紀錄」關閉時為空
    ResponseRaw TEXT,                    -- 模型原始回覆全文；「保存完整紀錄」關閉時為空
    KeywordTermIds TEXT,                 -- 命中的關鍵詞名詞 Id（JSON 陣列）；「保存完整紀錄」關閉時為空
    FixIds TEXT,                         -- 套用的聽錯字修正 Id（JSON 陣列）；「保存完整紀錄」關閉時為空
    PromptTokens INTEGER,                -- prompt token 數
    CompletionTokens INTEGER,            -- completion token 數
    LatencyMs INTEGER,                   -- 呼叫耗時（毫秒）
    Outcome TEXT NOT NULL,               -- 結果：Ok（成功）| Timeout（逾時）| Error（錯誤）
    CreatedAt DATETIME NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS IX_LlmCalls_CreatedAt ON LlmCalls(CreatedAt);
CREATE INDEX IF NOT EXISTS IX_LlmCalls_StreamId ON LlmCalls(StreamId);
CREATE INDEX IF NOT EXISTS IX_LlmCalls_JobId ON LlmCalls(JobId);
