namespace PKForge.Domain;

/// <summary>Which saves the library lists.</summary>
public enum SaveLibraryFilter { All, OnShelf, Hidden, Copies }

/// <summary>
/// One save file as the library shows it. <paramref name="Title"/> is the game as the player
/// named it; <paramref name="Who"/> the trainer, play time and emulator; <paramref name="Where"/>
/// the folder, file and when it last changed; <paramref name="Tag"/> why it is off the shelf, if it is.
/// </summary>
public sealed record SaveLibraryEntry(DetectedSave Save, string Title, string Who, string Where, string Tag)
{
    public bool OnShelf => Save.Identity?.IsHidden != true;
}

/// <summary>One playthrough in the library: the file shown, and its other versions (copies), newest first.</summary>
public sealed record SaveLibraryGroup(SaveLibraryEntry Lead, IReadOnlyList<SaveLibraryEntry> Versions);

/// <summary>
/// The library of every save PKForge knows: those on the Home shelf, those the player hid
/// and the backup copies set aside, in one list. Home shows one cartridge per game; this
/// is where each file is told apart, kept, and put back.
/// </summary>
public static class SaveLibrary
{
    /// <summary>
    /// Every save as a library entry: by game, the most recently played first within a game,
    /// and a game's copies after its real saves.
    /// </summary>
    public static IReadOnlyList<SaveLibraryEntry> Build(IEnumerable<DetectedSave> saves, DateTimeOffset now, Func<EmulatorKind, string> emulatorName)
    {
        ArgumentNullException.ThrowIfNull(saves);
        ArgumentNullException.ThrowIfNull(emulatorName);
        return
        [
            .. saves
                .OrderBy(save => save.Identity?.GameLabel ?? save.GameLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(save => save.IsCopy)
                .ThenByDescending(save => save.LastModified ?? DateTimeOffset.MinValue)
                .Select(save => new SaveLibraryEntry(save, save.GameLabel, Who(save, emulatorName), Where(save, now), TagOf(save))),
        ];
    }

    /// <summary>
    /// The entries gathered by playthrough: files of one game with the same trainer name and
    /// trainer ID are the same adventure (the save and its copies), shown as one entry. The
    /// lead is the newest real save (the newest copy when there is none), and the others its
    /// versions, newest first. A trainer ID unknown on one side does not split a group, but
    /// two different known IDs always do. A save without a trainer name stands alone.
    /// Groups keep the order of their leads in <paramref name="entries"/>.
    /// </summary>
    public static IReadOnlyList<SaveLibraryGroup> Group(IEnumerable<SaveLibraryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var all = entries.ToList();
        var buckets = new List<List<SaveLibraryEntry>>();
        foreach (var entry in all)
        {
            var bucket = buckets.FirstOrDefault(members => members.All(member => SamePlaythrough(member.Save, entry.Save)));
            if (bucket is null) buckets.Add([entry]);
            else bucket.Add(entry);
        }
        return
        [
            .. buckets
                .Select(members =>
                {
                    var newest = members.OrderByDescending(member => member.Save.LastModified ?? DateTimeOffset.MinValue).ToList();
                    var lead = newest.FirstOrDefault(member => !member.Save.IsCopy) ?? newest[0];
                    return new SaveLibraryGroup(lead, [.. newest.Where(member => !ReferenceEquals(member, lead))]);
                })
                .OrderBy(group => all.IndexOf(group.Lead)),
        ];
    }

    /// <summary>Whether two files are the same adventure: one game, one trainer name, one trainer ID.</summary>
    public static bool SamePlaythrough(DetectedSave first, DetectedSave second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (string.IsNullOrWhiteSpace(first.TrainerName) || string.IsNullOrWhiteSpace(second.TrainerName)) return false;
        return first.Generation == second.Generation
            && string.Equals(first.Identity?.GameLabel ?? first.GameLabel, second.Identity?.GameLabel ?? second.GameLabel, StringComparison.OrdinalIgnoreCase)
            // In-game names are case-sensitive: "RED" and "Red" are two trainers.
            && string.Equals(first.TrainerName!.Trim(), second.TrainerName!.Trim(), StringComparison.Ordinal)
            && (first.TrainerId is null || second.TrainerId is null || first.TrainerId == second.TrainerId);
    }

    public static IEnumerable<SaveLibraryEntry> Apply(IEnumerable<SaveLibraryEntry> entries, SaveLibraryFilter filter) => filter switch
    {
        SaveLibraryFilter.OnShelf => entries.Where(entry => entry.OnShelf),
        SaveLibraryFilter.Hidden => entries.Where(entry => !entry.OnShelf),
        SaveLibraryFilter.Copies => entries.Where(entry => entry.Save.IsCopy),
        _ => entries,
    };

    public static string Label(SaveLibraryFilter filter) => filter switch
    {
        SaveLibraryFilter.OnShelf => "On Home",
        SaveLibraryFilter.Hidden => "Hidden",
        SaveLibraryFilter.Copies => "Backup copies",
        _ => "All",
    };

    private static string Who(DetectedSave save, Func<EmulatorKind, string> emulatorName)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(save.TrainerName)) parts.Add(save.TrainerName!);
        if (!string.IsNullOrWhiteSpace(save.PlayTime)) parts.Add(save.PlayTime!);
        parts.Add(emulatorName(save.Emulator));
        return string.Join(" · ", parts);
    }

    private static string Where(DetectedSave save, DateTimeOffset now)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(save.FolderHint)) parts.Add(save.FolderHint!);
        parts.Add(save.FileName);
        if (save.LastModified is { } modified) parts.Add($"changed {RestorePoints.When(modified, now)}");
        return string.Join(" · ", parts);
    }

    private static string TagOf(DetectedSave save) =>
        save.Identity?.IsHidden == true
            ? save.IsCopy ? "Backup copy" : "Hidden"
            : save.IsCopy ? "Copy on Home" : "";

    // ── Snapshots ───────────────────────────────────────────────────────────
    // A snapshot is a restore point the player made on purpose and named: pinned, so it
    // never leaves to make room, and recognised by the sentence it records.

    private const string SnapshotPrefix = "Snapshot: ";

    public static string SnapshotDescription(string name) => SnapshotPrefix + (string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim());

    public static bool IsSnapshot(string? description) => description?.StartsWith(SnapshotPrefix, StringComparison.Ordinal) == true;

    /// <summary>The name the player gave a snapshot, or null when <paramref name="description"/> is not one.</summary>
    public static string? SnapshotName(string? description) => IsSnapshot(description) ? description![SnapshotPrefix.Length..] : null;

    /// <summary>The snapshots kept of the file <paramref name="documentId"/>, newest first.</summary>
    public static IReadOnlyList<BackupInfo> SnapshotsOf(IEnumerable<BackupInfo> points, string documentId)
    {
        ArgumentNullException.ThrowIfNull(points);
        return
        [
            .. points
                .Where(point => IsSnapshot(point.ChangeDescription) && string.Equals(point.DocumentId, documentId, StringComparison.Ordinal))
                .OrderByDescending(point => point.CreatedUtc),
        ];
    }
}

