namespace PKForge.Domain;

/// <summary>Where Pokémon live for the autopilot: a game save or the Bank.</summary>
public enum LivingDexSourceKind
{
    Save,
    Bank,
}

/// <summary>Why a save takes no part in a plan. Anything but <see cref="None"/> means the
/// autopilot neither takes Pokémon out of it nor puts any in.</summary>
public enum LivingDexExclusion
{
    None,
    /// <summary>Hardcore mode is on: the autopilot evolves and rearranges, which Hardcore forbids.</summary>
    Hardcore,
    /// <summary>A ROM hack (CFRU engines, a hack identity or a suspected hack layout).</summary>
    RomHack,
    /// <summary>The safe writer refuses every write of this save (read-only identity or layout).</summary>
    ReadOnly,
    /// <summary>The player hid the save from Home.</summary>
    Hidden,
    /// <summary>The engine cannot do box operations on this save.</summary>
    Unsupported,
    /// <summary>The bytes no longer parse (revoked grant, mid-write file).</summary>
    Unreadable,
    /// <summary>The save is open in PKForge with changes that are not written yet.</summary>
    UnsavedChanges,
}

/// <summary>Plan-level warnings on one step; none of them blocks the step.</summary>
[Flags]
public enum LivingDexWarnings
{
    None = 0,
    /// <summary>The Pokémon goes back to an older generation: a sanitized conversion (moves, ability, ball may change).</summary>
    Downgrade = 1,
    /// <summary>The source Pokémon is already flagged illegal.</summary>
    IllegalSource = 2,
    /// <summary>The Pokémon leaves the party (only when the player allowed party Pokémon).</summary>
    FromParty = 4,
    /// <summary>The last copy of the species leaves its save (only when the player allowed it).</summary>
    LastCopy = 8,
    /// <summary>The move converts between generations (a forward, official route).</summary>
    Conversion = 16,
}

/// <summary>One Pokémon the collection holds.</summary>
/// <param name="Box">Box index; -1 is the party. Ignored for the Bank (see <paramref name="BankId"/>).</param>
/// <param name="Generation">Format generation of the entity (the game it currently lives in, or the Bank entry's).</param>
public sealed record LivingDexHolding(
    int Species,
    int Form,
    bool Shiny,
    int Box,
    int Slot,
    int Generation,
    bool IsEgg = false,
    bool IsLegal = true,
    Guid? BankId = null,
    string? Nickname = null)
{
    public bool IsParty => Box == -1;
}

/// <summary>One save (or the Bank) as the planner sees it.</summary>
/// <param name="Id">Stable id: the save's document id, or <see cref="LivingDexPlanner.BankId"/>.</param>
/// <param name="MaxSpeciesId">Highest species id the save can store.</param>
/// <param name="FreeSlots">Empty box slots in placement order (saves as destination only).</param>
/// <param name="Storable">Species/form the destination game contains; null = everything up to <paramref name="MaxSpeciesId"/>.</param>
/// <param name="BoxCount">The Bank's box count.</param>
/// <param name="Fingerprint">Hash of the bytes the plan was made from; the executor refuses a file that changed since.</param>
/// <param name="Caution">A non-blocking caution shown in the dry run (recently written: close the emulator).</param>
public sealed record LivingDexSource(
    string Id,
    string Label,
    LivingDexSourceKind Kind,
    int Generation,
    int MaxSpeciesId,
    IReadOnlyList<LivingDexHolding> Holdings,
    LivingDexExclusion Exclusion = LivingDexExclusion.None,
    string? ExclusionReason = null,
    IReadOnlyList<SlotRef>? FreeSlots = null,
    Func<int, int, bool>? Storable = null,
    int BoxCount = 0,
    string? Fingerprint = null,
    string? Caution = null)
{
    public bool IsExcluded => Exclusion != LivingDexExclusion.None;
}

/// <summary>What a trade evolution turns into (plain link trade, no held item).</summary>
public sealed record LivingDexTradeEvolution(int From, int FromForm, int To, int ToForm);

