namespace Translation.Bridge.Core.Tests;

/// <summary>TranslationManagerTests 與 VodPrefetcherTests 都會透過真的 TranslationManager
/// 經靜態共用的 WeakReferenceMessenger.Default 送出 TranslationJob 訊息；
/// 兩類平行執行時，一邊送的訊息會被另一邊還註冊中的測試收走（jobs 數量偶爾多一筆而斷言失敗）。
/// 綁進同一個 collection 讓這兩類彼此循序執行（其他類不受影響，照常平行）。</summary>
[CollectionDefinition("translation-messenger")]
public class TranslationMessengerCollection;