/// <summary>What a save holds, read without opening it for editing.</summary>
public sealed record SaveInsight(
    string Trainer, int TrainerId, uint Money, string? PlayTime,
    IReadOnlyList<int> Party, int Boxed, int Shiny, int Eggs,
    int DexSeen, int DexCaught, int DexTotal,
    // The party as slots (species, form, shininess), for drawing it.
    IReadOnlyList<SlotSummary>? PartyMembers = null,
    // The gym badges, where the game keeps them.
    SaveBadges? Badges = null);

/// <summary>A region's gym badges, in the order the save stores them.</summary>
public enum BadgeSet { Kanto, JohtoKanto, Hoenn, Sinnoh, Unova, Unova2, Kalos, Galar }

/// <summary>
/// The gym badges a save holds: how many of how many, and their names when the save says
/// which (it stores one bit per badge); empty when it only lets a count be read.
/// </summary>
public sealed record SaveBadges(int Earned, int Total, IReadOnlyList<string> Names)
{
    private static readonly string[] Kanto = ["Boulder", "Cascade", "Thunder", "Rainbow", "Soul", "Marsh", "Volcano", "Earth"];
    private static readonly string[] Johto = ["Zephyr", "Hive", "Plain", "Fog", "Storm", "Mineral", "Glacier", "Rising"];

    public static IReadOnlyList<string> NamesOf(BadgeSet set) => set switch
    {
        BadgeSet.Kanto => Kanto,
        BadgeSet.JohtoKanto => [.. Johto, .. Kanto],
        BadgeSet.Hoenn => ["Stone", "Knuckle", "Dynamo", "Heat", "Balance", "Feather", "Mind", "Rain"],
        BadgeSet.Sinnoh => ["Coal", "Forest", "Cobble", "Fen", "Relic", "Mine", "Icicle", "Beacon"],
        BadgeSet.Unova => ["Trio", "Basic", "Insect", "Bolt", "Quake", "Jet", "Freeze", "Legend"],
        BadgeSet.Unova2 => ["Basic", "Toxic", "Insect", "Bolt", "Quake", "Jet", "Legend", "Wave"],
        BadgeSet.Kalos => ["Bug", "Cliff", "Rumble", "Plant", "Voltage", "Fairy", "Psychic", "Iceberg"],
        // The fourth and sixth gyms differ by version (Fighting or Ghost, Rock or Ice).
        BadgeSet.Galar => ["Grass", "Water", "Fire", "Fighting/Ghost", "Fairy", "Rock/Ice", "Dark", "Dragon"],
        _ => [],
    };

    /// <summary>The badges whose bits are set in <paramref name="mask"/>, bit 0 the first badge.</summary>
    public static SaveBadges FromMask(BadgeSet set, int mask)
    {
        var names = NamesOf(set);
        var earned = names.Where((_, i) => (mask & (1 << i)) != 0).ToList();
        return new SaveBadges(earned.Count, names.Count, earned);
    }

