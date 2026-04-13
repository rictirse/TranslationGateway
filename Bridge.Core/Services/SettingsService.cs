using Microsoft.Extensions.Logging;
using System.Text.Json;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

public class SettingsService : ISettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Current { get; private set; } = new();

    public string SettingsPath { get; }

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;

        SettingsPath = "settings.json";
    }

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                await SaveAsync();
                _logger.LogInformation("Settings created: {SettingsPath}", SettingsPath);
                return;
            }

            var json = await File.ReadAllTextAsync(SettingsPath);
            var obj = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
            if (obj is not null) Current = obj;

            _logger.LogInformation("Settings loaded: host={Host}, model={Model}", Current.ApiHost, Current.Model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings; using defaults.");
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, _jsonOptions);
            await File.WriteAllTextAsync(SettingsPath, json);
            _logger.LogInformation("Settings saved: {SettingsPath}", SettingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings.");
            throw;
        }
    }
}