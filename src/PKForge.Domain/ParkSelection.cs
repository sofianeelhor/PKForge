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

    public static IReadOnlyList<T> SelectBalanced<T>(IEnumerable<T> candidates, Func<T, string> identity,
        Func<T, string> source, int count, Random? rng = null)
    {
        rng ??= Random.Shared;
        var unique = candidates.DistinctBy(identity).ToList();
        count = Math.Clamp(count, 1, 12);
        if (unique.Count == 0) return [];

        var selected = new List<T>(Math.Min(count, unique.Count));
        var groups = unique.GroupBy(source).Where(g => g.Any()).Select(g => g.ToList()).ToList();

        // In combined-source mode, reserve one random resident from every
        // available source before filling the remaining slots globally.
        if (count >= groups.Count)
        {
            foreach (var group in groups.OrderBy(_ => rng.Next()))
            {
                var choice = group[rng.Next(group.Count)];
                selected.Add(choice);
                unique.RemoveAll(item => identity(item) == identity(choice));
            }
        }

        while (selected.Count < count && unique.Count > 0)
        {
            var index = rng.Next(unique.Count);
            selected.Add(unique[index]);
            unique.RemoveAt(index);
        }
        return selected;
    }
}