/// <summary>The dex the plan aims for: species ids in dex order, their collectible forms, trade evolutions.</summary>
/// <param name="Species">Valid national dex ids, ascending.</param>
/// <param name="Forms">Collectible (storable, not battle-only) forms per species; missing = form 0 only.</param>
public sealed record LivingDexCatalog(
    IReadOnlyList<int> Species,
    IReadOnlyDictionary<int, IReadOnlyList<int>> Forms,
    IReadOnlyList<LivingDexTradeEvolution> TradeEvolutions)
{
    public IReadOnlyList<int> FormsOf(int species) =>
        Forms.TryGetValue(species, out var forms) && forms.Count > 0 ? forms : [0];
}

/// <param name="DestinationId">The save document id, or <see cref="LivingDexPlanner.BankId"/>.</param>
/// <param name="Shiny">Shiny living dex: only shinies count and move.</param>
/// <param name="Forms">One of each collectible form instead of one per species.</param>
/// <param name="AllowLastCopies">Allow a save's only copy of a species to leave it.</param>
/// <param name="IncludeParty">Allow party Pokémon to move.</param>
/// <param name="AllowDowngrades">Allow moves into an older generation (sanitized, with warnings).</param>
/// <param name="BankStartBox">First Bank box of the living dex region; null picks the first empty stretch that fits it.</param>
public sealed record LivingDexOptions(
    string DestinationId,
    bool Shiny = false,
    bool Forms = false,
    bool AllowLastCopies = false,
    bool IncludeParty = false,
    bool AllowDowngrades = true,
    int? BankStartBox = null);

public enum LivingDexStepKind
{
    /// <summary>Move a duplicate into the destination.</summary>
    Move,
    /// <summary>Evolve a spare pre-evolution by trade, then move the result.</summary>
    EvolveAndMove,
    /// <summary>A Bank entry already in the Bank moves to its dex-ordered living dex slot.</summary>
    Arrange,
    /// <summary>Nobody owns it: catch it (the encounter data says where).</summary>
    Guide,
}

/// <summary>One line of the plan.</summary>
/// <param name="Species">The species that fills the living dex slot (after evolving).</param>
/// <param name="SourceId">Where the Pokémon comes from (null for a guide step).</param>
/// <param name="Holding">The exact Pokémon used.</param>
/// <param name="Destination">Its landing slot (Bank: absolute box/slot); null when <paramref name="Blocked"/> or a guide step.</param>
/// <param name="FromSpecies">Evolve steps: the species that evolves.</param>
/// <param name="Blocked">Why the step cannot run (no room); blocked steps are listed, never executed.</param>
public sealed record LivingDexStep(
    LivingDexStepKind Kind,
    int Species,
    int Form,
    bool Shiny,
    int Ordinal,
    string? SourceId = null,
    LivingDexHolding? Holding = null,
    SlotRef? Destination = null,
    int FromSpecies = 0,
    LivingDexWarnings Warnings = LivingDexWarnings.None,
    string? Blocked = null)
{
    public bool IsRunnable => Kind != LivingDexStepKind.Guide && Blocked is null;
    public bool Fills => Kind is LivingDexStepKind.Move or LivingDexStepKind.EvolveAndMove && Blocked is null;
}

/// <summary>A save left out of the plan and why.</summary>
public sealed record LivingDexSkippedSource(string Id, string Label, LivingDexExclusion Exclusion, string Reason);

