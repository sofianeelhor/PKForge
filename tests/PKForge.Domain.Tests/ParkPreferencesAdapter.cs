namespace PKForge.App.Services;

/// <summary>Test-only replacement for Maui's platform preference storage.</summary>
internal sealed class Preferences
{
    public static Preferences Default { get; } = new();
    private readonly Dictionary<string, string> _values = [];
    public string Get(string key, string defaultValue) => _values.GetValueOrDefault(key, defaultValue);
    public void Set(string key, string value) => _values[key] = value;
    public void Clear() => _values.Clear();
}
