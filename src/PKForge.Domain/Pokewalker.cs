namespace PKForge.Domain;

/// <summary>One item a course's Dowsing can find, from <paramref name="Steps"/> steps into a walk.</summary>
public sealed record WalkerItem(string Name, int Steps);

/// <summary>
/// One Pokéwalker course as the HeartGold/SoulSilver device has it. <paramref name="SlotSteps"/>
/// follows PKHeX's walker table order (groups A, A, B, B, C, C): the steps a walk needs before the
/// Poké Radar can find that slot. <paramref name="Advantage"/> are the three types (<see cref="ParkType"/>)
/// that cut those steps by a quarter.
/// </summary>
public sealed record WalkerCourse(int Index, string Name, int Watts, bool NeedsNationalDex,
    IReadOnlyList<int> Advantage, IReadOnlyList<int> SlotSteps, IReadOnlyList<WalkerItem> Items)
{
    /// <summary>A (0), B (1) or C (2): the slot's group.</summary>
    public static int GroupOf(int slot) => slot / 2;
}

/// <summary>
/// The 20 courses a Pokéwalker opens by watts (the 7 event courses after them are never earned by
/// walking). Watts, the National Dex rule, advantage types, Pokémon step thresholds and Dowsing
/// tables come from the route pages of pldh.net and serebii.net (Bulbapedia's route list agrees);
/// the Pokémon themselves are PKHeX's (<c>encounter_walker4.pkl</c>), in the same slot order.
/// </summary>
public static class WalkerCourses
{
    private static WalkerItem[] I(params (string Name, int Steps)[] items) => [.. items.Select(i => new WalkerItem(i.Name, i.Steps))];

