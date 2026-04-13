using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows;
using Translation.Bridge.Core.Models;

namespace Translation.Gateway.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    public ObservableCollection<TranslationJob> Jobs { get; } = new();

    [ObservableProperty] private TranslationJob? _selectedJob;

    public DashboardViewModel()
    {
        WeakReferenceMessenger.Default.Register<TranslationJob>(this, (r, job) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Jobs.Insert(0, job);

                if (Jobs.Count > 200)
                {
                    Jobs.RemoveAt(Jobs.Count - 1);
                }
            });
        });
    }
}