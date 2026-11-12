using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>The game journal tells what happened in a save between two readings of it.</summary>
public sealed class SaveJournalTests
{
    private static string Name(int species) => species switch
    {
        4 => "Charmander", 5 => "Charmeleon", 16 => "Pidgey", 19 => "Rattata", 25 => "Pikachu", _ => $"#{species}",
    };

    private static JournalState State(uint money = 1000, int? minutes = 60, int seen = 10, int caught = 5,
        SaveBadges? badges = null, params JournalMon[] roster) =>
        new(money, minutes, seen, caught, badges, roster);

    private static JournalMon Mon(string key, int species, int level, string? nickname = null, string? met = null) =>
        new(key, species, 0, level, nickname, met);

    [Fact]
    public void TheSameSaveTwice_HasNothingToTell()
    {
        var state = State(badges: SaveBadges.FromMask(BadgeSet.Kanto, 0b1), roster: [Mon("a", 25, 10)]);
        var facts = SaveJournal.Diff(state, state, Name);
        Assert.Empty(facts);
        Assert.Equal("Nothing changed.", SaveJournal.Report(facts));
    }

    [Fact]
    public void ANewPokemon_IsTold_WithWhereItWasMet()
    {
        var facts = SaveJournal.Diff(State(roster: [Mon("a", 25, 10)]),
            State(roster: [Mon("a", 25, 10), Mon("b", 16, 3, met: "Route 1")]), Name);
        var fact = Assert.Single(facts);
        Assert.Equal(JournalFactKind.Caught, fact.Kind);
        Assert.Equal("New: Pidgey, Lv. 3 (Route 1)", fact.Text);
    }

    [Fact]
    public void LevelUpsAndEvolutions_FollowThePokemonNotTheSpecies()
    {
        var facts = SaveJournal.Diff(
            State(roster: [Mon("a", 4, 15, nickname: "Flamey"), Mon("b", 25, 10)]),
            State(roster: [Mon("b", 25, 12), Mon("a", 5, 16, nickname: "Flamey")]), Name);
        Assert.Equal(
        [
            "Flamey (Charmander) evolved into Charmeleon (Lv. 16)",
            "Pikachu: Lv. 10 → 12",
        ], facts.Select(fact => fact.Text));
    }

    [Fact]
    public void TwoIdenticalClones_PairOffOneForOne()
    {
        var facts = SaveJournal.Diff(State(roster: [Mon("a", 19, 5)]),
            State(roster: [Mon("a", 19, 5), Mon("a", 19, 5)]), Name);
        Assert.Equal(JournalFactKind.Caught, Assert.Single(facts).Kind);
    }

    [Fact]
    public void APokemonThatLeft_IsSaidPlainly()
    {
        var facts = SaveJournal.Diff(State(roster: [Mon("a", 19, 5), Mon("b", 25, 9)]), State(roster: [Mon("b", 25, 9)]), Name);
        Assert.Equal("No longer in the save (released, traded or moved out): Rattata", Assert.Single(facts).Text);
    }

    [Fact]
    public void BadgesAreNamed_WhenTheSaveSaysWhich()
    {
        var facts = SaveJournal.Diff(State(badges: SaveBadges.FromMask(BadgeSet.Kanto, 0b1)),
            State(badges: SaveBadges.FromMask(BadgeSet.Kanto, 0b111)), Name);
        Assert.Equal(["Earned the Cascade Badge (3 of 8)", "Earned the Thunder Badge (3 of 8)"], facts.Select(fact => fact.Text));
    }

    [Fact]
    public void BadgesAreCounted_WhenTheSaveOnlyKeepsACount()
    {
        var facts = SaveJournal.Diff(State(badges: SaveBadges.Counted(2, BadgeSet.Sinnoh)),
            State(badges: SaveBadges.Counted(3, BadgeSet.Sinnoh)), Name);
        Assert.Equal("Earned 1 badge (3 of 8)", Assert.Single(facts).Text);
    }

    [Fact]
    public void DexMoneyAndPlayTime_AreDeltas()
    {
        var facts = SaveJournal.Diff(State(money: 3000, minutes: 125, seen: 10, caught: 5),
            State(money: 1800, minutes: 260, seen: 14, caught: 6), Name);
        Assert.Equal(
        [
            "Pokédex: +1 caught, +4 seen (now 6 caught, 14 seen)",
            "Money: −1,200 (now 1,800)",
            "Played 2 h 15 min more (now 4:20)",
        ], facts.Select(fact => fact.Text));
    }

    [Fact]
    public void NoClock_NoPlayTimeLine()
    {
        Assert.Empty(SaveJournal.Diff(State(minutes: null), State(minutes: 90), Name));
    }

    [Fact]
    public void BadgesComeFirst_ThenNewPokemon()
    {
        var facts = SaveJournal.Diff(State(money: 0, badges: SaveBadges.FromMask(BadgeSet.Kanto, 0)),
            State(money: 500, badges: SaveBadges.FromMask(BadgeSet.Kanto, 1), roster: [Mon("x", 16, 4)]), Name);
        Assert.Equal([JournalFactKind.Badge, JournalFactKind.Caught, JournalFactKind.Money], facts.Select(fact => fact.Kind));
    }

    [Fact]
    public void LastTime_IsTheNewestRestorePointOfThatFile_ThatDiffersFromNow()
    {
        static BackupInfo Point(string id, int day, string sha, string doc) =>
            new(id, new DateTimeOffset(2026, 11, day, 12, 0, 0, TimeSpan.Zero), sha, null, "SAV4", 4, 1, DocumentId: doc);
        BackupInfo[] points =
        [
            Point("old", 1, "AA", "save"),
            Point("newer", 3, "BB", "save"),
            Point("same-as-now", 5, "cc", "save"),
            Point("other-file", 6, "DD", "other"),
        ];
        Assert.Equal("newer", SaveJournal.Baseline(points, "save", "CC")?.BackupId);
        Assert.Null(SaveJournal.Baseline(points, "none", "CC"));
    }

    [Fact]
    public void ALongReport_CountsWhatItDoesNotShow()
    {
        var facts = Enumerable.Range(0, 30).Select(i => new JournalFact(JournalFactKind.Caught, $"#{i}")).ToList();
        var report = SaveJournal.Report(facts, maxLines: 5).Split('\n');
        Assert.Equal(6, report.Length);
        Assert.Equal("…and 25 more.", report[^1]);
    }
}