    public static IReadOnlyList<WalkerCourse> All { get; } =
    [
        new(0, "Refreshing Field", 0, false, [ParkType.Fire, ParkType.Bug, ParkType.Flying], [3000, 2000, 500, 500, 0, 0],
            I(("Revive", 2500), ("Full Heal", 2000), ("Burn Heal", 1000), ("Ice Heal", 900), ("Chesto Berry", 800), ("Awakening", 700),
              ("Cheri Berry", 600), ("Paralyze Heal", 500), ("Oran Berry", 300), ("Potion", 0))),
        new(1, "Noisy Forest", 0, false, [ParkType.Fire, ParkType.Ice, ParkType.Flying], [4000, 3000, 700, 700, 0, 0],
            I(("Net Ball", 5000), ("Revive", 2500), ("Green Shard", 2000), ("Ether", 1000), ("Energy Root", 900), ("Energy Powder", 800),
              ("Big Mushroom", 700), ("Pecha Berry", 500), ("Tiny Mushroom", 200), ("Cheri Berry", 0))),
        new(2, "Rugged Road", 50, false, [ParkType.Water, ParkType.Fighting, ParkType.Ground], [5000, 4000, 1000, 1000, 0, 0],
            I(("PP Up", 7000), ("Hard Stone", 5000), ("Red Shard", 3000), ("Star Piece", 2000), ("Full Heal", 1500), ("Super Repel", 1000),
              ("Burn Heal", 800), ("Antidote", 500), ("Escape Rope", 100), ("Repel", 0))),
        new(3, "Beautiful Beach", 200, false, [ParkType.Grass, ParkType.Electric, ParkType.Dragon], [4000, 5000, 1000, 1500, 0, 0],
            I(("Dive Ball", 5000), ("Big Pearl", 4000), ("Blue Shard", 3000), ("Heart Scale", 2000), ("Leppa Berry", 1800), ("Full Heal", 1500),
              ("Aspear Berry", 1000), ("Soda Pop", 800), ("Rawst Berry", 100), ("Fresh Water", 0))),
        new(4, "Suburban Area", 500, false, [ParkType.Fighting, ParkType.Normal, ParkType.Psychic], [5000, 4000, 1000, 1000, 0, 0],
            I(("PP Up", 5000), ("Guard Spec.", 2000), ("X Sp. Def", 1500), ("Dire Hit", 1000), ("X Accuracy", 750), ("X Speed", 500),
              ("X Defense", 250), ("X Attack", 100), ("Potion", 0))),
        new(5, "Dim Cave", 1000, false, [ParkType.Water, ParkType.Dark, ParkType.Rock], [5000, 5000, 1000, 1000, 0, 0],
            I(("TM18", 6000), ("Silver Powder", 5000), ("Yellow Shard", 4500), ("Elixir", 3500), ("Persim Berry", 2500), ("Max Potion", 2000),
              ("Max Ether", 1500), ("Hyper Potion", 1000), ("Ether", 700), ("Sitrus Berry", 0))),
        new(6, "Blue Lake", 2000, false, [ParkType.Grass, ParkType.Electric, ParkType.Dragon], [5000, 4000, 500, 500, 0, 0],
            I(("TM11", 5000), ("Net Ball", 4000), ("Dive Ball", 3500), ("Lum Berry", 3000), ("Star Piece", 2500), ("Stardust", 2000),
              ("Sitrus Berry", 1000), ("Pearl", 500), ("Leppa Berry", 100), ("Fresh Water", 0))),
        new(7, "Town Outskirts", 3000, false, [ParkType.Fighting, ParkType.Ghost, ParkType.Normal], [5000, 3000, 1500, 1500, 0, 0],
            I(("TM37", 5000), ("Guard Spec.", 3000), ("X Sp. Def", 2500), ("Ultra Ball", 2000), ("Lum Berry", 1500), ("X Attack", 1000),
              ("Great Ball", 750), ("X Accuracy", 500), ("Dire Hit", 100), ("Poké Ball", 0))),
        new(8, "Hoenn Field", 5000, true, [ParkType.Fire, ParkType.Bug, ParkType.Flying], [7500, 5000, 2000, 2000, 0, 0],
            I(("Ganlon Berry", 8000), ("Wacan Berry", 3500), ("Passho Berry", 3000), ("Occa Berry", 2500), ("Qualot Berry", 2000), ("Kelpsy Berry", 1500),
              ("Pomeg Berry", 1000), ("Nanab Berry", 500), ("Bluk Berry", 250), ("Razz Berry", 0))),
        new(9, "Warm Beach", 7500, true, [ParkType.Grass, ParkType.Electric, ParkType.Dragon], [7000, 5000, 1500, 1500, 0, 0],
            I(("Fire Stone", 5000), ("Yellow Shard", 4900), ("Blue Shard", 4000), ("Heart Scale", 3000), ("Big Pearl", 2500), ("Revive", 2000),
              ("Pearl", 1000), ("Wepear Berry", 100), ("Fresh Water", 0))),
        new(10, "Volcano Path", 10000, true, [ParkType.Water, ParkType.Fighting, ParkType.Rock], [5000, 5000, 2000, 2000, 0, 0],
            I(("Apicot Berry", 8000), ("Sun Stone", 5500), ("Moon Stone", 5000), ("Flame Orb", 4000), ("Lava Cookie", 3000), ("Revive", 2000),
              ("Pinap Berry", 1000), ("Red Shard", 500), ("Ice Heal", 100), ("Potion", 0))),
        new(11, "Treehouse", 15000, true, [ParkType.Fire, ParkType.Ice, ParkType.Flying], [5000, 5000, 1000, 1000, 0, 0],
            I(("Salac Berry", 8000), ("Calcium", 4500), ("Max Elixir", 4000), ("Max Potion", 3500), ("Max Ether", 3000), ("Ether", 2000),
              ("Revival Herb", 1000), ("Heal Powder", 500), ("Energy Root", 100), ("Energy Powder", 0))),
        new(12, "Scary Cave", 20000, true, [ParkType.Dark, ParkType.Ghost, ParkType.Rock], [5000, 5000, 500, 1000, 0, 0],
            I(("Petaya Berry", 8000), ("Iron", 4900), ("Thick Club", 4500), ("Dusk Ball", 4000), ("Blue Shard", 3900), ("Old Gateau", 3500),
              ("Green Shard", 2000), ("Yellow Shard", 1500), ("Red Shard", 1000), ("Repel", 0))),
        new(13, "Sinnoh Field", 25000, true, [ParkType.Fire, ParkType.Ice, ParkType.Flying], [7000, 7000, 3000, 3000, 0, 0],
            I(("Rare Candy", 5000), ("Full Restore", 4000), ("Max Potion", 3800), ("Repeat Ball", 3000), ("Timer Ball", 2500), ("Revive", 2000),
              ("Hyper Potion", 1500), ("Full Heal", 1000), ("Nest Ball", 500), ("Super Potion", 0))),
        new(14, "Icy Mountain Rd.", 30000, true, [ParkType.Fire, ParkType.Ice, ParkType.Ground], [10000, 10000, 3000, 3000, 0, 0],
            I(("TM07", 6000), ("Yache Berry", 5000), ("Rindo Berry", 4500), ("Icy Rock", 4000), ("Smooth Rock", 3500), ("Heat Rock", 3000),
              ("Damp Rock", 2500), ("Max Repel", 1000), ("Full Heal", 100), ("X Defense", 0))),
        new(15, "Big Forest", 40000, true, [ParkType.Fire, ParkType.Ice, ParkType.Flying], [6000, 5000, 1000, 1000, 0, 0],
            I(("Hondew Berry", 5000), ("Qualot Berry", 4500), ("Bluk Berry", 4000), ("Durin Berry", 3500), ("Big Mushroom", 3000), ("Belue Berry", 2500),
              ("Honey", 2000), ("Tamato Berry", 1000), ("Grepa Berry", 500), ("Tiny Mushroom", 0))),
        new(16, "White Lake", 50000, true, [ParkType.Grass, ParkType.Electric, ParkType.Dragon], [5000, 6000, 500, 1000, 0, 0],
            I(("TM68", 10000), ("Kebia Berry", 5000), ("Chople Berry", 4500), ("Watmel Berry", 4000), ("Pamtre Berry", 3500), ("Spelon Berry", 3000),
              ("Nomel Berry", 2500), ("Rabuta Berry", 2000), ("Magost Berry", 1000), ("Cornn Berry", 0))),
        new(17, "Stormy Beach", 65000, true, [ParkType.Grass, ParkType.Electric, ParkType.Dragon], [4000, 5000, 500, 1500, 0, 0],
            I(("PP Up", 5000), ("Payapa Berry", 4500), ("Coba Berry", 4000), ("Shuca Berry", 3500), ("Tanga Berry", 3000), ("Charti Berry", 2500),
              ("Heal Ball", 2000), ("Quick Ball", 1500), ("Heart Scale", 800), ("Fresh Water", 0))),
        new(18, "Resort", 80000, true, [ParkType.Fighting, ParkType.Normal, ParkType.Psychic], [8000, 8000, 4000, 4000, 0, 0],
            I(("Luxury Ball", 6000), ("Poké Doll", 5000), ("Fluffy Tail", 3000), ("Moomoo Milk", 2500), ("Honey", 2000), ("Soda Pop", 1000),
              ("Max Repel", 800), ("Lemonade", 500), ("Green Shard", 200), ("Fresh Water", 0))),
        new(19, "Quiet Cave", 100000, true, [ParkType.Dark, ParkType.Ghost, ParkType.Psychic], [10000, 10000, 500, 500, 0, 0],
            I(("TM15", 10000), ("Nugget", 5500), ("TM10", 5000), ("Big Pearl", 3000), ("Heart Scale", 2500), ("Dusk Ball", 2000),
              ("Stardust", 1000), ("Pearl", 500), ("Elixir", 0))),
    ];

