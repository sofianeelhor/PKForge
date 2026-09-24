using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>The Autopilot review's groups and sentences, built from tiny synthetic plans.</summary>
public sealed class LivingDexPlanSummaryTests
{
    private const int Bulbasaur = 1, Charmander = 4, Squirtle = 7, Pikachu = 25, Kadabra = 64, Alakazam = 65, Eevee = 133;

    private static readonly LivingDexCatalog Catalog = new(
        [Bulbasaur, Charmander, Squirtle, Pikachu, Kadabra, Alakazam, Eevee],
        new Dictionary<int, IReadOnlyList<int>>(),
        [new LivingDexTradeEvolution(Kadabra, 0, Alakazam, 0)]);

    private static LivingDexHolding Mon(int species, int box = 0, int slot = 0, int gen = 4) => new(species, 0, false, box, slot, gen);

    private static LivingDexSource Save(string id, int gen, int free, params LivingDexHolding[] holdings) =>
        new(id, id.ToUpperInvariant(), LivingDexSourceKind.Save, gen, 493, holdings,
            FreeSlots: [.. Enumerable.Range(0, free).Select(i => new SlotRef(5, i))]);

    private static LivingDexSource Bank(params LivingDexHolding[] holdings) =>
        new(LivingDexPlanner.BankId, "Bank", LivingDexSourceKind.Bank, 0, 1025, holdings, BoxCount: 2);

    [Fact]
    public void GroupsFollowTheReviewOrderAndSpeakPlainly()
    {
        var dest = Save("dest", 4, 30, Mon(Bulbasaur));
        var b = Save("b", 4, 0, Mon(Charmander, 0, 0), Mon(Charmander, 0, 1), Mon(Kadabra, 0, 2), Mon(Kadabra, 0, 3), Mon(Kadabra, 0, 4));
        var c = Save("c", 4, 0, Mon(Squirtle, 1, 0), Mon(Squirtle, 1, 1));
        var plan = LivingDexPlanner.Plan(Catalog, [dest, b, c], new LivingDexOptions("dest"));

        var groups = LivingDexPlanSummary.Group(plan);

        Assert.Equal([LivingDexGroupKind.Move, LivingDexGroupKind.Evolve, LivingDexGroupKind.Catch], groups.Select(g => g.Kind));
        var move = groups[0];
        Assert.Equal("Move 3 Pokémon from 2 games", move.Title);
        Assert.Equal("B 2 · C 1", move.Detail);
        Assert.Equal("Evolve 1 by trade on the way", groups[1].Title);
        Assert.Equal($"Still to catch: {plan.Guides}", groups[2].Title);
        Assert.Equal(plan.Steps.Count, groups.Sum(g => g.Steps.Count));
    }

    [Fact]
    public void ASingleSourceIsNamedAndTheBankIsCalledTheBank()
    {
        var dest = Save("dest", 4, 30);
        var plan = LivingDexPlanner.Plan(Catalog, [dest, Bank(Mon(Pikachu, 0, 0))], new LivingDexOptions("dest"));

        var move = Assert.Single(LivingDexPlanSummary.Group(plan), g => g.Kind == LivingDexGroupKind.Move);
        Assert.Equal("Move 1 Pokémon from the Bank", move.Title);
        Assert.Contains("DEST", move.Detail);
    }

    [Fact]
    public void BlockedAndDryRunRefusedStepsGoToCannotMove()
    {
        // No room in the destination: the fill is blocked.
        var dest = Save("dest", 4, 0);
        var b = Save("b", 4, 0, Mon(Charmander, 0, 0), Mon(Charmander, 0, 1), Mon(Squirtle, 0, 2), Mon(Squirtle, 0, 3));
        var blocked = LivingDexPlanner.Plan(Catalog, [dest, b], new LivingDexOptions("dest"));
        var groups = LivingDexPlanSummary.Group(blocked);
        Assert.DoesNotContain(groups, g => g.Kind == LivingDexGroupKind.Move);
        Assert.Equal("2 Pokémon can't move", Assert.Single(groups, g => g.Kind == LivingDexGroupKind.CannotMove).Title);

        var roomy = LivingDexPlanner.Plan(Catalog, [Save("dest", 4, 30), b], new LivingDexOptions("dest"));
        var refused = roomy.Steps.First(s => s.Fills);
        var afterDryRun = LivingDexPlanSummary.Group(roomy, s => ReferenceEquals(s, refused));
        Assert.Equal("Move 1 Pokémon from B", Assert.Single(afterDryRun, g => g.Kind == LivingDexGroupKind.Move).Title);
        Assert.Same(refused, Assert.Single(Assert.Single(afterDryRun, g => g.Kind == LivingDexGroupKind.CannotMove).Steps));
    }

    [Fact]
    public void HeadlineSaysWhatTheDestinationBecomes()
    {
        var dest = Save("dest", 4, 30, Mon(Bulbasaur));
        var b = Save("b", 4, 0, Mon(Charmander, 0, 0), Mon(Charmander, 0, 1));
        var plan = LivingDexPlanner.Plan(Catalog, [dest, b], new LivingDexOptions("dest"));
        Assert.Equal("DEST's living dex goes from 1 to 2 of 7 (+1).", LivingDexPlanSummary.Headline(plan));

        var bankPlan = LivingDexPlanner.Plan(Catalog, [Bank(), Save("b", 4, 0, Mon(Charmander))], new LivingDexOptions(LivingDexPlanner.BankId));
        Assert.StartsWith("Your Bank living dex has 0 of 7.", LivingDexPlanSummary.Headline(bankPlan));
    }

    [Fact]
    public void CaveatsAreWordsNotTagsAndSeriousOnesComeFirst()
    {
        var caveats = LivingDexPlanSummary.Caveats(LivingDexWarnings.Downgrade | LivingDexWarnings.LastCopy, landsLegal: false);
        Assert.Equal(["may be flagged", "only copy", "older game"], caveats.Select(c => c.Short));
        Assert.True(caveats[0].Serious);
        Assert.False(caveats[2].Serious);
        Assert.Empty(LivingDexPlanSummary.Caveats(LivingDexWarnings.None));
    }
}
