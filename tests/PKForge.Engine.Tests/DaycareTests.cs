using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class DaycareTests
{
    [Fact]
    public void DepositedPokemonIsShownAndWithdrawnToFirstEmptyBoxSlot()
    {
        var save = new SAV7USUM();
        var deposited = new PK7 { Species = 25, CurrentLevel = 23, Nickname = "Sparky", Version = GameVersion.UM };
        deposited.RefreshChecksum();
        deposited.WriteEncryptedDataStored(save.GetDaycareSlot(0).Span);
        save.SetDaycareOccupied(0, true);

        using var session = new SaveEngineSession(save, null);
        var before = session.GetDaycare();
        var facility = Assert.Single(before.Facilities);
        var slot = facility.Slots[0];
        Assert.True(before.Supported);
        Assert.True(slot.Occupied);
        Assert.Equal("Pikachu", slot.SpeciesName);
        Assert.Equal("Sparky", slot.Nickname);
        Assert.Equal(23, slot.Level);

        var result = session.WithdrawDaycareToFirstEmptyBox(0, 0);

        Assert.Equal(0, result.Box);
        Assert.Equal(0, result.Slot);
        Assert.Equal("Pikachu", result.SpeciesName);
        Assert.True(session.ReadEntity(0, 0).Species == 25);
        Assert.False(session.GetDaycare().Facilities[0].Slots[0].Occupied);
    }

    [Fact]
    public void UnsupportedSaveHasNoDaycareSurface()
    {
        using var session = new SaveEngine().OpenBlankSession(9);
        Assert.False(session.GetDaycare().Supported);
    }
}
