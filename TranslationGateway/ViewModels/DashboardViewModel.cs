using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows;
using TranslationGateway.Models;

namespace TranslationGateway.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    public ObservableCollection<TranslationJob> Jobs { get; } = new();

    [ObservableProperty] private TranslationJob? _selectedJob;

    // 透過 DI 注入，即便目前沒用到外部 Service，保留建構子彈性也是好習慣
    public DashboardViewModel()
    {
        // 訂閱來自 TranslationManager 的翻譯任務訊息
        WeakReferenceMessenger.Default.Register<TranslationJob>(this, (r, job) =>
        {
            // 使用 Application.Current.Dispatcher 確保 UI 執行緒安全
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 將新任務插入列表最上方
                Jobs.Insert(0, job);

                // 效能優化：限制顯示筆數為 200 筆
                if (Jobs.Count > 200)
                {
                    Jobs.RemoveAt(Jobs.Count - 1);
                }
            });
        });
    }
}