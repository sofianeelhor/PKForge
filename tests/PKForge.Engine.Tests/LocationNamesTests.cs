using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>QA 2026-11-12: a FireRed mon sent to Platinum showed "Met location: #55" instead of Pal Park.</summary>
public sealed class LocationNamesTests
{
    [Fact]
    public void Gen3MonInAGen4FormatIsNamedFromTheGen4Table()
    {
        var pk3 = new PK3 { Species = (ushort)Species.Charmander, Language = 2, Version = GameVersion.FR, MetLocation = 88, MetLevel = 5, CurrentLevel = 5 };
        var pk4 = pk3.ConvertToPK4();
        Assert.Equal(3, pk4.Generation);
        Assert.Equal("Pal Park", LocationNames.Of(pk4, pk4.MetLocation));
    }

    [Fact]
    public void NativeLocationsAreUnchanged()
    {
        var pk3 = new PK3 { Species = (ushort)Species.Charmander, Language = 2, Version = GameVersion.FR, MetLocation = 88 };
        Assert.Equal(GameInfo.GetLocationName(false, 88, 3, 3, GameVersion.FR), LocationNames.Of(pk3, 88));
        Assert.NotEqual("", LocationNames.Of(pk3, 88));
    }
}