    public static WalkerCourse? Find(int index) => (uint)index < (uint)All.Count ? All[index] : null;
}

/// <summary>A small, seeded, reproducible random source (xorshift32). The walk keeps the seed and
/// how many draws were made, so every Radar and Dowsing outcome can be replayed.</summary>
public sealed class WalkerRandom
{
    private uint _state;

    public WalkerRandom(uint seed, int skip = 0)
    {
        _state = seed == 0 ? 0x9E3779B9u : seed;
        for (var i = 0; i < skip; i++) Next();
    }

    public int Draws { get; private set; }

    public uint Next()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        Draws++;
        return x;
    }

    /// <summary>0 ≤ value &lt; <paramref name="count"/>.</summary>
    public int Below(int count) => count <= 1 ? 0 : (int)(Next() % (uint)count);

    /// <summary>True <paramref name="percent"/> times in a hundred.</summary>
    public bool Chance(int percent) => Below(100) < percent;
}

/// <summary>The stroll in progress: who, where, how far, and what it has found so far.</summary>
/// <param name="ResidentId">The Poképark resident on the walk (its save or Bank Pokémon).</param>
/// <param name="Fingerprint">SHA-256 of its data when it left, so nothing is written to a Pokémon changed since.</param>
/// <param name="Slots">The course slot met this walk in each group A, B, C (the other of each pair stays away).</param>
/// <param name="Catches">Course slots caught with the Poké Radar, at most three.</param>
/// <param name="Items">Item names found by Dowsing, at most three.</param>
public sealed record WalkerWalk(string ResidentId, string Name, int Species, int Form, bool Shiny, string? Fingerprint,
    int Course, bool Advantaged, DateTimeOffset Started, int Steps, uint Seed, int Draws,
    IReadOnlyList<int> Slots, IReadOnlyList<int> Catches, IReadOnlyList<string> Items);

