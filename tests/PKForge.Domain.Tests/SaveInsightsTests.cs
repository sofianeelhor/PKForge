using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// What the Saves section says about a save without opening it for editing, how it compares
/// two saves, and how an exported copy is named so that it replaces nothing.
/// </summary>
public sealed class SaveInsightsTests
{
    private static SlotSummary Mon(int box, int slot, int species, string? nickname = null, bool shiny = false, bool egg = false) =>
        new(box, slot, species, nickname, shiny, true, 0, egg);

    private static SaveSnapshot Snapshot(params SlotSummary[] slots) =>
        new("PK4", 4, new byte[] { 1 }, [.. slots, new SlotSummary(0, 29, null, null, false, true)], "save");

    private static string Name(int species) => species switch { 25 => "Pikachu", 6 => "Charizard", 16 => "Pidgey", 143 => "Snorlax", _ => $"#{species}" };

    [Fact]
    public void ASaveIsSummedUpInAFewLines()
    {
        var snapshot = Snapshot(Mon(-1, 0, 25), Mon(-1, 1, 6, shiny: true), Mon(0, 0, 16), Mon(0, 1, 143), Mon(1, 0, 16, egg: true));

        var insight = SaveInsights.From(snapshot, "SOF", 1234, 3000, "0:48:47", dexSeen: 40, dexCaught: 12, dexTotal: 493);

        Assert.Equal(
        [
            "Trainer SOF · ID 01234 · 0:48:47 played",
            "Party: Pikachu, Charizard",
            "3 Pokémon in the boxes · 1 shiny · 1 egg",
            "Pokédex: 12 caught, 40 seen of 493",
            "Money: 3,000",
        ], SaveInsights.Lines(insight, Name));
    }

    [Fact]
    public void AnEmptySaveStillReads()
    {
        var insight = SaveInsights.From(Snapshot(), "RED", 1, 0, null, 0, 0, 0);

        Assert.Equal(["Trainer RED · ID 00001", "Party: empty", "0 Pokémon in the boxes"], SaveInsights.Lines(insight, Name));
    }

    [Fact]
    public void TwoSavesAreComparedByWhatEachHasAlone()
    {
        var before = Snapshot(Mon(-1, 0, 25), Mon(0, 0, 16), Mon(0, 1, 16), Mon(0, 2, 143));
        var after = Snapshot(Mon(-1, 0, 25), Mon(0, 5, 16), Mon(0, 6, 6, shiny: true));

        var comparison = SaveInsights.Compare(before, after);

        Assert.Equal(2, comparison.InBoth); // Pikachu and one Pidgey, wherever they sit
        Assert.Equal([16, 143], comparison.OnlyInFirst.Select(slot => slot.Species));
        Assert.Equal([6], comparison.OnlyInSecond.Select(slot => slot.Species));
    }

    [Fact]
    public void ANicknameOrAShineTellsTwoOfTheSameSpeciesApart()
    {
        var first = Snapshot(Mon(0, 0, 25, "SPARKY"), Mon(0, 1, 25));
        var second = Snapshot(Mon(0, 0, 25), Mon(0, 1, 25, shiny: true));

        var comparison = SaveInsights.Compare(first, second);

        Assert.Equal(1, comparison.InBoth);
        Assert.Equal("SPARKY", Assert.Single(comparison.OnlyInFirst).Nickname);
        Assert.True(Assert.Single(comparison.OnlyInSecond).IsShiny);
    }

    [Theory]
    [InlineData("Platinum.sav", new string[0], "Platinum.sav")]
    [InlineData("Platinum.sav", new[] { "Black.sav" }, "Platinum.sav")]
    [InlineData("Platinum.sav", new[] { "platinum.SAV" }, "Platinum (copy).sav")]
    [InlineData("Platinum.sav", new[] { "Platinum.sav", "Platinum (copy).sav" }, "Platinum (copy 2).sav")]
    [InlineData("main", new[] { "main" }, "main (copy)")]
    public void AnExportedCopyNeverTakesANameInUse(string name, string[] existing, string expected) =>
        Assert.Equal(expected, SaveInsights.FreeFileName(name, existing));

    [Fact]
    public void BadgesAreCountedAndNamedFromTheirBits()
    {
        var badges = SaveBadges.FromMask(BadgeSet.Kanto, 0b0000_0111);
        Assert.Equal(3, badges.Earned);
        Assert.Equal(8, badges.Total);
        Assert.Equal("Badges: 3 of 8 (Boulder, Cascade, Thunder)", badges.Line);
        Assert.Equal("Badges: 8 of 8, all of them", SaveBadges.FromMask(BadgeSet.Sinnoh, 0xFF).Line);
        // Johto first, then Kanto in the next byte.
        Assert.Equal(["Zephyr", "Boulder"], SaveBadges.FromMask(BadgeSet.JohtoKanto, 0x101).Names);
        Assert.Equal("Badges: 5 of 8", SaveBadges.Counted(5, BadgeSet.Sinnoh).Line);
    }

    [Fact]
    public void BadgesJoinTheSummary()
    {
        var insight = SaveInsights.From(Snapshot(), "SOF", 1, 0, null, 0, 0, 0) with { Badges = SaveBadges.FromMask(BadgeSet.Hoenn, 1) };
        Assert.Contains("Badges: 1 of 8 (Stone)", SaveInsights.Lines(insight, Name));
    }
}
