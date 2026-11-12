namespace PKForge.Domain;

/// <summary>Where one Pokémon of the collection lives: a Bank slot or a save's box/party slot.</summary>
/// <param name="SourceId">The save's document id, or <see cref="LivingDexPlanner.BankId"/>.</param>
/// <param name="Box">Box index; -1 is the party.</param>
/// <param name="BankId">The Bank entry's id; null for a save slot.</param>
public sealed record DexHolder(string SourceId, string SourceLabel, int Box, int Slot, Guid? BankId = null)
{
    public bool IsBank => BankId is not null;

    /// <summary>"Bank · box 03 slot 12", "Emerald · party 2".</summary>
    public string Describe() => Box < 0
        ? $"{SourceLabel} · party {Slot + 1}"
        : $"{SourceLabel} · box {Box + 1:00} slot {Slot + 1:00}";
}

/// <summary>
/// One Pokémon as the dex modes read it.
/// </summary>
/// <param name="OriginGeneration">The generation of the game it was obtained in (PKHeX's
/// <c>PKM.Generation</c>; Virtual Console Gen I/II count as 1/2). 0 when unknown, and for
/// Pokémon GO, which has no generation of its own: those never count for the Origin Dex.</param>
/// <param name="OriginGame">The origin game's name, for display ("Pokémon Red").</param>
/// <param name="FormArgument">Alcremie's sweet (0-6); 0 for everything else.</param>
public sealed record DexMon(
    int Species,
    int Form,
    bool Shiny,
    DexHolder Where,
    int OriginGeneration = 0,
    string OriginGame = "",
    bool Female = false,
    bool Gigantamax = false,
    int FormArgument = 0,
    bool IsEgg = false);

// ─────────────────────────────────────────────────────────────────────────────
// Origin Dex
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One species of the Origin Dex.</summary>
/// <param name="Generation">The species' introduction generation: its native era.</param>
/// <param name="Native">Pokémon of this species obtained in a game of that generation.</param>
/// <param name="Elsewhere">Pokémon of this species from any other generation (they do not count).</param>
public sealed record OriginDexEntry(int Species, int Generation, IReadOnlyList<DexMon> Native, IReadOnlyList<DexMon> Elsewhere)
{
    public bool Owned => Native.Count > 0;
    public bool ShinyOwned => Native.Any(m => m.Shiny);

    /// <summary>Owned, but never from its own era: the species to re-catch in an older game.</summary>
    public bool OnlyElsewhere => Native.Count == 0 && Elsewhere.Count > 0;
}

/// <summary>One generation's slice of the Origin Dex.</summary>
/// <param name="ElsewhereOnly">Species owned only from other generations.</param>
public sealed record OriginGenSegment(int Generation, int Native, int NativeShiny, int ElsewhereOnly, int Total);

public sealed record OriginDexReport(IReadOnlyList<OriginDexEntry> Entries, IReadOnlyList<OriginGenSegment> Segments)
{
    public int Total => Entries.Count;
    public int Native => Entries.Count(e => e.Owned);
    public int NativeShiny => Entries.Count(e => e.ShinyOwned);
    public IEnumerable<OriginDexEntry> Missing => Entries.Where(e => !e.Owned);
}

/// <summary>
/// The Origin Dex: a living dex where a species counts only through a Pokémon obtained in a
/// game of the species' own introduction generation - a Bulbasaur from Red/Blue/Yellow (or
/// their Virtual Console releases), a Treecko from a Gen III game, a Sprigatito from a Gen IX
/// game. Precisely: <c>OriginGeneration == IntroGeneration(Species)</c>. Eggs never count; a
/// Pokémon whose origin is unknown or Pokémon GO never counts. Pure: no I/O.
/// </summary>
public static class OriginDex
{
    /// <summary>The generation that introduced <paramref name="species"/>; 0 outside the national dex.</summary>
    public static int IntroGeneration(int species)
    {
        foreach (var (generation, first, last) in CollectionDex.GenRanges)
            if (species >= first && species <= last) return generation;
        return 0;
    }

    public static bool IsNative(DexMon mon) =>
        !mon.IsEgg && mon.OriginGeneration > 0 && mon.OriginGeneration == IntroGeneration(mon.Species);

