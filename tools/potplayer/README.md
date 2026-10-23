# PotPlayer 翻譯腳本

`SubtitleTranslate.as` 是 PotPlayer 的線上字幕翻譯腳本，會把字幕送到本程式的 `http://127.0.0.1:<port>/translate`。

## 由 Gateway 自動安裝

用 Gateway「開啟直播」時，Gateway 會自動處理，不需要手動安裝：

- 啟動時從 5000 開始找第一個可用的 port，開台時把 port 與上下文行數寫死進這個腳本，寫入 PotPlayer 的 `Extension\Subtitle\Translate\SubtitleTranslate - bing.as`。
- 同時改登錄檔（`HKCU\Software\DAUM\PotPlayerMini64\Settings`）開啟自動翻譯並選好翻譯引擎；PotPlayer 只在啟動時讀設定，還開著時 Gateway 會先詢問要不要關閉它。
- PotPlayer 的安裝目錄（例如 `C:\Program Files\DAUM\PotPlayer`）不能寫入時，Gateway 會把整個 PotPlayer 複製到 `%LOCALAPPDATA%\Translation.Gateway\PotPlayer`，改用複本啟動（複本跟原本共用同一份設定與播放記錄）。

以下手動安裝說明留給不經過 Gateway 開影片的情況使用。

## 手動安裝／還原

PotPlayer 更新時，可能會把這個腳本換回原版的 Bing 腳本，翻譯就會出錯（例如回應 `The request is not authorized because credentials are missing or invalid.`）。這時請重新複製：

1. 關閉 PotPlayer。
2. 把 `SubtitleTranslate.as` 複製到 PotPlayer 安裝目錄下的 `Extension\Subtitle\Translate\`，檔名改成 `SubtitleTranslate - bing.as`，蓋掉原本的檔案。
3. 重新開啟 PotPlayer，在字幕翻譯設定中選擇 Bing。
