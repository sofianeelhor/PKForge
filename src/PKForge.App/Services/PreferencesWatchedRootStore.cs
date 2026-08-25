using System.Text.Json;
using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>Persists granted emulator roots in MAUI preferences as a JSON list.</summary>
public sealed class PreferencesWatchedRootStore : IWatchedRootStore
{
    private const string Key = "watched_emulator_roots";

    public IReadOnlyList<WatchedRoot> GetRoots()
    {
        var raw = Preferences.Default.Get(Key, string.Empty);
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<WatchedRoot>>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void AddRoot(WatchedRoot root)
    {
        var roots = GetRoots().ToList();
        if (roots.Any(x => x.Kind == root.Kind && x.TreeId == root.TreeId)) return;
        roots.Add(root);
        Preferences.Default.Set(Key, JsonSerializer.Serialize(roots));
    }

    public void RemoveRoot(WatchedRoot root)
    {
        var roots = GetRoots().Where(x => !(x.Kind == root.Kind && x.TreeId == root.TreeId)).ToList();
        Preferences.Default.Set(Key, JsonSerializer.Serialize(roots));
    }
}
