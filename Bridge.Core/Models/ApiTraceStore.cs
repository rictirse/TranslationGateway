using System.Collections.ObjectModel;

namespace Translation.Bridge.Core.Models;

public record ApiTraceItem(DateTime Time, string Kind, string Content);

public class ApiTraceStore
{
    public readonly object LockObject = new();
    public ObservableCollection<ApiTraceItem> Items { get; } = new();

    public void Add(string kind, string content)
    {
        lock (LockObject)
        {
            var newItem = new ApiTraceItem(DateTime.Now, kind, content);

            Items.Add(newItem);

            if (Items.Count > 200)
            {
                Items.RemoveAt(0);
            }
        }
    }

    public void Clear()
    {
        lock (LockObject)
        {
            Items.Clear();
        }
    }
}