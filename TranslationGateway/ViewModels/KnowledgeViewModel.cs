using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows;
using TranslationGateway.Services;

namespace TranslationGateway.ViewModels;

public partial class KnowledgeViewModel : ObservableObject
{
    // 改為唯讀欄位，等待 DI 注入
    private readonly DatabaseService _db;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _editingTargetText = string.Empty;
    [ObservableProperty] private TranslationCacheItem? _selectedCacheItem;

    public ObservableCollection<TranslationCacheItem> CacheItems { get; } = new();

    // 正確的 DI 建構子
    public KnowledgeViewModel(DatabaseService db)
    {
        _db = db;

        // 檢查是否在設計模式（Design Mode），避免在 Visual Studio 預覽時執行資料庫邏輯
        if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(new DependencyObject()))
            return;

        LoadData();
    }

    partial void OnSelectedCacheItemChanged(TranslationCacheItem? value)
    {
        EditingTargetText = value?.TargetText ?? string.Empty;
    }

    [RelayCommand]
    private void LoadData()
    {
        var items = _db.GetAllCache(); // 使用注入的服務
        CacheItems.Clear();
        foreach (var item in items) CacheItems.Add(item);
    }

    [RelayCommand]
    private void SaveCorrection()
    {
        if (SelectedCacheItem == null || string.IsNullOrWhiteSpace(EditingTargetText)) return;

        // 標記為手動校正
        _db.SaveCache(SelectedCacheItem.SourceText, EditingTargetText, isManual: true);

        LoadData();
        EditingTargetText = string.Empty;
    }
}

// 簡單的 DTO，對應資料庫欄位
public class TranslationCacheItem
{
    public string SourceText { get; set; } = string.Empty;
    public string TargetText { get; set; } = string.Empty;
    public bool IsManual { get; set; }
    public DateTime LastUsed { get; set; }
}