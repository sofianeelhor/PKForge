using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class MoveShopTests
{
    [Fact]
    public void LegendsArceusMoveShopFlagsPersistInTheStoredEntity()
    {
        using var session = Seed(new PA8 { Version = GameVersion.PLA });
        var before = session.GetMoveShop(0, 0);
        Assert.True(before.Supported);
        var entry = before.Entries.First(entry => !entry.Purchased && !entry.Mastered);

        session.ApplyMoveShopEdit(0, 0, new MoveShopEdit(entry.Index, Purchased: true, Mastered: true));
        var after = session.GetMoveShop(0, 0);
        var edited = Assert.Single(after.Entries, candidate => candidate.Index == entry.Index);
        Assert.True(edited.Purchased);
        Assert.True(edited.Mastered);

        var exported = session.ExportSlot(0, 0).Data;
        var stored = Assert.IsType<PA8>(EntityFormat.GetFromBytes(exported));
        Assert.True(stored.GetPurchasedRecordFlag(entry.Index));
        Assert.True(stored.GetMasteredRecordFlag(entry.Index));
    }

    [Fact]
    public void MoveShopIsFormatGatedAndRejectsUnavailableEntries()
    {
        using var nonPla = Seed(new PK8 { Version = GameVersion.SW });
        Assert.False(nonPla.GetMoveShop(0, 0).Supported);
        Assert.Throws<InvalidOperationException>(() => nonPla.ApplyMoveShopEdit(0, 0, new MoveShopEdit(0, Purchased: true)));

        using var pla = Seed(new PA8 { Version = GameVersion.PLA });
        Assert.Throws<ArgumentOutOfRangeException>(() => pla.ApplyMoveShopEdit(0, 0, new MoveShopEdit(63, Purchased: true)));
    }

    private static SaveEngineSession Seed(PKM mon)
    {
        mon.Species = 25;
        mon.CurrentLevel = 20;
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        return (SaveEngineSession)new SaveEngine().OpenEntitySession(bytes)!;
    }
}
