using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Gen 3 e-Reader data on blank sessions: the berry moves only as a whole block the game would
/// accept, clears to zeros, and PKHeX reads the stored name back; the Battle e-Reader trainer likewise.
/// </summary>
public sealed class EReaderTests
{
    private static SaveEngineSession Blank(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);

    /// <summary>A berry block as a card writes it: a name, a growth time, a yield, the checksum.</summary>
    private static byte[] Berry(SAV3 save, string name, int size)
    {
        var block = new byte[size];
        save.SetString(block.AsSpan(0, 7), name, 7, StringConverterOption.ClearFF);
        block[0x0A] = 6;   // max yield
        block[0x0B] = 2;   // min yield
        block[0x14] = 18;  // growth time
        block[0x15] = 40;  // spicy
        EReaderService.SignBerry(block);
        return block;
    }

    [Theory]
    [InlineData(GameVersion.R, 0x530, "Ruby/Sapphire")]
    [InlineData(GameVersion.S, 0x530, "Ruby/Sapphire")]
    [InlineData(GameVersion.E, 0x34, "Emerald")]
    [InlineData(GameVersion.FR, 0x34, "FireRed/LeafGreen")]
    [InlineData(GameVersion.LG, 0x34, "FireRed/LeafGreen")]
    public void BerryImportsClearsAndRoundTrips(GameVersion version, int size, string game)
    {
        using var session = Blank(version);
        var save = (SAV3)session.SaveFile;
        Assert.True(EReaderService.IsSupported(session));
        var empty = EReaderService.Get(session);
        Assert.Equal(game, empty.Game);
        Assert.False(empty.Berry.Present);
        Assert.Equal(size, empty.Berry.BlockSize);
        Assert.Null(EReaderService.ExportBerry(session));

        var block = Berry(save, "PUMKIN", size);
        Assert.True(EReaderService.ImportBerry(session, block).Success);
        var stored = EReaderService.Get(session).Berry;
        Assert.True(stored.Present);
        Assert.True(stored.Valid);
        Assert.Equal("PUMKIN", stored.Name);
        Assert.Equal("An official English e-Reader berry.", stored.Distribution);
        Assert.Equal(block, EReaderService.ExportBerry(session));

        // PKHeX reads the same berry (its legality settings load from these properties).
        Assert.False(save.IsEBerryEngima);
        Assert.Equal("PUMKIN", save.EBerryName);

        Assert.True(EReaderService.ClearBerry(session).Success);
        Assert.False(EReaderService.Get(session).Berry.Present);
        Assert.False(save.LargeBlock.EReaderBerry.ContainsAnyExcept((byte)0));
        Assert.False(EReaderService.ClearBerry(session).Success);
    }

    [Fact]
    public void ABerryTheGameWouldRejectIsNeverWritten()
    {
        using var session = Blank(GameVersion.E);
        var save = (SAV3)session.SaveFile;
        var bad = Berry(save, "DRASH", 0x34);
        bad[0x15]++; // checksum no longer matches
        Assert.False(EReaderService.ImportBerry(session, bad).Success);
        Assert.False(EReaderService.Get(session).Berry.Present);

        var barren = new byte[0x34];
        save.SetString(barren.AsSpan(0, 7), "DRASH", 7, StringConverterOption.ClearFF);
        EReaderService.SignBerry(barren); // signed but no growth time or yield
        Assert.False(EReaderService.ImportBerry(session, barren).Success);

        Assert.False(EReaderService.ImportBerry(session, new byte[0x34]).Success);       // empty
        Assert.False(EReaderService.ImportBerry(session, Berry(save, "DRASH", 0x530)).Success); // a Ruby/Sapphire block
        Assert.False(EReaderService.Get(session).Berry.Present);
    }

    [Fact]
    public void RubySapphireChecksumsWithoutTheDescriptionPointers()
    {
        using var session = Blank(GameVersion.R);
        var save = (SAV3)session.SaveFile;
        var block = Berry(save, "STRIB", 0x530);
        // pokeruby nulls the two description pointers before summing: stored pointers must not count.
        block[0x0C] = 0x60; block[0x0F] = 0x02; block[0x10] = 0x90; block[0x13] = 0x02;
        Assert.True(EReaderService.IsBerryValid(block));
        Assert.True(EReaderService.ImportBerry(session, block).Success);
    }

    [Fact]
    public void AnUnofficialNameIsStoredButFlagged()
    {
        using var session = Blank(GameVersion.E);
        var save = (SAV3)session.SaveFile;
        Assert.True(EReaderService.ImportBerry(session, Berry(save, "FAKE", 0x34)).Success);
        Assert.StartsWith("Not an official", EReaderService.Get(session).Berry.Distribution);
    }

    [Fact]
    public void GiveEnigmaBerryAddsOneOnlyWhenThereIsNone()
    {
        using var session = Blank(GameVersion.E);
        Assert.True(EReaderService.GiveEnigmaBerry(session).Success);
        Assert.Equal(1, EReaderService.Get(session).Berry.EnigmaBerriesInBag);
        Assert.False(EReaderService.GiveEnigmaBerry(session).Success);
    }

    [Theory]
    [InlineData(GameVersion.R)]
    [InlineData(GameVersion.E)]
    [InlineData(GameVersion.FR)]
    public void TrainerImportsAndClears(GameVersion version)
    {
        using var session = Blank(version);
        var save = (SAV3)session.SaveFile;
        Assert.False(EReaderService.Get(session).Trainer.Present);

        var trainer = new byte[0xBC];
        trainer[1] = 0x20;
        save.SetString(trainer.AsSpan(4, 8), "GIOVANNI", 7, StringConverterOption.ClearFF);
        trainer[0x40] = 0x99;
        var unsigned = trainer.ToArray();
        unsigned[^1] = 1;
        Assert.False(EReaderService.ImportTrainer(session, unsigned).Success);
        Assert.False(EReaderService.ImportTrainer(session, new byte[0xBB]).Success);

        EReaderService.SignTrainer(trainer);
        Assert.True(EReaderService.ImportTrainer(session, trainer).Success);
        var stored = EReaderService.Get(session).Trainer;
        Assert.True(stored.Present);
        Assert.True(stored.Valid);
        Assert.StartsWith("GIOVANN", stored.Name);
        Assert.Equal(trainer, EReaderService.ExportTrainer(session));

        Assert.True(EReaderService.ClearTrainer(session).Success);
        Assert.False(EReaderService.Get(session).Trainer.Present);
    }

    [Fact]
    public void OtherGenerationsAreNotOffered()
    {
        using var session = Blank(GameVersion.Pt);
        Assert.False(EReaderService.IsSupported(session));
        Assert.False(EReaderService.ImportBerry(session, new byte[0x34]).Success);
    }
}
