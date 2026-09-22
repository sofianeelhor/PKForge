namespace PKForge.Domain;

/// <summary>What the random team roller was asked for: team size, the level band, and
/// the three standing filters. Levels are clamped to 1-100; the count to 1-6.</summary>
public sealed record RandomTeamOptions(
    int Count,
    int MinLevel,
    int MaxLevel,
    bool NoLegendaries,
    bool NoDuplicates,
    bool AllowNfe)
{
    public int TeamSize => Math.Clamp(Count, 1, 6);
    public int LowLevel => Math.Clamp(Math.Min(MinLevel, MaxLevel), 1, 100);
    public int HighLevel => Math.Clamp(Math.Max(MinLevel, MaxLevel), 1, 100);
}

/// <summary>
/// Pure species picker for the random team generator. The caller supplies the game's
/// own species pool (already capped to the save's species table) and the set of
/// not-fully-evolved ids; this side only rolls dice, so it is deterministic under a
/// seeded <see cref="Random"/> and unit-testable without any engine.
/// </summary>
public static class RandomTeamPlanner
{
    /// <summary>
    /// Rolls one team: distinct species when duplicates are refused and the pool
    /// allows it, each with a level drawn from the band. Returns fewer picks than
    /// requested only when the filtered pool is empty.
    /// </summary>
    public static IReadOnlyList<(int Species, int Level)> Plan(
        IReadOnlyList<int> speciesPool, IReadOnlySet<int> notFullyEvolved, RandomTeamOptions options, Random random)
    {
        ArgumentNullException.ThrowIfNull(speciesPool);
        ArgumentNullException.ThrowIfNull(notFullyEvolved);
        ArgumentNullException.ThrowIfNull(random);

        IEnumerable<int> candidates = speciesPool;
        if (options.NoLegendaries)
            candidates = candidates.Where(id => !SpeciesCategories.Legendary.Contains(id) && !SpeciesCategories.Mythical.Contains(id));
        if (!options.AllowNfe)
            candidates = candidates.Where(id => !notFullyEvolved.Contains(id));
        var pool = candidates.ToList();
        if (pool.Count == 0) return [];
        // Distinct sampling when asked for and the pool can carry it: partial
        // Fisher-Yates turns the first `count` slots into a uniform distinct sample.
        // Otherwise rolls are independent draws with replacement.
        var count = options.TeamSize;
        var picks = new List<int>(count);
        if (options.NoDuplicates)
        {
            count = Math.Min(count, pool.Count);
            for (var i = 0; i < count; i++)
            {
                var j = random.Next(i, pool.Count);
                (pool[i], pool[j]) = (pool[j], pool[i]);
                picks.Add(pool[i]);
            }
        }
        else
        {
            for (var i = 0; i < count; i++)
                picks.Add(pool[random.Next(pool.Count)]);
        }

        var team = new List<(int Species, int Level)>(count);
        foreach (var species in picks)
            team.Add((species, random.Next(options.LowLevel, options.HighLevel + 1)));
        return team;
    }
}
