using System.Text.Json;

namespace PKForge.Domain;

/// <summary>One stored preference, typed so it is put back exactly as it was read.</summary>
/// <param name="Type">"string", "bool", "int", "long" or "float".</param>
public sealed record PreferenceValue(string Key, string Type, string Value);

/// <summary>One kind of data inside a PKForge data archive and how many items it holds.</summary>
public sealed record DataArchiveSection(string Id, int Items);

/// <summary>manifest.json at the root of a PKForge data archive.</summary>
public sealed record DataArchiveManifest(
    string Format,
    int FormatVersion,
    string AppVersion,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<DataArchiveSection> Sections)
{
    public int ItemsIn(string sectionId) => Sections.FirstOrDefault(s => s.Id == sectionId)?.Items ?? 0;
}

/// <summary>Why an archive was refused, said in words a player can act on.</summary>
public sealed class DataArchiveException(string reason) : Exception(reason);

/// <summary>
/// Rules for moving PKForge's data to another device: which preferences belong to this
/// device only, and which linked folders must be linked again there (an Android folder
/// grant never travels with a file).
/// </summary>
public static class DataTransfer
{
    public const string BankSection = "bank";
    public const string RestorePointsSection = "restore-points";
    public const string AlbumSection = "pokepark-album";
    public const string ItemPresetsSection = "item-presets";
    public const string ItemBankSection = "item-bank";
    public const string MonPresetsSection = "mon-presets";
    public const string SaveIdentitiesSection = "save-identities";
    public const string GiftsSection = "injected-gifts";
    public const string HistorySection = "pokemon-history";
    public const string PreferencesSection = "preferences";

    /// <summary>Folders the player linked, kept with their names until they are linked again.</summary>
    public const string RelinkKey = "relink_emulator_roots";
    public const string WatchedRootsKey = "watched_emulator_roots";

    /// <summary>
    /// Preferences that describe this device rather than the player: folder grants and the
    /// scan cache built from them, the PKForge folder's grant (its tree, name and last report),
    /// the Living Dex destination (a document of this device), music picked from this device's
    /// storage, and the Poképark renderer chosen for this GPU. They are exported (the grants as
    /// names to relink) but never applied on import.
    /// </summary>
    public static bool IsDeviceBound(string key) =>
        key is WatchedRootsKey or RelinkKey or "music_library" or "pkforge.pokepark.renderer.v1" or "livingdex_destination"
        || key.StartsWith("scan_cache", StringComparison.Ordinal)
        || key.StartsWith("pkforge-folder.", StringComparison.Ordinal)
        || key.StartsWith("music_place_", StringComparison.Ordinal);

    /// <summary>The preferences an import writes: every one the archive holds but the device-bound ones.</summary>
    public static IReadOnlyList<PreferenceValue> ToApply(IReadOnlyList<PreferenceValue> archived) =>
        [.. archived.Where(p => !IsDeviceBound(p.Key))];

    /// <summary>
    /// The folders still to relink after an import: the ones already waiting here, then the
    /// archive's linked and waiting folders, once each (same emulator and same name).
    /// </summary>
    public static IReadOnlyList<WatchedRoot> ToRelink(IReadOnlyList<WatchedRoot> waiting, IReadOnlyList<PreferenceValue> archived)
    {
        var all = waiting
            .Concat(ReadRoots(archived.FirstOrDefault(p => p.Key == WatchedRootsKey)?.Value))
            .Concat(ReadRoots(archived.FirstOrDefault(p => p.Key == RelinkKey)?.Value));
        // A single linked file is a grant to one document on that device: it cannot be relinked
        // by linking an emulator here, so it never waits in the relink list.
        return [.. all.Where(r => r.Kind != EmulatorKind.LinkedFile).DistinctBy(r => (r.Kind, r.DisplayName))];
    }

    /// <summary>A JSON list of roots as preferences keep it; anything unreadable is no roots.</summary>
    public static IReadOnlyList<WatchedRoot> ReadRoots(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<WatchedRoot>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
