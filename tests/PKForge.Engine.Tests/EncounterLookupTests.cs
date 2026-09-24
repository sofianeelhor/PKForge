using System.Diagnostics;
using PKForge.Engine;
using Xunit;
using Xunit.Abstractions;

namespace PKForge.Engine.Tests;

/// <summary>
/// The save-free "how do I get this?" lookup: every mainline game's answer for a
/// species line, with no open save involved.
/// </summary>
public sealed class EncounterLookupTests
{
    private readonly ITestOutputHelper _output;
    public EncounterLookupTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PikachuIsDescribedAcrossManyGamesAndGenerations()
    {
        var lookup = new EncounterLookupService();
        var clock = Stopwatch.StartNew();
        var listings = lookup.Describe(25, 0);
        clock.Stop();
        _output.WriteLine($"{listings.Count} games in {clock.ElapsedMilliseconds}ms");
        foreach (var listing in listings)
            _output.WriteLine($"  {listing.GameName} (gen {listing.Generation}): {(listing.Obtainable ? $"{listing.Cards.Count} ways" : "not obtainable")}");

        Assert.NotEmpty(listings);
        var obtainable = listings.Where(l => l.Obtainable).ToList();
        Assert.True(obtainable.Count >= 8, $"expected Pikachu in many games, got {obtainable.Count}");
        Assert.True(obtainable.Select(l => l.Generation).Distinct().Count() >= 5, "expected several generations");
        Assert.True(clock.ElapsedMilliseconds < 20000, $"describe took {clock.ElapsedMilliseconds}ms");

        var all = obtainable.SelectMany(l => l.Cards).ToList();
        Assert.Contains(all, c => c.Kind == "Wild");
        Assert.Contains(all, c => c.Kind == "Egg");
        Assert.All(all, c => Assert.False(string.IsNullOrEmpty(c.GameName)));
    }

    [Fact]
    public void SecondQueryForTheSameSpeciesIsCached()
    {
        var lookup = new EncounterLookupService();
        var first = lookup.Describe(25, 0);
        var second = lookup.Describe(25, 0);
        // The cache hands back the very listing it built; a wall-clock bound flaked under load.
        Assert.Same(first, second);
    }

    [Fact]
    public void ModernSpeciesOnlyAppearInModernGames()
    {
        var lookup = new EncounterLookupService();
        // Sprigatito (906) exists only from Scarlet/Violet onward.
        var listings = lookup.Describe(906, 0);
        var obtainable = listings.Where(l => l.Obtainable).ToList();
        _output.WriteLine($"Sprigatito obtainable in: {string.Join(", ", obtainable.Select(l => l.GameName))}");
        Assert.NotEmpty(obtainable);
        Assert.All(obtainable, l => Assert.True(l.Generation >= 9, $"{l.GameName} is gen {l.Generation}"));
    }

    [Fact]
    public void DiamondAndPearlAreMirroredForWildEncounters()
    {
        // Regression guard for the version-group fix: a Diamond/Pearl blank save reports
        // the lumped DP version, and the encounter generator rejects it, so both games
        // are described through their concrete ids. The wild/egg half of the answer must
        // match across the pair (the remaining difference is PKHeX's per-version Gen 4
        // event-gift tables, which really are version specific).
        var diamond = new EncounterLookupService().Describe(25, 0).Single(l => l.GameName == "Diamond");
        var pearl = new EncounterLookupService().Describe(25, 0).Single(l => l.GameName == "Pearl");

        Assert.Contains(diamond.Cards, c => c.Kind == "Wild" && c.Location == "Trophy Garden");
        Assert.Contains(pearl.Cards, c => c.Kind == "Wild" && c.Location == "Trophy Garden");
        Assert.Contains(diamond.Cards, c => c.Kind == "Egg");
        Assert.Contains(pearl.Cards, c => c.Kind == "Egg");
        Assert.Equal(
            diamond.Cards.Where(c => c.Kind is "Wild" or "Egg").Select(c => (c.Kind, c.Location, c.LevelMin, c.LevelMax)),
            pearl.Cards.Where(c => c.Kind is "Wild" or "Egg").Select(c => (c.Kind, c.Location, c.LevelMin, c.LevelMax)));
    }

    [Fact]
    public void InvalidSpeciesDescribeNothing()
    {
        var lookup = new EncounterLookupService();
        Assert.Empty(lookup.Describe(0, 0));
        Assert.Empty(lookup.Describe(-5, 0));
    }
}