/// <summary>Everything the Pokéwalker keeps between visits.</summary>
/// <param name="Watts">Watts to spend.</param>
/// <param name="LifetimeWatts">Every watt ever earned: what opens courses, as on the device.</param>
/// <param name="StepRemainder">Steps toward the next watt.</param>
/// <param name="SyncedCourses">Courses a HeartGold/SoulSilver save had open (events included).</param>
/// <param name="NationalDex">Whether a synced save has the National Dex, which courses 9-20 need.</param>
/// <param name="SensorLast">The step counter's last reading (cumulative since the phone booted).</param>
/// <param name="StrollLast">When stroll mode last counted.</param>
/// <param name="UnsyncedSteps">Steps not yet added to a HeartGold/SoulSilver save.</param>
/// <param name="StrollDay">The day <paramref name="StrollToday"/> counts, for stroll mode's daily cap.</param>
/// <param name="Homecoming">A walk that ended and is being brought home (catches to the Bank, finds to
/// the Item Bank); set in the same write that ends the walk, cleared once everything landed.</param>
public sealed record WalkerState(WalkerWalk? Walk = null, int Watts = 0, long LifetimeWatts = 0, int StepRemainder = 0,
    long LifetimeSteps = 0, IReadOnlyList<int>? SyncedCourses = null, bool NationalDex = false,
    long? SensorLast = null, DateTimeOffset? StrollLast = null, long UnsyncedSteps = 0,
    DateOnly? StrollDay = null, int StrollToday = 0, WalkerPendingHome? Homecoming = null)
{
    public IReadOnlyList<int> Synced => SyncedCourses ?? [];
}

/// <summary>What a Radar search turned up before the player picks a bush: the rustling bush
/// and the "!" marks over it (3 group A, 2 group B, 1 group C).</summary>
public sealed record WalkerRadarRound(int Bush, int Slot, int Marks);

public enum WalkerRadarOutcome { WrongBush, Fled, Caught, Full }

/// <summary>A Dowsing search: where the item is hidden and what it is.</summary>
public sealed record WalkerDowsingRound(int Patch, string Item, int TriesLeft);

public enum WalkerDowsingOutcome { Found, Close, Far, Missed, Full }

/// <summary>What coming home gives the Pokémon: EXP (capped at its next level by the caller, who
/// knows its growth rate) and friendship.</summary>
public sealed record WalkerHomecoming(int Steps, uint Exp, int Friendship, IReadOnlyList<int> Catches, IReadOnlyList<string> Items);

/// <summary>A Poké Radar catch on its way to the Bank: its legal bytes (base64, made once, so a
/// homecoming finished after a crash banks the same Pokémon), and whether it is banked yet.</summary>
public sealed record WalkerPendingCatch(string Data, string Format, string Name, int Level, bool Banked = false, int Box = -1)
{
    public byte[] Bytes() => Convert.FromBase64String(Data);
}

