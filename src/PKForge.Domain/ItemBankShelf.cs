namespace PKForge.Domain;

/// <summary>How the Item Bank screen orders the bank's items.</summary>
public enum ItemBankSort { Name, Count, Recent, Game }

/// <summary>One banked item as the Item Bank screen lists it: the name to show, where it lands
/// in the chosen game (null when no game is chosen or it cannot go there), and why not.</summary>
public sealed record ItemBankRow(ItemBankEntry Entry, string Name, ItemBankPlace? Place, string? Reason)
{
    public bool CanWithdraw => Place is not null && Reason is null;
}

/// <summary>
/// The Item Bank screen's left side, as pure rules: which banked items a tab shows, narrowed by
/// the search and ordered by the chosen sort. With a game chosen, a pouch tab lists what goes in
/// that pouch, then what this game cannot hold at all (dimmed, each with its reason); the "all"
/// tab (a null pouch) lists everything. Items another pouch of the game takes show under that pouch.
/// </summary>
public static class ItemBankShelf
{
    public static string Label(ItemBankSort sort) => sort switch
    {
        ItemBankSort.Count => "Count",
        ItemBankSort.Recent => "Latest",
        ItemBankSort.Game => "Game",
        _ => "Name",
    };

    public static ItemBankSort Next(ItemBankSort sort) => (ItemBankSort)(((int)sort + 1) % 4);

    /// <param name="pouch">The tab: a pouch of the chosen game, or null for every item.</param>
    /// <param name="locate">Where a key lands in the chosen game; null when no game is chosen.</param>
    /// <param name="family">The chosen game's machine family (for the machine reason).</param>
    /// <param name="nameOf">The name to show: the game's own when it has the item.</param>
    public static IReadOnlyList<ItemBankRow> Rows(ItemBank bank, string? pouch, string query, ItemBankSort sort,
        Func<string, ItemBankPlace?>? locate = null, string? family = null, Func<ItemBankEntry, ItemBankPlace?, string>? nameOf = null)
    {
        ArgumentNullException.ThrowIfNull(bank);
        var needle = ItemBankKeys.NameKey(query ?? "");
        var rows = new List<(ItemBankRow Row, int Order)>();
        for (var i = 0; i < bank.Entries.Count; i++)
        {
            var entry = bank.Entries[i];
            var place = locate?.Invoke(entry.Key);
            if (pouch is not null && place is not null && place.Pouch != pouch) continue;
            var name = nameOf?.Invoke(entry, place) ?? entry.Name;
            if (needle.Length > 0 && !ItemBankKeys.NameKey(name).Contains(needle, StringComparison.Ordinal)
                && !ItemBankKeys.NameKey(entry.Name).Contains(needle, StringComparison.Ordinal)) continue;
            var reason = locate is not null && place is null ? ItemBankOffers.Reason(entry, family) : null;
            rows.Add((new ItemBankRow(entry, name, place, reason), i));
        }
        var byName = StringComparer.CurrentCultureIgnoreCase;
        IEnumerable<(ItemBankRow Row, int Order)> ordered = sort switch
        {
            ItemBankSort.Count => rows.OrderByDescending(r => r.Row.Entry.Count).ThenBy(r => r.Row.Name, byName),
            // The bank adds new kinds at its end: the latest deposits are the last entries.
            ItemBankSort.Recent => rows.OrderByDescending(r => r.Order),
            ItemBankSort.Game => rows.OrderBy(r => LatestGame(r.Row.Entry), byName).ThenBy(r => r.Row.Name, byName),
            _ => rows.OrderBy(r => r.Row.Name, byName),
        };
        // What the chosen game cannot take always follows what it can, whatever the sort.
        return [.. ordered.OrderBy(r => r.Row.Reason is null ? 0 : 1).Select(r => r.Row)];
    }

    /// <summary>"Platinum ×3 · HeartGold ×1": where the banked items came from, latest last.</summary>
    public static string Origins(ItemBankEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Origins.Count == 0) return "Origin unknown";
        return string.Join(" · ", entry.Origins
            .GroupBy(o => o.Game)
            .Select(g => $"{g.Key} ×{g.Sum(o => o.Count)}"));
    }

    /// <summary>The game the latest deposit of this item came from.</summary>
    public static string LatestGame(ItemBankEntry entry) => entry.Origins.Count > 0 ? entry.Origins[^1].Game : "";

    /// <summary>How many of <paramref name="count"/> a step of the amount arrows reaches,
    /// wrapping like the bag's count row: below 1 goes to the most, above the most back to 1.</summary>
    public static int Step(int amount, int delta, int most)
    {
        most = Math.Max(1, most);
        var next = amount + delta;
        if (next < 1) return most;
        if (next > most) return 1;
        return next;
    }
}