    /// <summary>Builds the report for <paramref name="species"/> (valid national ids; order is kept).</summary>
    public static OriginDexReport Compute(IEnumerable<DexMon> collection, IEnumerable<int> species)
    {
        var native = new Dictionary<int, List<DexMon>>();
        var elsewhere = new Dictionary<int, List<DexMon>>();
        foreach (var mon in collection)
        {
            if (mon.IsEgg || mon.Species <= 0) continue;
            var bucket = IsNative(mon) ? native : elsewhere;
            if (!bucket.TryGetValue(mon.Species, out var list)) bucket[mon.Species] = list = [];
            list.Add(mon);
        }

        var entries = new List<OriginDexEntry>();
        foreach (var id in species.Distinct())
        {
            var generation = IntroGeneration(id);
            if (generation == 0) continue;
            entries.Add(new OriginDexEntry(id, generation, Ordered(native.GetValueOrDefault(id)), Ordered(elsewhere.GetValueOrDefault(id))));
        }

        var segments = CollectionDex.GenRanges
            .Select(range =>
            {
                var slice = entries.Where(e => e.Generation == range.Generation).ToList();
                return new OriginGenSegment(range.Generation, slice.Count(e => e.Owned), slice.Count(e => e.ShinyOwned),
                    slice.Count(e => e.OnlyElsewhere), slice.Count);
            })
            .ToList();
        return new OriginDexReport(entries, segments);
    }

    /// <summary>Candidates in the order the player reaches them: the Bank first, then each save by name, box, slot.</summary>
    internal static IReadOnlyList<DexMon> Ordered(IEnumerable<DexMon>? mons) => mons is null ? [] :
        [.. mons.OrderBy(m => m.Where.IsBank ? 0 : 1)
            .ThenBy(m => m.Where.SourceLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Where.Box)
            .ThenBy(m => m.Where.Slot)];
}

// ─────────────────────────────────────────────────────────────────────────────
// Extended Living Dex
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Which kinds of entries an Extended Living Dex counts beyond one per species.</summary>
[Flags]
public enum DexFormCategories
{
    None = 0,
    /// <summary>Alolan, Galarian, Hisuian and Paldean forms.</summary>
    Regional = 1,
    /// <summary>Every other collectible form: Unown letters, Vivillon patterns, Alcremie creams...</summary>
    OtherForms = 2,
    /// <summary>A female entry for species whose female looks different (Pikachu, Hippowdon...).</summary>
    GenderDifferences = 4,
    /// <summary>A Gigantamax-capable entry for each species that has one.</summary>
    Gigantamax = 8,
    /// <summary>Alcremie's seven sweets on every cream.</summary>
    AlcremieSweets = 16,
    Standard = Regional | OtherForms,
    All = Regional | OtherForms | GenderDifferences | Gigantamax | AlcremieSweets,
}

/// <summary>What sets one entry apart from its species' base form.</summary>
public enum DexSlotVariant { Base, Female, Gigantamax, Sweet }

/// <summary>One entry of the Extended Living Dex.</summary>
/// <param name="Sweet">For <see cref="DexSlotVariant.Sweet"/> (and an Alcremie base while sweets count): the sweet, 0-6.</param>
public readonly record struct DexSlot(int Species, int Form, DexSlotVariant Variant = DexSlotVariant.Base, int Sweet = 0);

/// <summary>A collectible form of a species, as the game tables list it.</summary>
public sealed record DexFormInfo(int Form, string Name = "", bool Regional = false);

/// <summary>Everything the Extended Living Dex needs to know about a species.</summary>
/// <param name="Forms">The collectible forms, form 0 included; empty = form 0 only.</param>
/// <param name="GenderDifference">The female looks different (base form).</param>
/// <param name="GigantamaxForms">Forms with their own Gigantamax look.</param>
/// <param name="Sweets">How many sweets each form has (Alcremie: 7), 0 otherwise.</param>
public sealed record DexSpeciesForms(
    int Species,
    IReadOnlyList<DexFormInfo> Forms,
    bool GenderDifference = false,
    IReadOnlyList<int>? GigantamaxForms = null,
    int Sweets = 0);

/// <summary>One entry of the report: the slot, which toggle brought it in, and the Pokémon that fills it.</summary>
/// <param name="Category">The category that adds this entry; <see cref="DexFormCategories.None"/> for a species' base entry.</param>
/// <param name="Label">"Galar", "♀", "Gigantamax", "Strawberry Sweet"..., "" for the base entry.</param>
public sealed record ExtendedDexCell(DexSlot Slot, DexFormCategories Category, string Label, DexMon? Filled)
{
    public bool Owned => Filled is not null;
}

