namespace PKForge.Domain;

/// <summary>One generation's slice of the national dex, with living-dex ownership counts.</summary>
public sealed record GenDexSegment(int Generation, int First, int Last, int Owned, int Shiny, int Total);

/// <summary>Living-dex progress over a whole collection (bank plus open save).</summary>
/// <param name="TotalSpecies">Valid species ids in scope.</param>
/// <param name="Owned">Species you own at least one normal or shiny of.</param>
/// <param name="Shiny">Species you own at least one shiny of.</param>
/// <param name="Segments">Per-generation slices, Gen I through IX.</param>
public sealed record CollectionDexProgress(int TotalSpecies, int Owned, int Shiny, IReadOnlyList<GenDexSegment> Segments);

/// <summary>
/// Pure living-dex math: which species a collection actually contains, split per
/// generation, with a shiny living-dex count. "Owned" means you have the Pokémon
/// (bank entry or save slot), not that a dex flag says caught.
/// </summary>
public static class CollectionDex
{
    public static readonly (int Generation, int First, int Last)[] GenRanges =
    [
        (1, 1, 151), (2, 152, 251), (3, 252, 386), (4, 387, 493), (5, 494, 649),
        (6, 650, 721), (7, 722, 809), (8, 810, 905), (9, 906, 1025),
    ];

    public const int MaxSpecies = 1025;

    /// <summary>Computes progress. Species outside 1..<paramref name="maxSpeciesId"/> or
    /// without a name are ignored, so a Gen 4 save never counts Gen 5 mons.</summary>
    public static CollectionDexProgress Compute(
        IEnumerable<(int Species, bool Shiny)> collection, int maxSpeciesId, IReadOnlyList<string>? speciesNames = null)
    {
        var owned = new HashSet<int>();
        var shiny = new HashSet<int>();
        foreach (var (species, isShiny) in collection)
        {
            if (species < 1 || species > MaxSpecies || species > maxSpeciesId) continue;
            if (speciesNames is not null && ((uint)species >= (uint)speciesNames.Count || speciesNames[species].Length == 0)) continue;
            owned.Add(species);
            if (isShiny) shiny.Add(species);
        }

        var segments = new List<GenDexSegment>(GenRanges.Length);
        foreach (var (generation, first, last) in GenRanges)
        {
            var total = 0;
            var segmentOwned = 0;
            var segmentShiny = 0;
            for (var id = first; id <= last; id++)
            {
                if (id > maxSpeciesId) break;
                if (speciesNames is not null && ((uint)id >= (uint)speciesNames.Count || speciesNames[id].Length == 0)) continue;
                total++;
                if (owned.Contains(id)) segmentOwned++;
                if (shiny.Contains(id)) segmentShiny++;
            }
            segments.Add(new GenDexSegment(generation, first, last, segmentOwned, segmentShiny, total));
        }

        var scopeOwned = segments.Sum(s => s.Owned);
        var scopeShiny = segments.Sum(s => s.Shiny);
        return new CollectionDexProgress(segments.Sum(s => s.Total), scopeOwned, scopeShiny, segments);
    }
}
