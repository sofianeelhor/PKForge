using PKHeX.Core;
using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Covers the expert QoL engine surface: dex editing, living-dex gaps,
/// shiny-safe nature rerolls, Showdown box export, the Nuzlocke report and presets.</summary>
public sealed class QoLEngineTests
{
    private static string CorpusPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", file);
    }

    private static SaveEngineSession OpenGen5()
    {
        var engine = new SaveEngine();
        var session = engine.OpenBlankSession(5);
        var mon = new PK5 { Species = 25, CurrentLevel = 10, Version = GameVersion.B };
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        Assert.True(session.ImportSlot(0, 0, bytes));
        return (SaveEngineSession)session;
    }

    [Fact]
    public void DexEntriesToggleIndependently()
    {
        using var session = OpenGen5();
        Assert.False(session.GetDexEntry(6).Caught);

        session.SetDexEntry(6, seen: true, caught: false);
        Assert.True(session.GetDexEntry(6).Seen);
        Assert.False(session.GetDexEntry(6).Caught);

        session.SetDexEntry(6, seen: true, caught: true);
        Assert.True(session.GetDexEntry(6).Caught);
    }

    [Fact]
    public void DexEditsSurviveWriteAndReloadOnGen5()
    {
        using var session = OpenGen5();
        Assert.False(session.GetDexEntry(6).Seen);

        session.SetDexEntry(6, seen: true, caught: false);
        var bytes = session.Serialize().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var seenOnly));
        using var seenSession = new SaveEngineSession(seenOnly!, null);
        var seenState = seenSession.GetDexEntry(6);
        Assert.True(seenState.Seen);
        Assert.False(seenState.Caught);

        session.SetDexEntry(6, seen: true, caught: true);
        bytes = session.Serialize().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var caughtSave));
        using var caughtSession = new SaveEngineSession(caughtSave!, null);
        Assert.True(caughtSession.GetDexEntry(6).Caught);

        session.SetDexEntry(6, seen: false, caught: false);
        bytes = session.Serialize().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var cleared));
        using var clearedSession = new SaveEngineSession(cleared!, null);
        Assert.False(clearedSession.GetDexEntry(6).Seen);
    }

    [Fact]
    public void DexEditsSurviveWriteAndReloadOnGen7()
    {
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));
        const int species = 25;

        session.SetDexEntry(species, seen: true, caught: false);
        var bytes = session.Serialize().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var seenOnly));
        using var seenSession = new SaveEngineSession(seenOnly!, null);
        var seenState = seenSession.GetDexEntry(species);
        Assert.True(seenState.Seen);
        Assert.False(seenState.Caught);

        session.SetDexEntry(species, seen: true, caught: true);
        bytes = session.Serialize().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var caughtSave));
        using var caughtSession = new SaveEngineSession(caughtSave!, null);
        Assert.True(caughtSession.GetDexEntry(species).Caught);

        session.SetDexEntry(species, seen: false, caught: false);
        bytes = session.Serialize().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var cleared));
        using var clearedSession = new SaveEngineSession(cleared!, null);
        Assert.False(clearedSession.GetDexEntry(species).Seen);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void DexSettersActuallyChangeStateOnModernGenerations(int generation)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(generation);
        var species = 25;

        session.SetDexEntry(species, seen: true, caught: true);
        var state = session.GetDexEntry(species);
        Assert.True(state.Seen, $"gen {generation}: seen flag did not change");
        Assert.True(state.Caught, $"gen {generation}: caught flag did not change");

        session.SetDexEntry(species, seen: false, caught: false);
        state = session.GetDexEntry(species);
        Assert.False(state.Seen, $"gen {generation}: seen flag did not clear");
        Assert.False(state.Caught, $"gen {generation}: caught flag did not clear");
    }

    [Fact]
    public void MissingSpeciesIgnoresOwnedSpecies()
    {
        using var session = OpenGen5();
        var missing = session.GetMissingSpecies();
        Assert.Contains(6, missing); // nothing owns Charizard
        Assert.DoesNotContain(25, missing); // the seeded Pikachu
    }

    [Fact]
    public void NatureRerollOnGen5KeepsShinyAndSurvivesReload()
    {
        using var session = OpenGen5();
        var before = session.GetRngInfo(0, 0);
        var wanted = (before.Nature + 9) % 25;

        Assert.True(session.RerollNatureKeepShiny(0, 0, wanted));
        var after = session.GetRngInfo(0, 0);
        Assert.Equal(wanted, after.Nature);
        Assert.Equal(before.Pid, after.Pid);
        Assert.Equal(before.Shiny, after.Shiny);
        Assert.Equal(before.IVs, after.IVs);

        var bytes = session.Serialize().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var reopened));
        using var reloaded = new SaveEngineSession(reopened!, null);
        Assert.Equal(wanted, reloaded.GetRngInfo(0, 0).Nature);
    }

    [Fact]
    public void NatureRerollOnGen3PreservesShinyState()
    {
        var engine = new SaveEngine();
        using var raw = engine.OpenBlankSession(3);
        var session = (SaveEngineSession)raw;
        var mon = new PK3 { Species = 25, CurrentLevel = 10, Version = GameVersion.FR };
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        Assert.True(session.ImportSlot(0, 0, bytes));

        var before = session.GetRngInfo(0, 0);
        var wanted = (before.Nature + 13) % 25;
        Assert.True(session.RerollNatureKeepShiny(0, 0, wanted));

        var after = session.GetRngInfo(0, 0);
        Assert.Equal(wanted, after.Nature);
        Assert.Equal(before.Shiny, after.Shiny);
        Assert.Equal(before.IVs, after.IVs);
        Assert.Equal(before.Ability, after.Ability);
        Assert.NotEqual(before.Pid, after.Pid); // Gen 3 nature is PID-derived.
    }

    [Fact]
    public async Task FillSpeciesGeneratesLegalMonsIntoEmptySlots()
    {
        using var session = OpenGen5();
        var legalizer = new LegalizerService();

        var outcome = await Task.Run(() => legalizer.FillSpecies(session, [1, 4, 7]));

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(1, session.ReadEntity(0, 1).Species);
        Assert.Equal(4, session.ReadEntity(0, 2).Species);
        Assert.Equal(7, session.ReadEntity(0, 3).Species);
    }

    [Fact]
    public void BoxShowdownExportListsEveryMon()
    {
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));
        var box = session.Snapshot.Slots.First(s => s.Box >= 0 && s.Species is not null).Box;
        var count = Enumerable.Range(0, 30).Count(slot => !session.ReadEntity(box, slot).IsEmpty);

        var text = session.ExportBoxShowdown(box);

        Assert.True(text.Length > 0);
        Assert.Equal(count, text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void NuzlockeReportHasExactlyOneFirstCatchPerRoute()
    {
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));
        var report = session.GetNuzlockeReport();

        Assert.NotEmpty(report);
        Assert.All(
            report.GroupBy(c => c.Route),
            group => Assert.Equal(1, group.Count(c => c.FirstCatch)));
    }

    [Fact]
    public void HyperTrainBatchInstructionWorksOnGen7()
    {
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));
        var box = session.Snapshot.Slots.First(s => s.Box >= 0 && s.Species is not null).Box;

        var touched = session.BatchApply(["HyperTrain"], [box]);

        Assert.True(touched > 0);
        var slot = session.Snapshot.Slots.First(s => s.Box == box && s.Species is not null).Slot;
        Assert.All(session.GetPotential(box, slot).HyperTrained, trained => Assert.True(trained));
    }
}
