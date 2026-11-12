using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Diamond/Pearl/Platinum Underground pouches and records, written as SAV_Underground writes them.</summary>
public sealed class SinnohUndergroundTests
{
    [Theory]
    [InlineData(GameVersion.P)]
    [InlineData(GameVersion.Pt)]
    public void PouchesAddRemovePackAndSurviveReopen(GameVersion version)
    {
        using var session = Open(version);
        Assert.True(SinnohUndergroundService.IsSupported(session));
        var trap = SinnohUndergroundService.GetChoices(UndergroundPocket.Traps)[0];
        var good = SinnohUndergroundService.GetChoices(UndergroundPocket.Goods)[0];
        Assert.True(SinnohUndergroundService.Add(session, UndergroundPocket.Traps, trap.Id));
        Assert.True(SinnohUndergroundService.Add(session, UndergroundPocket.Goods, good.Id));
        Assert.True(SinnohUndergroundService.Add(session, UndergroundPocket.Goods, good.Id));
        Assert.True(SinnohUndergroundService.Add(session, UndergroundPocket.Spheres, 7, sphereSize: 500));
        SinnohUndergroundService.Remove(session, UndergroundPocket.Goods, 0);

        using var reopened = Reopen(version, session);
        Assert.Equal(trap.Name, SinnohUndergroundService.GetPocket(reopened, UndergroundPocket.Traps).Single().Name);
        var goods = SinnohUndergroundService.GetPocket(reopened, UndergroundPocket.Goods);
        Assert.Equal(0, goods.Single().Slot); // rewritten packed
        var sphere = SinnohUndergroundService.GetPocket(reopened, UndergroundPocket.Spheres).Single();
        Assert.Equal(("Pale Sphere (L)", 99), (sphere.Name, sphere.Count));
        Assert.Equal(12, SinnohUndergroundService.SetSphereSize(reopened, 0, 12));
    }

    [Fact]
    public void FillUsesFreeSlotsWithoutDuplicates()
    {
        using var session = Open(GameVersion.D);
        var treasure = SinnohUndergroundService.GetChoices(UndergroundPocket.Treasures)[2];
        SinnohUndergroundService.Add(session, UndergroundPocket.Treasures, treasure.Id);
        Assert.Equal(39, SinnohUndergroundService.Fill(session, UndergroundPocket.Treasures));
        var treasures = SinnohUndergroundService.GetPocket(session, UndergroundPocket.Treasures);
        Assert.Equal(40, treasures.Count);
        Assert.Equal(40, treasures.Select(t => t.Id).Distinct().Count());
        Assert.False(SinnohUndergroundService.Add(session, UndergroundPocket.Treasures, treasure.Id));

        Assert.Equal(40, SinnohUndergroundService.Fill(session, UndergroundPocket.Spheres));
        Assert.All(SinnohUndergroundService.GetPocket(session, UndergroundPocket.Spheres),
            s => Assert.True(s.Name.EndsWith("(L)", StringComparison.Ordinal) && s.Count == 99));

        SinnohUndergroundService.Clear(session, UndergroundPocket.Spheres);
        Assert.Empty(SinnohUndergroundService.GetPocket(session, UndergroundPocket.Spheres));
    }

    [Fact]
    public void RecordsClampAndSurviveReopen()
    {
        using var session = Open(GameVersion.Pt);
        Assert.Equal(13, SinnohUndergroundService.GetRecords(session).Count);
        Assert.Equal(999_999, SinnohUndergroundService.SetRecord(session, 11, int.MaxValue));
        Assert.Equal(42, SinnohUndergroundService.SetRecord(session, 4, 42));

        using var reopened = Reopen(GameVersion.Pt, session);
        var save = (SAV4Sinnoh)reopened.SaveFile;
        Assert.Equal((999_999u, 42u), (save.UG_FlagsCaptured, save.UG_Fossils));
    }

    [Fact]
    public void OtherGamesAreUnsupported()
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(GameVersion.HG, "PKForge", LanguageID.English), null);
        Assert.False(SinnohUndergroundService.IsSupported(session));
        Assert.Throws<NotSupportedException>(() => SinnohUndergroundService.GetRecords(session));
    }

    private static SaveEngineSession Open(GameVersion version) => new(Parse(version, new byte[0x80000]), null);

    private static SaveEngineSession Reopen(GameVersion version, SaveEngineSession session) =>
        new(Parse(version, session.Serialize().ToArray()), null);

    private static SAV4Sinnoh Parse(GameVersion version, byte[] data) =>
        version == GameVersion.Pt ? new SAV4Pt(data) : new SAV4DP(data) { Version = version };
}
