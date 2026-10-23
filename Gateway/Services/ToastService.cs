using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Translation.Gateway.Services;

/// <summary>提示框內容：標題、訊息與是否為錯誤樣式。</summary>
public sealed class ToastItem(string title, string message, bool isError = false)
{
    public string Title { get; } = title;

    public string Message { get; } = message;

    public bool IsError { get; } = isError;
}

/// <summary>
/// Gateway 視窗右下角的提示框（DI singleton）：只在主視窗內顯示，不使用任何 Windows 系統通知。
/// 同時最多 3 個，超過時移除最舊的；每則預設 10 秒後自動消失。
/// </summary>
public partial class ToastService : ObservableObject
{
    /// <summary>同時顯示的上限，超過時移除最舊的。</summary>
    private const int MaxItems = 3;

    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(10);

    public ObservableCollection<ToastItem> Items { get; } = [];

    /// <summary>顯示一則提示：預設 10 秒後自動移除（用 DispatcherTimer）；超過上限時先移除最舊的。</summary>
    public void Show(string title, string message, bool isError = false, TimeSpan? duration = null)
    {
        var item = new ToastItem(title, message, isError);
        RunOnUi(() =>
        {
            Items.Add(item);
            while (Items.Count > MaxItems)
            {
                Items.RemoveAt(0);
            }
        });

        var timer = new DispatcherTimer { Interval = duration ?? DefaultDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RunOnUi(() => Items.Remove(item));
        };
        timer.Start();
    }

    /// <summary>關閉一則提示（卡片右上角的 ×）。</summary>
    [RelayCommand]
    private void Dismiss(ToastItem? item)
    {
        if (item is not null)
        {
            RunOnUi(() => Items.Remove(item));
        }
    }

    /// <summary>操作 Items 一律在 UI 執行緒上執行（背景執行緒呼叫 Show 時移轉）。</summary>
    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
