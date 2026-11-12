using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The Saves section lists every file PKForge knows, the hidden ones and the backup copies
/// included, and tells them apart by trainer, folder and date.
/// </summary>
public sealed class SaveLibraryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 14, 0, 0, TimeSpan.FromHours(1));

    private static DetectedSave Save(string game, string file, string trainer, DateTimeOffset modified,
        bool hidden = false, bool copy = false, string folder = "ROMs/nds") =>
        new("doc:" + folder + "/" + file, file, game, EmulatorKind.MelonDS, false, modified, 4, trainer, "8:13:19",
            FolderHint: folder,
            Identity: new ResolvedSaveIdentity(game, game, null, null, SaveFormat.Auto, false, IsHidden: hidden),
            IsCopy: copy);

    private static IReadOnlyList<SaveLibraryEntry> Library() => SaveLibrary.Build(
    [
        Save("Platinum", "Platinum.sav", "VWWXX", Now.AddMinutes(-30)),
        Save("Platinum", "Platinum~old.sav", "VWWXX", Now.AddDays(-9), hidden: true, copy: true, folder: "ROMs/nds/.stversions"),
        Save("Black", "Black.sav", "mok", Now.AddDays(-1)),
        Save("HeartGold", "HeartGold (FR).sav", "SOF", Now.AddHours(-3)),
        Save("HeartGold", "HeartGold (EN).sav", "SOF", Now.AddDays(-45), hidden: true),
    ], Now, kind => kind == EmulatorKind.MelonDS ? "melonDS" : kind.ToString());

    [Fact]
    public void EverySaveIsListed_ByGame_NewestFirst_CopiesLast()
    {
        Assert.Equal(
            ["Black.sav", "HeartGold (FR).sav", "HeartGold (EN).sav", "Platinum.sav", "Platinum~old.sav"],
            Library().Select(entry => entry.Save.FileName));
    }

    [Fact]
    public void AnEntrySaysWhoseItIsAndWhereItLives()
    {
        var platinum = Library().Single(entry => entry.Save.FileName == "Platinum.sav");

        Assert.Equal("Platinum", platinum.Title);
        Assert.Equal("VWWXX · 8:13:19 · melonDS", platinum.Who);
        Assert.Equal("ROMs/nds · Platinum.sav · changed 30 min ago", platinum.Where);
        Assert.Equal("", platinum.Tag);
        Assert.True(platinum.OnShelf);
    }

    [Fact]
    public void WhatIsOffTheShelfSaysWhy()
    {
        var library = Library();
        Assert.Equal("Backup copy", library.Single(entry => entry.Save.FileName == "Platinum~old.sav").Tag);
        Assert.Equal("Hidden", library.Single(entry => entry.Save.FileName == "HeartGold (EN).sav").Tag);
    }

    [Theory]
    [InlineData(SaveLibraryFilter.All, 5)]
    [InlineData(SaveLibraryFilter.OnShelf, 3)]
    [InlineData(SaveLibraryFilter.Hidden, 2)]
    [InlineData(SaveLibraryFilter.Copies, 1)]
    public void FiltersNarrowTheList(SaveLibraryFilter filter, int expected) =>
        Assert.Equal(expected, SaveLibrary.Apply(Library(), filter).Count());

    [Fact]
    public void ASnapshotIsARestorePointThePlayerNamed()
    {
        var description = SaveLibrary.SnapshotDescription("  before the League ");

        Assert.Equal("Snapshot: before the League", description);
        Assert.True(SaveLibrary.IsSnapshot(description));
        Assert.Equal("before the League", SaveLibrary.SnapshotName(description));
        Assert.Equal(RestorePointKind.Snapshot, RestorePoints.KindOf(description));
        Assert.False(SaveLibrary.IsSnapshot("Moved 3 Pokémon from box 1 slot 14 on."));
        Assert.Null(SaveLibrary.SnapshotName(null));
        Assert.Equal("Snapshot: unnamed", SaveLibrary.SnapshotDescription(" "));
    }

    // ── Playthroughs ────────────────────────────────────────────────────────

    private static SaveLibraryEntry Entry(DetectedSave save) =>
        SaveLibrary.Build([save], Now, kind => kind.ToString()).Single();

    [Fact]
    public void CopiesOfOnePlaythroughAreOneEntry_TheNewestSaveLeads()
    {
        var older = Entry(Save("Platinum", "Platinum.sav", "VWWXX", Now.AddDays(-2), folder: "ROMs/nds") with { TrainerId = 4321 });
        var newer = Entry(Save("Platinum", "Platinum.sav", "VWWXX", Now.AddHours(-1), folder: "Sync/nds") with { TrainerId = 4321 });
        var copy = Entry(Save("Platinum", "Platinum~old.sav", "VWWXX", Now, copy: true, folder: "ROMs/nds/.stversions") with { TrainerId = 4321 });
        var other = Entry(Save("Black", "Black.sav", "mok", Now.AddDays(-1)));

        var groups = SaveLibrary.Group([other, older, newer, copy]);

        Assert.Equal(2, groups.Count);
        Assert.Same(other, groups[0].Lead);
        Assert.Empty(groups[0].Versions);
        // A real save leads even when a copy is newer; the others follow, newest first.
        Assert.Same(newer, groups[1].Lead);
        Assert.Equal([copy, older], groups[1].Versions);
    }

    [Fact]
    public void ADifferentTrainerIdNameOrGameIsAnotherPlaythrough()
    {
        var mine = Save("HeartGold", "a.sav", "SOF", Now) with { TrainerId = 1 };
        Assert.False(SaveLibrary.SamePlaythrough(mine, mine with { TrainerId = 2 }));
        Assert.False(SaveLibrary.SamePlaythrough(mine, mine with { TrainerName = "sof" }));
        Assert.False(SaveLibrary.SamePlaythrough(mine, mine with { GameLabel = "SoulSilver", Identity = null }));
        Assert.False(SaveLibrary.SamePlaythrough(mine with { TrainerName = null }, mine with { TrainerName = null }));
        // An ID not read on one side does not split them.
        Assert.True(SaveLibrary.SamePlaythrough(mine, mine with { TrainerId = null, FileName = "b.sav" }));
    }

    [Fact]
    public void ASavesSnapshotsAreItsOwn_NewestFirst()
    {
        static BackupInfo Point(string id, int day, string? description, string document) =>
            new(id, new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero), "", "x", "sav", 4, 1, description, document);
        var points = new[]
        {
            Point("a", 1, "Snapshot: before the League", "doc:1"),
            Point("b", 3, "Snapshot: shiny hunt", "doc:1"),
            Point("c", 4, "Moved 3 Pokémon.", "doc:1"),
            Point("d", 5, "Snapshot: elsewhere", "doc:2"),
        };

        Assert.Equal(["b", "a"], SaveLibrary.SnapshotsOf(points, "doc:1").Select(point => point.BackupId));
    }
}