/// <summary>
/// A homecoming in progress, kept in the walk file so it happens exactly once: the walk is cleared
/// in the same write that records this, each catch is marked as it reaches the Bank, and the finds
/// reach the Item Bank under <see cref="Key"/>, which the Item Bank takes only once.
/// </summary>
public sealed record WalkerPendingHome(string Key, WalkerWalk Walk, WalkerHomecoming Home,
    IReadOnlyList<WalkerPendingCatch> Catches, IReadOnlyList<string> Notes, bool ItemsBanked = false);

/// <summary>
/// The Pokéwalker's rules, pure: 20 steps a watt, courses opened by lifetime watts (and the
/// National Dex for 9-20), the Poké Radar (10 W, four bushes) and Dowsing (3 W, six patches, two
/// tries), three catches and three items a walk, and what a walk is worth on return.
/// </summary>
public static class WalkerRules
{
    public const int StepsPerWatt = 20;
    public const int RadarCost = 10;
    public const int DowsingCost = 3;
    public const int MaxHeld = 3;
    public const int Bushes = 4;
    public const int Patches = 6;
    public const int DowsingTries = 2;

    /// <summary>Stroll mode, for devices with no step counter: a gentle pace, capped per day.</summary>
    public const int StrollStepsPerHour = 600;
    public const int StrollDailyCap = 3000;

    /// <summary>One friendship point per this many steps, like walking in the games.</summary>
    public const int StepsPerFriendship = 256;

    /// <summary>Catch chances by group A, B, C.</summary>
    public static int CatchChance(int group) => group switch { 0 => 35, 1 => 50, _ => 70 };

    public static bool IsUnlocked(WalkerState state, WalkerCourse course) =>
        state.Synced.Contains(course.Index) ||
        (state.LifetimeWatts >= course.Watts && (!course.NeedsNationalDex || state.NationalDex));

    public static IReadOnlyList<WalkerCourse> Unlocked(WalkerState state) => [.. WalkerCourses.All.Where(c => IsUnlocked(state, c))];

    /// <summary>The next course still locked, for "N watts to go".</summary>
    public static WalkerCourse? NextLocked(WalkerState state) => WalkerCourses.All.FirstOrDefault(c => !IsUnlocked(state, c));

    /// <summary>The steps a slot needs this walk: a quarter fewer with an advantaged Pokémon.</summary>
    public static int StepsFor(WalkerCourse course, int slot, bool advantaged) =>
        advantaged ? course.SlotSteps[slot] * 3 / 4 : course.SlotSteps[slot];

    /// <summary>Whether a Pokémon of these types has the course's advantage.</summary>
    public static bool HasAdvantage(WalkerCourse course, IEnumerable<int> types) => types.Any(course.Advantage.Contains);

    /// <summary>Starts a walk: one slot of each pair is drawn for the whole walk.</summary>
    public static WalkerState Start(WalkerState state, WalkerCourse course, string residentId, string name, int species, int form, bool shiny,
        string? fingerprint, IEnumerable<int> types, uint seed, DateTimeOffset now)
    {
        if (state.Walk is not null) throw new InvalidOperationException("A Pokémon is already on a walk.");
        if (!IsUnlocked(state, course)) throw new InvalidOperationException($"{course.Name} is not open yet.");
        var random = new WalkerRandom(seed);
        int[] slots = [random.Below(2), 2 + random.Below(2), 4 + random.Below(2)];
        var walk = new WalkerWalk(residentId, name, species, form, shiny, fingerprint, course.Index, HasAdvantage(course, types),
            now, 0, seed, random.Draws, slots, [], []);
        return state with { Walk = walk };
    }

    /// <summary>Adds steps: to the walk, to the watts (20 a watt), and to the total for a later HGSS sync.</summary>
    public static WalkerState AddSteps(WalkerState state, int steps)
    {
        if (steps <= 0) return state;
        var pooled = state.StepRemainder + steps;
        var watts = pooled / StepsPerWatt;
        return state with
        {
            Walk = state.Walk is { } walk ? walk with { Steps = (int)Math.Min(int.MaxValue, (long)walk.Steps + steps) } : null,
            Watts = (int)Math.Min(9_999_999, (long)state.Watts + watts),
            LifetimeWatts = state.LifetimeWatts + watts,
            StepRemainder = pooled % StepsPerWatt,
            LifetimeSteps = state.LifetimeSteps + steps,
            UnsyncedSteps = state.UnsyncedSteps + steps,
        };
    }

