using PKForge.App.Services;
using PKForge.Domain;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class HabitatCatalogTests
{
    // Expected values are canonical park type IDs (1–18), not PKHeX's zero-based MoveType.
    [Theory]
    [InlineData(7, 0, ParkType.Water, ParkType.Water)] // Squirtle
    [InlineData(6, 0, ParkType.Fire, ParkType.Flying)] // Charizard: both fire and flight
    [InlineData(37, 1, ParkType.Ice, ParkType.Ice)] // Alolan Vulpix is ice, not fire
    [InlineData(479, 0, ParkType.Electric, ParkType.Ghost)] // Rotom is electric/ghost by default
    [InlineData(479, 2, ParkType.Electric, ParkType.Water)] // Wash Rotom
    [InlineData(908, 0, ParkType.Grass, ParkType.Dark)] // Meowscarada
    [InlineData(840, 0, ParkType.Grass, ParkType.Dragon)] // Applin
    public void UsesSpeciesAndFormData(int species, int form, int first, int second)
    {
        var types = HabitatCatalog.TypesFor(species, form);
        Assert.Equal(first == second ? new[] { first } : new[] { first, second }, types);
    }

    [Fact]
    public void UnknownOrInvalidSpeciesReportsNoType()
    {
        Assert.Equal(new[] { ParkType.Unknown }, HabitatCatalog.TypesFor(0));
        Assert.Equal(new[] { ParkType.Unknown }, HabitatCatalog.TypesFor(-5));
    }

    [Fact]
    public void HabitatAffinityUsesCanonicalTypes()
    {
        Assert.Equal(1, ParkEnvironment.WaterfallLake.MoodBonus(HabitatCatalog.TypesFor(7)));
        Assert.Equal(0, ParkEnvironment.WaterfallLake.MoodBonus(HabitatCatalog.TypesFor(6)));
        Assert.Equal(1, ParkEnvironment.EmberGrove.MoodBonus(HabitatCatalog.TypesFor(6)));
        Assert.Equal(0, ParkEnvironment.EmberGrove.MoodBonus(HabitatCatalog.TypesFor(37, 1)));
        Assert.Equal(1, ParkEnvironment.SkySummit.MoodBonus(HabitatCatalog.TypesFor(6)));
        Assert.Equal(4, ParkEnvironment.All.Count);
        Assert.Equal(ParkEnvironmentId.SkySummit, ParkEnvironment.Step(ParkEnvironmentId.EmberGrove, 1).Id);
    }

    [Fact]
    public void ProfileSelectsHabitatFromCanonicalTypes()
    {
        Assert.Equal("Cascade Lake", HabitatCatalog.Profile(HabitatCatalog.TypesFor(7)).Name);
        Assert.Equal("Ember Grove", HabitatCatalog.Profile(HabitatCatalog.TypesFor(6)).Name);
        Assert.Equal("Sky Summit", HabitatCatalog.Profile(HabitatCatalog.TypesFor(18)).Name);
        Assert.Equal("Verdant Grove", HabitatCatalog.Profile(HabitatCatalog.TypesFor(908)).Name);
        Assert.Equal("Meadow Commons", HabitatCatalog.Profile([ParkType.Normal]).Name);
    }
}
