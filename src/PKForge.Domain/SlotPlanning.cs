namespace PKForge.Domain;

/// <summary>Where a batch of Pokémon lands in a save's boxes.</summary>
public static class SlotPlanning
{
    /// <summary>
    /// The empty box slots at or after <paramref name="start"/>, in box then slot order, never
    /// wrapping back and never the party. Null <paramref name="start"/> means the first box.
    /// Occupied slots are skipped, so nothing already stored is ever replaced.
    /// </summary>
    public static IEnumerable<SlotRef> FreeSlotsFrom(IEnumerable<SlotSummary> slots, SlotRef? start)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var (box, slot) = start is { } from ? (from.Box, from.Slot) : (0, 0);
        return slots
            .Where(s => s.Box >= 0 && s.Species is null && (s.Box > box || s.Box == box && s.Slot >= slot))
            .OrderBy(s => s.Box).ThenBy(s => s.Slot)
            .Select(s => new SlotRef(s.Box, s.Slot));
    }
}
