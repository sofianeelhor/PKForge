namespace PKForge.Domain;

/// <summary>
/// A named shelf of Bank boxes ("Shiny", "Living dex"). A box sits in one group at most;
/// boxes stay a flat numbered sequence, groups only gather them. <paramref name="Boxes"/> are
/// box indices in ascending order.
/// </summary>
public sealed record BankBoxGroup(string Name, IReadOnlyList<int> Boxes);

/// <summary>One run of the box overview: a group (or the ungrouped boxes, Name null) and its boxes.</summary>
public sealed record BankBoxSection(string? Name, IReadOnlyList<int> Boxes);

/// <summary>
/// The pure rules of box groups. Every operation returns a new, normalized list (names trimmed
/// and unique ignoring case, each box in one group at most, boxes sorted), so whatever the
/// index holds, the app only ever sees a tidy set. Group order is the order they were made in.
/// </summary>
public static class BankBoxGroups
{
    public const int MaxNameLength = 24;

    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    /// <summary>Tidies any list (as read from disk): blank names dropped, duplicate names
    /// merged into the first, a box claimed twice kept by the first group, negative boxes and,
    /// when <paramref name="boxCount"/> is given, boxes past the end dropped.</summary>
    public static IReadOnlyList<BankBoxGroup> Normalize(IEnumerable<BankBoxGroup>? groups, int? boxCount = null)
    {
        var result = new List<(string Name, SortedSet<int> Boxes)>();
        var claimed = new HashSet<int>();
        foreach (var group in groups ?? [])
        {
            if (group?.Name is null) continue;
            var name = Clean(group.Name);
            if (name.Length == 0) continue;
            var at = result.FindIndex(g => Names.Equals(g.Name, name));
            if (at < 0) { result.Add((name, [])); at = result.Count - 1; }
            foreach (var box in group.Boxes ?? [])
            {
                if (box < 0 || (boxCount is { } count && box >= count) || !claimed.Add(box)) continue;
                result[at].Boxes.Add(box);
            }
        }
        return [.. result.Select(g => new BankBoxGroup(g.Name, [.. g.Boxes]))];
    }

    /// <summary>A group name as stored: trimmed, inner runs of space folded, at most 24 characters.</summary>
    public static string Clean(string name)
    {
        var folded = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return folded.Length > MaxNameLength ? folded[..MaxNameLength].TrimEnd() : folded;
    }

    public static bool Exists(IReadOnlyList<BankBoxGroup> groups, string name) =>
        groups.Any(g => Names.Equals(g.Name, Clean(name)));

    /// <summary>The group <paramref name="box"/> sits in, or null.</summary>
    public static string? GroupOf(IReadOnlyList<BankBoxGroup> groups, int box) =>
        groups.FirstOrDefault(g => g.Boxes.Contains(box))?.Name;

    /// <summary>The boxes of the group named <paramref name="name"/> (empty when there is none).</summary>
    public static IReadOnlyList<int> Members(IReadOnlyList<BankBoxGroup> groups, string name) =>
        groups.FirstOrDefault(g => Names.Equals(g.Name, name))?.Boxes ?? [];

    /// <summary>Adds an empty group at the end, optionally holding <paramref name="boxes"/> at once.</summary>
    public static IReadOnlyList<BankBoxGroup> Create(IReadOnlyList<BankBoxGroup> groups, string name, params int[] boxes)
    {
        var clean = Clean(name);
        if (clean.Length == 0) throw new ArgumentException("A group needs a name.", nameof(name));
        if (Exists(groups, clean)) throw new InvalidOperationException($"There is already a group called \"{clean}\".");
        var freed = groups.Select(g => g with { Boxes = [.. g.Boxes.Except(boxes)] });
        return Normalize([.. freed, new BankBoxGroup(clean, boxes)]);
    }

    /// <summary>Renames a group; its boxes stay. Renaming to its own name in another case is allowed.</summary>
    public static IReadOnlyList<BankBoxGroup> Rename(IReadOnlyList<BankBoxGroup> groups, string name, string newName)
    {
        var clean = Clean(newName);
        if (clean.Length == 0) throw new ArgumentException("A group needs a name.", nameof(newName));
        if (!Exists(groups, name)) throw new InvalidOperationException($"There is no group called \"{name}\".");
        if (!Names.Equals(Clean(name), clean) && Exists(groups, clean))
            throw new InvalidOperationException($"There is already a group called \"{clean}\".");
        return Normalize(groups.Select(g => Names.Equals(g.Name, name) ? g with { Name = clean } : g));
    }

    /// <summary>Deletes a group. Its boxes and their Pokémon stay where they are, just ungrouped.</summary>
    public static IReadOnlyList<BankBoxGroup> Delete(IReadOnlyList<BankBoxGroup> groups, string name) =>
        Normalize(groups.Where(g => !Names.Equals(g.Name, name)));

    /// <summary>Puts <paramref name="box"/> in the group named <paramref name="name"/> (taking it
    /// out of any other), or out of every group when <paramref name="name"/> is null.</summary>
    public static IReadOnlyList<BankBoxGroup> Assign(IReadOnlyList<BankBoxGroup> groups, int box, string? name)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(box);
        if (name is not null && !Exists(groups, name)) throw new InvalidOperationException($"There is no group called \"{name}\".");
        return Normalize(groups.Select(g =>
        {
            var without = g.Boxes.Where(b => b != box);
            return g with { Boxes = name is not null && Names.Equals(g.Name, name) ? [.. without, box] : [.. without] };
        }));
    }

    /// <summary>Groups follow their boxes through a remap; a deleted box leaves its group, the group stays.</summary>
    public static IReadOnlyList<BankBoxGroup> Remap(IReadOnlyList<BankBoxGroup> groups, BankBoxRemap remap)
    {
        ArgumentNullException.ThrowIfNull(remap);
        return Normalize(groups.Select(g => g with { Boxes = [.. g.Boxes.Select(remap.Map).OfType<int>()] }), remap.NewCount);
    }

    /// <summary>
    /// The box <paramref name="delta"/> steps from <paramref name="box"/> among <paramref name="members"/>,
    /// wrapping at either end. A box outside the members steps to the nearest member that way.
    /// Null when there are no members.
    /// </summary>
    public static int? Step(IReadOnlyList<int> members, int box, int delta)
    {
        if (members.Count == 0) return null;
        var at = members.ToList().IndexOf(box);
        if (at < 0)
        {
            if (delta >= 0) return members.FirstOrDefault(b => b > box, members[0]);
            return members.LastOrDefault(b => b < box, members[^1]);
        }
        var next = ((at + delta) % members.Count + members.Count) % members.Count;
        return members[next];
    }

    /// <summary>The overview's runs: every group in order (empty ones too), then the boxes in
    /// no group (Name null, left out when there are none). Without groups, one run of all boxes.</summary>
    public static IReadOnlyList<BankBoxSection> Sections(IReadOnlyList<BankBoxGroup> groups, int boxCount)
    {
        var sections = groups.Select(g => new BankBoxSection(g.Name, [.. g.Boxes.Where(b => b < boxCount)])).ToList();
        var grouped = groups.SelectMany(g => g.Boxes).ToHashSet();
        var rest = Enumerable.Range(0, boxCount).Where(b => !grouped.Contains(b)).ToList();
        if (rest.Count > 0 || sections.Count == 0) sections.Add(new BankBoxSection(null, rest));
        return sections;
    }
}
