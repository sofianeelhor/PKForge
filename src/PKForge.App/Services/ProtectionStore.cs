using System.Text.Json;

namespace PKForge.App.Services;

/// <summary>
/// Per-save protection flags (locked boxes, release-locked mons) persisted in
/// Preferences keyed by document id. Locks are advisory inside the app: destructive
/// flows consult this store before touching storage.
/// </summary>
public sealed class ProtectionStore
{
    private sealed record LockedMon(int Box, int Slot, uint Pid);
    private sealed record DocumentProtection(HashSet<int> LockedBoxes, List<LockedMon> LockedMons);

    private readonly Dictionary<string, DocumentProtection> _cache = [];

    public bool IsBoxLocked(string documentId, int box) => Load(documentId).LockedBoxes.Contains(box);

    /// <summary>Toggles a box lock; returns the new state.</summary>
    public bool ToggleBox(string documentId, int box)
    {
        var doc = Load(documentId);
        if (!doc.LockedBoxes.Remove(box)) doc.LockedBoxes.Add(box);
        Save(documentId, doc);
        return doc.LockedBoxes.Contains(box);
    }

    public IReadOnlySet<int> LockedBoxes(string documentId) => Load(documentId).LockedBoxes;

    public bool IsMonLocked(string documentId, int box, int slot, uint pid) =>
        Load(documentId).LockedMons.Exists(m => m.Box == box && m.Slot == slot && m.Pid == pid);

    /// <summary>Toggles a mon release-lock; returns the new state.</summary>
    public bool ToggleMon(string documentId, int box, int slot, uint pid)
    {
        var doc = Load(documentId);
        var index = doc.LockedMons.FindIndex(m => m.Box == box && m.Slot == slot && m.Pid == pid);
        if (index >= 0) doc.LockedMons.RemoveAt(index);
        else doc.LockedMons.Add(new LockedMon(box, slot, pid));
        Save(documentId, doc);
        return index < 0;
    }

    /// <summary>A release is allowed when the slot holds no locked mon identity.</summary>
    public bool CanRelease(string documentId, int box, int slot, uint pid) =>
        !IsMonLocked(documentId, box, slot, pid);

    private DocumentProtection Load(string documentId)
    {
        if (_cache.TryGetValue(documentId, out var cached)) return cached;
        DocumentProtection doc;
        try
        {
            var raw = Preferences.Default.Get(Key(documentId), "");
            doc = raw.Length == 0
                ? new DocumentProtection([], [])
                : JsonSerializer.Deserialize<DocumentProtection>(raw) ?? new DocumentProtection([], []);
        }
        catch
        {
            doc = new DocumentProtection([], []);
        }
        _cache[documentId] = doc;
        return doc;
    }

    private static void Save(string documentId, DocumentProtection doc) =>
        Preferences.Default.Set(Key(documentId), JsonSerializer.Serialize(doc));

    private static string Key(string documentId) => $"pkforge.protection.{documentId}";
}
