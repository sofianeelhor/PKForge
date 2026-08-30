using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class MoveDetailsTests
{
    [Fact]
    public void PPUpsAndRelearnMovesAreClampedAndPersisted()
    {
        using var session = Seed(7, new PK7 { Version = GameVersion.UM, Move1 = 85, Move2 = 33 });

        session.ApplyMoveDetails(0, 0, new MoveDetailsEdit(
            PP: [999, -10, 99, 99],
            PPUps: [99, 2, 3, 3],
            RelearnMoves: [85, 33, -1, 99999]));

        var details = session.GetMoveDetails(0, 0);
        Assert.Equal(4, details.Moves.Count);
        Assert.Equal(3, details.Moves[0].PPUps);
        Assert.Equal(details.Moves[0].MaxPP, details.Moves[0].PP);
        Assert.Equal(2, details.Moves[1].PPUps);
        Assert.Equal(0, details.Moves[2].PPUps);
        Assert.Equal(0, details.Moves[2].PP);
        Assert.True(details.SupportsRelearn);
        Assert.Equal([85, 33, 0, 728], details.RelearnMoves);
    }

    [Fact]
    public void OlderFormatsDoNotExposeRelearnSlots()
    {
        using var session = Seed(4, new PK4 { Version = GameVersion.Pt, Move1 = 85 });
        Assert.False(session.GetMoveDetails(0, 0).SupportsRelearn);
        Assert.Throws<InvalidOperationException>(() => session.ApplyMoveDetails(0, 0, new MoveDetailsEdit(RelearnMoves: [85, 0, 0, 0])));
    }

    private static SaveEngineSession Seed(int generation, PKM mon)
    {
        var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(generation);
        mon.Species = 25;
        mon.CurrentLevel = 20;
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        Assert.True(session.ImportSlot(0, 0, bytes));
        return session;
    }
}
