namespace Translation.Bridge.Core.Services;

/// <summary>待審數量可能改變時發出通知（資料探勘建卡、聽錯字探勘新增、審核頁存檔／核准／拒絕／刪除）；
/// 側邊欄收到後重新計數。執行緒安全，可以從背景執行緒呼叫。</summary>
public sealed class ReviewCountSignal
{
    private readonly object _lock = new();
    private EventHandler? _changed;

    public event EventHandler? Changed
    {
        add { lock (_lock) { _changed += value; } }
        remove { lock (_lock) { _changed -= value; } }
    }

    /// <summary>通知訂閱者待審數量可能變了（訂閱者自行決定要不要重新計數）。</summary>
    public void Notify()
    {
        EventHandler? handlers;
        lock (_lock)
        {
            handlers = _changed;
        }

        handlers?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>側邊欄顯示文字：0 或負數 → ""；1～99 → "(n)"；100 以上 → "(99+)"。</summary>
    public static string FormatBadge(int count) => count switch
    {
        <= 0 => string.Empty,
        <= 99 => $"({count})",
        _ => "(99+)",
    };
}