/// <summary>The full dry-run plan: nothing has been touched when this exists.</summary>
/// <param name="TargetCount">Living dex slots in scope (species, or species × forms).</param>
/// <param name="CoveredBefore">Slots the destination already fills.</param>
/// <param name="OwnedAnywhere">Slots the whole collection covers (destination included).</param>
public sealed record LivingDexPlan(
    LivingDexOptions Options,
    string DestinationLabel,
    LivingDexSourceKind DestinationKind,
    int TargetCount,
    int CoveredBefore,
    int OwnedAnywhere,
    IReadOnlyList<LivingDexStep> Steps,
    IReadOnlyList<LivingDexSkippedSource> Skipped,
    IReadOnlyList<LivingDexSource> Sources,
    int BankStartBox = 0)
{
    public int CoveredAfter => CoveredBefore + Steps.Count(s => s.Fills);
    public int Moves => Steps.Count(s => s is { Kind: LivingDexStepKind.Move, Blocked: null });
    public int Evolutions => Steps.Count(s => s is { Kind: LivingDexStepKind.EvolveAndMove, Blocked: null });
    public int Arranges => Steps.Count(s => s is { Kind: LivingDexStepKind.Arrange, Blocked: null });
    public int Guides => Steps.Count(s => s.Kind == LivingDexStepKind.Guide);
    public int BlockedCount => Steps.Count(s => s.Blocked is not null);
    public int Downgrades => Steps.Count(s => s.IsRunnable && s.Warnings.HasFlag(LivingDexWarnings.Downgrade));
    public bool HasWork => Steps.Any(s => s.IsRunnable);

    /// <summary>Pokémon leaving each source, by source id (runnable steps only).</summary>
    public IReadOnlyDictionary<string, int> OutgoingBySource => Steps
        .Where(s => s.IsRunnable && s.Kind != LivingDexStepKind.Arrange && s.SourceId is not null)
        .GroupBy(s => s.SourceId!)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>Every save the plan writes (destination first), Bank excluded.</summary>
    public IReadOnlyList<string> TouchedSaves
    {
        get
        {
            var touched = new List<string>();
            if (DestinationKind == LivingDexSourceKind.Save && Steps.Any(s => s.Fills)) touched.Add(Options.DestinationId);
            foreach (var id in OutgoingBySource.Keys)
                if (id != LivingDexPlanner.BankId && !touched.Contains(id)) touched.Add(id);
            return touched;
        }
    }
}

/// <summary>
/// The Living Dex Autopilot's planner: pure, deterministic, no I/O. Reads what the player owns
/// across every save and the Bank, and says how to fill one living dex in a destination
/// with Pokémon the player already has - moves only, never copies.
/// <para>Safety rules, all enforced here so the executor never has to guess:</para>
/// <list type="bullet">
/// <item>Excluded saves (Hardcore, ROM hacks, read-only, hidden, unsupported, unreadable, unsaved) are neither sources nor a destination.</item>
/// <item>A save's only copy of a species never leaves it unless <see cref="LivingDexOptions.AllowLastCopies"/> (the Bank is a vault and is exempt).</item>
/// <item>Party Pokémon and eggs never move (party only with <see cref="LivingDexOptions.IncludeParty"/>).</item>
/// <item>Every Pokémon is used at most once; a move never duplicates.</item>
/// <item>Forward and same-generation sources win over downgrades; downgrades carry a warning (or are refused).</item>
/// <item>A trade evolution only consumes a spare: a pre-evolution already reserved for its own slot is never evolved.</item>
/// </list>
/// </summary>
public static class LivingDexPlanner
{
    /// <summary>The Bank's source id.</summary>
    public const string BankId = "pkforge:bank";

    public const int BankSlotsPerBox = 30;

    /// <summary>Bank boxes a living dex of <paramref name="targetCount"/> slots spans.</summary>
    public static int BoxesFor(int targetCount) => Math.Max(1, (targetCount + BankSlotsPerBox - 1) / BankSlotsPerBox);

    /// <summary>
    /// First box starting <paramref name="boxes"/> consecutive empty Bank boxes, so a first run
    /// never lands on anything the player keeps. Boxes past the last one count as empty.
    /// </summary>
    public static int FirstEmptyStretch(LivingDexSource bank, int boxes)
    {
        ArgumentNullException.ThrowIfNull(bank);
        var used = bank.Holdings.Where(h => !h.IsParty).Select(h => h.Box).ToHashSet();
        var run = 0;
        for (var box = 0; box < bank.BoxCount; box++)
        {
            run = used.Contains(box) ? 0 : run + 1;
            if (run == boxes) return box - boxes + 1;
        }
        return bank.BoxCount - run;
    }

