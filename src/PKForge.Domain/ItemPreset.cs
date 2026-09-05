namespace PKForge.Domain;

public sealed record ItemPresetEntry(string Pouch, int ItemId, string ItemName, int Count);

/// <summary>Item IDs are only reusable within the originating generation and name table.</summary>
public sealed record ItemPreset(string Id, string Name, int Generation, IReadOnlyList<ItemPresetEntry> Items)
{
    public IReadOnlyList<ItemPresetEntry> CompatibleItems(int generation, IReadOnlyList<string> names,
        Func<string, IReadOnlyList<int>> legalItems)
    {
        if (generation != Generation) return [];
        return Items.Where(item => item.Count > 0 && item.ItemId > 0 && item.ItemId < names.Count
            && !string.IsNullOrWhiteSpace(item.ItemName)
            && string.Equals(names[item.ItemId], item.ItemName, StringComparison.Ordinal)
            && legalItems(item.Pouch).Contains(item.ItemId))
            .DistinctBy(item => (item.Pouch, item.ItemId)).ToArray();
    }
}
