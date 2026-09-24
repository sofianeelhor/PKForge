namespace PKForge.Domain;

/// <summary>
/// "Where is my Exp. Share?": pure lookups over held-item ids (PKHeX national numbering,
/// 0 = nothing held; ROM-hack sessions report -1 for an item with no national twin). Shared by
/// the save-side finder and the Bank search picker, so both count and match the same way.
/// </summary>
public static class HeldItemSearch
{
    /// <summary>Every occupied slot holding <paramref name="itemId"/> - or anything at all when
    /// it is null - boxes first in box/slot order, then the party. Eggs hold nothing a player can
    /// take, but PKHeX reports what the bytes say, so they are kept.</summary>
    public static IReadOnlyList<SlotSummary> Find(IEnumerable<SlotSummary> slots, int? itemId = null) =>
        slots.Where(s => s.Species is not null && s.HasItem && (itemId is not { } wanted || s.HeldItem == wanted))
            .OrderBy(s => s.Box < 0 ? 1 : 0)
            .ThenBy(s => s.Box)
            .ThenBy(s => s.Slot)
            .ToList();

    /// <summary>How many holders each item has, most common first, then by id: the picker's
    /// "which items do I actually have on my Pokémon" list. Zero ids are ignored.</summary>
    public static IReadOnlyList<(int ItemId, int Count)> Tally(IEnumerable<int> heldItems) =>
        heldItems.Where(id => id != 0)
            .GroupBy(id => id)
            .Select(g => (ItemId: g.Key, Count: g.Count()))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.ItemId)
            .ToList();

    /// <summary>Display name for an item id from the game's item table; ROM-only items (-1) and
    /// ids outside the table read as a generic label instead of throwing.</summary>
    public static string NameOf(IReadOnlyList<string> itemNames, int itemId) =>
        itemId > 0 && itemId < itemNames.Count && itemNames[itemId].Length > 0
            ? itemNames[itemId]
            : itemId == 0 ? "(none)" : "Unknown item";
}
