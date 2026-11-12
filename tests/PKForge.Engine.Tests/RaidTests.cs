using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Raid dens and Tera raids: every write must land in the save blocks that are
/// written out. Switch blanks can't be decrypted again, so they reopen from a deep copy of
/// the block list, the way DexRoundTripTests does.</summary>
public sealed class RaidTests
{
    private static SaveEngineSession Open(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);

    private static SaveEngineSession Reload(SaveEngineSession session)
    {
        _ = session.Serialize();
        return new SaveEngineSession(session.SaveFile.Clone(), null);
    }

    [Fact]
    public void Older_saves_are_not_supported()
    {
        using var session = Open(GameVersion.US);
        Assert.False(RaidService.IsSupported(session));
        Assert.Empty(RaidService.GetAreas(session));
    }

    [Fact]
    public void Sword_dens_activate_skipping_the_watchtower_and_survive_reload()
    {
        using var session = Open(GameVersion.SW);
        var galar = RaidService.GetAreas(session).Single(a => a.Index == 0);
        Assert.Equal(RaidSpawnList8.RaidCountLegal_O0, galar.Count);

        var count = RaidService.ActivateAll(session, 0);
        Assert.Equal(galar.Count - 1, count);
        Assert.Throws<InvalidOperationException>(() => RaidService.Activate(session, 0, 16));

        using var reloaded = Reload(session);
        var dens = RaidService.GetDens(reloaded, 0);
        Assert.All(dens.Where(d => !d.Special), d => { Assert.True(d.Active); Assert.True(d.WattsReady); });
        Assert.True(dens[16].Special);

        RaidService.Deactivate(reloaded, 0, 3);
        using var cleared = Reload(reloaded);
        Assert.False(RaidService.GetDen(cleared, 0, 3).Active);
        Assert.Equal(count - 1, RaidService.GetDens(cleared, 0).Count(d => d.Active && !d.Special));
    }

    [Fact]
    public void Activating_gives_a_fresh_seed()
    {
        using var session = Open(GameVersion.SH);
        var seeds = Enumerable.Range(0, 5).Select(_ => RaidService.Activate(session, 0, 0).Seed).ToList();
        Assert.True(seeds.Distinct().Count() > 1);
    }

    [Fact]
    public void Scarlet_raids_activate_and_clear()
    {
        using var session = Open(GameVersion.SL);
        var areas = RaidService.GetAreas(session);
        var paldea = areas.Single(a => a.Name == "Paldea");
        Assert.Equal(paldea.Count, RaidService.GetDens(session, 0).Count);

        // A blank save has no crystal positions; give one so it counts as a real spot.
        ((SAV9SV)session.SaveFile).RaidPaldea.GetRaid(5).AreaID = 3;
        var written = RaidService.Activate(session, 0, 5);
        Assert.True(written.Active);

        using var reloaded = Reload(session);
        Assert.True(RaidService.GetDen(reloaded, 0, 5).Active);
        Assert.Equal(written.Seed, RaidService.GetDen(reloaded, 0, 5).Seed);
        Assert.Equal(1, RaidService.DeactivateAll(reloaded, 0));
        Assert.False(RaidService.GetDen(reloaded, 0, 5).Active);
        Assert.Throws<InvalidOperationException>(() => RaidService.Activate(reloaded, 0, 6));
    }

    [Fact]
    public void Seven_star_raids_can_be_done_again()
    {
        using var session = Open(GameVersion.VL);
        var seven = ((SAV9SV)session.SaveFile).RaidSevenStar;
        Assert.True(seven.CountAll > 1);
        var raid = seven.GetRaid(0);
        raid.Identifier = 20230127;
        raid.Captured = true;
        raid.Defeated = true;
        seven.GetRaid(1).Identifier = 20230210;

        var listed = RaidService.GetSevenStar(session);
        Assert.Equal(2, listed.Count);
        Assert.True(listed[0].Captured);

        Assert.Equal(1, RaidService.ResetAllSevenStar(session));
        using var reloaded = Reload(session);
        var back = RaidService.GetSevenStar(reloaded).Single(r => r.Identifier == 20230127);
        Assert.False(back.Captured);
        Assert.False(back.Defeated);
        Assert.Throws<ArgumentOutOfRangeException>(() => RaidService.ResetSevenStar(reloaded, 5));
    }
}
