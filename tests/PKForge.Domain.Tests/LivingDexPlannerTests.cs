using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The Living Dex Autopilot planner: moves only, safety rules enforced at plan time, and a
/// deterministic, dex-ordered result. Every case is a tiny synthetic collection.
/// </summary>
public sealed class LivingDexPlannerTests
{
    private const int Bulbasaur = 1, Charmander = 4, Squirtle = 7, Pikachu = 25, Vulpix = 37, Abra = 63, Kadabra = 64, Alakazam = 65, Eevee = 133;

    private static readonly LivingDexCatalog Catalog = new(
        [Bulbasaur, Charmander, Squirtle, Pikachu, Vulpix, Abra, Kadabra, Alakazam, Eevee],
        new Dictionary<int, IReadOnlyList<int>> { [Vulpix] = [0, 1] },
        [new LivingDexTradeEvolution(Kadabra, 0, Alakazam, 0)]);

    private static LivingDexHolding Mon(int species, int box = 0, int slot = 0, int gen = 4, bool shiny = false, int form = 0,
        bool egg = false, bool legal = true, Guid? bankId = null) =>
        new(species, form, shiny, box, slot, gen, egg, legal, bankId);

    private static IReadOnlyList<SlotRef> Free(int count, int box = 5) =>
        [.. Enumerable.Range(0, count).Select(i => new SlotRef(box + i / 30, i % 30))];

    private static LivingDexSource Save(string id, int gen, params LivingDexHolding[] holdings) =>
        new(id, id.ToUpperInvariant(), LivingDexSourceKind.Save, gen, 493, holdings, FreeSlots: Free(60));

    private static LivingDexSource Bank(int boxCount, params LivingDexHolding[] holdings) =>
        new(LivingDexPlanner.BankId, "Bank", LivingDexSourceKind.Bank, 0, 1025, holdings, BoxCount: boxCount);

    private static LivingDexPlan Plan(LivingDexOptions options, params LivingDexSource[] sources) =>
        LivingDexPlanner.Plan(Catalog, sources, options);

    private static LivingDexStep Fill(LivingDexPlan plan, int species) =>
        Assert.Single(plan.Steps, s => s.Fills && s.Species == species);

    private static bool Guided(LivingDexPlan plan, int species) =>
        plan.Steps.Any(s => s.Kind == LivingDexStepKind.Guide && s.Species == species);

    [Fact]
    public void DuplicatesAcrossSavesFillTheDestination()
    {
        var dest = Save("dest", 4, Mon(Bulbasaur));
        var b = Save("b", 4, Mon(Charmander, 0, 0), Mon(Charmander, 0, 1));
        var c = Save("c", 4, Mon(Squirtle, 1, 0), Mon(Squirtle, 1, 1), Mon(Squirtle, 1, 2));

        var plan = Plan(new LivingDexOptions("dest"), dest, b, c);

        Assert.Equal(1, plan.CoveredBefore);
        Assert.Equal("b", Fill(plan, Charmander).SourceId);
        // The save with the most copies gives first.
        Assert.Equal("c", Fill(plan, Squirtle).SourceId);
        Assert.Equal(3, plan.CoveredAfter);
        Assert.DoesNotContain(plan.Steps, s => s.Fills && s.Species == Bulbasaur);
        Assert.Equal(new Dictionary<string, int> { ["b"] = 1, ["c"] = 1 }, plan.OutgoingBySource);
        Assert.Equal(["dest", "b", "c"], plan.TouchedSaves);
    }

    [Fact]
    public void AnOnlyCopyNeverLeavesItsSaveUnlessAllowed()
    {
        var dest = Save("dest", 4);
        var b = Save("b", 4, Mon(Pikachu));

        var guarded = Plan(new LivingDexOptions("dest"), dest, b);
        Assert.True(Guided(guarded, Pikachu));
        Assert.False(guarded.HasWork);

        var allowed = Plan(new LivingDexOptions("dest", AllowLastCopies: true), dest, b);
        Assert.True(Fill(allowed, Pikachu).Warnings.HasFlag(LivingDexWarnings.LastCopy));
    }

    [Fact]
    public void TheOnlyCopyRuleCountsEveryFormAndThePartyOfASpecies()
    {
        // One Vulpix in a box and one in the party: the boxed one may go, the save keeps one.
        var dest = Save("dest", 4);
        var b = Save("b", 4, Mon(Vulpix, 0, 0), Mon(Vulpix, -1, 0));
        var plan = Plan(new LivingDexOptions("dest"), dest, b);
        var step = Fill(plan, Vulpix);
        Assert.Equal(0, step.Holding!.Box);
        Assert.False(step.Warnings.HasFlag(LivingDexWarnings.LastCopy));
    }

