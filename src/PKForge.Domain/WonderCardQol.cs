using System.Text.Json;

namespace PKForge.Domain;

/// <summary>The open save's identity: what "compatible" means for wonder cards, and the
/// game half of an injected-history key.</summary>
public sealed record EventGiftSaveProfile(int Generation, int Language, string GameLabel);

/// <summary>
/// Wonder-card gallery filters. The default (everything off/null) browses the full shelf,
/// exactly as before; a null save profile means no session is open and compatibility can
/// never narrow anything.
/// </summary>
public sealed record EventGiftFilter(bool CompatibleOnly = false, int? Generation = null, int? Year = null)
{
    public bool IsActive => CompatibleOnly || Generation is not null || Year is not null;
    public bool Matches(EventGift gift, EventGiftSaveProfile? profile) =>
        (!CompatibleOnly || profile is null || EventGiftRules.IsCompatible(gift, profile))
        && (Generation is not { } generation || gift.Generation == generation)
        && (Year is not { } year || gift.Year == year);
}

/// <summary>Pure compatibility rules for the event gallery.</summary>
public static class EventGiftRules
{
    /// <summary>
    /// A card fits the open save when its generation matches and its language restriction
    /// (0 = distributed in every language) is the save's language. A save with no known
    /// language (0 or negative, or no session) can never be narrowed, so it matches.
    /// </summary>
    public static bool IsCompatible(EventGift gift, EventGiftSaveProfile? profile) =>
        profile is { } save
        && gift.Generation == save.Generation
        && (gift.Language <= 0 || save.Language <= 0 || gift.Language == save.Language);
}

/// <summary>
/// The local "already injected" ledger: PKSM-style markers for cards received through this
/// app, keyed by card identity + game label. Markers are quality of life only - a recorded
/// card stays receivable. Backed by a small json in the app data directory.
/// </summary>
public sealed class InjectedGiftHistory
{
    private readonly string? _path;
    private readonly HashSet<string> _injected;

    public InjectedGiftHistory(string? path)
    {
        _path = path;
        _injected = [.. Load(path)];
    }

    public int Count => _injected.Count;

    public bool IsInjected(string key) => _injected.Contains(key);

    /// <summary>Stable identity of one received card: its distribution (generation, card
    /// number, title) plus the game it was injected into. The gallery's receive index is
    /// deliberately not part of it - archives grow, indices shift.</summary>
    public static string KeyFor(EventGift gift, EventGiftSaveProfile profile) =>
        $"{profile.GameLabel}|{gift.Generation}|{gift.CardId:0000}|{gift.Title}";

    public void Record(string key)
    {
        if (!_injected.Add(key)) return;
        Save();
    }

    /// <summary>Forgets every marker. The user confirms this in the gallery's filter menu.</summary>
    public void Clear()
    {
        if (_injected.Count == 0) return;
        _injected.Clear();
        Save();
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(_injected.Order(StringComparer.Ordinal).ToArray()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A ledger that cannot be written must never break the wonder card menu.
        }
    }

    private static string[] Load(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return []; // corrupt or locked ledger: start fresh rather than crash the shelf
        }
    }
}
