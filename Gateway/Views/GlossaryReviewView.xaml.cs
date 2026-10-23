using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Translation.Gateway.ViewModels;

namespace Translation.Gateway.Views;

/// <summary>
/// 詞彙卡審核頁：code-behind 只負責用瀏覽器開啟來源網址（外部程式不進 ViewModel），
/// 以及把確認對話框接上 MessageBox（View 才有的東西）。
/// </summary>
public partial class GlossaryReviewView : UserControl
{
    public GlossaryReviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookConfirmDialog();
        Loaded += (_, _) => HookConfirmDialog();
    }

    /// <summary>把 ViewModel 的 ConfirmDialog 設成 MessageBox（預設直接回傳 true，測試可替換）。</summary>
    private void HookConfirmDialog()
    {
        if (DataContext is not GlossaryReviewViewModel vm) return;

        vm.ConfirmDialog = message =>
            MessageBox.Show(message, "詞彙卡", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    /// <summary>用預設瀏覽器開啟來源網址；只在 url 是 http／https 時執行。</summary>
    private void OpenSourceUrl_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GlossaryReviewViewModel vm ||
            vm.SelectedCard?.SourceUrl?.Trim() is not { Length: > 0 } url ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