    [Fact]
    public void TheBankIsAVaultAndMayGiveItsOnlyCopy()
    {
        var dest = Save("dest", 4);
        var plan = Plan(new LivingDexOptions("dest"), dest, Bank(3, Mon(Pikachu, 0, 4, gen: 4, bankId: Guid.NewGuid())));
        Assert.Equal(LivingDexPlanner.BankId, Fill(plan, Pikachu).SourceId);
        Assert.Equal(["dest"], plan.TouchedSaves);
    }

    [Fact]
    public void PartyPokemonAndEggsStayByDefault()
    {
        var dest = Save("dest", 4);
        var b = Save("b", 4, Mon(Eevee, -1, 0), Mon(Eevee, -1, 1), Mon(Eevee, -1, 2), Mon(Pikachu, 0, 0, egg: true), Mon(Pikachu, 0, 1, egg: true));

        var plan = Plan(new LivingDexOptions("dest"), dest, b);
        Assert.True(Guided(plan, Eevee));
        Assert.True(Guided(plan, Pikachu)); // eggs never count or move

        var party = Plan(new LivingDexOptions("dest", IncludeParty: true), dest, b);
        Assert.True(Fill(party, Eevee).Warnings.HasFlag(LivingDexWarnings.FromParty));
        Assert.True(Guided(party, Pikachu));
    }

    [Theory]
    [InlineData(LivingDexExclusion.Hardcore)]
    [InlineData(LivingDexExclusion.RomHack)]
    [InlineData(LivingDexExclusion.ReadOnly)]
    [InlineData(LivingDexExclusion.Hidden)]
    [InlineData(LivingDexExclusion.UnsavedChanges)]
    public void ExcludedSavesAreNeverSourcesNorDestinations(LivingDexExclusion exclusion)
    {
        var dest = Save("dest", 4);
        var excluded = Save("hack", 4, Mon(Charmander, 0, 0), Mon(Charmander, 0, 1), Mon(Charmander, 0, 2)) with { Exclusion = exclusion };

        var plan = Plan(new LivingDexOptions("dest"), dest, excluded);
        Assert.True(Guided(plan, Charmander));
        Assert.DoesNotContain(plan.Steps, s => s.SourceId == "hack");
        Assert.Equal(exclusion, Assert.Single(plan.Skipped).Exclusion);
        Assert.DoesNotContain("hack", plan.TouchedSaves);

        Assert.Throws<InvalidOperationException>(() => Plan(new LivingDexOptions("hack"), dest, excluded));
    }

    [Fact]
    public void ASpareKadabraEvolvesByTradeToFillAlakazam()
    {
        var dest = Save("dest", 4);
        var bank = Bank(3, Mon(Kadabra, 0, 0, bankId: Guid.NewGuid()), Mon(Kadabra, 0, 1, bankId: Guid.NewGuid()));

        var plan = Plan(new LivingDexOptions("dest"), dest, bank);
        Assert.Equal(LivingDexStepKind.Move, Fill(plan, Kadabra).Kind);
        var evolve = Fill(plan, Alakazam);
        Assert.Equal(LivingDexStepKind.EvolveAndMove, evolve.Kind);
        Assert.Equal(Kadabra, evolve.FromSpecies);
        Assert.NotSame(Fill(plan, Kadabra).Holding, evolve.Holding);
    }

    [Fact]
    public void TheKadabraItsOwnSlotNeedsIsNeverEvolved()
    {
        // Two Kadabra in one save: one may leave (the save keeps the other), and it fills
        // Kadabra; Alakazam is left to catch rather than stripping the save.
        var dest = Save("dest", 4);
        var b = Save("b", 4, Mon(Kadabra, 0, 0), Mon(Kadabra, 0, 1));
        var plan = Plan(new LivingDexOptions("dest"), dest, b);
        Assert.Equal(LivingDexStepKind.Move, Fill(plan, Kadabra).Kind);
        Assert.True(Guided(plan, Alakazam));

        // The destination already has a Kadabra: the spare evolves instead.
        var owned = Plan(new LivingDexOptions("dest"), Save("dest", 4, Mon(Kadabra, 0, 0)), b);
        Assert.Equal(LivingDexStepKind.EvolveAndMove, Fill(owned, Alakazam).Kind);
    }

