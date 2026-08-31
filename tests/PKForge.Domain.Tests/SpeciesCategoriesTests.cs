using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The curated classification sets, spot-checked by NATIONAL ID. These ids were
/// resolved through the pinned PKHeX name table when the sets were written; the
/// engine-side FormFlags tests cover the derived facts.
/// </summary>
public sealed class SpeciesCategoriesTests
{
    [Theory]
    [InlineData(144, true)]  // Articuno
    [InlineData(150, true)]  // Mewtwo
    [InlineData(485, true)]  // Heatran
    [InlineData(800, true)]  // Necrozma (legendary, never an Ultra Beast)
    [InlineData(898, true)]  // Calyrex
    [InlineData(905, true)]  // Enamorus
    [InlineData(1007, true)] // Koraidon
    [InlineData(1017, true)] // Ogerpon
    [InlineData(1024, true)] // Terapagos
    [InlineData(998, false)] // Baxcalibur: pseudo, not legendary
    [InlineData(996, false)] // Frigibax sits right after the base paradox block
    public void LegendaryMembership(int species, bool expected) =>
        Assert.Equal(expected, SpeciesCategories.Legendary.Contains(species));

    [Theory]
    [InlineData(151, true)]  // Mew
    [InlineData(1025, true)] // Pecharunt
    [InlineData(893, true)]  // Zarude
    [InlineData(800, false)] // Necrozma is legendary, not mythical
    public void MythicalMembership(int species, bool expected) =>
        Assert.Equal(expected, SpeciesCategories.Mythical.Contains(species));

    [Theory]
    [InlineData(793, true)]  // Nihilego
    [InlineData(806, true)]  // Blacephalon
    [InlineData(799, true)]  // Guzzlord
    [InlineData(800, false)] // Necrozma is not an Ultra Beast
    public void UltraBeastMembership(int species, bool expected) =>
        Assert.Equal(expected, SpeciesCategories.UltraBeast.Contains(species));

    [Theory]
    [InlineData(984, true)]  // Great Tusk
    [InlineData(995, true)]  // Iron Thorns
    [InlineData(1005, true)] // Roaring Moon
    [InlineData(1009, true)] // Walking Wake
    [InlineData(1023, true)] // Iron Crown
    [InlineData(996, false)] // Frigibax is a regular line
    [InlineData(1007, false)] // Koraidon is legendary, not paradox
    public void ParadoxMembership(int species, bool expected) =>
        Assert.Equal(expected, SpeciesCategories.Paradox.Contains(species));

    [Fact]
    public void CategoriesAreMutuallyExclusiveWhereTheyMustBe()
    {
        Assert.Empty(SpeciesCategories.Legendary.Intersect(SpeciesCategories.Mythical));
        Assert.Empty(SpeciesCategories.Legendary.Intersect(SpeciesCategories.UltraBeast));
        Assert.Empty(SpeciesCategories.Legendary.Intersect(SpeciesCategories.Paradox));
        Assert.Empty(SpeciesCategories.Mythical.Intersect(SpeciesCategories.UltraBeast));
        Assert.Empty(SpeciesCategories.Mythical.Intersect(SpeciesCategories.Paradox));
    }

    [Fact]
    public void StarterLinesCoverAllNineGenerations()
    {
        Assert.Equal(81, SpeciesCategories.Starter.Count); // 9 generations x 3 lines x 3 stages
        Assert.Contains(1, SpeciesCategories.Starter);
        Assert.Contains(906, SpeciesCategories.Starter);
        Assert.DoesNotContain(25, SpeciesCategories.Starter); // Pikachu is no starter
    }

    [Fact]
    public void PseudoLegendaryFamiliesAreCompleteLines()
    {
        Assert.Equal(27, SpeciesCategories.PseudoLegendary.Count); // 9 families x 3 stages
        Assert.Contains(149, SpeciesCategories.PseudoLegendary);   // Dragonite
        Assert.Contains(998, SpeciesCategories.PseudoLegendary);   // Baxcalibur
    }

    [Fact]
    public void BabySetHoldsTheKnownBabies()
    {
        Assert.Equal(18, SpeciesCategories.Baby.Count);
        Assert.Contains(172, SpeciesCategories.Baby); // Pichu
        Assert.Contains(447, SpeciesCategories.Baby); // Riolu
        Assert.DoesNotContain(448, SpeciesCategories.Baby);
    }
}
