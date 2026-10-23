namespace Translation.Bridge.Core.Services;

/// <summary>
/// 偵測「長句只回一兩個字」的崩壞譯文。多輪歷史中只要出現幾輪這種壞譯文，
/// 小模型就會模仿而之後每句都只回一個字（finish_reason 仍是正常的 stop，截斷偵測不到），
/// 因此在程式端偵測並隔離：不寫入快取、不加入歷史，必要時重置歷史重試。
/// </summary>
public static class CollapseDetector
{
    /// <summary>
    /// 判斷譯文是否崩壞：只計算 <see cref="char.IsLetterOrDigit"/> 為 true 的字元數，
    /// 原文有一定長度（src &gt;= 6）而譯文幾乎沒有內容（dst &lt;= 1 或不到原文的 1/8）即視為崩壞。
    /// </summary>
    public static bool IsCollapsed(string source, string translation)
    {
        var src = source.Count(char.IsLetterOrDigit);
        var dst = translation.Count(char.IsLetterOrDigit);
        return src >= 6 && (dst <= 1 || dst * 8 < src);
    }
}
