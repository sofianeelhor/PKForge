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

    /// <summary>PKHeX reports Diamond and Pearl saves as the combined DP version, which has
    /// no name of its own: the shelf must still name the game, never fall back to the file.</summary>
    [Theory]
    [InlineData(GameVersion.DP, "Diamond / Pearl")]
    [InlineData(GameVersion.GS, "Gold / Silver")]
    [InlineData(GameVersion.BW, "Black / White")]
    public void CombinedVersionsAreNamedByTheirEditions(GameVersion version, string expected)
    {
        Assert.Equal(expected, SaveEngine.EditionPair(version, GameInfo.GetStrings("en")));
    }

    [Fact]
    public void SingleVersionsAreNotPairs()
    {
        Assert.Null(SaveEngine.EditionPair(GameVersion.Pt, GameInfo.GetStrings("en")));
    }

    private static PK3 OwnMon(SaveFile save, GameVersion caughtIn, string ot)
    {
        var pk = new PK3 { Species = (ushort)Species.Bulbasaur, CurrentLevel = 5, Version = caughtIn, ID32 = save.ID32, OriginalTrainerName = ot, Language = (int)LanguageID.English };
        pk.RefreshChecksum();
        return pk;
    }

    /// <summary>A FireRed/LeafGreen save does not name its edition, but the player's own
    /// Pokémon record the cartridge they were caught in.</summary>
    [Theory]
    [InlineData(GameVersion.LG)]
    [InlineData(GameVersion.FR)]
    public void OwnPokemonNameTheFireRedLeafGreenEdition(GameVersion edition)
    {
        var save = new SAV3FRLG { OT = "RED", TID16 = 1234, SID16 = 5678 };
        save.SetPartySlotAtIndex(OwnMon(save, edition, "RED"), 0);

        Assert.Equal(edition, SaveParser.EditionFromOwnPokemon(save));
    }

    [Fact]
    public void TradedOrDisagreeingPokemonNeverPickTheEdition()
    {
        var save = new SAV3FRLG { OT = "RED", TID16 = 1234, SID16 = 5678 };
        save.SetPartySlotAtIndex(OwnMon(save, GameVersion.LG, "BLUE"), 0); // another trainer's mon
        Assert.Null(SaveParser.EditionFromOwnPokemon(save));

        save.SetPartySlotAtIndex(OwnMon(save, GameVersion.FR, "RED"), 0);
        save.SetPartySlotAtIndex(OwnMon(save, GameVersion.LG, "RED"), 1);
        Assert.Null(SaveParser.EditionFromOwnPokemon(save));
    }

    [Fact]
    public void AChosenEditionOutranksTheOwnPokemon()
    {
        var save = new SAV3RS { OT = "MAY", TID16 = 1, SID16 = 2 };
        var pk = new PK3 { Species = (ushort)Species.Treecko, CurrentLevel = 5, Version = GameVersion.S, ID32 = save.ID32, OriginalTrainerName = "MAY", Language = (int)LanguageID.English };
        pk.RefreshChecksum();
        save.SetPartySlotAtIndex(pk, 0);

        SaveParser.ApplyVersionHint(save, null);
        Assert.Equal(GameVersion.S, save.Version);
        SaveParser.ApplyVersionHint(save, "Pokémon Ruby");
        Assert.Equal(GameVersion.R, save.Version);
    }

    /// <summary>
    /// The stored party count is one raw byte; an early or damaged FireRed save can hold 7 or
    /// more while the party has 6 slots. Reading the edition from it must not run off the end.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(0xFF)]
    public void AnOutOfRangePartyCountNeverBreaksTheEditionRead(byte storedCount)
    {
        var save = new SAV3FRLG { OT = "RED", TID16 = 1234, SID16 = 5678 };
        save.SetPartySlotAtIndex(OwnMon(save, GameVersion.LG, "RED"), 0);
        save.LargeBlock.PartyCount = storedCount;

        Assert.Equal(GameVersion.LG, SaveParser.EditionFromOwnPokemon(save));
        SaveParser.ApplyVersionHint(save, "FireRed / LeafGreen");
        Assert.Equal(GameVersion.LG, save.Version);
    }
}