    /// <summary>A save that only lets the count be read.</summary>
    public static SaveBadges Counted(int earned, BadgeSet set)
    {
        var total = NamesOf(set).Count;
        return new SaveBadges(Math.Clamp(earned, 0, total), total, []);
    }

    /// <summary>"Badges: 3 of 8 (Boulder, Cascade, Thunder)".</summary>
    public string Line => Names.Count == 0 || Names.Count == Total
        ? $"Badges: {Earned} of {Total}" + (Earned == Total && Total > 0 ? ", all of them" : "")
        : $"Badges: {Earned} of {Total} ({string.Join(", ", Names)})";
}

/// <summary>Two saves of one game side by side: what each has that the other lacks.</summary>
public sealed record SaveComparison(IReadOnlyList<SlotSummary> OnlyInFirst, IReadOnlyList<SlotSummary> OnlyInSecond, int InBoth);

public static class SaveInsights
{
    /// <summary>Counts what a save's slots hold. The party is box -1.</summary>
    public static SaveInsight From(SaveSnapshot snapshot, string trainer, int trainerId, uint money, string? playTime,
        int dexSeen, int dexCaught, int dexTotal)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var held = snapshot.Slots.Where(slot => slot.Species is not null).ToList();
        return new SaveInsight(trainer, trainerId, money, playTime,
            [.. held.Where(slot => slot.Box == -1).OrderBy(slot => slot.Slot).Select(slot => slot.Species!.Value)],
            held.Count(slot => slot.Box >= 0),
            held.Count(slot => slot.IsShiny && !slot.IsEgg),
            held.Count(slot => slot.IsEgg),
            dexSeen, dexCaught, dexTotal,
            [.. held.Where(slot => slot.Box == -1).OrderBy(slot => slot.Slot)]);
    }

    /// <summary>The insight as the lines of a save's details.</summary>
    public static IReadOnlyList<string> Lines(SaveInsight insight, Func<int, string> speciesName)
    {
        ArgumentNullException.ThrowIfNull(insight);
        ArgumentNullException.ThrowIfNull(speciesName);
        var lines = new List<string>
        {
            $"Trainer {insight.Trainer} · ID {insight.TrainerId:00000}" + (insight.PlayTime is { Length: > 0 } time ? $" · {time} played" : ""),
            insight.Party.Count == 0 ? "Party: empty" : $"Party: {string.Join(", ", insight.Party.Select(speciesName))}",
            $"{insight.Boxed} Pokémon in the boxes" + (insight.Shiny > 0 ? $" · {insight.Shiny} shiny" : "") + (insight.Eggs > 0 ? $" · {insight.Eggs} {(insight.Eggs == 1 ? "egg" : "eggs")}" : ""),
        };
        if (insight.Badges is { Total: > 0 } badges) lines.Add(badges.Line);
        if (insight.DexTotal > 0) lines.Add($"Pokédex: {insight.DexCaught} caught, {insight.DexSeen} seen of {insight.DexTotal}");
        // The app speaks English whatever the device's language: 3,000, never 3 000.
        if (insight.Money > 0) lines.Add($"Money: {insight.Money.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}");
        return lines;
    }

    /// <summary>
    /// What each of two saves holds that the other does not. A Pokémon is told by species,
    /// form, shininess and name, so two unnamed Pidgey count as two of the same and pair off.
    /// </summary>
    public static SaveComparison Compare(SaveSnapshot first, SaveSnapshot second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        static (int, int, bool, string?) Key(SlotSummary slot) => (slot.Species!.Value, slot.Form, slot.IsShiny, slot.Nickname);
        var left = first.Slots.Where(slot => slot.Species is not null && !slot.IsEgg).ToList();
        var right = second.Slots.Where(slot => slot.Species is not null && !slot.IsEgg).ToList();
        var unmatched = right.GroupBy(Key).ToDictionary(group => group.Key, group => new Queue<SlotSummary>(group));
        var onlyFirst = new List<SlotSummary>();
        var both = 0;
        foreach (var slot in left)
        {
            if (unmatched.TryGetValue(Key(slot), out var twins) && twins.Count > 0) { twins.Dequeue(); both++; }
            else onlyFirst.Add(slot);
        }
        return new SaveComparison(onlyFirst, [.. unmatched.Values.SelectMany(queue => queue)], both);
    }

    /// <summary>
    /// A name for a copy that replaces nothing in <paramref name="existing"/>: the name itself
    /// when it is free, else "name (copy).ext", "name (copy 2).ext"…
    /// </summary>
    public static string FreeFileName(string fileName, IEnumerable<string> existing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(fileName)) return fileName;
        var dot = fileName.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (fileName[..dot], fileName[dot..]) : (fileName, "");
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? $"{stem} (copy){extension}" : $"{stem} (copy {n}){extension}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
