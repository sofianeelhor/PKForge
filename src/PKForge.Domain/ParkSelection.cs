namespace PKForge.Domain;

/// <summary>Read-only resident selection shared by park clients.</summary>
public static class ParkSelection
{
    public static IReadOnlyList<T> Select<T>(IEnumerable<T> candidates, Func<T, string> identity,
        bool random, int count, IEnumerable<string> selectedIds, Random? rng = null)
    {
        var unique = candidates.DistinctBy(identity).ToList();
        count = Math.Clamp(count, 1, 12);
        if (!random)
        {
            var lookup = unique.ToDictionary(identity);
            return selectedIds.Distinct().Where(lookup.ContainsKey).Take(count).Select(id => lookup[id]).ToArray();
        }
        rng ??= Random.Shared;
        // Partial Fisher-Yates samples without replacement, with equal probability.
        for (var i = 0; i < Math.Min(count, unique.Count); i++)
        {
            var j = rng.Next(i, unique.Count);
            (unique[i], unique[j]) = (unique[j], unique[i]);
        }
        return unique.Take(count).ToArray();
    }
}
