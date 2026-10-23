using System.Windows;
using System.Windows.Controls;
using Translation.Gateway.ViewModels;

namespace Translation.Gateway.Views;

/// <summary>
/// 聽錯字修正審核頁：code-behind 只負責把確認對話框接上 MessageBox（View 才有的東西）。
/// </summary>
public partial class MishearingReviewView : UserControl
{
    public MishearingReviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookConfirmDialog();
        Loaded += (_, _) => HookConfirmDialog();
    }

    /// <summary>把 ViewModel 的 ConfirmDialog 設成 MessageBox（預設直接回傳 true，測試可替換）。</summary>
    private void HookConfirmDialog()
    {
        if (DataContext is not MishearingReviewViewModel vm) return;

        vm.ConfirmDialog = message =>
            MessageBox.Show(message, "聽錯字修正", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
}
