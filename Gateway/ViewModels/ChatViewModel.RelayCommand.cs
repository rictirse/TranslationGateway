using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;
using System.Windows;
using Translation.Core.Models;

namespace Translation.Gateway.ViewModels;

public partial class ChatViewModel
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [RelayCommand]
    private async Task SendChat()
    {
        if (string.IsNullOrWhiteSpace(ChatInput)) return;

        var preProcessText = _textProcessingService.PreProcess(ChatInput);
        try
        {
            var chatResult = await _openAiClient.SendAsync(
                preProcessText,
                SystemInput,
                CancellationToken.None
            );

            ChatOutput = _textProcessingService.PostProcess(chatResult.Content, ChatInput);
        }
        catch (Exception ex)
        {
            ChatOutput = $"[Error]: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddPrompt()
    {
        var dt = DateTime.Now;
        var prompt = new PromptHistory
        {
            ModelName = this.CurrentModel,
            SaveTime = dt,
            SystemPrompt = this.SystemInput
        };

        var filePath = Path.Combine(this._settings.Current.BaseStoragePath, "PromptHistory", this.CurrentModel, $"{dt:yyyyMMdd_HHmmss}.txt");
        var fi = new FileInfo(filePath);

        if (!(fi.Directory?.Exists ?? false))
        {
            fi.Directory!.Create();
        }

        try
        {
            var json = JsonSerializer.Serialize(prompt, _jsonOptions);
            await File.WriteAllTextAsync(fi.FullName, json);
            RefreshList();
            _logger.LogInformation("prompt saved: {SettingsPath}", fi.FullName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Prompt.");
            throw;
        }
    }

    [RelayCommand]
    private void RefreshList()
    {
        // 從 Settings 取得起始路徑
        var basePath = this._settings.Current.BaseStoragePath;
        var promptDir = Path.Combine(basePath, "PromptHistory");

        if (!Directory.Exists(promptDir))
        {
            Directory.CreateDirectory(promptDir);
            return;
        }

        PromptFiles.Clear();

        // 1. 使用 SearchOption.AllDirectories 進行遞迴搜尋
        // 2. 透過 File.GetCreationTime 排序，這樣就不依賴檔名格式
        var files = Directory.GetFiles(promptDir, "*.txt", SearchOption.AllDirectories)
                             .Select(fullPath => new
                             {
                                 FullPath = fullPath,
                                 // 取得相對於 PromptHistory 的路徑，方便在 UI 顯示層級（例如：Qwen/test.txt）
                                 RelativePath = Path.GetRelativePath(promptDir, fullPath),
                                 CreationTime = File.GetCreationTime(fullPath)
                             })
                             .OrderByDescending(x => x.CreationTime)
                             .ToArray();

        foreach (var f in files)
        {
            // 建議存 RelativePath，這樣你在 ListBox 就能看到它是屬於哪個分類(子資料夾)
            PromptFiles.Add(f.RelativePath);
        }
    }

    // 2. 刪除選定 (加上 CanExecute 判斷，沒選中時按鈕會自動變灰)
    [RelayCommand(CanExecute = nameof(CanOperateOnSelected))]
    private void DeletePrompt()
    {
        var result = MessageBox.Show($"確定要刪除 {SelectedPromptFile} 嗎？", "確認刪除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            var filePath = Path.Combine(this._settings.Current.BaseStoragePath, "PromptHistory", SelectedPromptFile);
            File.Delete(filePath);
            RefreshList();
            SelectedPromptFile = string.Empty;
            _logger.LogInformation("prompt delete: {SettingsPath}", filePath);
        }
    }

    // 3. 載入內容 (連點兩下觸發)
    [RelayCommand(CanExecute = nameof(CanOperateOnSelected))]
    private async Task LoadPrompt()
    {
        if (string.IsNullOrEmpty(SelectedPromptFile)) return;

        var filePath = Path.Combine(this._settings.Current.BaseStoragePath, "PromptHistory", SelectedPromptFile);

        if (File.Exists(filePath))
        {
            var json = await File.ReadAllTextAsync(filePath);
            var obj = JsonSerializer.Deserialize<PromptHistory>(json, _jsonOptions);
            if (obj is null) return;
            SystemInput = obj.SystemPrompt ?? string.Empty;
            CurrentModel = obj.ModelName ?? string.Empty;
        }
    }

    // 判斷按鈕是否可用的邏輯
    private bool CanOperateOnSelected => !string.IsNullOrEmpty(SelectedPromptFile);

    // 當選中項改變時，通知 Command 重新檢查可用性
    partial void OnSelectedPromptFileChanged(string value)
    {
        DeletePromptCommand.NotifyCanExecuteChanged();
        LoadPromptCommand.NotifyCanExecuteChanged();
    }
}