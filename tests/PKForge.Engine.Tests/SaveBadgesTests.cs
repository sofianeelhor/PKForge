using System.Numerics;
using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>The fiche reads the gym badges where each generation keeps them.</summary>
public sealed class SaveBadgesTests
{
    // The real copies stay outside the repository; these tests are skipped without them.
    private static byte[]? Local(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, ".local-testdata", "thor", file);
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }
        return null;
    }

    private static SaveBadges? Badges(byte[] bytes)
    {
        using var session = new SaveEngine().OpenSession(bytes);
        return session.GetBadges();
    }

    [Fact]
    public void BlackBadgesAreUnova_AndTheTrainerIdIsRead()
    {
        var save = (SAV5BW)BlankSaveFile.Get(GameVersion.B, "HILDA", LanguageID.English);
        save.TID16 = 4242;
        save.Misc.Badges = 0b101;
        var bytes = save.Write().ToArray();

        var badges = Badges(bytes)!;
        Assert.Equal(8, badges.Total);
        Assert.Equal(["Trio", "Insect"], badges.Names);
        Assert.Equal(4242, new SaveEngine().TryDescribe(bytes)!.TrainerId);
    }

    [Fact]
    public void ANewGameHasNone()
    {
        var bytes = BlankSaveFile.Get(GameVersion.B2, "NATE", LanguageID.English).Write().ToArray();
        Assert.Equal("Badges: 0 of 8", Badges(bytes)!.Line);
    }

    [Theory]
    [InlineData("nds/Pokemon - Platinum Version (Europe).sav", 8)]
    [InlineData("nds/Pokemon - Version Or HeartGold (France).sav", 16)]
    [InlineData("nds/Pokemon - Version Noire (France) (NDSi Enhanced).sav", 8)]
    [InlineData("retroarch/mGBA/Pokemon Fire Red.sav", 8)]
    [InlineData("retroarch/POKEMON_EMER_BPEJ_00.srm", 8)]
    public void RealSavesCountWhatPKHeXReads(string file, int total)
    {
        if (Local(file) is not { } bytes) return;
        Assert.True(SaveParser.TryGetSaveFile(RetroArchSaveContainer.Decode(bytes), out var save));
        var expected = save switch
        {
            SAV4HGSS hgss => BitOperations.PopCount((uint)(hgss.Badges | (hgss.Badges16 << 8))),
            SAV4Sinnoh sinnoh => BitOperations.PopCount((uint)sinnoh.Badges),
            SAV5 gen5 => BitOperations.PopCount((uint)gen5.Misc.Badges),
            SAV3 gen3 => BitOperations.PopCount((uint)gen3.Badges),
            _ => -1,
        };

        var badges = Badges(bytes)!;
        Assert.Equal(total, badges.Total);
        Assert.Equal(expected, badges.Earned);
    }
}