public sealed record ExtendedDexReport(IReadOnlyList<ExtendedDexCell> Cells, DexFormCategories Categories, bool Shiny)
{
    public int Total => Cells.Count;
    public int Owned => Cells.Count(c => c.Owned);

    /// <summary>Owned and total for the entries one category adds (None: the base entries).</summary>
    public (int Owned, int Total) Tally(DexFormCategories category)
    {
        var slice = Cells.Where(c => c.Category == category).ToList();
        return (slice.Count(c => c.Owned), slice.Count);
    }

    /// <summary>Owned and total per introduction generation of the species.</summary>
    public IReadOnlyList<(int Generation, int Owned, int Total)> ByGeneration() =>
        [.. CollectionDex.GenRanges.Select(r =>
        {
            var slice = Cells.Where(c => c.Slot.Species >= r.First && c.Slot.Species <= r.Last).ToList();
            return (r.Generation, slice.Count(c => c.Owned), slice.Count);
        })];
}

/// <summary>
/// The Extended Living Dex: one entry per species plus, per toggle, every regional form, every
/// other collectible form, a female for visible gender differences, a Gigantamax-capable one
/// and each Alcremie sweet. Each Pokémon fills at most one entry; the most specific entry wins
/// first (a female fills the ♀ entry before the base one), and a form that is not counted
/// (regional forms off) still fills its species' base entry. Pure: no I/O.
/// </summary>
public static class ExtendedDex
{
    private static readonly string[] SweetNames =
        ["Strawberry", "Berry", "Love", "Star", "Clover", "Flower", "Ribbon"];

    /// <summary>The entries in dex order: per species, per form: base, ♀, Gigantamax, sweets.</summary>
    public static IReadOnlyList<(DexSlot Slot, DexFormCategories Category, string Label)> Entries(
        IEnumerable<DexSpeciesForms> catalog, DexFormCategories categories)
    {
        var list = new List<(DexSlot, DexFormCategories, string)>();
        foreach (var species in catalog.OrderBy(s => s.Species))
        {
            var forms = species.Forms.Count == 0 ? [new DexFormInfo(0)] : species.Forms.OrderBy(f => f.Form).ToList();
            if (forms[0].Form != 0) forms.Insert(0, new DexFormInfo(0));
            foreach (var form in forms)
            {
                DexFormCategories category;
                if (form.Form == 0) category = DexFormCategories.None;
                else if (form.Regional) category = DexFormCategories.Regional;
                else category = DexFormCategories.OtherForms;
                if (category != DexFormCategories.None && !categories.HasFlag(category)) continue;

                var sweets = categories.HasFlag(DexFormCategories.AlcremieSweets) ? species.Sweets : 0;
                list.Add((new DexSlot(species.Species, form.Form), category, form.Form == 0 ? "" : form.Name));
                if (form.Form == 0 && species.GenderDifference && categories.HasFlag(DexFormCategories.GenderDifferences))
                    list.Add((new DexSlot(species.Species, 0, DexSlotVariant.Female), DexFormCategories.GenderDifferences, "♀"));
                if (species.GigantamaxForms?.Contains(form.Form) == true && categories.HasFlag(DexFormCategories.Gigantamax))
                    list.Add((new DexSlot(species.Species, form.Form, DexSlotVariant.Gigantamax), DexFormCategories.Gigantamax,
                        form.Form == 0 ? "Gigantamax" : $"{form.Name} Gigantamax"));
                for (var sweet = 1; sweet < sweets; sweet++)
                {
                    var name = sweet < SweetNames.Length ? SweetNames[sweet] : $"Sweet {sweet}";
                    list.Add((new DexSlot(species.Species, form.Form, DexSlotVariant.Sweet, sweet), DexFormCategories.AlcremieSweets,
                        form.Form == 0 ? $"{name} Sweet" : $"{form.Name} · {name} Sweet"));
                }
            }
        }
        return list;
    }

