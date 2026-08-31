using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class FormFlagsTests
{
    [Fact]
    public void FormFactsMatchTheModernTables()
    {
        var flags = new GameDataService().FormFlags;

        Assert.True(flags[6].Mega);            // Charizard: Mega X/Y
        Assert.True(flags[6].Gigantamax);      // and G-Max
        Assert.True(flags[3].Mega);            // Venusaur
        Assert.True(flags[3].Gigantamax);      // Venusaur also G-Maxes
        Assert.True(flags[26].Regional);       // Raichu: Alolan
        Assert.True(flags[25].Gigantamax);     // Pikachu
        Assert.True(flags[52].Regional);       // Meowth: Alolan + Galarian
        Assert.True(flags[386].HasForms);      // Deoxys
        Assert.False(flags[386].Mega);
        Assert.False(flags[43].HasForms);      // Oddish: plain
        Assert.False(flags[887].HasForms);     // Dragapult: no alternate forms
    }

    [Fact]
    public void GMaxRosterIsTheClosedList()
    {
        Assert.Equal(32, PKForge.Domain.SpeciesCategories.GigantamaxCapable.Count);
        Assert.Contains(12, PKForge.Domain.SpeciesCategories.GigantamaxCapable); // Butterfree
        Assert.Contains(809, PKForge.Domain.SpeciesCategories.GigantamaxCapable); // Melmetal
        Assert.DoesNotContain(2, PKForge.Domain.SpeciesCategories.GigantamaxCapable); // Ivysaur: mega line, no G-Max
    }
}
