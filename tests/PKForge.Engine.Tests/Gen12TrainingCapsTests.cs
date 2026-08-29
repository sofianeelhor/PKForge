using PKHeX.Core;
using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Gen 1/2 train with 4-bit DVs and 16-bit stat experience, not the modern
/// 31/252 caps: every writer must clamp to what the storage can hold.</summary>
public sealed class Gen12TrainingCapsTests
{
    private static void Seed(SaveEngineSession session, PKM mon)
    {
        mon.Species = 25;
        mon.CurrentLevel = 50;
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        Assert.True(session.ImportSlot(0, 0, bytes));
    }

    private static SaveEngineSession Open(int generation)
    {
        var engine = new SaveEngine();
        var raw = engine.OpenBlankSession(generation);
        var session = (SaveEngineSession)raw;
        Seed(session, generation switch
        {
            1 => new PK1(),
            2 => new PK2(),
            5 => new PK5 { Version = GameVersion.B },
            _ => new PK9 { Version = GameVersion.VL },
        });
        return session;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void GameBoyGenerationsExposeDvAndStatExperienceCaps(int generation)
    {
        using var session = Open(generation);
        var caps = session.GetTrainingCaps();
        Assert.Equal(15, caps.IvMax);
        Assert.Equal(65535, caps.EvMax);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void GameBoyEditsClampDVsAndStatExperience(int generation)
    {
        using var session = Open(generation);
        session.ApplyEdit(0, 0, new EntityEdit(
            IVs: [20, 31, 15, 16, 0, 9],
            EVs: [70000, 65535, 65536, -3, 12345, 999]));

        var detail = session.ReadEntity(0, 0);
        // Gen 1/2 HP (and the shared SpDef) DVs are DERIVED from the other nibbles:
        // authentic behavior, not a bug. Everything must land inside 0..15 though.
        Assert.All(detail.IVs, iv => Assert.InRange(iv, 0, 15));
        Assert.Equal(15, detail.IVs[1]);
        Assert.Equal(15, detail.IVs[2]);
        // Gen 1/2 store one writable Special value (SpA in the app order); SpD mirrors it.
        Assert.Equal([65535, 65535, 65535, 0, 0, 999], detail.EVs);
    }

    [Fact]
    public void Gen1BatchClampsToGBCaps()
    {
        using var session = Open(1);
        session.BatchApply(["IV_HP=31", "EV_HP=65535", "EV_ATK=70000"], [0]);

        var detail = session.ReadEntity(0, 0);
        Assert.All(detail.IVs, iv => Assert.InRange(iv, 0, 15));
        Assert.Equal(65535, detail.EVs[0]);
        Assert.Equal(65535, detail.EVs[1]);
    }

    [Fact]
    public void Gen5Allows255Evs()
    {
        using var session = Open(5);
        Assert.Equal(new TrainingCaps(31, 255), session.GetTrainingCaps());

        session.BatchApply(["EV_HP=300", "IV_HP=40"], [0]);
        var detail = session.ReadEntity(0, 0);
        Assert.Equal(255, detail.EVs[0]);
        Assert.Equal(31, detail.IVs[0]);
    }

    [Fact]
    public void ModernGamesKeep252Cap()
    {
        using var session = Open(9);
        Assert.Equal(new TrainingCaps(31, 252), session.GetTrainingCaps());

        session.BatchApply(["EV_HP=300"], [0]);
        Assert.Equal(252, session.ReadEntity(0, 0).EVs[0]);
    }
}
