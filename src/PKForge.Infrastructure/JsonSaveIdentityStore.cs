using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// <see cref="ISaveIdentityStore"/> as one small JSON file, written atomically (temp file +
/// replace) so a crash mid-write never loses every save's name. A null path keeps it in memory.
/// </summary>
public sealed class JsonSaveIdentityStore : ISaveIdentityStore
{
    private readonly string? _path;
    private readonly Dictionary<string, SaveIdentity> _entries;
    private readonly Lock _gate = new();

    public JsonSaveIdentityStore(string? path)
    {
        _path = path;
        _entries = Load(path);
    }

    public event Action<string>? Changed;

    public SaveIdentity? Get(string documentId)
    {
        lock (_gate) return _entries.GetValueOrDefault(documentId);
    }

    public void Set(SaveIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.DocumentId);
        var clean = identity with
        {
            DisplayName = string.IsNullOrWhiteSpace(identity.DisplayName) ? null : identity.DisplayName.Trim(),
            ColorKey = SaveIdentityPalette.IsKnown(identity.ColorKey) ? identity.ColorKey : null,
            ArtKey = CustomGameArt.IsValidKey(identity.ArtKey) ? identity.ArtKey : null,
        };
        lock (_gate)
        {
            if (clean.IsEmpty) _entries.Remove(clean.DocumentId);
            else _entries[clean.DocumentId] = clean;
            Persist();
        }
        Changed?.Invoke(identity.DocumentId);
    }

    public void Reset(string documentId)
    {
        lock (_gate)
        {
            if (!_entries.Remove(documentId)) return;
            Persist();
        }
        Changed?.Invoke(documentId);
    }

    private void Persist()
    {
        if (_path is null) return;
        try
        {
            AtomicFile.WriteText(_path, JsonSerializer.Serialize(_entries.Values.ToList()));
        }
        catch (IOException) { } // names are a convenience; never fail a scan over them
        catch (UnauthorizedAccessException) { }
    }

    private static Dictionary<string, SaveIdentity> Load(string? path)
    {
        var entries = new Dictionary<string, SaveIdentity>(StringComparer.Ordinal);
        if (path is null || !File.Exists(path)) return entries;
        try
        {
            // Entries written by older builds may carry only retired detection flags: drop them.
            foreach (var entry in JsonSerializer.Deserialize<List<SaveIdentity>>(File.ReadAllText(path)) ?? [])
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.DocumentId) && !entry.IsEmpty) entries[entry.DocumentId] = entry;
        }
        catch (JsonException) { }
        catch (NotSupportedException) { }
        catch (IOException) { }
        return entries;
    }
}
