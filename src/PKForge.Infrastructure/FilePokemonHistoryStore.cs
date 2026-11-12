using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// Pokémon History on disk: one small JSON per Pokémon (<c>&lt;id&gt;.json</c>, the copies' bytes
/// inline as base64) under app data's <c>pokemon-history/</c>. Writes go through a temporary file,
/// so a record is either the old one or the new one. Records are read when asked for (the folder
/// mirror and data imports change files underneath), so nothing here goes stale.
/// </summary>
public sealed class FilePokemonHistoryStore(string directory) : IPokemonHistoryStore
{
    public const string Folder = "pokemon-history";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Lock _gate = new();

    public string Directory => directory;

    /// <summary>
    /// The record of a Pokémon known by any of <paramref name="souls"/>. When several files hold it
    /// (a conflict copy from a sync, an older device's copy), they are merged into one, starting
    /// from the record stored under its own name, so no copy hides another's departures or trips.
    /// </summary>
    public PokemonHistoryRecord? Find(IEnumerable<string> souls)
    {
        var wanted = souls.ToHashSet(StringComparer.Ordinal);
        var matches = Files().Where(file => file.Record.Souls.Any(wanted.Contains))
            .OrderBy(file => file.Name == file.Record.Id ? 0 : 1)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => file.Record)
            .ToList();
        return matches.Count == 0 ? null : matches.Skip(1).Aggregate(matches[0], PokemonHistoryRecord.Merge);
    }

    public void Save(PokemonHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        // The id names the file: anything but the id PokemonHistoryRecord.IdFor makes is refused,
        // so a record from a folder or an archive can never write outside this folder.
        if (!IsValidId(record.Id)) throw new ArgumentException($"\"{record.Id}\" is not a Pokémon History record id.", nameof(record));
        lock (_gate)
        {
            var root = Path.GetFullPath(directory);
            var path = Path.GetFullPath(Path.Combine(root, record.Id + ".json"));
            if (Path.GetDirectoryName(path) != root.TrimEnd(Path.DirectorySeparatorChar))
                throw new ArgumentException("A Pokémon History record must stay in its folder.", nameof(record));
            AtomicFile.WriteText(path, JsonSerializer.Serialize(record, Json));
        }
    }

    public IReadOnlyList<PokemonHistoryRecord> All() => [.. Files().Select(file => file.Record)];

    private IEnumerable<(string Name, PokemonHistoryRecord Record)> Files()
    {
        if (!System.IO.Directory.Exists(directory)) yield break;
        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            if (Read(path) is { } record) yield return (Path.GetFileNameWithoutExtension(path), record);
    }

    public PokemonHistoryRecord Update(IEnumerable<string> souls, Func<PokemonHistoryRecord?, PokemonHistoryRecord> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var updated = change(Find(souls));
            Save(updated);
            return updated;
        }
    }

    /// <summary>The ids records are stored under: 20 lowercase hex digits (<see cref="PokemonHistoryRecord.IdFor"/>).</summary>
    public static bool IsValidId(string? id) => id is { Length: 20 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A record file's content, or null when it is not one (damaged, foreign).</summary>
    public static PokemonHistoryRecord? Read(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public static PokemonHistoryRecord? Parse(string json)
    {
        try
        {
            var record = JsonSerializer.Deserialize<PokemonHistoryRecord>(json, Json);
            return record is { Souls.Count: > 0, Departures: not null, Arrivals: not null, Trips: not null } && IsValidId(record.Id)
                && record.Souls.All(soul => soul is not null) && record.Departures.All(c => c is { Format: not null, Data: not null })
                && record.Arrivals.All(c => c is { Format: not null, Data: not null }) && record.Trips.All(t => t is not null)
                ? record : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return null; }
    }

    public static string Serialize(PokemonHistoryRecord record) => JsonSerializer.Serialize(record, Json);

    /// <summary>Adds <paramref name="incoming"/> without losing anything here: a record this store
    /// does not know is added, one it knows (by any identity) is merged. True when something changed.</summary>
    public bool Merge(PokemonHistoryRecord incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        lock (_gate)
        {
            var mine = Find(incoming.Souls);
            var merged = mine is null ? incoming : PokemonHistoryRecord.Merge(mine, incoming);
            if (mine is not null && Serialize(mine) == Serialize(merged)) return false;
            Save(merged);
            return true;
        }
    }
}
