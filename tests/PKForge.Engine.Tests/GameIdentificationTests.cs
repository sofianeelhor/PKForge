using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class GameIdentificationTests
{
    [Theory]
    [InlineData("Pokémon Red", GameVersion.RD)]
    [InlineData("Pokémon Blue (INT)", GameVersion.GN)]
    [InlineData("Pokémon Green (JP)", GameVersion.GN)]
    [InlineData("Pokémon Blue (JP)", GameVersion.BU)]
    public void TheChosenGameResolvesTheRedBlueEdition(string chosenGame, GameVersion expected)
    {
        var save = new SAV1();

        SaveParser.ApplyVersionHint(save, chosenGame);

        Assert.Equal(expected, save.Version);
    }

    [Theory]
    [InlineData("Pokemon - Red Version (USA, Europe).sav")]
    [InlineData("Pokémon Red / Blue")]
    [InlineData("Pokémon Blue")]
    public void FileNamesAndTheGroupLabelNeverPickTheRedBlueEdition(string name)
    {
        var save = new SAV1();

        SaveParser.ApplyVersionHint(save, name);

        Assert.Equal(GameVersion.RB, save.Version);
    }

    [Fact]
    public void ARedBlueSaveIsDescribedAsTheSharedGroup()
    {
        var description = new SaveEngine().TryDescribe(RecognizableRedBlue());

        Assert.NotNull(description);
        Assert.Equal("Red / Blue", description!.GameName);
        Assert.Equal(1, description.Generation);
    }

    [Fact]
    public void OpeningTheChosenCartridgeStampsThatOriginOnATransfer()
    {
        var previous = EntityConverter.VirtualConsoleSourceGen1;
        try
        {
            using var session = new SaveEngineSession(RecognizableRedBlue(), "Pokémon Blue (JP)");
            Assert.Equal(GameVersion.BU, Assert.IsType<SAV1>(session.SaveFile).Version);

            session.MakeActive();

            Assert.Equal(GameVersion.BU, EntityConverter.VirtualConsoleSourceGen1);
            var transferred = new PK1(jp: true) { Species = (ushort)Species.Pikachu, CurrentLevel = 5 }.ConvertToPK7();
            Assert.Equal(GameVersion.BU, transferred.Version);
        }
        finally
        {
            EntityConverter.VirtualConsoleSourceGen1 = previous;
        }
    }

    [Fact]
    public void OpeningYellowStampsYellowAsTheTransferOrigin()
    {
        var previous = EntityConverter.VirtualConsoleSourceGen1;
        try
        {
            using var session = new SaveEngineSession(new SAV1(LanguageID.English, GameVersion.YW), "Pokémon Yellow");

            session.MakeActive();

            Assert.Equal(GameVersion.YW, EntityConverter.VirtualConsoleSourceGen1);
        }
        finally
        {
            EntityConverter.VirtualConsoleSourceGen1 = previous;
        }
    }

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

    /// <summary>
    /// A blank <see cref="SAV1"/> does not write the party and box list terminators the
    /// recognizer requires. An empty international list at each offset is enough.
    /// </summary>
    private static byte[] RecognizableRedBlue()
    {
        var data = new byte[0x8000];
        data[0x2F2C] = 0;
        data[0x2F2D] = 0xFF;
        data[0x30C0] = 0;
        data[0x30C1] = 0xFF;
        return data;
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
