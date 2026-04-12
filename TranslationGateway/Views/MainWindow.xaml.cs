using System.Windows;
using TranslationGateway.Interface;
using TranslationGateway.ViewModels;

namespace TranslationGateway.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ISettingsService _settingsService;

    public MainWindow(
        MainViewModel viewModel,
        ISettingsService settingsService)
    {
        InitializeComponent();
        this.DataContext = viewModel;
        _settingsService = settingsService;
    }

    private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        await _settingsService.SaveAsync();
    }
}