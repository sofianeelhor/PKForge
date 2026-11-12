namespace PKForge.Domain;

/// <summary>
/// Bank box locks, named like the save-side box manager's ("Lock / Unlock box"): a locked box
/// is a finished shelf. It can be opened and its Pokémon looked at (summary, editor, export),
/// but nothing is moved into or out of it - no sort, compaction, bulk move, carry-and-drop,
/// release, send to a game or auto-placed deposit touches it. These are the pure rules; the
/// bank service enforces them again on every write so no caller can slip past a lock.
/// </summary>
public static class BankBoxLocks
{
    /// <summary>What the user is told when a write would touch a locked box.</summary>
    public static string Refusal(int box) => $"Box {box + 1:00} is locked. Unlock it from the bank's box actions first.";

    /// <summary>Splits a selection into the entries an organizer action may touch and how many
    /// it has to leave alone because their box is locked.</summary>
    public static (List<BankEntry> Free, int Locked) Split(IEnumerable<BankEntry> entries, IReadOnlySet<int> locked)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var free = new List<BankEntry>();
        var held = 0;
        foreach (var entry in entries)
        {
            if (locked.Contains(entry.Box)) held++;
            else free.Add(entry);
        }
        return (free, held);
    }

    /// <summary>
    /// The first locked box a batch of placements would take an entry out of or put one into,
    /// or null when the batch stays clear of every lock. Entries already where they are placed
    /// do not count: a sort that leaves a locked box exactly as it is does not touch it.
    /// </summary>
    public static int? FirstTouched(
        IReadOnlyList<(Guid Id, int Box, int Slot)> placements, IReadOnlyList<BankEntry> all, IReadOnlySet<int> locked)
    {
        if (locked.Count == 0) return null;
        var byId = all.ToDictionary(e => e.Id);
        foreach (var (id, box, slot) in placements)
        {
            if (!byId.TryGetValue(id, out var entry) || (entry.Box == box && entry.Slot == slot)) continue;
            if (locked.Contains(entry.Box)) return entry.Box;
            if (locked.Contains(box)) return box;
        }
        return null;
    }

    /// <summary>Locks follow their boxes through a remap; a deleted box's lock goes with it.</summary>
    public static HashSet<int> Remap(IReadOnlySet<int> locked, BankBoxRemap remap)
    {
        ArgumentNullException.ThrowIfNull(remap);
        return [.. locked.Select(remap.Map).OfType<int>()];
    }
}
