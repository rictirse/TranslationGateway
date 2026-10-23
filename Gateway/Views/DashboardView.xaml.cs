using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Translation.Bridge.Core.Models;
using Translation.Gateway.ViewModels;

namespace Translation.Gateway.Views;

/// <summary>
/// Interaction logic for DashboardView.xaml
/// </summary>
public partial class DashboardView : UserControl
{
    private DashboardViewModel? ViewModel => DataContext as DashboardViewModel;

    public DashboardView()
    {
        InitializeComponent();

        JobsGrid.BeginningEdit += JobsGrid_BeginningEdit;
        JobsGrid.CellEditEnding += JobsGrid_CellEditEnding;
        JobsGrid.PreviewMouseRightButtonDown += JobsGrid_PreviewMouseRightButtonDown;

        // 編輯中切換頁面時 CellEditEnding 不一定會觸發：離開頁面一律解鎖，避免新字幕一直卡在暫存
        Unloaded += (_, _) =>
        {
            if (ViewModel is { IsLocked: true, IsRetranslating: false } vm)
            {
                vm.Unlock();
            }
        };
    }

    /// <summary>單擊僅選取，雙擊才進入編輯；進入編輯前鎖定列表，重翻中則不開放編輯</summary>
    private void JobsGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            e.Cancel = true;
            return;
        }

        if (e.EditingEventArgs is MouseButtonEventArgs { ClickCount: < 2 })
        {
            e.Cancel = true;
            return;
        }

        if (vm.IsRetranslating)
        {
            e.Cancel = true;
            return;
        }

        vm.Lock();
    }

    /// <summary>編輯結束：依欄位把新文字交給 ViewModel（原文欄走重翻、翻譯欄走校正），Esc 一律解鎖</summary>
    private void JobsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (e.EditAction != DataGridEditAction.Commit)
        {
            vm.Unlock();
            return;
        }

        if (e.Row.Item is not TranslationJob job)
        {
            vm.Unlock();
            return;
        }

        var text = FindTextBox(e.EditingElement)?.Text ?? string.Empty;

        switch (e.Column.SortMemberPath)
        {
            case "Current":
                _ = vm.CommitSourceEditAsync(job, text);
                break;
            case "TranslatedText":
                vm.CommitTranslationEdit(job, text);
                break;
            default:
                vm.Unlock();
                break;
        }
    }

    /// <summary>右鍵先選取游標下的那一行，右鍵選單的操作才會作用在這一行（編輯中不切換，保留輸入框自己的右鍵選單）</summary>
    private void JobsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is { IsLocked: true })
        {
            return;
        }

        var node = e.OriginalSource as System.Windows.DependencyObject;
        while (node is not null and not DataGridRow)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : System.Windows.LogicalTreeHelper.GetParent(node);
        }

        if (node is DataGridRow row)
        {
            row.IsSelected = true;
        }
    }

    /// <summary>模板欄進入編輯時，游標直接放進輸入框並全選</summary>
    private void EditBox_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    /// <summary>模板欄的編輯元素是 ContentPresenter，往下找出裡面的 TextBox</summary>
    private static TextBox? FindTextBox(System.Windows.DependencyObject root)
    {
        if (root is TextBox box)
        {
            return box;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindTextBox(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
