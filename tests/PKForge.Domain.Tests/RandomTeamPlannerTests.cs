using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The random team roller's pure half: filters, distinct sampling, and level band.
/// Seeded Random makes every roll deterministic, so these assertions are exact.
/// </summary>
public sealed class RandomTeamPlannerTests
{
    // 4 = Charmander (NFE), 6 = Charizard (fully evolved), 25 = Pikachu (NFE),
    // 26 = Raichu, 150 = Mewtwo (legendary), 151 = Mew (mythical), 129 = Magikarp (NFE).
    private static readonly IReadOnlySet<int> Nfe = new HashSet<int> { 4, 25, 129 };

    private static RandomTeamOptions Options(
        int count = 6, int min = 50, int max = 50,
        bool noLegendaries = true, bool noDuplicates = true, bool allowNfe = false) =>
        new(count, min, max, noLegendaries, noDuplicates, allowNfe);

    [Fact]
    public void RespectsLegendaryAndNfeFiltersTogether()
    {
        var pool = new[] { 4, 6, 25, 26, 150, 151 };
        var team = RandomTeamPlanner.Plan(pool, Nfe, Options(), new Random(1));
        // Only fully-evolved, non-legendary ids survive the default filters.
        Assert.All(team, pick => Assert.Contains(pick.Species, new[] { 6, 26 }));
    }

    [Fact]
    public void AllowNfeAddsBaseStagesBack()
    {
        var pool = new[] { 4, 25, 129 };
        var team = RandomTeamPlanner.Plan(pool, Nfe, Options(allowNfe: true), new Random(1));
        Assert.Equal(3, team.Count);
        Assert.All(team, pick => Assert.Contains(pick.Species, pool));
    }

    [Fact]
    public void NoLegendariesOffKeepsLegendaryAndMythical()
    {
        var pool = new[] { 150, 151 };
        var team = RandomTeamPlanner.Plan(pool, Nfe, Options(count: 2, noLegendaries: false, allowNfe: true), new Random(1));
        Assert.Equal(2, team.Count);
        Assert.Contains(150, team.Select(p => p.Species));
        Assert.Contains(151, team.Select(p => p.Species));
    }

    [Fact]
    public void NoDuplicatesYieldsDistinctSpecies()
    {
        var pool = Enumerable.Range(1, 100).ToArray();
        var team = RandomTeamPlanner.Plan(pool, Nfe, Options(count: 6), new Random(7));
        Assert.Equal(6, team.Count);
        Assert.Equal(6, team.Select(p => p.Species).Distinct().Count());
    }

    [Fact]
    public void DuplicatesAllowedWhenAsked()
    {
        var pool = new[] { 6 };
        var team = RandomTeamPlanner.Plan(pool, Nfe, Options(count: 3, noDuplicates: false, allowNfe: false), new Random(3));
        // A one-species pool only fills three slots with replacement.
        Assert.Equal(3, team.Count);
        Assert.All(team, pick => Assert.Equal(6, pick.Species));
    }

    [Fact]
    public void SmallPoolWithoutDuplicatesFillsWhatItCan()
    {
        var pool = new[] { 6, 26 };
        var team = RandomTeamPlanner.Plan(pool, Nfe, Options(count: 6), new Random(9));
        Assert.Equal(2, team.Count);
    }

    [Fact]
    public void EmptyPoolAfterFiltersRollsNothing()
    {
        var team = RandomTeamPlanner.Plan(new[] { 150 }, Nfe, Options(), new Random(1));
        Assert.Empty(team);
    }

    [Fact]
    public void CountIsClampedToPartySize()
    {
        var pool = Enumerable.Range(1, 50).ToArray();
        var team = RandomTeamPlanner.Plan(pool, Nfe, Options(count: 40), new Random(1));
        Assert.Equal(6, team.Count);
    }

    [Theory]
    [InlineData(5, 10)]
    [InlineData(1, 100)]
    [InlineData(50, 50)]
    public void LevelsStayInsideTheBand(int min, int max)
    {
        var pool = Enumerable.Range(1, 60).ToArray();
        var team = RandomTeamPlanner.Plan(pool, Nfe, Options(count: 6, min: min, max: max, allowNfe: true), new Random(11));
        Assert.All(team, pick => Assert.InRange(pick.Level, Math.Min(min, max), Math.Max(min, max)));
    }

    [Theory]
    [InlineData(0, 5)]   // level 0 clamps up to 1
    [InlineData(120, 200)] // over 100 clamps down
    [InlineData(90, 10)]  // inverted band is normalized
    public void OutOfRangeBandsAreClamped(int min, int max)
    {
        var pool = Enumerable.Range(1, 60).ToArray();
        var team = RandomTeamPlanner.Plan(pool, Nfe, Options(count: 6, min: min, max: max, allowNfe: true), new Random(5));
        Assert.All(team, pick => Assert.InRange(pick.Level, 1, 100));
    }

    [Fact]
    public void SameSeedRollsTheSameTeam()
    {
        var pool = Enumerable.Range(1, 80).ToArray();
        var options = Options(count: 6, min: 40, max: 60, allowNfe: true);
        var first = RandomTeamPlanner.Plan(pool, Nfe, options, new Random(42));
        var second = RandomTeamPlanner.Plan(pool, Nfe, options, new Random(42));
        Assert.Equal(first, second);
    }
}
