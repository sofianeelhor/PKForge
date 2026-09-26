using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// Where a batch starts landing in a save: "first free space" (the default), or a box and
/// then a slot. Each box shows its free spaces and each slot whether it is free, so the
/// player sees what will happen; occupied slots are always skipped, never replaced.
/// </summary>
public static class StartSlotPicker
{
    private const string FirstFree = "First free space";

    /// <summary>The player's choice: <see cref="Start"/> is null for "first free space".</summary>
    public sealed record Choice(SlotRef? Start);

    /// <summary>Null when the player backs out; otherwise where the batch should start.</summary>
    public static async Task<Choice?> PickAsync(Grid host, string title, IReadOnlyList<SlotSummary> slots, int count, bool offerFirstFree = true)
    {
        var boxes = slots.Where(s => s.Box >= 0).GroupBy(s => s.Box).OrderBy(g => g.Key).ToList();
        var options = new List<PadOption>();
        if (offerFirstFree)
            options.Add(new PadOption(FirstFree, IconPath: "fill", Detail: "Wherever there is room, box by box."));
        foreach (var box in boxes)
        {
            var free = box.Count(s => s.Species is null);
            options.Add(new PadOption($"Box {box.Key + 1:00}", IconPath: "box", Detail: free == 0 ? "Full" : $"{free} free"));
        }

        var boxChoice = await PadMenu.ShowAsync(host, title, $"{count} Pokémon. Pick where they start.", [.. options]);
        if (boxChoice is null) return null;
        if (boxChoice == FirstFree) return new Choice(null);
        var boxIndex = boxes[options.FindIndex(o => o.Label == boxChoice) - (offerFirstFree ? 1 : 0)].Key;

        var boxSlots = boxes.First(g => g.Key == boxIndex).OrderBy(s => s.Slot).ToList();
        var slotOptions = boxSlots
            .Select(s => new PadOption($"Slot {s.Slot + 1:00}", Detail: s.Species is null ? "Free" : "Taken, skipped"))
            .ToArray();
        var slotChoice = await PadMenu.ShowAsync(host, $"Box {boxIndex + 1:00}: start at which slot?",
            "They fill the free slots from here on, then the next boxes. Taken slots are skipped.", slotOptions);
        if (slotChoice is null) return null;
        var slot = boxSlots[Array.FindIndex(slotOptions, o => o.Label == slotChoice)].Slot;
        return new Choice(new SlotRef(boxIndex, slot));
    }

    /// <summary>"box 08 slot 07 to box 08 slot 12", or why there is no room, for the confirmation.</summary>
    public static string Describe(IReadOnlyList<SlotSummary> slots, SlotRef? start, int count)
    {
        var landing = SlotPlanning.FreeSlotsFrom(slots, start).Take(count).ToList();
        if (landing.Count == 0) return "There is no free slot from there on.";
        static string Name(SlotRef s) => $"box {s.Box + 1:00} slot {s.Slot + 1:00}";
        var range = landing.Count == 1 ? Name(landing[0]) : $"{Name(landing[0])} to {Name(landing[^1])}";
        return landing.Count < count ? $"{range}; {count - landing.Count} will not fit." : range;
    }
}
