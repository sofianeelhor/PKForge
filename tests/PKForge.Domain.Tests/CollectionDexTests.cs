using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class CollectionDexTests
{
    [Fact]
    public void GenerationRangesCoverEveryNationalSpeciesExactlyOnce()
    {
        var ids = CollectionDex.GenRanges.SelectMany(r => Enumerable.Range(r.First, r.Last - r.First + 1)).ToList();
        Assert.Equal(CollectionDex.MaxSpecies, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(1, ids.Min());
        Assert.Equal(CollectionDex.MaxSpecies, ids.Max());
    }

    [Fact]
    public void OwnershipCountsNormalShinyAndGenerationBoundaries()
    {
        var collection = new[]
        {
            (25, false), // Gen 1, normal only
            (25, true),  // same species, also shiny
            (152, true), // Gen 2, shiny only: counts owned AND shiny
            (251, false),
            (252, false), // Gen 3
            (386, false),
            (1025, false), // Gen 9
        };

        var progress = CollectionDex.Compute(collection, CollectionDex.MaxSpecies);

        Assert.Equal(6, progress.Owned);
        Assert.Equal(2, progress.Shiny); // 25 and 152
        var gen1 = progress.Segments.Single(s => s.Generation == 1);
        Assert.Equal((1, 151, 151), (gen1.First, gen1.Last, gen1.Total));
        Assert.Equal(1, gen1.Owned);
        Assert.Equal(1, gen1.Shiny);
        var gen3 = progress.Segments.Single(s => s.Generation == 3);
        Assert.Equal(2, gen3.Owned);
        Assert.Equal(0, gen3.Shiny);
        var gen9 = progress.Segments.Single(s => s.Generation == 9);
        Assert.Equal(1, gen9.Owned);
    }

    [Fact]
    public void MaxSpeciesScopesOldGamesAndSkipsNamelessAndInvalidIds()
    {
        var collection = new[] { (25, false), (500, false), (650, false), (1000, false), (0, true), (9999, false) };

        // A Gen 5 save: 650 exists, 1000 does not.
        var gen5 = CollectionDex.Compute(collection, 649);
        Assert.Equal(2, gen5.Owned);
        Assert.Equal(0, gen5.Segments.Count(s => s.Generation > 5 && s.Owned > 0));

        // Species-name filtering: ids the name table has no entry for never count.
        var names = new[] { "", "Bulbasaur", "Ivysaur" }; // ids 0,1,2 only
        var filtered = CollectionDex.Compute(new[] { (1, false), (2, true), (3, false) }, 1025, names);
        Assert.Equal(2, filtered.Owned);
        Assert.Equal(1, filtered.Shiny);
    }
}
