using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class GameIdentificationTests
{
    [Theory]
    [InlineData("Pokémon LeafGreen", GameVersion.LG)]
    [InlineData("Pokémon FireRed", GameVersion.FR)]
    public void TheChosenGameResolvesTheFireRedLeafGreenEdition(string chosenGame, GameVersion expected)
    {
        var save = new SAV3FRLG();

        SaveParser.ApplyVersionHint(save, chosenGame);

        Assert.Equal(expected, save.Version);
    }

    [Theory]
    [InlineData("Pokemon - LeafGreen Version (USA, Europe).sav")]
    [InlineData("pokemon.gba.sav")]
    [InlineData("Pokémon FireRed / LeafGreen")]
    public void FileNamesAndUnchosenLabelsNeverPickTheEdition(string name)
    {
        var save = new SAV3FRLG();
        var parserDefault = save.Version;

        SaveParser.ApplyVersionHint(save, name);

        Assert.Equal(parserDefault, save.Version);
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