    [Fact]
    public void FormsModeWantsEveryCollectibleForm()
    {
        var dest = Save("dest", 7);
        var b = Save("b", 7, Mon(Vulpix, 0, 0, gen: 7), Mon(Vulpix, 0, 1, gen: 7),
            Mon(Vulpix, 0, 2, gen: 7, form: 1), Mon(Vulpix, 0, 3, gen: 7, form: 1));

        var species = Plan(new LivingDexOptions("dest"), dest, b);
        Assert.Single(species.Steps, s => s.Fills && s.Species == Vulpix);

        var forms = Plan(new LivingDexOptions("dest", Forms: true), dest, b);
        var vulpix = forms.Steps.Where(s => s.Fills && s.Species == Vulpix).ToList();
        Assert.Equal([0, 1], vulpix.Select(s => s.Form).Order());
        Assert.Equal(Catalog.Species.Count + 1, forms.TargetCount);
    }

    [Fact]
    public void ShinyModeOnlyCountsAndMovesShinies()
    {
        var dest = Save("dest", 4, Mon(Bulbasaur)); // a plain one does not fill the shiny dex
        var b = Save("b", 4, Mon(Bulbasaur, 0, 0, shiny: true), Mon(Bulbasaur, 0, 1, shiny: true),
            Mon(Charmander, 0, 2), Mon(Charmander, 0, 3));

        var plan = Plan(new LivingDexOptions("dest", Shiny: true), dest, b);
        Assert.Equal(0, plan.CoveredBefore);
        Assert.True(Fill(plan, Bulbasaur).Shiny);
        Assert.True(Guided(plan, Charmander));
    }

    [Fact]
    public void TheNormalDexKeepsShiniesForTheShinyDex()
    {
        var dest = Save("dest", 4);
        var b = Save("b", 4, Mon(Squirtle, 0, 0, shiny: true), Mon(Squirtle, 0, 1), Mon(Squirtle, 0, 2, shiny: true));
        Assert.False(Fill(Plan(new LivingDexOptions("dest"), dest, b), Squirtle).Shiny);
    }

    [Fact]
    public void ForwardSourcesWinAndDowngradesAreFlaggedOrRefused()
    {
        var dest = Save("dest", 4);
        var newer = Save("newer", 8, Mon(Pikachu, 0, 0, gen: 8), Mon(Pikachu, 0, 1, gen: 8), Mon(Eevee, 0, 2, gen: 8), Mon(Eevee, 0, 3, gen: 8));
        var older = Save("older", 3, Mon(Pikachu, 0, 0, gen: 3), Mon(Pikachu, 0, 1, gen: 3));

        var plan = Plan(new LivingDexOptions("dest"), dest, newer, older);
        var pikachu = Fill(plan, Pikachu);
        Assert.Equal("older", pikachu.SourceId);
        Assert.True(pikachu.Warnings.HasFlag(LivingDexWarnings.Conversion));
        Assert.True(Fill(plan, Eevee).Warnings.HasFlag(LivingDexWarnings.Downgrade));
        Assert.Equal(1, plan.Downgrades);

        var strict = Plan(new LivingDexOptions("dest", AllowDowngrades: false), dest, newer, older);
        Assert.True(Guided(strict, Eevee));
    }

    [Fact]
    public void SaveDestinationFillsFreeSlotsInDexOrderAndBlocksWhenFull()
    {
        var dest = Save("dest", 4) with { FreeSlots = [new SlotRef(2, 7), new SlotRef(3, 0)] };
        var b = Save("b", 4, Mon(Squirtle, 0, 0), Mon(Squirtle, 0, 1), Mon(Bulbasaur, 0, 2), Mon(Bulbasaur, 0, 3),
            Mon(Charmander, 0, 4), Mon(Charmander, 0, 5));

        var plan = Plan(new LivingDexOptions("dest"), dest, b);
        Assert.Equal(new SlotRef(2, 7), Fill(plan, Bulbasaur).Destination);
        Assert.Equal(new SlotRef(3, 0), Fill(plan, Charmander).Destination);
        var squirtle = Assert.Single(plan.Steps, s => s.Species == Squirtle && s.Kind == LivingDexStepKind.Move);
        Assert.NotNull(squirtle.Blocked);
        Assert.False(squirtle.IsRunnable);
        Assert.Equal(2, plan.CoveredAfter);
    }

