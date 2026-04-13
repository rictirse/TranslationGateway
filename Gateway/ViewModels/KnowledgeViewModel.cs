using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Gateway.ViewModels;

public partial class KnowledgeViewModel : ObservableObject
{
    private readonly DatabaseService _db;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _editingTargetText = string.Empty;
    [ObservableProperty] private TranslationCacheItem? _selectedCacheItem;

    public ObservableCollection<TranslationCacheItem> CacheItems { get; } = new();

    public KnowledgeViewModel(DatabaseService db)
    {
        _db = db;

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
        var items = _db.GetAllCache();
        CacheItems.Clear();
        foreach (var item in items) CacheItems.Add(item);
    }

    [RelayCommand]
    private void SaveCorrection()
    {
        if (SelectedCacheItem == null || string.IsNullOrWhiteSpace(EditingTargetText)) return;

        _db.SaveCache(SelectedCacheItem.SourceText, EditingTargetText, isManual: true);

        LoadData();
        EditingTargetText = string.Empty;
    }
}