using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;

namespace PKForge.App.Services;

/// <summary>
/// The vault's facts that its index does not carry, for the organizer's filters: species names
/// from the engine's tables, canonical typings from the shared park catalog, and the egg, gender and ball
/// read straight from the stored bytes with PKHeX's own entity parser - in-process and offline,
/// exactly how <see cref="HabitatCatalog"/> reads typings. Stored facts are probed once per entry
/// and kept for the lifetime of the view asking.
/// </summary>
public sealed class BankFacts(IBankService bank, IGameDataService data) : IBankFacts
{
    /// <summary>What one parse of the stored bytes tells the organizer.</summary>
    private readonly record struct Stored(bool IsEgg, int Gender, int Ball, int HeldItem);

    private readonly Dictionary<Guid, Stored> _stored = [];
    private readonly Dictionary<Guid, int> _backfill = [];

    public string SpeciesName(int species) =>
        species >= 0 && species < data.SpeciesNames.Count ? data.SpeciesNames[species] : "";

    /// <summary>True while the stored mon is an unhatched egg. The index keeps no egg flag
    /// (the bytes are authoritative), so this is the one filter fact worth a file read.</summary>
    public bool IsEgg(BankEntry entry) => Probe(entry).IsEgg;

    public int Gender(BankEntry entry) => Probe(entry).Gender;

    public int Ball(BankEntry entry) => Probe(entry).Ball;

    /// <summary>The index's held item when it has one; older entries read it from the bytes
    /// once and queue it for <see cref="FlushHeldItemBackfill"/>.</summary>
    public int HeldItem(BankEntry entry) => entry.Info.HeldItem ?? Probe(entry).HeldItem;

    /// <summary>
    /// Lazy migration: writes every held item probed from the bytes of a pre-field entry back
    /// into the index (one write), so the next view reads it without touching a file. Entries
    /// that changed since the probe are skipped. Returns how many entries were updated.
    /// </summary>
    public int FlushHeldItemBackfill()
    {
        Dictionary<Guid, int> pending;
        lock (_backfill)
        {
            if (_backfill.Count == 0) return 0;
            pending = new(_backfill);
            _backfill.Clear();
        }
        var updates = bank.GetAll()
            .Where(e => e.Info.HeldItem is null && pending.ContainsKey(e.Id))
            .Select(e => (e.Id, e.Info with { HeldItem = pending[e.Id] }))
            .ToList();
        try { return updates.Count == 0 ? 0 : bank.UpdateInfo(updates); }
        catch (IOException) { return 0; /* the index stays as it was; the probe runs again next time */ }
    }

    public IReadOnlyList<int> Types(int species, int form) => HabitatCatalog.TypesFor(species, form);

    /// <summary>One parse per entry for egg, gender and ball; an unreadable or vanished entry
    /// reads as a genderless non-egg in no ball, so it sorts last instead of failing the view.</summary>
    private Stored Probe(BankEntry entry)
    {
        if (_stored.TryGetValue(entry.Id, out var cached)) return cached;
        var stored = new Stored(false, 2, 0, 0);
        try
        {
            if (EntityBytes.Parse(entry, bank.GetData(entry.Id)) is { } pk)
                {
                stored = new Stored(pk.IsEgg, pk.Gender, pk.Ball, pk.HeldItem);
                if (entry.Info.HeldItem is null)
                    lock (_backfill) _backfill[entry.Id] = pk.HeldItem;
            }
        }
        catch (IOException) { /* an unreadable file carries no facts */ }
        catch (InvalidOperationException) { /* the entry left the bank */ }
        return _stored[entry.Id] = stored;
    }
}