    [Fact]
    public void BankDestinationLaysOutDexOrderedBoxesAndArrangesWhatItHas()
    {
        var charmanderId = Guid.NewGuid();
        var bank = Bank(4, Mon(Charmander, 1, 9, bankId: charmanderId));
        var b = Save("b", 4, Mon(Bulbasaur, 0, 0), Mon(Bulbasaur, 0, 1), Mon(Squirtle, 0, 2), Mon(Squirtle, 0, 3));

        var plan = Plan(new LivingDexOptions(LivingDexPlanner.BankId), bank, b);
        Assert.Equal(4, plan.BankStartBox);
        Assert.Equal(new SlotRef(4, 0), Fill(plan, Bulbasaur).Destination);
        Assert.Equal(new SlotRef(4, 2), Fill(plan, Squirtle).Destination);
        var arrange = Assert.Single(plan.Steps, s => s.Kind == LivingDexStepKind.Arrange);
        Assert.Equal(charmanderId, arrange.Holding!.BankId);
        Assert.Equal(new SlotRef(4, 1), arrange.Destination);
        Assert.Equal(1, plan.CoveredBefore);
        Assert.Equal(3, plan.CoveredAfter);
        Assert.DoesNotContain(LivingDexPlanner.BankId, plan.TouchedSaves);
        Assert.Equal(["b"], plan.TouchedSaves);

        // A second run keeps the same region and finds everything in place.
        var settled = Bank(5,
            Mon(Bulbasaur, 4, 0, bankId: Guid.NewGuid()), Mon(Charmander, 4, 1, bankId: charmanderId), Mon(Squirtle, 4, 2, bankId: Guid.NewGuid()));
        var again = Plan(new LivingDexOptions(LivingDexPlanner.BankId, BankStartBox: 4), settled, Save("b", 4, Mon(Bulbasaur), Mon(Squirtle, 0, 1)));
        Assert.False(again.HasWork);
        Assert.Equal(3, again.CoveredBefore);
    }

    [Fact]
    public void ABankSlotHeldByAnotherPokemonBlocksRatherThanSwaps()
    {
        var bank = Bank(4, Mon(Eevee, 4, 0, bankId: Guid.NewGuid()), Mon(Eevee, 0, 0, bankId: Guid.NewGuid()));
        var b = Save("b", 4, Mon(Bulbasaur, 0, 0), Mon(Bulbasaur, 0, 1));
        var plan = Plan(new LivingDexOptions(LivingDexPlanner.BankId, BankStartBox: 4), bank, b);
        var bulbasaur = Assert.Single(plan.Steps, s => s.Species == Bulbasaur);
        Assert.NotNull(bulbasaur.Blocked);
    }

    [Fact]
    public void TheDestinationCapBoundsTheTargetAndStorableFiltersIt()
    {
        var gen1 = new LivingDexSource("rb", "Red", LivingDexSourceKind.Save, 1, 151, [], FreeSlots: Free(30),
            Storable: (species, _) => species != Eevee);
        var plan = Plan(new LivingDexOptions("rb"), gen1, Save("b", 4));
        Assert.Equal(Catalog.Species.Count - 1, plan.TargetCount);
        Assert.False(Guided(plan, Eevee));
    }

    [Fact]
    public void EveryPokemonIsUsedAtMostOnce()
    {
        var dest = Save("dest", 4);
        var bank = Bank(3, [.. Enumerable.Range(0, 5).Select(i => Mon(Kadabra, 0, i, bankId: Guid.NewGuid()))]);
        var plan = Plan(new LivingDexOptions("dest"), dest, bank);
        var used = plan.Steps.Where(s => s.Holding is not null).Select(s => s.Holding!).ToList();
        Assert.Equal(used.Count, used.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(2, used.Count); // Kadabra + Alakazam, the other three stay in the Bank
    }

    [Fact]
    public void GuideStepsCoverEverythingNobodyOwnsInDexOrder()
    {
        var plan = Plan(new LivingDexOptions("dest"), Save("dest", 4, Mon(Pikachu)));
        var guides = plan.Steps.Where(s => s.Kind == LivingDexStepKind.Guide).Select(s => s.Species).ToList();
        Assert.Equal(Catalog.Species.Where(s => s != Pikachu), guides);
        Assert.Equal(1, plan.OwnedAnywhere);
    }
}
