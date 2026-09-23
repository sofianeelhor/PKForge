using PKForge.Domain;
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
    private readonly record struct Stored(bool IsEgg, int Gender, int Ball);

    private readonly Dictionary<Guid, Stored> _stored = [];

    public string SpeciesName(int species) =>
        species >= 0 && species < data.SpeciesNames.Count ? data.SpeciesNames[species] : "";

    /// <summary>True while the stored mon is an unhatched egg. The index keeps no egg flag
    /// (the bytes are authoritative), so this is the one filter fact worth a file read.</summary>
    public bool IsEgg(BankEntry entry) => Probe(entry).IsEgg;

    public int Gender(BankEntry entry) => Probe(entry).Gender;

    public int Ball(BankEntry entry) => Probe(entry).Ball;

    public IReadOnlyList<int> Types(int species, int form) => HabitatCatalog.TypesFor(species, form);

    /// <summary>One parse per entry for egg, gender and ball; an unreadable or vanished entry
    /// reads as a genderless non-egg in no ball, so it sorts last instead of failing the view.</summary>
    private Stored Probe(BankEntry entry)
    {
        if (_stored.TryGetValue(entry.Id, out var cached)) return cached;
        var stored = new Stored(false, 2, 0);
        try
        {
            if (EntityFormat.GetFromBytes(bank.GetData(entry.Id)) is { } pk)
                stored = new Stored(pk.IsEgg, pk.Gender, pk.Ball);
        }
        catch (IOException) { /* an unreadable file carries no facts */ }
        catch (InvalidOperationException) { /* the entry left the bank */ }
        return _stored[entry.Id] = stored;
    }
}
