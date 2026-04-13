using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Interface;

public interface ISettingsService
{
    AppSettings Current { get; }
    string SettingsPath { get; }

    Task LoadAsync();
    Task SaveAsync();
}