    /// <summary>Fills the entries with the collection. <paramref name="shiny"/>: only shinies count.</summary>
    public static ExtendedDexReport Compute(IEnumerable<DexSpeciesForms> catalog, IEnumerable<DexMon> collection,
        DexFormCategories categories, bool shiny = false)
    {
        var entries = Entries(catalog, categories);
        var sweetsOn = categories.HasFlag(DexFormCategories.AlcremieSweets);
        var genderOn = categories.HasFlag(DexFormCategories.GenderDifferences);
        var femaleSpecies = entries.Where(e => e.Slot.Variant == DexSlotVariant.Female).Select(e => e.Slot.Species).ToHashSet();
        var sweetSpecies = entries.Where(e => e.Slot.Variant == DexSlotVariant.Sweet).Select(e => e.Slot.Species).ToHashSet();

        var pool = OriginDex.Ordered(collection.Where(m => !m.IsEgg && m.Species > 0 && (!shiny || m.Shiny))).ToList();
        var used = new bool[pool.Count];
        var filled = new DexMon?[entries.Count];
        var index = entries.Select((e, i) => (e.Slot, i)).ToDictionary(p => p.Slot, p => p.i);

        bool Female(DexMon m) => genderOn && femaleSpecies.Contains(m.Species) && m.Form == 0 && m.Female;
        int SweetOf(DexMon m) => sweetsOn && sweetSpecies.Contains(m.Species) ? m.FormArgument : 0;

        void Pass(Func<DexMon, DexSlot?> target)
        {
            for (var i = 0; i < pool.Count; i++)
            {
                if (used[i] || target(pool[i]) is not { } slot) continue;
                if (!index.TryGetValue(slot, out var at) || filled[at] is not null) continue;
                filled[at] = pool[i];
                used[i] = true;
            }
        }

        // 1. The specific entries: ♀, Gigantamax, a sweet.
        Pass(m => Female(m) ? new DexSlot(m.Species, 0, DexSlotVariant.Female) : null);
        Pass(m => m.Gigantamax ? new DexSlot(m.Species, m.Form, DexSlotVariant.Gigantamax) : null);
        Pass(m => SweetOf(m) > 0 ? new DexSlot(m.Species, m.Form, DexSlotVariant.Sweet, SweetOf(m)) : null);
        // 2. The form's own entry; a female never stands in for the male look, nor a sweet for the plain cream.
        Pass(m => Female(m) || SweetOf(m) > 0 ? null : new DexSlot(m.Species, m.Form));
        // 3. A form the dex does not count (or Gigantamax spares) still fills the species' base entry.
        Pass(m => Female(m) || SweetOf(m) > 0 || index.ContainsKey(new DexSlot(m.Species, m.Form)) && m.Form != 0
            ? null : new DexSlot(m.Species, 0));

        var cells = entries.Select((e, i) => new ExtendedDexCell(e.Slot, e.Category, e.Label, filled[i])).ToList();
        return new ExtendedDexReport(cells, categories, shiny);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Box layout planner
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>How the planner breaks the dex into boxes.</summary>
public enum DexBoxBreaks
{
    /// <summary>Entries run straight through, 30 to a box.</summary>
    Continuous,
    /// <summary>A species' entries never straddle two boxes (when they fit in one).</summary>
    KeepSpeciesTogether,
    /// <summary>Each generation starts a new box (species kept together too).</summary>
    Generations,
}

/// <summary>One planned box.</summary>
/// <param name="Cells">Index into the report's cells per slot (30), null for an empty slot.</param>
public sealed record DexPlannedBox(int Index, string Title, IReadOnlyList<int?> Cells);

/// <summary>What applying a plan to the Bank does.</summary>
/// <param name="Placements">The single batch for <see cref="IBankService.Place"/>.</param>
/// <param name="Placed">Entries that land in their planned slot (already there included).</param>
/// <param name="Displaced">Pokémon that sat in the region without a planned slot and move out of it.</param>
/// <param name="Refusal">Why it cannot run (a locked box in the region); nothing to place then.</param>
public sealed record DexBankArrangement(
    IReadOnlyList<(Guid Id, int Box, int Slot)> Placements,
    int StartBox,
    int Boxes,
    int Placed,
    int Displaced,
    string? Refusal = null);

/// <summary>
/// Lays the Extended Living Dex out in boxes, and turns that layout into one Bank placement
/// batch. The Bank batch is a pure rearrangement: no entry is created, copied or removed, every
/// entry keeps exactly one slot, Pokémon already in the region that have no planned slot move
/// to free slots after it, and a locked box is never touched.
/// </summary>
public static class DexBoxPlanner
{
    public const int SlotsPerBox = IBankService.SlotsPerBox;

    public static IReadOnlyList<DexPlannedBox> Layout(IReadOnlyList<ExtendedDexCell> cells, DexBoxBreaks breaks)
    {
        var boxes = new List<int?[]>();
        var current = new int?[SlotsPerBox];
        var used = 0;
        void Flush()
        {
            boxes.Add(current);
            current = new int?[SlotsPerBox];
            used = 0;
        }

        var i = 0;
        var lastGeneration = 0;
        while (i < cells.Count)
        {
            var species = cells[i].Slot.Species;
            var run = 1;
            while (i + run < cells.Count && cells[i + run].Slot.Species == species) run++;
            var generation = OriginDex.IntroGeneration(species);
            if (breaks == DexBoxBreaks.Generations && used > 0 && generation != lastGeneration) Flush();
            if (breaks != DexBoxBreaks.Continuous && used > 0 && used + run > SlotsPerBox && run <= SlotsPerBox) Flush();
            for (var k = 0; k < run; k++)
            {
                if (used == SlotsPerBox) Flush();
                current[used++] = i + k;
            }
            lastGeneration = generation;
            i += run;
        }
        if (used > 0) Flush();

        return [.. boxes.Select((slots, n) => new DexPlannedBox(n, Title(cells, slots), slots))];
    }

    private static string Title(IReadOnlyList<ExtendedDexCell> cells, int?[] slots)
    {
        var species = slots.Where(s => s is not null).Select(s => cells[s!.Value].Slot.Species).ToList();
        if (species.Count == 0) return "";
        var (first, last) = (species.Min(), species.Max());
        return first == last ? $"#{first:0000}" : $"#{first:0000}-{last:0000}";
    }

    /// <summary>The first box of <paramref name="boxes"/> consecutive empty, unlocked boxes (the end of the Bank when none).</summary>
    public static int FirstFreeStretch(IReadOnlyList<BankEntry> bank, int boxCount, IReadOnlySet<int> locked, int boxes)
    {
        var used = bank.Select(e => e.Box).ToHashSet();
        for (var start = 0; ; start++)
        {
            var ok = true;
            for (var b = start; b < start + boxes && ok; b++)
                if (b < boxCount && (used.Contains(b) || locked.Contains(b))) ok = false;
            if (ok) return start;
        }
    }

    /// <summary>
    /// The placement batch that puts every Bank Pokémon the report picked into its planned
    /// slot, boxes <paramref name="startBox"/> onward. Only fills held by an unlocked Bank
    /// entry move; the rest of the plan stays a gap.
    /// </summary>
    public static DexBankArrangement PlanBank(IReadOnlyList<ExtendedDexCell> cells, IReadOnlyList<DexPlannedBox> layout,
        IReadOnlyList<BankEntry> bank, int startBox, IReadOnlySet<int> locked)
    {
        var region = Enumerable.Range(startBox, layout.Count).ToHashSet();
        var hit = region.Where(locked.Contains).Order().DefaultIfEmpty(-1).First();
        if (hit >= 0)
            return new DexBankArrangement([], startBox, layout.Count, 0, 0, $"Box {hit + 1:00} is locked and lies in the plan's boxes.");

        var byId = bank.ToDictionary(e => e.Id);
        var placements = new List<(Guid Id, int Box, int Slot)>();
        var chosen = new HashSet<Guid>();
        foreach (var box in layout)
        {
            for (var slot = 0; slot < box.Cells.Count; slot++)
            {
                if (box.Cells[slot] is not { } at || cells[at].Filled?.Where.BankId is not { } id) continue;
                if (!byId.TryGetValue(id, out var entry) || locked.Contains(entry.Box) || !chosen.Add(id)) continue;
                placements.Add((id, startBox + box.Index, slot));
            }
        }

        var targets = placements.Select(p => (p.Box, p.Slot)).ToHashSet();
        var displaced = bank.Where(e => region.Contains(e.Box) && !chosen.Contains(e.Id))
            .OrderBy(e => e.Box).ThenBy(e => e.Slot).ToList();
        var staying = bank.Where(e => !chosen.Contains(e.Id) && !region.Contains(e.Box)).Select(e => (e.Box, e.Slot)).ToHashSet();

        // The displaced take free slots after the region (the vault grows), never a locked box.
        var nextBox = startBox + layout.Count;
        var nextSlot = 0;
        foreach (var entry in displaced)
        {
            while (true)
            {
                if (nextSlot == SlotsPerBox) (nextBox, nextSlot) = (nextBox + 1, 0);
                if (locked.Contains(nextBox)) { nextBox++; nextSlot = 0; continue; }
                var (box, slot) = (nextBox, nextSlot++);
                if (staying.Contains((box, slot)) || targets.Contains((box, slot))) continue;
                placements.Add((entry.Id, box, slot));
                break;
            }
        }
        return new DexBankArrangement(placements, startBox, layout.Count, chosen.Count, displaced.Count);
    }
}
