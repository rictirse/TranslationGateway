using TranslationGateway.Models;

namespace TranslationGateway.Interface;

public interface ISettingsService
{
    AppSettings Current { get; }
    string SettingsPath { get; }

    Task LoadAsync();
    Task SaveAsync();
}