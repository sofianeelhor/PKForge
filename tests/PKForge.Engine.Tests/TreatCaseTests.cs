using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Poffin and Pokéblock cases: PKHeX's fill/delete semantics, per game, through a serialize and reopen.</summary>
public sealed class TreatCaseTests
{
    [Theory]
    [InlineData(GameVersion.D)]
    [InlineData(GameVersion.Pt)]
    public void SinnohPoffinsFillDeleteAndSurviveReopen(GameVersion version)
    {
        using var session = OpenSinnoh(version);
        Assert.Equal(TreatCaseKind.Poffins4, TreatCaseService.GetKind(session));
        Assert.Empty(TreatCaseService.GetTreats(session));

        Assert.Equal(100, TreatCaseService.Fill(session));
        TreatCaseService.Delete(session, 3);

        using var reopened = new SaveEngineSession(ParseSinnoh(version, session.Serialize().ToArray()), null);
        var treats = TreatCaseService.GetTreats(reopened);
        Assert.Equal(99, treats.Count);
        Assert.DoesNotContain(treats, t => t.Slot == 3);
        Assert.All(treats, t => Assert.Equal(("Rich Poffin", 255, 255, 255), (t.Name, t.Level, t.Smoothness, t.Sour)));

        TreatCaseService.DeleteAll(reopened);
        Assert.Empty(TreatCaseService.GetTreats(reopened));
    }

    [Fact]
    public void BdspPoffinsUsePkhexGiveAllValues()
    {
        using var session = new SaveEngineSession(new SAV8BS { OT = "PKForge", Version = GameVersion.SP }, null);
        Assert.Equal(TreatCaseKind.Poffins8b, TreatCaseService.GetKind(session));
        TreatCaseService.DeleteAll(session);
        Assert.Empty(TreatCaseService.GetTreats(session));

        Assert.Equal(100, TreatCaseService.Fill(session));
        TreatCaseService.Delete(session, 0);

        using var reopened = new SaveEngineSession(new SAV8BS(session.Serialize().ToArray()), null);
        var treats = TreatCaseService.GetTreats(reopened);
        Assert.Equal(99, treats.Count);
        Assert.All(treats, t => Assert.Equal(("Mild Poffin", 60, 255, 255), (t.Name, t.Level, t.Smoothness, t.Spicy)));
    }

    [Theory]
    [InlineData(GameVersion.E)]
    [InlineData(GameVersion.R)]
    public void HoennPokeblocksFillAndDelete(GameVersion version)
    {
        // Gen 3 blank saves do not export; the case is checked on the in-memory save.
        using var session = new SaveEngineSession(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);
        Assert.Equal(TreatCaseKind.PokeBlocks3, TreatCaseService.GetKind(session));
        Assert.Equal(40, TreatCaseService.Fill(session));
        Assert.All(TreatCaseService.GetTreats(session), t => Assert.Equal(("Gold Pokéblock", 255, 255), (t.Name, t.Level, t.Smoothness)));

        TreatCaseService.Delete(session, 39);
        Assert.Equal(39, TreatCaseService.GetTreats(session).Count);
        Assert.Equal(PokeBlock3Color.NoBlock, ((ISaveBlock3LargeHoenn)((SAV3)session.SaveFile).LargeBlock).PokeBlocks.Blocks[39].Color);

        TreatCaseService.DeleteAll(session);
        Assert.Empty(TreatCaseService.GetTreats(session));
    }

    [Fact]
    public void OrasBlockCountersClampAndSurviveReopen()
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(GameVersion.AS, "PKForge", LanguageID.English), null);
        Assert.Equal(TreatCaseKind.PokeBlocks6, TreatCaseService.GetKind(session));
        Assert.Equal(12, TreatCaseService.Fill(session));
        Assert.Equal(0, TreatCaseService.SetBlockCount(session, 5, -4));
        Assert.Equal(999, TreatCaseService.SetBlockCount(session, 6, 5000));

        using var reopened = new SaveEngineSession(new SAV6AO(session.Serialize().ToArray()), null);
        var counts = TreatCaseService.GetBlockCounts(reopened);
        Assert.Equal("Rainbow Pokéblock", counts[5].Name);
        Assert.Equal(0, counts[5].Count);
        Assert.Equal(11, counts.Count(c => c.Count == 999));

        TreatCaseService.DeleteAll(reopened);
        Assert.All(TreatCaseService.GetBlockCounts(reopened), c => Assert.Equal(0, c.Count));
    }

    [Theory]
    [InlineData(GameVersion.HG)]
    [InlineData(GameVersion.FR)]
    [InlineData(GameVersion.X)]
    [InlineData(GameVersion.SW)]
    public void OtherGamesHaveNoCase(GameVersion version)
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);
        Assert.False(TreatCaseService.IsSupported(session));
        Assert.Empty(TreatCaseService.GetTreats(session));
        Assert.Throws<NotSupportedException>(() => TreatCaseService.Fill(session));
    }

    // Zero-filled 512 KB image: PKHeX's in-memory Sinnoh blank cannot be written (see HoneyTreeTests).
    private static SaveEngineSession OpenSinnoh(GameVersion version) => new(ParseSinnoh(version, new byte[0x80000]), null);

    private static SAV4Sinnoh ParseSinnoh(GameVersion version, byte[] data) =>
        version == GameVersion.Pt ? new SAV4Pt(data) : new SAV4DP(data) { Version = version };
}
