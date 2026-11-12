using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class DexModesTests
{
    private static int _slot;

    private static DexHolder Bank(Guid? id = null, int box = 0, int slot = -1) =>
        new(LivingDexPlanner.BankId, "Bank", box, slot < 0 ? _slot++ % 30 : slot, id ?? Guid.NewGuid());

    private static DexHolder Save(string label, int box = 0, int slot = 0) => new("doc:" + label, label, box, slot);

    private static DexMon Mon(int species, int origin, bool shiny = false, DexHolder? where = null, int form = 0,
        bool female = false, bool gmax = false, int sweet = 0, bool egg = false) =>
        new(species, form, shiny, where ?? Bank(), origin, $"Gen {origin}", female, gmax, sweet, egg);

    // ── Origin Dex ──

    [Theory]
    [InlineData(1, 1)]
    [InlineData(151, 1)]
    [InlineData(152, 2)]
    [InlineData(386, 3)]
    [InlineData(387, 4)]
    [InlineData(899, 8)] // Wyrdeer: Legends: Arceus is Gen 8
    [InlineData(906, 9)]
    [InlineData(1025, 9)]
    [InlineData(0, 0)]
    [InlineData(1026, 0)]
    public void IntroGenerationFollowsTheNationalDexRanges(int species, int generation) =>
        Assert.Equal(generation, OriginDex.IntroGeneration(species));

    [Fact]
    public void OnlyPokemonFromTheSpeciesOwnEraCount()
    {
        var mons = new[]
        {
            Mon(1, origin: 1),           // Bulbasaur from Red: native
            Mon(4, origin: 7),           // Charmander from USUM: elsewhere
            Mon(252, origin: 3, shiny: true), // Treecko from Emerald: native shiny
            Mon(7, origin: 0),           // GO / unknown origin: never native
            Mon(25, origin: 1, egg: true), // eggs never count
        };
        var report = OriginDex.Compute(mons, Enumerable.Range(1, 386));

        Assert.Equal(386, report.Total);
        Assert.Equal(2, report.Native);
        Assert.Equal(1, report.NativeShiny);
        var charmander = report.Entries.Single(e => e.Species == 4);
        Assert.False(charmander.Owned);
        Assert.True(charmander.OnlyElsewhere);
        Assert.Single(charmander.Elsewhere);
        Assert.False(report.Entries.Single(e => e.Species == 25).OnlyElsewhere);

        var gen1 = report.Segments.Single(s => s.Generation == 1);
        Assert.Equal((1, 0, 2, 151), (gen1.Native, gen1.NativeShiny, gen1.ElsewhereOnly, gen1.Total));
        var gen3 = report.Segments.Single(s => s.Generation == 3);
        Assert.Equal((1, 1, 135), (gen3.Native, gen3.NativeShiny, gen3.Total));
        Assert.Equal(0, report.Segments.Single(s => s.Generation == 9).Total);
        Assert.Equal(384, report.Missing.Count());
    }

    [Fact]
    public void CandidatesListTheBankFirstThenSavesInBoxOrder()
    {
        var mons = new[]
        {
            Mon(1, 1, where: Save("Yellow", 2, 5)),
            Mon(1, 1, where: Save("Blue", 0, 3)),
            Mon(1, 1, where: Bank(box: 4, slot: 1)),
            Mon(1, 1, where: Save("Blue", 0, 1)),
        };
        var entry = OriginDex.Compute(mons, [1]).Entries.Single();
        Assert.Equal(["Bank", "Blue", "Blue", "Yellow"], entry.Native.Select(m => m.Where.SourceLabel));
        Assert.Equal(1, entry.Native[1].Where.Slot);
        Assert.Equal("Blue · box 01 slot 02", entry.Native[1].Where.Describe());
        Assert.Equal("Yellow · party 3", new DexHolder("x", "Yellow", -1, 2).Describe());
    }

    [Fact]
    public void SpeciesOutsideTheDexAreIgnored()
    {
        var report = OriginDex.Compute([Mon(2000, 9)], [0, 1, 1, 2000]);
        Assert.Equal([1], report.Entries.Select(e => e.Species));
    }

    // ── Extended Living Dex ──

    private static readonly DexSpeciesForms Pikachu = new(25, [new(0)], GenderDifference: true, GigantamaxForms: [0]);
    private static readonly DexSpeciesForms Meowth = new(52, [new(0), new(1, "Alola", true), new(2, "Galar", true)], GigantamaxForms: [0]);
    private static readonly DexSpeciesForms Unown = new(201, [.. Enumerable.Range(0, 28).Select(f => new DexFormInfo(f, $"{(char)('A' + Math.Min(f, 25))}"))]);
    private static readonly DexSpeciesForms Alcremie = new(869, [.. Enumerable.Range(0, 9).Select(f => new DexFormInfo(f, $"Cream {f}"))],
        GigantamaxForms: [0], Sweets: 7);
    private static readonly DexSpeciesForms[] Catalog = [Pikachu, Meowth, Unown, Alcremie];

    [Theory]
    [InlineData(DexFormCategories.None, 4)]
    [InlineData(DexFormCategories.Regional, 6)]
    [InlineData(DexFormCategories.OtherForms, 4 + 27 + 8)]
    [InlineData(DexFormCategories.GenderDifferences, 5)]
    [InlineData(DexFormCategories.Gigantamax, 7)]
    [InlineData(DexFormCategories.AlcremieSweets, 4 + 6)]
    [InlineData(DexFormCategories.OtherForms | DexFormCategories.AlcremieSweets, 4 + 27 + 8 + 9 * 6)]
    [InlineData(DexFormCategories.All, 4 + 2 + 27 + 8 + 1 + 3 + 9 * 6)]
    public void EachToggleAddsExactlyItsEntries(DexFormCategories categories, int expected) =>
        Assert.Equal(expected, ExtendedDex.Entries(Catalog, categories).Count);

    [Fact]
    public void EntriesStayInDexOrderWithVariantsAfterTheirForm()
    {
        var entries = ExtendedDex.Entries(Catalog, DexFormCategories.All);
        Assert.Equal(entries.OrderBy(e => e.Slot.Species).Select(e => e.Slot), entries.Select(e => e.Slot));
        var pikachu = entries.Where(e => e.Slot.Species == 25).Select(e => e.Slot.Variant).ToList();
        Assert.Equal([DexSlotVariant.Base, DexSlotVariant.Female, DexSlotVariant.Gigantamax], pikachu);
        Assert.Equal("Alola", entries.Single(e => e.Slot == new DexSlot(52, 1)).Label);
        Assert.Equal(DexFormCategories.Regional, entries.Single(e => e.Slot == new DexSlot(52, 2)).Category);
        Assert.Equal("Cream 3 · Love Sweet", entries.Single(e => e.Slot == new DexSlot(869, 3, DexSlotVariant.Sweet, 2)).Label);
        Assert.Equal(entries.Count, entries.Select(e => e.Slot).Distinct().Count());
    }

    [Fact]
    public void MostSpecificEntryFillsFirstAndEachPokemonFillsOnce()
    {
        var mons = new[]
        {
            Mon(25, 8, female: true, gmax: true), // one mon: gender beats gmax (♀ entry)
            Mon(25, 8, gmax: true),
            Mon(52, 7, form: 1),
        };
        var report = ExtendedDex.Compute(Catalog, mons, DexFormCategories.All);
        Assert.True(Cell(report, 25, 0, DexSlotVariant.Female).Owned);
        Assert.True(Cell(report, 25, 0, DexSlotVariant.Gigantamax).Owned);
        Assert.False(Cell(report, 25, 0).Owned); // nobody left for the male base
        Assert.True(Cell(report, 52, 1).Owned);
        Assert.False(Cell(report, 52, 0).Owned); // an Alolan Meowth is not the Kanto one when regionals count
        Assert.Equal(3, report.Owned);
        Assert.Equal(3, report.Cells.Where(c => c.Filled is not null).Select(c => c.Filled).Distinct().Count());
    }

    [Fact]
    public void FormsThatDoNotCountStillFillTheSpeciesBase()
    {
        var mons = new[] { Mon(52, 8, form: 2), Mon(25, 8, female: true), Mon(869, 8, form: 4, sweet: 3) };
        var report = ExtendedDex.Compute(Catalog, mons, DexFormCategories.None);
        Assert.Equal(3, report.Owned);
        Assert.Equal(4, report.Total);
        Assert.False(Cell(report, 201, 0).Owned);
    }

    [Fact]
    public void FemaleNeverStandsInForTheMaleLookWhenGenderCounts()
    {
        var mons = new[] { Mon(25, 8, female: true), Mon(25, 8, female: true) };
        var report = ExtendedDex.Compute(Catalog, mons, DexFormCategories.GenderDifferences);
        Assert.True(Cell(report, 25, 0, DexSlotVariant.Female).Owned);
        Assert.False(Cell(report, 25, 0).Owned);
    }

    [Fact]
    public void SweetsAreMatchedByFormArgumentAndPlainCreamsNeedSweetZero()
    {
        var mons = new[] { Mon(869, 8, form: 0, sweet: 0), Mon(869, 8, form: 0, sweet: 6), Mon(869, 8, form: 1, sweet: 6) };
        var report = ExtendedDex.Compute(Catalog, mons, DexFormCategories.OtherForms | DexFormCategories.AlcremieSweets);
        Assert.True(Cell(report, 869, 0).Owned);
        Assert.True(Cell(report, 869, 0, DexSlotVariant.Sweet, 6).Owned);
        Assert.True(Cell(report, 869, 1, DexSlotVariant.Sweet, 6).Owned);
        Assert.False(Cell(report, 869, 1).Owned);
        Assert.Equal((2, 54), report.Tally(DexFormCategories.AlcremieSweets));
    }

    [Fact]
    public void ShinyModeCountsOnlyShinies()
    {
        var mons = new[] { Mon(25, 8), Mon(52, 8, shiny: true) };
        var report = ExtendedDex.Compute(Catalog, mons, DexFormCategories.None, shiny: true);
        Assert.Equal(1, report.Owned);
        Assert.True(Cell(report, 52, 0).Owned);
        Assert.Equal((1, 4), (report.ByGeneration()[0].Owned, report.ByGeneration().Sum(g => g.Total)));
    }

    [Fact]
    public void TheBankIsPreferredOverSaves()
    {
        var mons = new[] { Mon(25, 8, where: Save("Sword")), Mon(25, 8, where: Bank()) };
        var report = ExtendedDex.Compute(Catalog, mons, DexFormCategories.None);
        Assert.True(Cell(report, 25, 0).Filled!.Where.IsBank);
    }

    private static ExtendedDexCell Cell(ExtendedDexReport report, int species, int form,
        DexSlotVariant variant = DexSlotVariant.Base, int sweet = 0) =>
        report.Cells.Single(c => c.Slot == new DexSlot(species, form, variant, sweet));

    // ── Box planner ──

    private static List<ExtendedDexCell> Cells(params (int Species, int Count)[] runs) =>
        [.. runs.SelectMany(r => Enumerable.Range(0, r.Count).Select(f => new ExtendedDexCell(new DexSlot(r.Species, f), DexFormCategories.None, "", null)))];

    [Fact]
    public void ContinuousLayoutFillsThirtyToABox()
    {
        var cells = Cells([.. Enumerable.Range(1, 65).Select(id => (id, 1))]);
        var boxes = DexBoxPlanner.Layout(cells, DexBoxBreaks.Continuous);
        Assert.Equal(3, boxes.Count);
        Assert.All(boxes, b => Assert.Equal(30, b.Cells.Count));
        Assert.Equal(5, boxes[2].Cells.Count(c => c is not null));
        Assert.Equal("#0001-0030", boxes[0].Title);
        Assert.Equal(Enumerable.Range(0, 65).Select(i => (int?)i), boxes.SelectMany(b => b.Cells).Where(c => c is not null));
    }

    [Fact]
    public void KeepingSpeciesTogetherPushesARunThatWouldStraddleToTheNextBox()
    {
        var cells = Cells([.. Enumerable.Range(1, 25).Select(id => (id, 1)), (201, 28), (202, 1)]);
        var boxes = DexBoxPlanner.Layout(cells, DexBoxBreaks.KeepSpeciesTogether);
        Assert.Equal(2, boxes.Count);
        Assert.Equal(25, boxes[0].Cells.Count(c => c is not null));
        Assert.Equal(29, boxes[1].Cells.Count(c => c is not null));
        Assert.Equal("#0201-0202", boxes[1].Title);

        // A run longer than a box still flows on instead of looping.
        var huge = DexBoxPlanner.Layout(Cells((1, 1), (869, 63)), DexBoxBreaks.KeepSpeciesTogether);
        Assert.Equal(64, huge.SelectMany(b => b.Cells).Count(c => c is not null));
    }

    [Fact]
    public void GenerationBreaksStartEachEraInAFreshBox()
    {
        var cells = Cells((150, 1), (151, 1), (152, 1), (252, 1));
        var boxes = DexBoxPlanner.Layout(cells, DexBoxBreaks.Generations);
        Assert.Equal(3, boxes.Count);
        Assert.Equal([2, 1, 1], boxes.Select(b => b.Cells.Count(c => c is not null)));
    }

    private static BankEntry Entry(Guid id, int box, int slot, int species) =>
        new(id, box, slot, new BankEntryInfo(species, 0, false, "", 5, 8, "Bank"), DateTimeOffset.UnixEpoch);

    [Fact]
    public void BankArrangementMovesPicksIntoPlaceAndLosesNothing()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var stranger = Guid.NewGuid(); var far = Guid.NewGuid();
        var bank = new List<BankEntry>
        {
            Entry(a, 5, 7, 1),        // Bulbasaur, elsewhere
            Entry(stranger, 0, 0, 999), // sits in the region with no planned slot
            Entry(b, 0, 1, 2),        // Ivysaur already in the region
            Entry(far, 3, 0, 500),    // untouched
        };
        var mons = new[]
        {
            Mon(1, 1, where: new DexHolder(LivingDexPlanner.BankId, "Bank", 5, 7, a)),
            Mon(2, 1, where: new DexHolder(LivingDexPlanner.BankId, "Bank", 0, 1, b)),
        };
        var catalog = Enumerable.Range(1, 3).Select(id => new DexSpeciesForms(id, [])).ToList();
        var report = ExtendedDex.Compute(catalog, mons, DexFormCategories.None);
        var layout = DexBoxPlanner.Layout(report.Cells, DexBoxBreaks.Continuous);
        var plan = DexBoxPlanner.PlanBank(report.Cells, layout, bank, 0, new HashSet<int>());

        Assert.Null(plan.Refusal);
        Assert.Equal((2, 1), (plan.Placed, plan.Displaced));
        var at = plan.Placements.ToDictionary(p => p.Id, p => (p.Box, p.Slot));
        Assert.Equal((0, 0), at[a]);
        Assert.Equal((0, 1), at[b]);
        Assert.Equal((1, 0), at[stranger]); // first free slot after the region
        Assert.False(at.ContainsKey(far));
        AssertNothingLost(bank, plan);
    }

    [Fact]
    public void DisplacedSkipLockedBoxesAndOccupiedSlots()
    {
        var x = Guid.NewGuid(); var y = Guid.NewGuid(); var keeper = Guid.NewGuid(); var pinned = Guid.NewGuid();
        var bank = new List<BankEntry>
        {
            Entry(x, 0, 0, 900), Entry(y, 0, 1, 901),
            Entry(keeper, 1, 0, 902), // box 1 slot 0 is taken and stays
            Entry(pinned, 2, 0, 903), // box 2 is locked
        };
        var report = ExtendedDex.Compute([new DexSpeciesForms(1, [])], [], DexFormCategories.None);
        var layout = DexBoxPlanner.Layout(report.Cells, DexBoxBreaks.Continuous);
        var plan = DexBoxPlanner.PlanBank(report.Cells, layout, bank, 0, new HashSet<int> { 2 });
        var at = plan.Placements.ToDictionary(p => p.Id, p => (p.Box, p.Slot));
        Assert.Equal((1, 1), at[x]);
        Assert.Equal((1, 2), at[y]);
        Assert.False(at.ContainsKey(pinned));
        AssertNothingLost(bank, plan);
    }

    [Fact]
    public void ALockedBoxInTheRegionRefusesAndAPickInALockedBoxStays()
    {
        var bank = new List<BankEntry> { Entry(Guid.NewGuid(), 0, 0, 1) };
        var cells = Cells((1, 1));
        var layout = DexBoxPlanner.Layout(cells, DexBoxBreaks.Continuous);
        var refused = DexBoxPlanner.PlanBank(cells, layout, bank, 0, new HashSet<int> { 0 });
        Assert.NotNull(refused.Refusal);
        Assert.Empty(refused.Placements);

        var id = Guid.NewGuid();
        var lockedPick = new List<BankEntry> { Entry(id, 4, 0, 1) };
        var filled = new List<ExtendedDexCell> { cells[0] with { Filled = Mon(1, 1, where: new DexHolder(LivingDexPlanner.BankId, "Bank", 4, 0, id)) } };
        var plan = DexBoxPlanner.PlanBank(filled, layout, lockedPick, 0, new HashSet<int> { 4 });
        Assert.Empty(plan.Placements);
        Assert.Equal(0, plan.Placed);
    }

    [Fact]
    public void FirstFreeStretchFindsEmptyUnlockedBoxesOrTheEnd()
    {
        var bank = new List<BankEntry> { Entry(Guid.NewGuid(), 0, 0, 1), Entry(Guid.NewGuid(), 3, 0, 1) };
        Assert.Equal(1, DexBoxPlanner.FirstFreeStretch(bank, 8, new HashSet<int>(), 2));
        Assert.Equal(4, DexBoxPlanner.FirstFreeStretch(bank, 8, new HashSet<int> { 2 }, 2));
        Assert.Equal(4, DexBoxPlanner.FirstFreeStretch(bank, 4, new HashSet<int>(), 40));
    }

    [Fact]
    public void ARealisticPlanOverAFullBankStaysAValidPermutation()
    {
        var random = new Random(7);
        var bank = new List<BankEntry>();
        var mons = new List<DexMon>();
        for (var box = 0; box < 12; box++)
            for (var slot = 0; slot < 30; slot++)
            {
                if (random.Next(4) == 0) continue;
                var id = Guid.NewGuid();
                var species = random.Next(1, 160);
                bank.Add(Entry(id, box, slot, species));
                mons.Add(Mon(species, 1, where: new DexHolder(LivingDexPlanner.BankId, "Bank", box, slot, id)));
            }
        var catalog = Enumerable.Range(1, 151).Select(id => new DexSpeciesForms(id, [])).ToList();
        var report = ExtendedDex.Compute(catalog, mons, DexFormCategories.None);
        var layout = DexBoxPlanner.Layout(report.Cells, DexBoxBreaks.Generations);
        var locked = new HashSet<int> { 9 };
        var plan = DexBoxPlanner.PlanBank(report.Cells, layout, bank, 2, locked);
        Assert.Null(plan.Refusal);
        Assert.Equal(report.Owned - report.Cells.Count(c => c.Filled?.Where.Box == 9), plan.Placed);
        AssertNothingLost(bank, plan);
        Assert.DoesNotContain(plan.Placements, p => locked.Contains(p.Box));
    }

    /// <summary>After the batch every entry sits in exactly one slot, no two share one, and the count is unchanged.</summary>
    private static void AssertNothingLost(List<BankEntry> bank, DexBankArrangement plan)
    {
        Assert.Equal(plan.Placements.Count, plan.Placements.Select(p => p.Id).Distinct().Count());
        var moved = plan.Placements.ToDictionary(p => p.Id, p => (p.Box, p.Slot));
        var after = bank.Select(e => moved.TryGetValue(e.Id, out var to) ? to : (e.Box, e.Slot)).ToList();
        Assert.Equal(bank.Count, after.Count);
        Assert.Equal(after.Count, after.Distinct().Count());
        Assert.All(plan.Placements, p => Assert.InRange(p.Slot, 0, 29));
    }
}