    /// <summary>New steps from the hardware counter, which counts from boot: a lower reading means
    /// the phone restarted, and the reading itself is what was walked since.</summary>
    public static long SensorDelta(long? last, long now) => last is not { } before ? 0 : now >= before ? now - before : now;

    /// <summary>Stroll-mode steps between two moments: 600 an hour, at most 3,000 on any one day.</summary>
    public static int StrollSteps(DateTimeOffset from, DateTimeOffset to, int alreadyToday = 0)
    {
        if (to <= from) return 0;
        var total = 0L;
        var cursor = from;
        var first = true;
        // At most a month is looked at: a walk forgotten longer earns no more.
        for (var day = 0; day < 31 && cursor < to; day++)
        {
            var midnight = new DateTimeOffset(cursor.Date.AddDays(1), cursor.Offset);
            var end = midnight < to ? midnight : to;
            var steps = (long)((end - cursor).TotalHours * StrollStepsPerHour);
            var room = StrollDailyCap - (first ? alreadyToday : 0);
            total += Math.Clamp(steps, 0, Math.Max(0, room));
            first = false;
            cursor = end;
        }
        return (int)total;
    }

    /// <summary>Stroll mode up to <paramref name="now"/>: the steps since the last count, within the
    /// day's cap, added to the walk. Without a walk it only moves the clock on.</summary>
    public static WalkerState Stroll(WalkerState state, DateTimeOffset now)
    {
        var from = state.StrollLast ?? now;
        if (state.Walk is null || now <= from) return state with { StrollLast = now > from ? now : from };
        var day = DateOnly.FromDateTime(from.Date);
        var today = state.StrollDay == day ? state.StrollToday : 0;
        var steps = StrollSteps(from, now, today);
        var nowDay = DateOnly.FromDateTime(now.Date);
        var midnight = new DateTimeOffset(now.Date, now.Offset);
        var countedToday = nowDay == day ? today + steps : StrollSteps(midnight > from ? midnight : from, now);
        return AddSteps(state, steps) with { StrollLast = now, StrollDay = nowDay, StrollToday = countedToday };
    }

    /// <summary>Spends 10 W on a Radar search. Null when the watts are short or three catches are held.</summary>
    public static (WalkerState State, WalkerRadarRound Round)? Radar(WalkerState state)
    {
        if (state.Walk is not { } walk || state.Watts < RadarCost || walk.Catches.Count >= MaxHeld) return null;
        var course = WalkerCourses.Find(walk.Course)!;
        var random = new WalkerRandom(walk.Seed, walk.Draws);
        var ready = walk.Slots.Where(s => walk.Steps >= StepsFor(course, s, walk.Advantaged)).ToArray();
        // Common C six times as often as A, B in between.
        var weights = ready.Select(s => CourseGroupWeight(WalkerCourse.GroupOf(s))).ToArray();
        var pick = random.Below(weights.Sum());
        var slot = ready[0];
        foreach (var (s, w) in ready.Zip(weights))
        {
            if (pick < w) { slot = s; break; }
            pick -= w;
        }
        var round = new WalkerRadarRound(random.Below(Bushes), slot, 3 - WalkerCourse.GroupOf(slot));
        return (state with { Watts = state.Watts - RadarCost, Walk = walk with { Draws = random.Draws } }, round);
    }

    private static int CourseGroupWeight(int group) => group switch { 0 => 1, 1 => 3, _ => 6 };

