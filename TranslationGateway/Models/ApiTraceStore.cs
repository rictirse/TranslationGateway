using System.Collections.ObjectModel;
using System.Windows;

namespace TranslationGateway.Models;

public record ApiTraceItem(DateTime Time, string Kind, string Content);

public class ApiTraceStore
{
    // 改為唯讀，由 DI 容器確保 Singleton
    public ObservableCollection<ApiTraceItem> Items { get; } = new();

    public void Add(string kind, string content)
    {
        // 封裝新增動作
        Action addAction = () =>
        {
            Items.Add(new ApiTraceItem(DateTime.Now, kind, content));

            // 限制筆數，避免記憶體洩漏
            if (Items.Count > 200)
            {
                Items.RemoveAt(0);
            }
        };

        // 執行緒安全處理：確保在 UI 執行緒更新集合
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
                addAction();
            else
                dispatcher.Invoke(addAction);
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