using System.Windows;
using System.Windows.Controls;
using Translation.Gateway.ViewModels;

namespace Translation.Gateway.Views;

/// <summary>
/// Interaction logic for SettingsView.xaml
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookConfirmDialog();
        Loaded += (_, _) => HookConfirmDialog();
    }

    /// <summary>把 ViewModel 的 ConfirmDialog／InfoDialog 設成 MessageBox（預設無動作，測試可替換）。</summary>
    private void HookConfirmDialog()
    {
        if (DataContext is not SettingsViewModel vm) return;

        vm.ConfirmDialog = message =>
            MessageBox.Show(message, "設定", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        vm.InfoDialog = message =>
            MessageBox.Show(message, "開台前更換 IPv6 設定說明", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
