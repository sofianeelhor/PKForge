using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>MAUI Preferences adapter for the standalone offline journal service.</summary>
public sealed class PokeparkOfflinePreferencesStore : IParkOfflineStateStore
{
    public string? Read(string key) => Preferences.Default.Get<string?>(key, null);
    public void Write(string key, string value) => Preferences.Default.Set(key, value);
}
