using System.Collections.ObjectModel;
using System.Windows;

namespace Translation.Gateway.Logging;

public class UiLogStore
{
    public ObservableCollection<UiLogItem> Items { get; } = new();

    public void Add(string level, string category, string message)
    {
        Action addAction = () =>
        {
            Items.Add(new UiLogItem(DateTime.Now, level, category, message));
            if (Items.Count > 500) Items.RemoveAt(0);
        };

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

public record UiLogItem(DateTime Time, string Level, string Category, string Message);