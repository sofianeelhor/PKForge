using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class EmulatorSaveHeuristicsTests
{
    [Theory]
    [InlineData("pokemon.srm", true)]
    [InlineData("Pokemon Platinum.sav", true)]
    [InlineData("main", true)]
    [InlineData("game.dsv", true)]
    [InlineData("backup.BAK", true)]
    [InlineData("rom.gba", false)]
    [InlineData("screenshot.png", false)]
    public void CandidateFileNameFilter(string name, bool expected) =>
        Assert.Equal(expected, EmulatorSaveHeuristics.IsCandidateFileName(name));

    [Theory]
    [InlineData("01-GC6E-PokemonColosseum.gci", true)]
    [InlineData("01-GXXP-PokemonXD.GCI", true)]
    [InlineData("MemoryCardA.USA.raw", false)]
    [InlineData("game.sav", false)]
    [InlineData("game.s01", false)]
    public void DolphinLinksOnlyIndividualGciSaves(string name, bool expected) =>
        Assert.Equal(expected, EmulatorSaveHeuristics.IsCandidateFileName(name, EmulatorKind.Dolphin));

    [Theory]
    [InlineData("MemoryCardA.USA.raw", true)]
    [InlineData("MemoryCardB.GCP", true)]
    [InlineData("PokemonXD.gci", false)]
    public void DolphinCardsAreRecognizedForExportGuidance(string name, bool expected) =>
        Assert.Equal(expected, EmulatorSaveHeuristics.IsDolphinMemoryCard(name));

    [Theory]
    [InlineData(EmulatorKind.DraStic, "Pokemon Platinum.dsv", true)]
    [InlineData(EmulatorKind.DraStic, "Pokemon Platinum.dss", false)]
    [InlineData(EmulatorKind.PizzaBoyGba, "Pokemon Ruby.sav", true)]
    [InlineData(EmulatorKind.PizzaBoyGbc, "Pokemon Crystal.sav", true)]
    [InlineData(EmulatorKind.PizzaBoyGba, "Pokemon Ruby.stat", false)]
    public void HandheldEmulatorsFindBatterySavesNotStates(EmulatorKind emulator, string name, bool expected) =>
        Assert.Equal(expected, EmulatorSaveHeuristics.IsCandidateFileName(name, emulator));

    [Theory]
    [InlineData("main", true)]
    [InlineData("save.bin", true)]
    [InlineData("main.txt", false)]
    [InlineData("mainx", false)]
    public void EdenSaveFileNameFilter(string name, bool expected) =>
        Assert.Equal(expected, EmulatorSaveHeuristics.IsEdenSaveFileName(name));

    [Fact]
    public void GuessesSwitchGameFromTitleIdSegment()
    {
        var docId = "primary:Android/data/dev.eden.eden_emulator/files/nand/user/save/0000/uid/0100ABF008968000/main";
        Assert.Equal("Sword", EmulatorSaveHeuristics.GuessSwitchGameLabel(docId));
    }

    [Fact]
    public void UnknownTitleFallsBack() =>
        Assert.Equal("Switch save", EmulatorSaveHeuristics.GuessSwitchGameLabel("a/b/DEADBEEF/main"));

    [Fact]
    public void NandBasedEmulatorsRequireExtraCare()
    {
        Assert.True(EmulatorSaveHeuristics.RequiresExtraCare(EmulatorKind.Eden));
        Assert.True(EmulatorSaveHeuristics.RequiresExtraCare(EmulatorKind.Azahar));
        Assert.False(EmulatorSaveHeuristics.RequiresExtraCare(EmulatorKind.RetroArch));
        Assert.False(EmulatorSaveHeuristics.RequiresExtraCare(EmulatorKind.MelonDS));
    }

    [Fact]
    public void NormalizeDeduplicatesAndSortsNewestFirst()
    {
        DetectedSave Save(string id, int day) =>
            new(id, "main", "g", EmulatorKind.Eden, true, new DateTimeOffset(2026, 7, day, 0, 0, 0, TimeSpan.Zero));

        var result = EmulatorSaveHeuristics.Normalize([Save("a", 1), Save("b", 5), Save("a", 9)]);

        Assert.Equal(2, result.Count);
        Assert.Equal("b", result[0].DocumentId);
        Assert.Equal("a", result[1].DocumentId);
    }
}
