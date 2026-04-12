using System.Collections.ObjectModel;
using System.Windows;

namespace TranslationGateway.Logging;

public class UiLogStore
{
    public ObservableCollection<UiLogItem> Items { get; } = new();

    public void Add(string level, string category, string message)
    {
        // 封裝新增邏輯
        Action addAction = () =>
        {
            Items.Add(new UiLogItem(DateTime.Now, level, category, message));
            if (Items.Count > 500) Items.RemoveAt(0);
        };

        // 確保在 UI 執行緒執行
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                addAction();
            }
            else
            {
                dispatcher.Invoke(addAction);
            }
        }
    }

    public void Clear()
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true)
            Items.Clear();
        else
            Application.Current?.Dispatcher.Invoke(Items.Clear);
    }
}

// 支援的 Model
public record UiLogItem(DateTime Time, string Level, string Category, string Message);