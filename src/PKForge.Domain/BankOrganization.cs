namespace PKForge.Domain;

/// <summary>
/// The vault's organizer logic: what a collector filters for, how the slots are ordered, and
/// where rearranged entries land. Filtering and ordering are pure functions of the index record
/// plus the few facts the app already holds; the service applies the resulting placements in a
/// single index write. Nothing here touches a save.
/// </summary>
public interface IBankFacts
{
    /// <summary>Display name of a species id; empty when the id is outside the table.</summary>
    string SpeciesName(int species);

    /// <summary>True while the stored bytes are an unhatched egg - read from the entity itself,
    /// never from the index. Callers should ask for this last: it is the only costly fact.</summary>
    bool IsEgg(BankEntry entry);

    /// <summary>Canonical <see cref="ParkType"/> ids (1-18) of a species/form.</summary>
    IReadOnlyList<int> Types(int species, int form);

    /// <summary>Gender from the stored bytes (0 male, 1 female, 2 genderless), PKHeX's encoding.
    /// Shares the egg probe's single parse.</summary>
    int Gender(BankEntry entry);

    /// <summary>Poké Ball id from the stored bytes (PKHeX's <c>Ball</c> numbering; 0 when the
    /// format carries none). Shares the egg probe's single parse.</summary>
    int Ball(BankEntry entry);

    /// <summary>Held item id (PKHeX national numbering, 0 = none). The index carries it for
    /// entries deposited since the field existed; older entries are read from the bytes.</summary>
    int HeldItem(BankEntry entry) => entry.Info.HeldItem ?? 0;
}

/// <summary>Which rare mons a filter keeps.</summary>
public enum BankRarity
{
    /// <summary>No rarity filter.</summary>
    Any,
    /// <summary>Legendaries only (mythicals excluded, as PKHeX classifies them).</summary>
    Legendary,
    /// <summary>Mythicals only.</summary>
    Mythical,
    /// <summary>Both, for "show me everything rare".</summary>
    LegendaryOrMythical,
}

/// <summary>
/// The collector's filters over the vault, all ANDed. Every field is optional; the default
/// instance keeps everything, which is what RESET restores.
/// </summary>
public sealed record BankFilter
{
    /// <summary>No filter at all: every entry matches.</summary>
    public static readonly BankFilter None = new();

    /// <summary>Free text matched against the species name or the nickname.</summary>
    public string Query { get; init; } = "";
    public bool ShinyOnly { get; init; }
    /// <summary>Source generation of the stored mon (its format), not the game it came from.</summary>
    public int? Generation { get; init; }
    /// <summary>Source game name as captured at deposit time (e.g. "Emerald").</summary>
    public string? SourceName { get; init; }
    /// <summary>A canonical <see cref="ParkType"/> id; either of the mon's types may match.</summary>
    public int? TypeId { get; init; }
    public BankRarity Rarity { get; init; } = BankRarity.Any;
    public bool EggOnly { get; init; }
    /// <summary>Only mons still wearing their species name - the trades-and-gifts view.</summary>
    public bool DefaultNamedOnly { get; init; }
    public int? LevelMin { get; init; }
    public int? LevelMax { get; init; }
    /// <summary>PKHeX gender code (0 male, 1 female, 2 genderless); read from the bytes.</summary>
    public int? Gender { get; init; }
    /// <summary>Only mons holding something (any item).</summary>
    public bool HoldsItemOnly { get; init; }
    /// <summary>Only mons holding exactly this item id (PKHeX national numbering).</summary>
    public int? HeldItemId { get; init; }

    /// <summary>True when anything at all narrows the vault (drives the RESET chip).</summary>
    public bool IsActive =>
        Query.Trim().Length > 0 || ShinyOnly || Generation is not null || SourceName is { Length: > 0 }
        || TypeId is not null || Rarity != BankRarity.Any || EggOnly || DefaultNamedOnly
        || LevelMin is not null || LevelMax is not null || Gender is not null
        || HoldsItemOnly || HeldItemId is not null;

