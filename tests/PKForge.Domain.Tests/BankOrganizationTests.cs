using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The vault's organizer logic: the filter predicates, the sort comparators and the
/// placement arithmetic, all pure functions of the index record. The facts the logic
/// cannot know from the index alone (names, typings, eggs) are faked here, so the
/// tests pin exactly the rules the bank grid and the search overlay share.
/// </summary>
public sealed class BankOrganizationTests
{
    /// <summary>
    /// The facts the vault reads outside its index. Counts egg probes so the tests can
    /// pin that the byte-level check really is the last resort of a narrowed view.
    /// </summary>
    private sealed class FakeFacts : IBankFacts
    {
        public Dictionary<int, string> Names { get; } = [];
        public HashSet<Guid> Eggs { get; } = [];
        public Dictionary<(int Species, int Form), int[]> TypeMap { get; } = [];
        public int EggProbes { get; private set; }

        public string SpeciesName(int species) => Names.GetValueOrDefault(species, "");
        public bool IsEgg(BankEntry entry)
        {
            EggProbes++;
            return Eggs.Contains(entry.Id);
        }
        public IReadOnlyList<int> Types(int species, int form) => TypeMap.GetValueOrDefault((species, form), []);
        public Dictionary<Guid, int> Genders { get; } = [];
        public Dictionary<Guid, int> Balls { get; } = [];
        public int Gender(BankEntry entry) => Genders.GetValueOrDefault(entry.Id, 2);
        public int Ball(BankEntry entry) => Balls.GetValueOrDefault(entry.Id);
    }

