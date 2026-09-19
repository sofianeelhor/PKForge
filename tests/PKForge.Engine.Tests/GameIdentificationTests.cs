using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class GameIdentificationTests
{
    [Theory]
    [InlineData("Pokemon LeafGreen.sav", GameVersion.LG)]
    [InlineData("Pokemon - LeafGreen Version (USA, Europe).sav", GameVersion.LG)]
    [InlineData("Pokemon FireRed.sav", GameVersion.FR)]
    [InlineData("Pokemon - FireRed Version (USA, Europe).sav", GameVersion.FR)]
    public void FireRedAndLeafGreenFilenameHintsResolveTheEdition(string fileName, GameVersion expected)
    {
        var save = new SAV3FRLG();

        SaveParser.ApplyVersionHint(save, fileName);

        Assert.Equal(expected, save.Version);
    }

    [Fact]
    public void AmbiguousFireRedLeafGreenSaveKeepsTheParserDefault()
    {
        var save = new SAV3FRLG();

        SaveParser.ApplyVersionHint(save, "pokemon.gba.sav");

        Assert.Equal(GameVersion.FR, save.Version);
    }

    [Theory]
    [InlineData("01-GC6E-PokemonColosseum.gci", "Colosseum")]
    [InlineData("01-GXXP-PokemonXD.GCI", "XD")]
    public void DolphinGciNamesAreEligibleForGameCubeDetection(string fileName, string expectedMarker)
    {
        Assert.True(PKForge.Infrastructure.EmulatorSaveHeuristics.IsCandidateFileName(
            fileName, PKForge.Domain.EmulatorKind.Dolphin));
        Assert.Contains(expectedMarker, fileName, StringComparison.OrdinalIgnoreCase);
    }
}
