namespace PKForge.Domain;

/// <summary>
/// The storage organizer's marks: a set of (box, slot) references to occupied slots, kept
/// across box changes like the games' multi-select. Box -1 is the party: it can be marked
/// slot by slot or as a page, but never by the "all boxes" sweep (a party cannot be emptied).
/// Pure bookkeeping over the save's slot summaries - the view model owns the writes.
/// </summary>
public sealed class StorageMarks
{
    private readonly HashSet<(int Box, int Slot)> _marked = [];

    public int Count => _marked.Count;

    public bool Contains(int box, int slot) => _marked.Contains((box, slot));

    /// <summary>Every mark in storage order (party first, then box by box).</summary>
    public IReadOnlyList<(int Box, int Slot)> Ordered =>
        _marked.OrderBy(m => m.Box).ThenBy(m => m.Slot).ToList();

    public int CountIn(int box) => _marked.Count(m => m.Box == box);

    /// <summary>Flips one occupied slot; false (and no change) for an empty one.</summary>
    public bool Toggle(IReadOnlyList<SlotSummary> slots, int box, int slot)
    {
        if (!IsOccupied(slots, box, slot)) return false;
        if (!_marked.Remove((box, slot))) _marked.Add((box, slot));
        return true;
    }

    /// <summary>True when the page has Pokémon and every one of them is marked.</summary>
    public bool IsPageFullyMarked(IReadOnlyList<SlotSummary> slots, int box)
    {
        var occupied = Occupied(slots).Where(s => s.Box == box).ToList();
        return occupied.Count > 0 && occupied.All(s => _marked.Contains((s.Box, s.Slot)));
    }

    /// <summary>Marks every Pokémon on one page (a box, or the party).</summary>
    public void MarkPage(IReadOnlyList<SlotSummary> slots, int box) =>
        Set(Occupied(slots).Where(s => s.Box == box), mark: true);

    public void UnmarkPage(int box) => _marked.RemoveWhere(m => m.Box == box);

    /// <summary>The one-button "select all in this box": marks the page, or clears it when it
    /// is already fully marked. Returns true when the page ended up marked.</summary>
    public bool TogglePage(IReadOnlyList<SlotSummary> slots, int box)
    {
        if (IsPageFullyMarked(slots, box)) { UnmarkPage(box); return false; }
        MarkPage(slots, box);
        return IsPageFullyMarked(slots, box);
    }

    /// <summary>Marks every boxed Pokémon. The party stays out: it cannot be emptied.</summary>
    public void MarkAllBoxes(IReadOnlyList<SlotSummary> slots) =>
        Set(Occupied(slots).Where(s => s.Box >= 0), mark: true);

    /// <summary>Marks every Pokémon in the given boxes (the box manager's hand-off).</summary>
    public void MarkBoxes(IReadOnlyList<SlotSummary> slots, IReadOnlyCollection<int> boxes) =>
        Set(Occupied(slots).Where(s => s.Box >= 0 && boxes.Contains(s.Box)), mark: true);

    /// <summary>Flips every occupied slot of one page.</summary>
    public void InvertPage(IReadOnlyList<SlotSummary> slots, int box)
    {
        foreach (var s in Occupied(slots).Where(s => s.Box == box))
            if (!_marked.Remove((s.Box, s.Slot))) _marked.Add((s.Box, s.Slot));
    }

    /// <summary>Marks the given slots (the held-item finder's "mark all"); empty or unknown
    /// slots are skipped, since only occupied slots can be marked.</summary>
    public void MarkSlots(IReadOnlyList<SlotSummary> slots, IEnumerable<(int Box, int Slot)> targets)
    {
        var wanted = targets.ToHashSet();
        Set(Occupied(slots).Where(s => wanted.Contains((s.Box, s.Slot))), mark: true);
    }

    public void Clear() => _marked.Clear();

    /// <summary>Drops marks that no longer point at a Pokémon (after a write moved things).</summary>
    public void Prune(IReadOnlyList<SlotSummary> slots)
    {
        var live = Occupied(slots).Select(s => (s.Box, s.Slot)).ToHashSet();
        _marked.RemoveWhere(m => !live.Contains(m));
    }

    /// <summary>Marks (or unmarks) every occupied slot of the rectangle spanned by two slots
    /// of one page - the hold-A / drag gesture.</summary>
    public void SetRectangle(IReadOnlyList<SlotSummary> slots, int box, int from, int to, int columns, bool mark) =>
        Set(Occupied(slots).Where(s => s.Box == box && StorageRectangle.Contains(from, to, s.Slot, columns)), mark);

    private void Set(IEnumerable<SlotSummary> targets, bool mark)
    {
        foreach (var s in targets)
            if (mark) _marked.Add((s.Box, s.Slot));
            else _marked.Remove((s.Box, s.Slot));
    }

    private static bool IsOccupied(IReadOnlyList<SlotSummary> slots, int box, int slot) =>
        slots.Any(s => s.Box == box && s.Slot == slot && s.Species is not null);

    private static IEnumerable<SlotSummary> Occupied(IReadOnlyList<SlotSummary> slots) =>
        slots.Where(s => s.Species is not null);
}

/// <summary>Grid rectangle arithmetic for a page laid out row-major in <c>columns</c> columns.</summary>
public static class StorageRectangle
{
    public static bool Contains(int from, int to, int slot, int columns)
    {
        if (columns <= 0 || from < 0 || to < 0 || slot < 0) return false;
        var (left, right) = (Math.Min(from % columns, to % columns), Math.Max(from % columns, to % columns));
        var (top, bottom) = (Math.Min(from / columns, to / columns), Math.Max(from / columns, to / columns));
        var (col, row) = (slot % columns, slot / columns);
        return col >= left && col <= right && row >= top && row <= bottom;
    }
}
