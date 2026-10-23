using System.Windows;
using Translation.Bridge.Core.Interface;
using Translation.Gateway.ViewModels;

namespace Translation.Gateway.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ISettingsService _settingsService;
    private readonly GlossaryReviewViewModel _glossaryReview;

    public MainWindow(
        MainViewModel viewModel,
        ISettingsService settingsService,
        GlossaryReviewViewModel glossaryReview)
    {
        InitializeComponent();
        this.DataContext = viewModel;
        _settingsService = settingsService;
        _glossaryReview = glossaryReview;
    }

    private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 詞彙卡審核頁有未儲存的變更時先問使用者（MessageBox 只出現在 View 層）
        if (_glossaryReview.HasUnsavedChanges)
        {
            var result = MessageBox.Show(this,
                $"詞彙卡有 {_glossaryReview.DirtyCount} 筆變更尚未儲存，要儲存嗎？",
                "詞彙卡", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (result == MessageBoxResult.Yes)
            {
                _glossaryReview.SaveCommand.Execute(null);
            }
        }

        await _settingsService.SaveAsync();
    }
}