    private static readonly DateTimeOffset Deposit = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static BankEntry Entry(
        int species, string nickname = "", int level = 50, bool shiny = false,
        int generation = 3, string source = "Emerald", int form = 0,
        int box = 0, int slot = 0, DateTimeOffset? added = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), box, slot,
            new BankEntryInfo(species, form, shiny, nickname, level, generation, source),
            added ?? Deposit);

    private static FakeFacts StandardFacts() => new()
    {
        Names =
        {
            [1] = "Bulbasaur", [4] = "Charmander", [7] = "Squirtle", [25] = "Pikachu",
            [133] = "Eevee", [144] = "Articuno", [151] = "Mew", [448] = "Lucario",
        },
    };

    // ── Sorting ────────────────────────────────────────────────────────────────

    [Fact]
    public void SortsByDexNumberThenForm()
    {
        var facts = StandardFacts();
        var entries = new[] { Entry(7), Entry(4, form: 1), Entry(4), Entry(1) };
        var ordered = BankSorting.Order(entries, BankSortOrder.DexNumber, facts);
        Assert.Equal([1, 4, 4, 7], ordered.Select(e => e.Info.Species).ToArray());
        Assert.Equal([0, 0, 1, 0], ordered.Select(e => e.Info.Form).ToArray());
    }

    [Fact]
    public void SortsBySpeciesNameAlphabetically()
    {
        var facts = StandardFacts();
        var entries = new[] { Entry(7), Entry(1), Entry(25), Entry(4) };
        var ordered = BankSorting.Order(entries, BankSortOrder.SpeciesName, facts);
        Assert.Equal(["Bulbasaur", "Charmander", "Pikachu", "Squirtle"],
            ordered.Select(e => facts.SpeciesName(e.Info.Species)).ToArray());
    }

    [Fact]
    public void SortsByLevelStrongestFirst()
    {
        var facts = StandardFacts();
        var entries = new[] { Entry(1, level: 12), Entry(4, level: 99), Entry(7, level: 50) };
        var ordered = BankSorting.Order(entries, BankSortOrder.LevelDesc, facts);
        Assert.Equal([99, 50, 12], ordered.Select(e => e.Info.Level).ToArray());
    }

    [Fact]
    public void SortsShiniesFirst()
    {
        var facts = StandardFacts();
        var entries = new[] { Entry(1, shiny: false), Entry(4, shiny: true), Entry(7, shiny: false), Entry(25, shiny: true) };
        var ordered = BankSorting.Order(entries, BankSortOrder.ShinyFirst, facts);
        Assert.Equal([4, 25, 1, 7], ordered.Select(e => e.Info.Species).ToArray());
    }

    [Fact]
    public void SortsByDepositDate()
    {
        var facts = StandardFacts();
        var oldest = Entry(1, added: Deposit.AddDays(-2));
        var middle = Entry(4, added: Deposit);
        var newest = Entry(7, added: Deposit.AddDays(2));
        Assert.Equal([newest.Id, middle.Id, oldest.Id],
            BankSorting.Order([oldest, middle, newest], BankSortOrder.NewestAdded, facts).Select(e => e.Id));
        Assert.Equal([oldest.Id, middle.Id, newest.Id],
            BankSorting.Order([newest, middle, oldest], BankSortOrder.OldestAdded, facts).Select(e => e.Id));
    }

    [Fact]
    public void SortsByGenerationThenDexNumber()
    {
        var facts = StandardFacts();
        var entries = new[] { Entry(133, generation: 4), Entry(4, generation: 3), Entry(1, generation: 3) };
        var ordered = BankSorting.Order(entries, BankSortOrder.Generation, facts);
        Assert.Equal([1, 4, 133], ordered.Select(e => e.Info.Species).ToArray());
    }

    [Fact]
    public void SortsByNicknameCaseInsensitively()
    {
        var facts = StandardFacts();
        var entries = new[] { Entry(1, nickname: "zeta"), Entry(4, nickname: "ALPHA"), Entry(7, nickname: "Beta") };
        var ordered = BankSorting.Order(entries, BankSortOrder.Nickname, facts);
        Assert.Equal(["ALPHA", "Beta", "zeta"], ordered.Select(e => e.Info.Nickname).ToArray());
    }

    [Fact]
    public void EveryOrderIsATotalOrderAcrossPermutations()
    {
        // Same species, same form, same deposit date: only the id can break the tie, so
        // two banks holding the same mons in different orders must settle identically.
        var facts = StandardFacts();
        var first = Entry(25, added: Deposit, id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var second = Entry(25, added: Deposit, id: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        foreach (var order in Enum.GetValues<BankSortOrder>())
        {
            var forward = BankSorting.Order([first, second], order, facts).Select(e => e.Id).ToArray();
            var reversed = BankSorting.Order([second, first], order, facts).Select(e => e.Id).ToArray();
            Assert.Equal(forward, reversed);
        }
    }

    // ── Filtering ──────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultFilterKeepsEverything()
    {
        var facts = StandardFacts();
        var entries = new[] { Entry(1), Entry(4, shiny: true), Entry(151, generation: 1) };
        Assert.All(entries, e => Assert.True(BankFilter.None.Matches(e, facts)));
        Assert.False(BankFilter.None.IsActive);
        Assert.Equal(0, BankFilter.None.ActiveCount);
    }

    [Fact]
    public void FiltersByShinyGenerationSourceAndLevelRange()
    {
        var facts = StandardFacts();
        var sparky = Entry(25, nickname: "Sparky", level: 50, shiny: true, generation: 7, source: "Emerald");
        var plain = Entry(4, nickname: "Charmander", level: 10, shiny: false, generation: 3, source: "FireRed");

        Assert.False(new BankFilter { ShinyOnly = true }.Matches(plain, facts));
        Assert.True(new BankFilter { ShinyOnly = true }.Matches(sparky, facts));

        Assert.True(new BankFilter { Generation = 7 }.Matches(sparky, facts));
        Assert.False(new BankFilter { Generation = 3 }.Matches(sparky, facts));

        Assert.True(new BankFilter { SourceName = "emerald" }.Matches(sparky, facts)); // case-insensitive
        Assert.False(new BankFilter { SourceName = "FireRed" }.Matches(sparky, facts));

        Assert.True(new BankFilter { LevelMin = 50, LevelMax = 50 }.Matches(sparky, facts)); // bounds are inclusive
        Assert.False(new BankFilter { LevelMin = 51 }.Matches(sparky, facts));
        Assert.False(new BankFilter { LevelMax = 49 }.Matches(sparky, facts));
    }

    [Fact]
    public void FiltersByRarity()
    {
        var facts = StandardFacts();
        var articuno = Entry(144);
        var mew = Entry(151);
        var pikachu = Entry(25);

        Assert.True(new BankFilter { Rarity = BankRarity.Legendary }.Matches(articuno, facts));
        Assert.False(new BankFilter { Rarity = BankRarity.Legendary }.Matches(mew, facts)); // mythical, not legendary
        Assert.False(new BankFilter { Rarity = BankRarity.Legendary }.Matches(pikachu, facts));

        Assert.True(new BankFilter { Rarity = BankRarity.Mythical }.Matches(mew, facts));
        Assert.False(new BankFilter { Rarity = BankRarity.Mythical }.Matches(articuno, facts));

        var both = new BankFilter { Rarity = BankRarity.LegendaryOrMythical };
        Assert.True(both.Matches(articuno, facts));
        Assert.True(both.Matches(mew, facts));
        Assert.False(both.Matches(pikachu, facts));
    }

    [Fact]
    public void FiltersByTypeEitherSideOfThePair()
    {
        var facts = StandardFacts();
        facts.TypeMap[(4, 0)] = [ParkType.Fire, ParkType.Fighting];
        var charmander = Entry(4);

        Assert.True(new BankFilter { TypeId = ParkType.Fire }.Matches(charmander, facts));
        Assert.True(new BankFilter { TypeId = ParkType.Fighting }.Matches(charmander, facts));
        Assert.False(new BankFilter { TypeId = ParkType.Water }.Matches(charmander, facts));
    }

    [Fact]
    public void DefaultNamedKeepsOnlySpeciesNamedMons()
    {
        var facts = StandardFacts();
        var filter = new BankFilter { DefaultNamedOnly = true };
        Assert.True(filter.Matches(Entry(1, nickname: "Bulbasaur"), facts)); // the species name again
        Assert.True(filter.Matches(Entry(1, nickname: "bulbasaur"), facts)); // casing does not count
        Assert.True(filter.Matches(Entry(1, nickname: "  Bulbasaur  "), facts)); // padding does not count
        Assert.True(filter.Matches(Entry(1, nickname: ""), facts)); // no nickname at all
        Assert.False(filter.Matches(Entry(1, nickname: "Sprout"), facts));
    }

    [Fact]
    public void QueryMatchesSpeciesNameOrNickname()
    {
        var facts = StandardFacts();
        var sparky = Entry(25, nickname: "Sparky");
        Assert.True(new BankFilter { Query = "spark" }.Matches(sparky, facts)); // nickname hit
        Assert.True(new BankFilter { Query = "PIKA" }.Matches(sparky, facts));  // species name hit
        Assert.True(new BankFilter { Query = "  pika " }.Matches(sparky, facts)); // trimmed
        Assert.False(new BankFilter { Query = "charmander" }.Matches(sparky, facts));
    }

    [Fact]
    public void EggOnlyKeepsUnhatchedEggs()
    {
        var facts = StandardFacts();
        var egg = Entry(133);
        var hatched = Entry(4);
        facts.Eggs.Add(egg.Id);

        Assert.True(new BankFilter { EggOnly = true }.Matches(egg, facts));
        Assert.False(new BankFilter { EggOnly = true }.Matches(hatched, facts));
    }

    [Fact]
    public void SetFiltersAreAllAnded()
    {
        var facts = StandardFacts();
        var filter = new BankFilter { ShinyOnly = true, Generation = 7 };
        Assert.True(filter.Matches(Entry(25, shiny: true, generation: 7), facts));
        Assert.False(filter.Matches(Entry(25, shiny: false, generation: 7), facts));
        Assert.False(filter.Matches(Entry(25, shiny: true, generation: 3), facts));
    }

    [Fact]
    public void EggProbeRunsOnlyWhenEveryCheapFilterPassed()
    {
        // The egg flag costs a file read, so a rejected entry must never be probed.
        var facts = StandardFacts();
        var egg = Entry(133, shiny: false);
        facts.Eggs.Add(egg.Id);

        var rejected = new BankFilter { ShinyOnly = true, EggOnly = true }; // fails shiny first
        Assert.False(rejected.Matches(egg, facts));
        Assert.Equal(0, facts.EggProbes);

        var accepted = new BankFilter { EggOnly = true };
        Assert.True(accepted.Matches(egg, facts));
        Assert.Equal(1, facts.EggProbes);
    }

    [Fact]
    public void ActivityFlagsCountWhatNarrowsTheVault()
    {
        Assert.False(new BankFilter { Query = "mew" }.IsActive is false); // the text box counts
        var narrow = new BankFilter
        {
            Query = "mew", ShinyOnly = true, Generation = 1, SourceName = "Red", TypeId = ParkType.Psychic,
            Rarity = BankRarity.Mythical, EggOnly = true, DefaultNamedOnly = true, LevelMin = 5, LevelMax = 50,
        };
        Assert.True(narrow.IsActive);
        // The text box is not counted; the level range is one filter, not two.
        Assert.Equal(8, narrow.ActiveCount);
    }

    // ── Placement arithmetic ───────────────────────────────────────────────────

    [Fact]
    public void IntoBoxFillsTheGapsInOrder()
    {
        var resident = Entry(1, box: 1, slot: 0);
        var gapKeeper = Entry(4, box: 1, slot: 2);
        var all = new[] { resident, gapKeeper, Entry(7, box: 0, slot: 0), Entry(25, box: 0, slot: 1) };

        var placements = BankPlacement.IntoBox([all[2].Id, all[3].Id], 1, all);
        Assert.Equal([(all[2].Id, 1, 1), (all[3].Id, 1, 3)], placements);
    }

    [Fact]
    public void IntoBoxSkipsEntriesAlreadyInThatBoxAndStopsWhenFull()
    {
        var staying = Entry(1, box: 1, slot: 0);
        var all = new List<BankEntry> { staying };
        for (var slot = 1; slot < IBankService.SlotsPerBox; slot++)
            if (slot != 5) all.Add(Entry(7 + slot, box: 1, slot: slot)); // box 1 full except slot 5

        var mover = Entry(133, box: 0, slot: 0);
        all.Add(mover);

        // The resident is not "moved into" its own box; the mover takes the one gap.
        var placements = BankPlacement.IntoBox([staying.Id, mover.Id], 1, all);
        Assert.Equal([(mover.Id, 1, 5)], placements);
    }

    [Fact]
    public void InsertRunSlidesTheDisplacedBack()
    {
        var a = Entry(1, box: 0, slot: 0);
        var b = Entry(4, box: 0, slot: 1);
        var c = Entry(7, box: 0, slot: 2);
        var mover = Entry(25, box: 2, slot: 0);
        var all = new[] { a, b, c, mover };

        var placements = BankPlacement.InsertRun([mover.Id], 0, 1, all);
        Assert.Equal(
        [
            (mover.Id, 0, 1), // the run lands at the chosen slot
            (a.Id, 0, 0),     // everything before stays put
            (b.Id, 0, 2),     // the rest slides back
            (c.Id, 0, 3),
        ], placements);
    }

    [Fact]
    public void InsertRunTrimsItselfToTheRoomTheBoxHas()
    {
        // A box with one gap (slot 15): the run and the displaced have to share the
        // 30 slots, so exactly one mover fits.
        var all = new List<BankEntry>();
        for (var slot = 0; slot < IBankService.SlotsPerBox; slot++)
            if (slot != 15) all.Add(Entry(1 + slot, box: 0, slot: slot));
        var movers = Enumerable.Range(0, 3).Select(i => Entry(133, box: 1, slot: i)).ToList();
        all.AddRange(movers);

        // From slot 29 there is no room at all: the box stands exactly as it was.
        Assert.Empty(BankPlacement.InsertRun(movers.Select(e => e.Id).ToArray(), 0, 29, all));

        // From slot 0 exactly one mover fits; the rest are left behind, not crammed in.
        var placements = BankPlacement.InsertRun(movers.Select(e => e.Id).ToArray(), 0, 0, all);
        Assert.Equal(IBankService.SlotsPerBox, placements.Count);
        Assert.Contains((movers[0].Id, 0, 0), placements);
        Assert.DoesNotContain(placements, p => p.Id == movers[1].Id);
        Assert.DoesNotContain(placements, p => p.Id == movers[2].Id);
    }

    [Fact]
    public void ReorderLandsOneBoxFromItsFirstSlotAndTheVaultFromBoxOne()
    {
        var ordered = new[]
        {
            Entry(1, box: 3, slot: 7), Entry(4, box: 0, slot: 0), Entry(7, box: 1, slot: 4),
        };

        var box = BankPlacement.Reorder(2, ordered);
        Assert.Equal([(ordered[0].Id, 2, 0), (ordered[1].Id, 2, 1), (ordered[2].Id, 2, 2)], box);

        var vault = BankPlacement.Reorder(null, ordered);
        Assert.Equal([(ordered[0].Id, 0, 0), (ordered[1].Id, 0, 1), (ordered[2].Id, 0, 2)], vault);
    }

    [Fact]
    public void ReorderSpillsIntoNewBoxesPastThirty()
    {
        var ordered = Enumerable.Range(0, IBankService.SlotsPerBox + 2)
            .Select((_, i) => Entry(1 + i, box: 0, slot: 0))
            .ToList();
        var placements = BankPlacement.Reorder(null, ordered);
        Assert.Equal((ordered[0].Id, 0, 0), placements[0]);
        Assert.Equal((ordered[IBankService.SlotsPerBox - 1].Id, 0, IBankService.SlotsPerBox - 1), placements[29]);
        Assert.Equal((ordered[IBankService.SlotsPerBox].Id, 1, 0), placements[30]);
        Assert.Equal((ordered[IBankService.SlotsPerBox + 1].Id, 1, 1), placements[31]);
    }

    [Fact]
    public void ChangedCountCountsOnlyEntriesThatActuallyMove()
    {
        var settled = Entry(1, box: 0, slot: 0);
        var moving = Entry(4, box: 0, slot: 1);
        var all = new[] { settled, moving };
        var placements = new[] { (settled.Id, 0, 0), (moving.Id, 2, 3) };
        Assert.Equal(1, BankPlacement.ChangedCount(placements, all));
    }

    // ── The collector's extra axes ─────────────────────────────────────────────

    [Fact]
    public void ReverseFlipsOnlyThePrimaryKey()
    {
        var facts = StandardFacts();
        var entries = new[] { Entry(4, level: 10), Entry(1, level: 30), Entry(7, level: 20) };
        Assert.Equal([10, 20, 30],
            BankSorting.Order(entries, BankSortOrder.LevelDesc, facts, reverse: true).Select(e => e.Info.Level));
        Assert.Equal(["Squirtle", "Charmander", "Bulbasaur"],
            BankSorting.Order(entries, BankSortOrder.SpeciesName, facts, reverse: true)
                .Select(e => facts.SpeciesName(e.Info.Species)));

        // Ties under a reversed key still break ascending by dex number.
        var twins = new[] { Entry(7, level: 5), Entry(1, level: 5) };
        Assert.Equal([1, 7],
            BankSorting.Order(twins, BankSortOrder.LevelDesc, facts, reverse: true).Select(e => e.Info.Species));
    }

    [Fact]
    public void SortsByTypeWithMonoTypesLeadingAndUnknownsLast()
    {
        var facts = StandardFacts();
        facts.TypeMap[(1, 0)] = [ParkType.Grass, ParkType.Poison];
        facts.TypeMap[(4, 0)] = [ParkType.Fire];
        facts.TypeMap[(7, 0)] = [ParkType.Water];
        facts.TypeMap[(448, 0)] = [ParkType.Fighting, ParkType.Steel];
        var entries = new[] { Entry(1), Entry(999), Entry(7), Entry(448), Entry(4) };
        Assert.Equal([448, 4, 7, 1, 999],
            BankSorting.Order(entries, BankSortOrder.Type, facts).Select(e => e.Info.Species));
    }

    [Fact]
    public void SortsBySourceGameRarityGenderAndBall()
    {
        var facts = StandardFacts();
        var red = Entry(1, source: "Red");
        var emerald = Entry(4, source: "emerald");
        Assert.Equal([emerald.Id, red.Id],
            BankSorting.Order([red, emerald], BankSortOrder.SourceGame, facts).Select(e => e.Id));

        var commoner = Entry(25);
        var mythical = Entry(151);
        var legendary = Entry(144);
        Assert.Equal([144, 151, 25],
            BankSorting.Order([commoner, mythical, legendary], BankSortOrder.Rarity, facts).Select(e => e.Info.Species));

        var female = Entry(133);
        var male = Entry(133);
        var genderless = Entry(151);
        facts.Genders[female.Id] = 1;
        facts.Genders[male.Id] = 0;
        Assert.Equal([male.Id, female.Id, genderless.Id],
            BankSorting.Order([genderless, female, male], BankSortOrder.Gender, facts).Select(e => e.Id));

        var master = Entry(25);
        var poke = Entry(25);
        facts.Balls[master.Id] = 1;
        facts.Balls[poke.Id] = 4;
        Assert.Equal([master.Id, poke.Id],
            BankSorting.Order([poke, master], BankSortOrder.Ball, facts).Select(e => e.Id));
    }

    [Fact]
    public void FiltersByGenderAfterTheCheapChecks()
    {
        var facts = StandardFacts();
        var female = Entry(133, shiny: true);
        var male = Entry(133);
        facts.Genders[female.Id] = 1;
        facts.Genders[male.Id] = 0;
        var filter = new BankFilter { Gender = 1 };
        Assert.True(filter.Matches(female, facts));
        Assert.False(filter.Matches(male, facts));
        Assert.True(filter.IsActive);
        Assert.Equal(1, filter.ActiveCount);
    }

    [Fact]
    public void RangeMarkWalksTheVaultFlatInEitherDirection()
    {
        var a = Entry(1, box: 0, slot: 28);
        var b = Entry(4, box: 1, slot: 0);
        var c = Entry(7, box: 1, slot: 5);
        var outside = Entry(25, box: 1, slot: 6);
        var all = new[] { outside, c, a, b };
        var forward = BankSelection.Range((0, 20), (1, 5), all);
        Assert.Equal([(0, 28), (1, 0), (1, 5)], forward);
        Assert.Equal(forward, BankSelection.Range((1, 5), (0, 20), all));
    }
}