    /// <summary>The player picks a bush: the wrong one and the Pokémon is gone; the right one and
    /// it is caught or flees, by its group's chance.</summary>
    public static (WalkerState State, WalkerRadarOutcome Outcome) Search(WalkerState state, WalkerRadarRound round, int bush)
    {
        if (state.Walk is not { } walk) return (state, WalkerRadarOutcome.Fled);
        if (walk.Catches.Count >= MaxHeld) return (state, WalkerRadarOutcome.Full);
        if (bush != round.Bush) return (state, WalkerRadarOutcome.WrongBush);
        var random = new WalkerRandom(walk.Seed, walk.Draws);
        var caught = random.Chance(CatchChance(WalkerCourse.GroupOf(round.Slot)));
        walk = walk with { Draws = random.Draws };
        if (caught) walk = walk with { Catches = [.. walk.Catches, round.Slot] };
        return (state with { Walk = walk }, caught ? WalkerRadarOutcome.Caught : WalkerRadarOutcome.Fled);
    }

    /// <summary>Spends 3 W on Dowsing: an item is hidden in one of six patches, drawn among those
    /// the walk's steps have reached, the rarer (more steps) the less often.</summary>
    public static (WalkerState State, WalkerDowsingRound Round)? Dowse(WalkerState state)
    {
        if (state.Walk is not { } walk || state.Watts < DowsingCost || walk.Items.Count >= MaxHeld) return null;
        var course = WalkerCourses.Find(walk.Course)!;
        var random = new WalkerRandom(walk.Seed, walk.Draws);
        var reached = course.Items.Where(i => walk.Steps >= i.Steps).OrderBy(i => i.Steps).ToArray();
        // The most common item weighs as many as there are reached items, the rarest one.
        var weights = reached.Select((_, i) => reached.Length - i).ToArray();
        var pick = random.Below(weights.Sum());
        var item = reached[0];
        for (var i = 0; i < reached.Length; i++)
        {
            if (pick < weights[i]) { item = reached[i]; break; }
            pick -= weights[i];
        }
        var round = new WalkerDowsingRound(random.Below(Patches), item.Name, DowsingTries);
        return (state with { Watts = state.Watts - DowsingCost, Walk = walk with { Draws = random.Draws } }, round);
    }

    /// <summary>One try at a patch: found, or a hint (close: the next patch over) while tries are left.</summary>
    public static (WalkerState State, WalkerDowsingRound Round, WalkerDowsingOutcome Outcome) Dig(WalkerState state, WalkerDowsingRound round, int patch)
    {
        if (state.Walk is not { } walk || round.TriesLeft <= 0) return (state, round, WalkerDowsingOutcome.Missed);
        if (walk.Items.Count >= MaxHeld) return (state, round, WalkerDowsingOutcome.Full);
        if (patch == round.Patch)
            return (state with { Walk = walk with { Items = [.. walk.Items, round.Item] } }, round with { TriesLeft = 0 }, WalkerDowsingOutcome.Found);
        var left = round with { TriesLeft = round.TriesLeft - 1 };
        if (left.TriesLeft == 0) return (state, left, WalkerDowsingOutcome.Missed);
        return (state, left, Math.Abs(patch - round.Patch) == 1 ? WalkerDowsingOutcome.Close : WalkerDowsingOutcome.Far);
    }

    /// <summary>The walk ends: what the Pokémon brings home. EXP is one per step, friendship one
    /// per 256 steps; the state forgets the walk and keeps the watts.</summary>
    /// <summary>The once-only key of a walk's homecoming: when it started, its seed and who walked.</summary>
    public static string HomeKey(WalkerWalk walk) =>
        $"walk:{walk.Started.ToUnixTimeMilliseconds()}:{walk.Seed}:{walk.ResidentId}";

    public static (WalkerState State, WalkerHomecoming Homecoming) Return(WalkerState state)
    {
        if (state.Walk is not { } walk) throw new InvalidOperationException("Nobody is on a walk.");
        var home = new WalkerHomecoming(walk.Steps, (uint)walk.Steps, walk.Steps / StepsPerFriendship, walk.Catches, walk.Items);
        return (state with { Walk = null }, home);
    }

    /// <summary>The course a HeartGold/SoulSilver sync opens in the save: only one per sync, as on
    /// the device; the lowest open here and shut there that the save's language has.</summary>
    public static int? CourseToSend(WalkerState state, Func<int, bool> openInSave, Func<int, bool> availableInSave) =>
        Unlocked(state).Select(c => c.Index).Where(i => !openInSave(i) && availableInSave(i)).Select(i => (int?)i).FirstOrDefault();
}
