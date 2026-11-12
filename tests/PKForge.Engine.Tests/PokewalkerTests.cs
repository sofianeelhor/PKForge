using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>The Pokémon paired with the HeartGold/SoulSilver Pokéwalker, read through PKHeX's own
/// misc slot (counters and courses are covered in WorldEventsTests).</summary>
public sealed class PokewalkerTests
{
    [Fact]
    public void BlankSaveHasNobodyOnAStroll()
    {
        using var session = OpenBlank();
        Assert.Null(PokewalkerService.GetPairedPokemon(session));
        Assert.Equal(27, PokewalkerService.GetState(session).Courses.Count);
    }

    [Fact]
    public void ThePairedPokemonSurvivesSerializeAndReopen()
    {
        using var session = OpenBlank();
        var save = (SAV4HGSS)session.SaveFile;
        var pk = new PK4 { Species = (ushort)Species.Pikachu, Language = (int)LanguageID.English, OriginalTrainerName = "SOF", PID = 0x12345678, TID16 = 1, SID16 = 2 };
        pk.CurrentLevel = 15;
        pk.ClearNickname();
        pk.RefreshChecksum();
        var slot = save.GetExtraSlots().Single(s => s.Type == StorageSlotType.Pokéwalker);
        save.SetSlotFormatStored(pk, slot.Data.Span);

        using var reloaded = Reopen(session);
        var walking = PokewalkerService.GetPairedPokemon(reloaded);
        Assert.NotNull(walking);
        Assert.Equal((ushort)Species.Pikachu, walking.Species);
        Assert.Equal(15, walking.Level);
        Assert.Equal("Pikachu", walking.SpeciesName);
        Assert.False(walking.Shiny);
    }

    [Fact]
    public void OtherSavesAreUnsupported()
    {
        using var session = new SaveEngineSession(new SAV4Pt(new byte[0x80000]), null);
        Assert.False(PokewalkerService.IsSupported(session));
        Assert.Throws<NotSupportedException>(() => PokewalkerService.GetPairedPokemon(session));
    }

    /// <summary>A full-size, zero-filled 512 KB HGSS image (see HoneyTreeTests.OpenBlank for why not BlankSaveFile).</summary>
    private static SaveEngineSession OpenBlank() =>
        new(new SAV4HGSS(new byte[0x80000]) { Language = (int)LanguageID.English }, null);

    private static SaveEngineSession Reopen(SaveEngineSession session) =>
        new(new SAV4HGSS(session.Serialize().ToArray()), null);
}