    public static LivingDexPlan Plan(LivingDexCatalog catalog, IReadOnlyList<LivingDexSource> sources, LivingDexOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        var destination = sources.FirstOrDefault(s => s.Id == options.DestinationId)
            ?? throw new ArgumentException("The destination is not one of the sources.", nameof(options));
        if (destination.IsExcluded)
            throw new InvalidOperationException($"{destination.Label} cannot be the destination: {destination.ExclusionReason ?? destination.Exclusion.ToString()}");

        var skipped = sources.Where(s => s.IsExcluded)
            .Select(s => new LivingDexSkippedSource(s.Id, s.Label, s.Exclusion, s.ExclusionReason ?? Describe(s.Exclusion)))
            .ToList();

        // ── The target: every (species, form) slot the destination can hold, in dex order ──
        var cap = destination.Kind == LivingDexSourceKind.Bank ? int.MaxValue : destination.MaxSpeciesId;
        var targets = new List<(int Species, int Form)>();
        foreach (var species in catalog.Species)
        {
            if (species < 1 || species > cap) continue;
            var forms = options.Forms ? catalog.FormsOf(species) : [0];
            foreach (var form in forms)
            {
                // Non-form mode stores the species, whatever its form; the game must have it.
                if (destination.Storable is { } storable && !storable(species, options.Forms ? form : 0)) continue;
                targets.Add((species, form));
            }
        }
        var ordinal = new Dictionary<(int, int), int>(targets.Count);
        for (var i = 0; i < targets.Count; i++) ordinal[targets[i]] = i;

        (int, int) KeyOf(LivingDexHolding h) => (h.Species, options.Forms ? h.Form : 0);
        bool Counts(LivingDexHolding h) => !h.IsEgg && (!options.Shiny || h.Shiny) && ordinal.ContainsKey(KeyOf(h));

        // ── What the destination already has ──
        var covered = new HashSet<(int, int)>();
        foreach (var holding in destination.Holdings)
            if (Counts(holding)) covered.Add(KeyOf(holding));
        var coveredBefore = covered.Count;

        var eligible = sources.Where(s => !s.IsExcluded).ToList();
        var ownedAnywhere = new HashSet<(int, int)>(covered);
        foreach (var source in eligible)
            foreach (var holding in source.Holdings)
                if (Counts(holding)) ownedAnywhere.Add(KeyOf(holding));

        // ── Candidate pool: every movable holding outside the destination ──
        // Copies per (source, species) guard the only-copy rule: the count includes the party
        // and every form, because "the save keeps a Pikachu" is what the rule protects.
        var copies = new Dictionary<(string, int), int>();
        foreach (var source in eligible)
            foreach (var holding in source.Holdings)
                if (!holding.IsEgg)
                    copies[(source.Id, holding.Species)] = copies.GetValueOrDefault((source.Id, holding.Species)) + 1;

        var sourceOrder = eligible.Select((s, i) => (s.Id, i)).ToDictionary(t => t.Id, t => t.i, StringComparer.Ordinal);
        var pool = new List<(LivingDexSource Source, LivingDexHolding Holding)>();
        foreach (var source in eligible)
        {
            if (source.Id == destination.Id) continue;
            foreach (var holding in source.Holdings)
            {
                if (holding.IsEgg) continue;
                if (holding.IsParty && !options.IncludeParty) continue;
                if (!options.AllowDowngrades && holding.Generation > destination.Generation && destination.Kind == LivingDexSourceKind.Save) continue;
                pool.Add((source, holding));
            }
        }

        var used = new HashSet<LivingDexHolding>(ReferenceEqualityComparer.Instance);
        var taken = new Dictionary<(string, int), int>();

        bool MayTake(LivingDexSource source, LivingDexHolding holding)
        {
            if (used.Contains(holding)) return false;
            if (source.Kind == LivingDexSourceKind.Bank || options.AllowLastCopies) return true;
            var key = (source.Id, holding.Species);
            return copies.GetValueOrDefault(key) - taken.GetValueOrDefault(key) > 1;
        }

        void Take(LivingDexSource source, LivingDexHolding holding)
        {
            used.Add(holding);
            var key = (source.Id, holding.Species);
            taken[key] = taken.GetValueOrDefault(key) + 1;
        }

        LivingDexWarnings WarningsFor(LivingDexSource source, LivingDexHolding holding)
        {
            var warnings = LivingDexWarnings.None;
            if (destination.Kind == LivingDexSourceKind.Save && holding.Generation > 0 && destination.Generation > 0)
            {
                if (holding.Generation > destination.Generation) warnings |= LivingDexWarnings.Downgrade;
                else if (holding.Generation < destination.Generation) warnings |= LivingDexWarnings.Conversion;
            }
            if (!holding.IsLegal) warnings |= LivingDexWarnings.IllegalSource;
            if (holding.IsParty) warnings |= LivingDexWarnings.FromParty;
            if (source.Kind == LivingDexSourceKind.Save && copies.GetValueOrDefault((source.Id, holding.Species)) - taken.GetValueOrDefault((source.Id, holding.Species)) <= 1)
                warnings |= LivingDexWarnings.LastCopy;
            return warnings;
        }

        // Best candidate first: forward over downgrade (the smaller generation gap the better),
        // legal over flagged, boxes over the party, a plain one over a shiny in the normal dex
        // (shinies stay for the shiny dex), the Bank's vault before a save's spare, the save
        // with the most copies before one that barely has two. Then stable by place.
        IEnumerable<(LivingDexSource Source, LivingDexHolding Holding)> Ranked(IEnumerable<(LivingDexSource Source, LivingDexHolding Holding)> candidates) =>
            candidates
                .OrderBy(c => GenerationRank(c.Holding.Generation, destination))
                .ThenBy(c => c.Holding.IsLegal ? 0 : 1)
                .ThenBy(c => c.Holding.IsParty ? 1 : 0)
                .ThenBy(c => !options.Shiny && c.Holding.Shiny ? 1 : 0)
                .ThenBy(c => c.Source.Kind == LivingDexSourceKind.Bank ? 0 : 1)
                .ThenByDescending(c => copies.GetValueOrDefault((c.Source.Id, c.Holding.Species)))
                .ThenBy(c => sourceOrder[c.Source.Id])
                .ThenBy(c => c.Holding.Box)
                .ThenBy(c => c.Holding.Slot);

        var byKey = pool.Where(c => Counts(c.Holding)).GroupBy(c => KeyOf(c.Holding)).ToDictionary(g => g.Key, g => Ranked(g).ToList());

        // ── Pass 1: direct moves ──
        var fills = new List<LivingDexStep>();
        foreach (var target in targets)
        {
            if (covered.Contains(target) || !byKey.TryGetValue(target, out var candidates)) continue;
            foreach (var (source, holding) in candidates)
            {
                if (!MayTake(source, holding)) continue;
                var warnings = WarningsFor(source, holding);
                Take(source, holding);
                covered.Add(target);
                fills.Add(new LivingDexStep(LivingDexStepKind.Move, target.Species, target.Form, holding.Shiny, ordinal[target],
                    source.Id, holding, Warnings: warnings));
                break;
            }
        }

        // ── Pass 2: evolve a spare by trade (two Kadabra: one fills Kadabra, one becomes Alakazam) ──
        // Only spares are left in the pool now: pass 1 reserved every pre-evolution its own slot needed.
        foreach (var evolution in catalog.TradeEvolutions.OrderBy(e => e.To).ThenBy(e => e.ToForm))
        {
            var target = (evolution.To, options.Forms ? evolution.ToForm : 0);
            if (covered.Contains(target) || !ordinal.ContainsKey(target)) continue;
            var candidates = pool.Where(c => !c.Holding.IsEgg && c.Holding.Species == evolution.From
                && (!options.Forms || c.Holding.Form == evolution.FromForm)
                && (!options.Shiny || c.Holding.Shiny));
            foreach (var (source, holding) in Ranked(candidates))
            {
                if (!MayTake(source, holding)) continue;
                var warnings = WarningsFor(source, holding);
                Take(source, holding);
                covered.Add(target);
                fills.Add(new LivingDexStep(LivingDexStepKind.EvolveAndMove, target.Item1, target.Item2, holding.Shiny, ordinal[target],
                    source.Id, holding, FromSpecies: evolution.From, Warnings: warnings));
                break;
            }
        }
        fills.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));

        // ── Landing slots ──
        var steps = new List<LivingDexStep>(targets.Count);
        var bankStart = 0;
        if (destination.Kind == LivingDexSourceKind.Save)
        {
            var free = destination.FreeSlots ?? [];
            var next = 0;
            foreach (var step in fills)
            {
                steps.Add(next < free.Count
                    ? step with { Destination = free[next++] }
                    : step with { Blocked = $"No free box slot left in {destination.Label}." });
            }
        }
        else
        {
            // The Bank's living dex boxes: every target has a fixed slot by dex order, so the
            // boxes read like the Pokédex and a later catch has its gap waiting for it.
            bankStart = Math.Max(0, options.BankStartBox ?? FirstEmptyStretch(destination, BoxesFor(targets.Count)));
            SlotRef SlotOf(int index) => new(bankStart + index / BankSlotsPerBox, index % BankSlotsPerBox);
            var occupied = new Dictionary<SlotRef, LivingDexHolding>();
            foreach (var holding in destination.Holdings) occupied[new SlotRef(holding.Box, holding.Slot)] = holding;

            // Entries already in the Bank that fill a target: arrange them into their slot
            // (one per target; the first in dex/box order wins, the rest stay where they are).
            var arranged = new HashSet<(int, int)>();
            var arrangeSteps = new List<LivingDexStep>();
            var vacated = new HashSet<SlotRef>();
            var placedInto = new HashSet<SlotRef>();
            foreach (var holding in destination.Holdings.Where(Counts).OrderBy(h => ordinal[KeyOf(h)]).ThenBy(h => h.Shiny == options.Shiny ? 0 : 1).ThenBy(h => h.Box).ThenBy(h => h.Slot))
            {
                var key = KeyOf(holding);
                if (!arranged.Add(key)) continue;
                var slot = SlotOf(ordinal[key]);
                if (holding.Box == slot.Box && holding.Slot == slot.Slot) { placedInto.Add(slot); continue; }
                arrangeSteps.Add(new LivingDexStep(LivingDexStepKind.Arrange, key.Item1, key.Item2, holding.Shiny, ordinal[key],
                    LivingDexPlanner.BankId, holding, slot));
                vacated.Add(new SlotRef(holding.Box, holding.Slot));
                placedInto.Add(slot);
            }

            // A slot is free when nothing sits there, or its occupant is itself moving away.
            // Anything else (the player parked another Pokémon in the living dex boxes) blocks it.
            bool Free(SlotRef slot, LivingDexHolding? mover) =>
                !occupied.TryGetValue(slot, out var occupant) || ReferenceEquals(occupant, mover) || vacated.Contains(slot);

            foreach (var step in arrangeSteps)
                steps.Add(Free(step.Destination!.Value, step.Holding)
                    ? step
                    : step with { Destination = null, Blocked = "Its living dex slot in the Bank holds another Pokémon." });
            foreach (var step in fills)
            {
                var slot = SlotOf(step.Ordinal);
                steps.Add(Free(slot, null) && !placedInto.Contains(slot)
                    ? step with { Destination = slot }
                    : step with { Blocked = "Its living dex slot in the Bank holds another Pokémon." });
            }
        }

        // ── Guide steps: nobody owns these; catch them ──
        foreach (var target in targets)
            if (!covered.Contains(target))
                steps.Add(new LivingDexStep(LivingDexStepKind.Guide, target.Species, target.Form, options.Shiny, ordinal[target]));

        steps.Sort(static (a, b) =>
        {
            var kind = KindOrder(a.Kind).CompareTo(KindOrder(b.Kind));
            return kind != 0 ? kind : a.Ordinal.CompareTo(b.Ordinal);
        });

        return new LivingDexPlan(options, destination.Label, destination.Kind, targets.Count, coveredBefore,
            ownedAnywhere.Count, steps, skipped, sources, bankStart);
    }

    private static int KindOrder(LivingDexStepKind kind) => kind switch
    {
        LivingDexStepKind.Move or LivingDexStepKind.EvolveAndMove => 0,
        LivingDexStepKind.Arrange => 1,
        _ => 2,
    };

    /// <summary>0 = same generation (no conversion), then forward by growing gap, then downgrades.</summary>
    private static int GenerationRank(int generation, LivingDexSource destination)
    {
        if (destination.Kind == LivingDexSourceKind.Bank || generation <= 0 || destination.Generation <= 0) return 0;
        var gap = destination.Generation - generation;
        return gap >= 0 ? gap : 100 - gap;
    }

    public static string Describe(LivingDexExclusion exclusion) => exclusion switch
    {
        LivingDexExclusion.Hardcore => "Hardcore mode is on: the autopilot evolves and rearranges, which Hardcore forbids.",
        LivingDexExclusion.RomHack => "ROM hack: its Pokémon tables are not the retail game's.",
        LivingDexExclusion.ReadOnly => "Read-only in PKForge.",
        LivingDexExclusion.Hidden => "Hidden from Home.",
        LivingDexExclusion.Unsupported => "PKForge cannot do box operations on this game.",
        LivingDexExclusion.Unreadable => "The file could not be read.",
        LivingDexExclusion.UnsavedChanges => "Open in PKForge with unsaved changes: save them first.",
        _ => "",
    };
}