    /// <summary>How many of the optional filters are set, ignoring the text box and the sort.</summary>
    public int ActiveCount
    {
        get
        {
            var count = 0;
            if (ShinyOnly) count++;
            if (Generation is not null) count++;
            if (SourceName is { Length: > 0 }) count++;
            if (TypeId is not null) count++;
            if (Rarity != BankRarity.Any) count++;
            if (EggOnly) count++;
            if (DefaultNamedOnly) count++;
            if (LevelMin is not null || LevelMax is not null) count++;
            if (Gender is not null) count++;
            if (HoldsItemOnly) count++;
            if (HeldItemId is not null) count++;
            return count;
        }
    }

    /// <summary>
    /// True when the entry passes every set filter. Cheap index checks run first and the
    /// byte-level egg probe runs last, so a narrowed view never reads a single stored file.
    /// </summary>
    public bool Matches(BankEntry entry, IBankFacts facts)
    {
        var info = entry.Info;
        if (ShinyOnly && !info.Shiny) return false;
        if (Generation is { } generation && info.Generation != generation) return false;
        if (SourceName is { Length: > 0 } source
            && !string.Equals(info.SourceName, source, StringComparison.OrdinalIgnoreCase)) return false;
        if (LevelMin is { } min && info.Level < min) return false;
        if (LevelMax is { } max && info.Level > max) return false;
        if (Rarity != BankRarity.Any && !MatchesRarity(info.Species)) return false;
        if (TypeId is { } type && !facts.Types(info.Species, info.Form).Contains(type)) return false;

        var speciesName = facts.SpeciesName(info.Species);
        if (DefaultNamedOnly && !IsDefaultNamed(info.Nickname, speciesName)) return false;

        var query = Query.Trim();
        if (query.Length > 0
            && !info.Nickname.Contains(query, StringComparison.OrdinalIgnoreCase)
            && !speciesName.Contains(query, StringComparison.OrdinalIgnoreCase)) return false;

        if (EggOnly && !facts.IsEgg(entry)) return false;
        if (Gender is { } gender && facts.Gender(entry) != gender) return false;
        if (HoldsItemOnly || HeldItemId is not null)
        {
            var item = facts.HeldItem(entry);
            if (HoldsItemOnly && item == 0) return false;
            if (HeldItemId is { } wanted && item != wanted) return false;
        }
        return true;
    }

    private bool MatchesRarity(int species) => Rarity switch
    {
        BankRarity.Legendary => SpeciesCategories.Legendary.Contains(species),
        BankRarity.Mythical => SpeciesCategories.Mythical.Contains(species),
        BankRarity.LegendaryOrMythical =>
            SpeciesCategories.Legendary.Contains(species) || SpeciesCategories.Mythical.Contains(species),
        _ => true,
    };

