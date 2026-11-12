using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// A blank Game Boy save must reopen as what it was built as. Gen 1 used Blue (Japan),
/// whose blank save takes the Japanese layout while the session called it English; once
/// written and reparsed, the international offsets read the boxes and the Pokémon was gone.
/// </summary>
public sealed class BlankGameBoySaveRoundTripTests
{
    public static TheoryData<int, PKM> Mons => new()
    {
        { 1, Mon(new PK1()) },
        { 2, Mon(new PK2()) },
    };

    private static PKM Mon(PKM pk)
    {
        pk.Species = 25;
        pk.CurrentLevel = 12;
        pk.Move1 = 84; // Thunder Shock
        pk.OriginalTrainerName = "ASH";
        pk.TID16 = 1234;
        pk.Nickname = "PIKACHU";
        pk.RefreshChecksum();
        return pk;
    }

    [Theory]
    [MemberData(nameof(Mons))]
    public void APokemonWrittenToABlankSaveSurvivesExportAndReparse(int generation, PKM mon)
    {
        var engine = new SaveEngine();
        using var blank = (SaveEngineSession)engine.OpenBlankSession(generation);
        var built = blank.SaveFile;
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        Assert.True(blank.ImportSlot(0, 0, bytes, generation == 1 ? "pk1" : "pk2"));

        var exported = blank.Serialize().ToArray();
        Assert.True(SaveParser.TryGetSaveFile(exported, out var reparsed), $"{exported.Length} bytes did not reparse");
        Assert.Equal(25, reparsed!.GetBoxSlotAtIndex(0, 0).Species);
        // Game Boy saves store no language byte: the layout (Japanese or international) is
        // what must agree between the save that was built and the one that is read back.
        Assert.Equal(((ILangDeviantSave)built).Japanese, ((ILangDeviantSave)reparsed).Japanese);
        Assert.Equal((int)LanguageID.English, built.Language);
        Assert.False(((ILangDeviantSave)built).Japanese);

        using var reopened = engine.OpenSession(exported);
        var detail = reopened.ReadEntity(0, 0);
        Assert.Equal(25, detail.Species);
        Assert.Equal(12, detail.Level);
    }
}
