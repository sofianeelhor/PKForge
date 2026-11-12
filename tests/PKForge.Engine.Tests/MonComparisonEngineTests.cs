using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>The comparison over real summaries from two generations: a Gen 3 slot against a
/// Gen 8 one, and a Gen 1 one against a Gen 9 one, built in memory.</summary>
public sealed class MonComparisonEngineTests
{
    private static readonly MonSummaryService Summaries = new();

    [Fact]
    public void ComparesAGen3PokemonWithAGen8One()
    {
        using var g3 = Legal(3, 25);
        using var g8 = Legal(8, 25);
        var a = Summaries.Build(g3, 0, 0, analyzeLegality: true)!;
        var b = Summaries.Build(g8, 0, 0, analyzeLegality: true)!;
        var c = MonComparison.Build(a, "Emerald", b, "Bank");

        Assert.Equal("PK3", c.Left.Format);
        Assert.Equal("PK8", c.Right.Format);
        var profile = c.Sections.Single(s => s.Title == "Profile").Rows;
        Assert.Equal(CompareEdge.Same, profile.Single(r => r.Label == "Species").Edge);
        Assert.NotEqual(MonComparison.Missing, profile.Single(r => r.Label == "Nature").Left);
        Assert.NotEqual(MonComparison.Missing, profile.Single(r => r.Label == "Ability").Right);
        var origin = c.Sections.Single(s => s.Title == "Origin").Rows;
        Assert.Equal("Legal", origin.Single(r => r.Label == "Legality").Left);
        Assert.Equal("Legal", origin.Single(r => r.Label == "Legality").Right);
        // Gen 3 keeps no met date; Gen 8 does.
        Assert.Equal(MonComparison.Missing, origin.Single(r => r.Label == "Met on").Left);
        Assert.NotEqual(MonComparison.Missing, origin.Single(r => r.Label == "Met on").Right);
        // Each game's own base stats: Pikachu's defenses were raised in Gen 6.
        var bases = c.Sections.Single(s => s.Title == "Base stats").Bars;
        Assert.Equal(CompareEdge.Same, bases.Single(b => b.Label == "HP").Edge);
        Assert.Equal(CompareEdge.Right, bases.Single(b => b.Label == "Def").Edge);
        Assert.Equal(CompareEdge.Right, bases.Single(b => b.Label == "Total").Edge);
        Assert.All(c.Sections.Single(s => s.Title == "Stats").Bars, bar => Assert.True(bar.Left > 0 && bar.Right > 0));
    }

    [Fact]
    public void AGen1PokemonShowsDashesWhereItStoresNothing()
    {
        using var g1 = Legal(1, 25);
        using var g9 = Legal(9, 445);
        var c = MonComparison.Build(Summaries.Build(g1, 0, 0)!, "Red", Summaries.Build(g9, 0, 0)!, "Bank");
        var profile = c.Sections.Single(s => s.Title == "Profile").Rows;
        foreach (var label in new[] { "Nature", "Ability", "Held item", "Ball" })
        {
            var row = profile.Single(r => r.Label == label);
            Assert.Equal(MonComparison.Missing, row.Left);
            Assert.NotEqual(MonComparison.Missing, row.Right);
            Assert.Equal(CompareEdge.None, row.Edge);
        }
        Assert.Equal(CompareEdge.Differs, profile.Single(r => r.Label == "Species").Edge);
        // DVs against IVs: never ranked.
        Assert.All(c.Sections.Single(s => s.Title == "IVs").Bars, bar => Assert.Equal(CompareEdge.None, bar.Edge));
        Assert.Equal(MonComparison.Missing, c.Sections.Single(s => s.Title == "Origin").Rows.Single(r => r.Label == "Ribbons").Left);
    }

    private static SaveEngineSession Legal(int generation, int species)
    {
        var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(generation);
        var outcome = new LegalizerService().Generate(session, 0, 0,
            new GenerationRequest(species, 50, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null, Form: 0));
        Assert.True(outcome.Success, outcome.Message);
        return session;
    }
}