    /// <summary>A mon with no nickname, or one that is just its species name again.</summary>
    public static bool IsDefaultNamed(string? nickname, string speciesName) =>
        string.IsNullOrWhiteSpace(nickname)
        || string.Equals(nickname.Trim(), speciesName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// How the vault orders its slots. The bank's own axes: deposit date, source generation and
/// nickname live only in its index, while the save-side <see cref="SortCriteria"/> describes
/// editable slot facts (IVs, met date, typing) the vault does not carry. Each sorter's switch
/// stays exhaustive over its own data.
/// </summary>
public enum BankSortOrder
{
    /// <summary>National dex number, then form.</summary>
    DexNumber,
    /// <summary>Species display name, A-Z.</summary>
    SpeciesName,
    /// <summary>Current level, strongest first.</summary>
    LevelDesc,
    /// <summary>Shinies first, then dex number.</summary>
    ShinyFirst,
    /// <summary>Most recently deposited first.</summary>
    NewestAdded,
    /// <summary>Earliest deposit first - the oldest residents.</summary>
    OldestAdded,
    /// <summary>Source generation, then dex number: one era per run.</summary>
    Generation,
    /// <summary>Nickname A-Z.</summary>
    Nickname,
    /// <summary>Primary type, then secondary (mono-types lead their type), in <see cref="ParkType"/> order.</summary>
    Type,
    /// <summary>Source game name A-Z: every game's catches together.</summary>
    SourceGame,
    /// <summary>Legendaries first, then mythicals, then everyone else.</summary>
    Rarity,
    /// <summary>Male, female, genderless (read from the bytes).</summary>
    Gender,
    /// <summary>Poké Ball id, so matching balls sit together (read from the bytes).</summary>
    Ball,
}

/// <summary>Ordering for the vault's slots (pure; the caller writes the result).</summary>
public static class BankSorting
{
    /// <summary>
    /// The vault's order for one scope. Every order falls through to dex number, form, deposit
    /// date and id, so the result is a total order: the same entries always land in the same
    /// slots, and ties never reshuffle a settled bank. <paramref name="reverse"/> flips only the
    /// primary key (Z-A, weakest first, shinies last); the tie-breaks stay ascending so a
    /// reversed sort is just as stable.
    /// </summary>
    public static List<BankEntry> Order(
        IEnumerable<BankEntry> entries, BankSortOrder order, IBankFacts facts, bool reverse = false)
    {
        var names = StringComparer.OrdinalIgnoreCase;
        var ordered = order switch
        {
            BankSortOrder.SpeciesName => By(entries, e => facts.SpeciesName(e.Info.Species), reverse, names),
            BankSortOrder.LevelDesc => By(entries, e => e.Info.Level, !reverse),
            BankSortOrder.ShinyFirst => By(entries, e => e.Info.Shiny, !reverse),
            BankSortOrder.NewestAdded => By(entries, e => e.AddedUtc, !reverse),
            BankSortOrder.OldestAdded => By(entries, e => e.AddedUtc, reverse),
            BankSortOrder.Generation => By(entries, e => e.Info.Generation, reverse),
            BankSortOrder.Nickname => By(entries, e => e.Info.Nickname, reverse, names),
            BankSortOrder.Type => By(entries, e => TypeKey(facts.Types(e.Info.Species, e.Info.Form)), reverse),
            BankSortOrder.SourceGame => By(entries, e => e.Info.SourceName, reverse, names),
            BankSortOrder.Rarity => By(entries, e => RarityRank(e.Info.Species), reverse),
            BankSortOrder.Gender => By(entries, facts.Gender, reverse),
            BankSortOrder.Ball => By(entries, facts.Ball, reverse),
            _ => By(entries, e => e.Info.Species, reverse),
        };
        return ordered
            .ThenBy(e => e.Info.Species)
            .ThenBy(e => e.Info.Form)
            .ThenBy(e => e.AddedUtc)
            .ThenBy(e => e.Id)
            .ToList();
    }

    private static IOrderedEnumerable<BankEntry> By<TKey>(
        IEnumerable<BankEntry> entries, Func<BankEntry, TKey> key, bool descending, IComparer<TKey>? comparer = null) =>
        descending ? entries.OrderByDescending(key, comparer) : entries.OrderBy(key, comparer);

    /// <summary>Primary type then secondary, packed into one key; a mono-type sorts before any
    /// dual-type of its primary, and unknown typings trail the known ones.</summary>
    private static int TypeKey(IReadOnlyList<int> types) => types.Count switch
    {
        0 => int.MaxValue,
        1 => types[0] * 100,
        _ => types[0] * 100 + types[1],
    };

    /// <summary>0 legendary, 1 mythical, 2 everyone else.</summary>
    public static int RarityRank(int species) =>
        SpeciesCategories.Legendary.Contains(species) ? 0
        : SpeciesCategories.Mythical.Contains(species) ? 1
        : 2;
}

/// <summary>Which slots a mark gesture covers: pure arithmetic over the flat vault.</summary>
public static class BankSelection
{
    /// <summary>
    /// Every occupied slot between two positions, inclusive and in either direction, walking
    /// the vault as one flat sequence (box 2 slot 1 follows box 1 slot 30) - the range mark.
    /// </summary>
    public static IReadOnlyList<(int Box, int Slot)> Range(
        (int Box, int Slot) from, (int Box, int Slot) to, IReadOnlyList<BankEntry> all)
    {
        static int Flat((int Box, int Slot) p) => p.Box * IBankService.SlotsPerBox + p.Slot;
        var low = Math.Min(Flat(from), Flat(to));
        var high = Math.Max(Flat(from), Flat(to));
        return all.Where(e => Flat((e.Box, e.Slot)) >= low && Flat((e.Box, e.Slot)) <= high)
            .OrderBy(e => e.Box).ThenBy(e => e.Slot)
            .Select(e => (e.Box, e.Slot))
            .ToList();
    }
}

/// <summary>Where the organizer's entries land: pure slot arithmetic the service then applies.</summary>
public static class BankPlacement
{
    /// <summary>
    /// Moving a selection into a box: the entries take that box's gaps in ascending order, and
    /// an entry already in the box stays where it is (it is not "moved into" anything). When the
    /// box runs out of room the rest keep their slots - the caller reports the leftovers.
    /// </summary>
    public static IReadOnlyList<(Guid Id, int Box, int Slot)> IntoBox(
        IReadOnlyList<Guid> ids, int box, IReadOnlyList<BankEntry> all)
    {
        var byId = all.ToDictionary(e => e.Id);
        var occupied = all.Where(e => e.Box == box).Select(e => e.Slot).ToHashSet();
        var placements = new List<(Guid Id, int Box, int Slot)>();
        var next = 0;
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var entry) || entry.Box == box) continue;
            while (next < IBankService.SlotsPerBox && occupied.Contains(next)) next++;
            if (next >= IBankService.SlotsPerBox) break;
            placements.Add((id, box, next));
            occupied.Add(next);
        }
        return placements;
    }

    /// <summary>
    /// Inserting a selection at a chosen position: the entries take consecutive slots from
    /// <paramref name="startSlot"/>, and the box's other entries keep their relative order in
    /// the remaining slots (so everything before the insertion point stays put and the rest
    /// slides back). Selection members already in the box are not duplicated, and entries that
    /// cannot fit are left behind. Returns the box's whole new layout.
    /// </summary>
    public static IReadOnlyList<(Guid Id, int Box, int Slot)> InsertRun(
        IReadOnlyList<Guid> ids, int box, int startSlot, IReadOnlyList<BankEntry> all)
    {
        var byId = all.ToDictionary(e => e.Id);
        var start = Math.Clamp(startSlot, 0, IBankService.SlotsPerBox - 1);
        var run = ids.Where(id => byId.ContainsKey(id)).Distinct().Take(IBankService.SlotsPerBox - start).ToList();
        var runIds = run.ToHashSet();
        var rest = all.Where(e => e.Box == box && !runIds.Contains(e.Id))
            .OrderBy(e => e.Slot).ThenBy(e => e.AddedUtc).ThenBy(e => e.Id)
            .Select(e => e.Id)
            .ToList();
        // The displaced entries only have the slots the run does not take; a full box fills
        // them exactly, so the run has to be trimmed to whatever room that leaves.
        var room = IBankService.SlotsPerBox - start - rest.Count;
        var fit = Math.Max(0, Math.Min(run.Count, room));
        if (fit == 0) return []; // nothing fits: the box stays exactly as it is
        var runSlots = Enumerable.Range(start, fit).ToHashSet();
        var free = Enumerable.Range(0, IBankService.SlotsPerBox).Where(s => !runSlots.Contains(s)).ToList();

        var placements = new List<(Guid Id, int Box, int Slot)>(fit + rest.Count);
        for (var i = 0; i < fit; i++) placements.Add((run[i], box, start + i));
        for (var i = 0; i < rest.Count && i < free.Count; i++) placements.Add((rest[i], box, free[i]));
        return placements;
    }

    /// <summary>
    /// The compacting reorder a sort writes: the ordered entries land in the scope's slots in
    /// order - one box, or the whole vault from box 1 slot 1 when <paramref name="box"/> is null.
    /// </summary>
    public static IReadOnlyList<(Guid Id, int Box, int Slot)> Reorder(int? box, IReadOnlyList<BankEntry> ordered)
    {
        var firstBox = box ?? 0;
        var placements = new List<(Guid Id, int Box, int Slot)>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
            placements.Add((ordered[index].Id, firstBox + index / IBankService.SlotsPerBox, index % IBankService.SlotsPerBox));
        return placements;
    }

    /// <summary>How many of the placements actually land somewhere new - what a sort will move.</summary>
    public static int ChangedCount(
        IReadOnlyList<(Guid Id, int Box, int Slot)> placements, IReadOnlyList<BankEntry> all)
    {
        var byId = all.ToDictionary(e => e.Id);
        var changed = 0;
        foreach (var (id, box, slot) in placements)
            if (byId.TryGetValue(id, out var entry) && (entry.Box != box || entry.Slot != slot)) changed++;
        return changed;
    }
}
