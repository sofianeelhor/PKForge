using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>The journal follows each Pokémon of a save through level-ups and evolutions.</summary>
public sealed class SaveJournalEngineTests
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

    private static JournalState Read(byte[] bytes)
    {
        using var session = new SaveEngine().OpenSession(bytes);
        return SaveJournal.Capture(session);
    }

    private static PK5 Mon(SaveFile save, Species species, uint pid, int level)
    {
        var mon = new PK5 { Species = (ushort)species, PID = pid, TID16 = save.TID16, SID16 = save.SID16, OriginalTrainerName = save.OT, Language = 2 };
        mon.CurrentLevel = (byte)level;
        mon.RefreshChecksum();
        return mon;
    }

    [Fact]
    public void AnEvolutionAndANewPokemon_AreToldBetweenTwoReadings()
    {
        var save = (SAV5B2W2)BlankSaveFile.Get(GameVersion.B2, "NATE", LanguageID.English);
        var tepig = Mon(save, Species.Tepig, 0x12345678, 16);
        save.SetBoxSlotAtIndex(tepig, 0, 0, EntityImportSettings.None);
        save.Money = 500;
        var before = Read(save.Write().ToArray());

        var pignite = (PK5)tepig.Clone();
        pignite.Species = (ushort)Species.Pignite;
        pignite.CurrentLevel = 17;
        pignite.RefreshChecksum();
        save.SetBoxSlotAtIndex(pignite, 0, 0, EntityImportSettings.None);
        save.SetBoxSlotAtIndex(Mon(save, Species.Lillipup, 0x0BADF00D, 4), 0, 1, EntityImportSettings.None);
        save.Money = 1500;
        var after = Read(save.Write().ToArray());

        var facts = SaveJournal.Diff(before, after, species => ((Species)species).ToString());
        Assert.Contains(facts, fact => fact.Kind == JournalFactKind.Evolved && fact.Text.StartsWith("Tepig evolved into Pignite", StringComparison.Ordinal));
        Assert.Contains(facts, fact => fact.Kind == JournalFactKind.Caught && fact.Text.StartsWith("New: Lillipup, Lv. 4", StringComparison.Ordinal));
        Assert.Contains(facts, fact => fact.Text == "Money: +1,000 (now 1,500)");
        Assert.DoesNotContain(facts, fact => fact.Kind == JournalFactKind.Left);
    }

    [Theory]
    [InlineData("nds/Pokemon - Platinum Version (Europe).sav")]
    [InlineData("retroarch/mGBA/Pokemon Fire Red.sav")]
    [InlineData("retroarch/POKEMON_EMER_BPEJ_00.srm")]
    public void ARealSaveAgainstItself_HasNothingToTell(string file)
    {
        if (Local(file) is not { } bytes) return;
        var state = Read(bytes);
        Assert.NotEmpty(state.Roster);
        Assert.Empty(SaveJournal.Diff(state, Read(bytes), species => $"#{species}"));
    }
}
