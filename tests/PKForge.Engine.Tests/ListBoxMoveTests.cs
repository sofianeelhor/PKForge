using PKForge.Domain;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// "Moving Pokémon around in boxes for Gen 1 doesn't save." The Game Boy games store a box
/// as a list with no gaps, so the written file closes any gap a move leaves and every
/// Pokémon after it changes slot. The write guard saw untargeted slots change and refused,
/// while the grid kept showing the move. The session now packs its boxes the way the file
/// will, and the guard lets a targeted list box shift as long as it loses nothing.
/// </summary>
public sealed class ListBoxMoveTests
{
    private static readonly SaveEngine Engine = new();
    private const ushort Bulbasaur = 1, Charmander = 4, Squirtle = 7;

    /// <summary>A save of <paramref name="version"/> with the three starters at the front of box 1.</summary>
    private static SaveFile SaveWithStarters(GameVersion version)
    {
        var save = BlankSaveFile.Get(version, "RED", LanguageID.English);
        ushort[] starters = [Bulbasaur, Charmander, Squirtle];
        for (var slot = 0; slot < starters.Length; slot++)
            save.SetBoxSlotAtIndex(Starter(save, starters[slot], slot), 0, slot, EntityImportSettings.None);
        return save;
    }

    /// <summary>The same save as the bytes of a file: a Game Boy save is only recognized
    /// with a party in it, so Pikachu leads one.</summary>
    private static byte[] FileWithStarters(GameVersion version)
    {
        var save = SaveWithStarters(version);
        save.SetPartySlotAtIndex(Starter(save, 25, 3), 0, EntityImportSettings.None);
        return save.Write().ToArray();
    }

    private static PKM Starter(SaveFile save, ushort species, int index)
    {
        var mon = save.BlankPKM;
        mon.Species = species;
        mon.CurrentLevel = (byte)(5 + index);
        mon.OriginalTrainerName = "RED";
        mon.Nickname = SpeciesName.GetSpeciesNameGeneration(species, (int)LanguageID.English, save.Generation);
        mon.Move1 = 33; // Tackle
        mon.RefreshChecksum();
        return mon;
    }

    private static int[] Box(ISaveEngineSession session, int box, int count) =>
        [.. Enumerable.Range(0, count).Select(slot => session.ReadEntity(box, slot) is { IsEmpty: false } mon ? mon.Species : 0)];

    [Theory]
    [InlineData(GameVersion.RD)]
    [InlineData(GameVersion.YW)]
    [InlineData(GameVersion.C)]
    public void AMoveOntoAFarEmptySlotIsWrittenAndLandsAtTheEndOfTheList(GameVersion version)
    {
        var original = FileWithStarters(version);
        using var session = Engine.OpenSession(original);

        session.MoveSlot(0, 0, 0, 10);
        Assert.True(session.PackListBoxes());
        var written = session.Serialize();

        Assert.Equal([Charmander, Squirtle, Bulbasaur, 0], Box(session, 0, 4));
        Assert.Null(Engine.CheckWriteSafety(original, written, WriteScope.Only(new SlotRef(0, 0), new SlotRef(0, 10))));
        // What the player sees is what the file holds.
        using var reopened = Engine.OpenSession(written);
        Assert.Equal(Box(session, 0, 20), Box(reopened, 0, 20));
    }

    [Fact]
    public void AMoveToAnotherBoxClosesTheGapItLeaves()
    {
        var original = FileWithStarters(GameVersion.RD);
        using var session = Engine.OpenSession(original);

        session.MoveSlot(0, 0, 1, 5);
        Assert.True(session.PackListBoxes());
        var written = session.Serialize();

        Assert.Equal([Charmander, Squirtle, 0], Box(session, 0, 3));
        Assert.Equal([Bulbasaur, 0], Box(session, 1, 2));
        Assert.Null(Engine.CheckWriteSafety(original, written, WriteScope.Only(new SlotRef(0, 0), new SlotRef(1, 5))));
    }

    [Fact]
    public void AListBoxMayShiftButNotLoseAPokemonTheEditDidNotTarget()
    {
        var original = FileWithStarters(GameVersion.RD);
        using var session = Engine.OpenSession(original);

        // The edit says it targets slot 1 only, yet Charmander (slot 2) is gone as well.
        session.ReleaseSlot(0, 0);
        session.ReleaseSlot(0, 1);
        session.PackListBoxes();

        var refusal = Engine.CheckWriteSafety(original, session.Serialize(), WriteScope.Only(new SlotRef(0, 0)));
        Assert.NotNull(refusal);
        Assert.Contains("would lose", refusal);
    }

    [Fact]
    public void AnotherBoxOfAListSaveStillMayNotChange()
    {
        var original = FileWithStarters(GameVersion.RD);
        using var session = Engine.OpenSession(original);

        session.MoveSlot(0, 0, 1, 0);
        session.PackListBoxes();

        // Box 2 received a Pokémon although only box 1 was declared.
        Assert.NotNull(Engine.CheckWriteSafety(original, session.Serialize(), WriteScope.Only(new SlotRef(0, 0))));
    }

    [Theory]
    [InlineData(GameVersion.E)]
    [InlineData(GameVersion.SW)]
    public void GamesWithFixedSlotsKeepTheirGaps(GameVersion version)
    {
        using var session = new SaveEngineSession(SaveWithStarters(version), null);

        session.MoveSlot(0, 0, 0, 10);

        Assert.False(session.PackListBoxes());
        Assert.Equal([0, Charmander, Squirtle], Box(session, 0, 3));
        Assert.Equal(Bulbasaur, session.ReadEntity(0, 10).Species);
    }
}
