using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class ParkSelectionTests
{
    [Fact]
    public void RandomSampleHasNoDuplicatesAndNeverExceedsAvailableResidents()
    {
        string[] candidates = ["a", "b", "a", "c"];
        var actual = ParkSelection.Select(candidates, x => x, true, 12, [], new Random(5));
        Assert.Equal(3, actual.Count);
        Assert.Equal(3, actual.Distinct().Count());
        Assert.All(actual, id => Assert.Contains(id, candidates));
    }

    [Fact]
    public void ManualRosterPreservesUserOrderAndDropsMissingIdentities()
    {
        var actual = ParkSelection.Select(new[] { "a", "b", "c" }, x => x, false, 3,
            ["missing", "c", "c", "a"]);
        Assert.Equal(new[] { "c", "a" }, actual);
    }

    [Fact]
    public void EmptyManualSelectionDoesNotSilentlySelectOtherPokemon()
    {
        Assert.Empty(ParkSelection.Select(new[] { "a", "b" }, x => x, false, 6, []));
        Assert.Empty(ParkSelection.Select(Array.Empty<string>(), x => x, true, 6, []));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(6, 6)]
    [InlineData(100, 12)]
    public void VisitorCountIsBounded(int requested, int expected)
    {
        var candidates = Enumerable.Range(1, 30).Select(i => i.ToString()).ToArray();
        Assert.Equal(expected, ParkSelection.Select(candidates, x => x, true, requested, []).Count);
        Assert.Equal(expected, ParkSelection.Select(candidates, x => x, false, requested, candidates).Count);
    }

    [Fact]
    public void RerollsCanReachEveryCandidate()
    {
        var candidates = Enumerable.Range(1, 30).ToArray();
        var seen = Enumerable.Range(0, 100).SelectMany(seed =>
            ParkSelection.Select(candidates, x => x.ToString(), true, 6, [], new Random(seed))).ToHashSet();
        Assert.Equal(candidates.Length, seen.Count);
    }
}
