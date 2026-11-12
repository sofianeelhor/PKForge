using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// QA 2026-11-12 issue 4: a backup copy of Platinum put back on Home was listed under the same
/// name as the live save, first, and a Bank send landed in it. Live saves come first, and a copy
/// is named as one before any truncation point.
/// </summary>
public sealed class SaveTargetsTests
{
    private static DetectedSave Save(string id, string label, bool copy, int day, string? trainer = "VWWXX",
        string file = "POKEMON_PL.sav", string? folder = "nds") =>
        new(id, file, label, EmulatorKind.MelonDS, false, new DateTimeOffset(2026, 11, day, 12, 0, 0, TimeSpan.Zero),
            Generation: 4, TrainerName: trainer, FolderHint: copy ? "nds/backup" : folder, IsCopy: copy);

    [Fact]
    public void LiveSavesComeBeforeCopiesKeepingTheirOrder()
    {
        var saves = new[]
        {
            Save("copy", "Pokémon Platinum", copy: true, day: 12),
            Save("hg", "Pokémon HeartGold", copy: false, day: 10),
            Save("live", "Pokémon Platinum", copy: false, day: 11),
        };
        Assert.Equal(["hg", "live", "copy"], SaveTargets.LiveFirst(saves).Select(s => s.DocumentId));
    }

    [Fact]
    public void ACopyIsNamedAsOneRightAfterTheGame()
    {
        var saves = SaveTargets.LiveFirst([
            Save("copy", "Pokémon Platinum", copy: true, day: 12),
            Save("live", "Pokémon Platinum", copy: false, day: 11),
        ]);
        var labels = SaveTargets.Labels(saves);
        Assert.Equal("Pokémon Platinum", labels[0]);
        Assert.Equal("Pokémon Platinum (backup)", labels[1]);
        Assert.StartsWith("Backup copy", SaveTargets.Detail(saves[1]));
        Assert.Contains("nds/backup/POKEMON_PL.sav", SaveTargets.Detail(saves[1]));
        Assert.Contains("2026-11-12", SaveTargets.Detail(saves[1]));
        Assert.DoesNotContain("Backup", SaveTargets.Detail(saves[0]));
    }

    [Fact]
    public void LabelsStayUniqueForTwinLiveSaves()
    {
        var saves = new[]
        {
            Save("a", "Pokémon Platinum", copy: false, day: 11, file: "a.sav"),
            Save("b", "Pokémon Platinum", copy: false, day: 11, file: "a.sav"),
        };
        var labels = SaveTargets.Labels(saves);
        Assert.Equal(2, labels.Distinct().Count());
        Assert.EndsWith("(#1)", labels[0]);
        Assert.EndsWith("(#2)", labels[1]);
    }

    [Fact]
    public void ShelfTilesPickTheLiveSaveEvenWhenTheCopyIsNewer()
    {
        var groups = SaveShelf.Group([
            Save("copy", "Pokémon Platinum", copy: true, day: 12),
            Save("live", "Pokémon Platinum", copy: false, day: 11),
        ]);
        var tile = Assert.Single(groups);
        Assert.Equal("live", tile[0].DocumentId);
        Assert.Equal("copy", tile[1].DocumentId);
    }
}

/// <summary>QA 2026-11-12: the Gen 1 edition picker ignored the file's own name.</summary>
public sealed class EditionFromFileNameTests
{
    private static IReadOnlyList<SaveGameChoice> RedBlue => SaveIdentityRules.ChoicesFor(SaveLayoutFamily.RedBlue, "Red / Blue");
    private static IReadOnlyList<SaveGameChoice> Frlg => SaveIdentityRules.ChoicesFor(SaveLayoutFamily.FireRedLeafGreen, "FireRed / LeafGreen");

    [Theory]
    [InlineData("Pokemon - Blue Version (USA, Europe).sav", "blue-int")]
    [InlineData("Pokemon - Blue Version (Japan).sav", "blue-jp")]
    [InlineData("Pokemon Red.sav", "red")]
    [InlineData("Pocket Monsters - Green (Japan).sav", "green-jp")]
    public void GameBoyEditionsComeFromTheFileName(string file, string id) =>
        Assert.Equal(id, RedBlue[SaveIdentityRules.EditionFromFileName(RedBlue, file)].Id);

    [Fact]
    public void FireRedIsNeverReadAsRed()
    {
        Assert.Equal("firered", Frlg[SaveIdentityRules.EditionFromFileName(Frlg, "Pokemon - FireRed Version.sav")].Id);
        Assert.Equal(-1, SaveIdentityRules.EditionFromFileName(RedBlue, "Pokemon - FireRed Version.sav"));
        Assert.Equal(-1, SaveIdentityRules.EditionFromFileName(RedBlue, "save1.sav"));
    }

    [Fact]
    public void YellowIsNoticed()
    {
        Assert.True(SaveIdentityRules.FileNameSaysYellow("Pokemon - Yellow Version (USA, Europe).sav"));
        Assert.False(SaveIdentityRules.FileNameSaysYellow("Recharged-Yellowish.sav"));
        Assert.Equal(-1, SaveIdentityRules.EditionFromFileName(RedBlue, "Pokemon - Yellow Version (USA, Europe).sav"));
    }
}
