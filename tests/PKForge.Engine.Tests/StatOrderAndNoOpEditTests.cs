using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Regression coverage for the app/PKHeX stat-order boundary and no-op editor saves.</summary>
public sealed class StatOrderAndNoOpEditTests
{
    private static string TestsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests");
    }

    [Fact]
    public void Gen4StatsUseHpAtkDefSpASpDSpeOrderInBothDirections()
    {
        var engine = new SaveEngine();
        using var session = (SaveEngineSession)engine.OpenBlankSession(4);
        var mon = new PK4
        {
            Species = 25,
            Version = GameVersion.Pt,
            CurrentLevel = 30,
            IV_HP = 1,
            IV_ATK = 2,
            IV_DEF = 3,
            IV_SPA = 4,
            IV_SPD = 5,
            IV_SPE = 6,
            EV_HP = 11,
            EV_ATK = 12,
            EV_DEF = 13,
            EV_SPA = 14,
            EV_SPD = 15,
            EV_SPE = 16,
        };
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        Assert.True(session.ImportSlot(0, 0, bytes));

        var detail = session.ReadEntity(0, 0);
        Assert.Equal([1, 2, 3, 4, 5, 6], detail.IVs);
        Assert.Equal([11, 12, 13, 14, 15, 16], detail.EVs);

        session.ApplyEdit(0, 0, new EntityEdit(
            IVs: [21, 22, 23, 24, 25, 26],
            EVs: [31, 32, 33, 34, 35, 36]));

        var edited = session.GetEntity(0, 0);
        Assert.Equal(24, edited.IV_SPA);
        Assert.Equal(25, edited.IV_SPD);
        Assert.Equal(26, edited.IV_SPE);
        Assert.Equal(34, edited.EV_SPA);
        Assert.Equal(35, edited.EV_SPD);
        Assert.Equal(36, edited.EV_SPE);
    }

    [Fact]
    public void Gen4FullyPopulatedNoOpEditPreservesPidBytesAndLegality()
    {
        var legalRoot = Path.Combine(TestsRoot(), "Legality", "Legal");
        var file = Directory.EnumerateFiles(legalRoot, "*.pk4", SearchOption.AllDirectories)
            .First(path => new LegalityAnalysis(EntityFormat.GetFromBytes(File.ReadAllBytes(path))!).Valid);
        var engine = new SaveEngine();
        using var session = engine.OpenEntitySession(File.ReadAllBytes(file), "gen4-no-op");
        Assert.NotNull(session);

        var before = session!.ReadEntity(0, 0);
        var beforePid = session.GetRngInfo(0, 0).Pid;
        var beforeBytes = session.ExportSlot(0, 0).Data;
        Assert.True(new LegalityService().Analyze(session, 0, 0).Valid);

        session.ApplyEdit(0, 0, new EntityEdit(
            Species: before.Species,
            Nickname: before.Nickname,
            Level: before.Level,
            Nature: before.Nature,
            Ability: before.Ability,
            HeldItem: before.HeldItem,
            Move1: before.Move1,
            Move2: before.Move2,
            Move3: before.Move3,
            Move4: before.Move4,
            IVs: before.IVs,
            EVs: before.EVs,
            Ball: before.Ball,
            OriginalTrainer: before.OriginalTrainer));

        Assert.Equal(beforePid, session.GetRngInfo(0, 0).Pid);
        Assert.Equal(beforeBytes, session.ExportSlot(0, 0).Data);
        Assert.True(new LegalityService().Analyze(session, 0, 0).Valid);
    }
}
