namespace PKForge.Domain;

/// <summary>One slot's verdict from a whole-save legality sweep: the PKHeX report
/// lines (the drill-in text) plus the first offending line as the plain-language
/// problem summary. Empty problem means legal.</summary>
public sealed record SlotLegality(
    int Box, int Slot, bool Valid, string Problem, IReadOnlyList<string> Report);

/// <summary>The identity facts a clone shares. The games' RNG makes exact EC+PID
/// collisions effectively impossible, so an identical pair is a duplicate (Gen 6+);
/// older formats store no EC and fall back to PID + trainer identity.</summary>
public sealed record MonFingerprint(
    uint Pid, uint? EncryptionConstant, string OriginalTrainer, int Tid,
    string SlotLabel, string DisplayName);

/// <summary>A set of slots holding the same fingerprint, with which key kind matched.</summary>
public sealed record CloneGroup(string KeyKind, IReadOnlyList<MonFingerprint> Members);

/// <summary>Clone and hack audit: pure grouping over identity facts collected by the caller.</summary>
public static class CollectionAudit
{
    /// <summary>The clone fingerprint: (EC, PID) when the format stores an encryption
    /// constant, otherwise (PID, OT, TID). Mons group only when the whole key matches.</summary>
    public static string CloneKey(MonFingerprint mon) => mon.EncryptionConstant is { } ec
        ? $"ec:{ec:X8}:{mon.Pid:X8}"
        : $"pid:{mon.Pid:X8}:{mon.OriginalTrainer}:{mon.Tid}";

    /// <summary>Groups the collection into clone sets (fingerprints held by two or more
    /// slots). Distinct fingerprints are not a finding and are left out.</summary>
    public static IReadOnlyList<CloneGroup> GroupClones(IReadOnlyList<MonFingerprint> mons) =>
        mons.Select(mon => (Mon: mon, Key: CloneKey(mon)))
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any())
            .Select(group => new CloneGroup(
                group.First().Mon.EncryptionConstant is not null ? "EC + PID" : "PID + OT + TID",
                group.Select(entry => entry.Mon).ToList()))
            .ToList();
}

/// <summary>One legality sweep's verdicts, keyed to the save document plus its mutation
/// generation: the answer stays valid only until the save is written again.</summary>
public sealed class LegalitySweepCache
{
    private (string DocumentId, long Generation)? _key;
    private Dictionary<(int Box, int Slot), SlotLegality>? _bySlot;

    public IReadOnlyList<SlotLegality>? Results { get; private set; }

    public bool IsFresh(string documentId, long generation) => _key == (documentId, generation);

    /// <summary>Replaces the cached sweep; the previous answer is discarded wholesale.</summary>
    public void Store(string documentId, long generation, IReadOnlyList<SlotLegality> results)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        _key = (documentId, generation);
        Results = results;
        _bySlot = results.ToDictionary(verdict => (verdict.Box, verdict.Slot));
    }

    /// <summary>The stored verdict for one slot; false unless the cache is still fresh for
    /// the given document and generation.</summary>
    public bool TryGetVerdict(string documentId, long generation, int box, int slot, out SlotLegality? verdict)
    {
        verdict = null;
        if (!IsFresh(documentId, generation) || _bySlot is null)
            return false;
        return _bySlot.TryGetValue((box, slot), out verdict);
    }
}

/// <summary>One ribbon tile in the album: its current value, the format's storage range,
/// and whether this Pokémon can legally gain more of it right now.</summary>
public sealed record RibbonAlbumEntry(
    string Id, string Name, int Value, int MaxValue, bool IsMark, bool Obtainable);

/// <summary>Merges the format's ribbon inventory with the engine's per-species legal
/// maxima into album tiles. A ribbon is obtainable when its legal maximum exceeds the
/// current value; ribbons missing from the legal map can never be earned by this
/// Pokémon and stay dimmed.</summary>
public static class RibbonAlbum
{
    public static IReadOnlyList<RibbonAlbumEntry> Build(
        IReadOnlyList<RibbonEntry> ribbons, IReadOnlyDictionary<string, int> legalMaxima)
    {
        var entries = new List<RibbonAlbumEntry>(ribbons.Count);
        foreach (var ribbon in ribbons)
        {
            var legal = legalMaxima.TryGetValue(ribbon.Id, out var max) ? max : 0;
            entries.Add(new RibbonAlbumEntry(
                ribbon.Id, ribbon.Name, ribbon.Value, ribbon.MaxValue, ribbon.IsMark,
                legal > ribbon.Value));
        }
        return entries;
    }
}
