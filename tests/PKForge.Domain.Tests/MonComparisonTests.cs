using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>The side-by-side comparison: which fields, which differ, which side is higher,
/// and a dash (never an invented value) for whatever a format does not store.</summary>
public sealed class MonComparisonTests
{
    private static MonSummary Mon(int generation = 8, int level = 50, string? nature = "Adamant", int? natureId = 3,
        IReadOnlyList<int>? ivs = null, IReadOnlyList<int>? evs = null, IReadOnlyList<int>? stats = null,
        TrainingCaps? caps = null, bool? legal = true, int ribbons = 2, int ball = 4, string ballName = "Poké Ball",
        IReadOnlyList<int>? moves = null, int heldItem = 0, string? heldItemName = null, MetInfo? met = null) =>
        new(445, 0, "Garchomp", "", "", false, false, 0, level, [16, 5], generation, $"PK{generation}",
            "Sof", 12345, 54321, natureId, nature, generation >= 3 ? 24 : null, generation >= 3 ? "Rough Skin" : null, null,
            heldItem, heldItemName, null, ball, ballName, 70, null, [],
            stats ?? [183, 182, 115, 90, 105, 122], [108, 130, 95, 80, 85, 102],
            ivs ?? [31, 31, 31, 31, 31, 31], evs ?? [0, 252, 0, 0, 4, 252], caps ?? new TrainingCaps(31, 252),
            null, null, null, null,
            [.. (moves ?? [89, 200]).Select(id => new SummaryMove(id, $"Move{id}", 0, MoveCategory.Physical, 100, 100, 10, 10, 0, ""))],
            [], met, ribbons, 0, [], legal, []);

    private static MonComparison Compare(MonSummary a, MonSummary b) => MonComparison.Build(a, "Save", b, "Bank");

    private static CompareRow Row(MonComparison c, string section, string label) =>
        c.Sections.Single(s => s.Title == section).Rows.Single(r => r.Label == label);

    private static CompareBar Bar(MonComparison c, string section, string label) =>
        c.Sections.Single(s => s.Title == section).Bars.Single(b => b.Label == label);

    [Fact]
    public void ListsEverySectionInOrder()
    {
        var c = Compare(Mon(), Mon());
        Assert.Equal(["Profile", "Stats", "Base stats", "IVs", "EVs", "Moves", "Origin"], c.Sections.Select(s => s.Title));
        Assert.Equal(7, c.Sections[1].Bars.Count); // six stats and the total
        Assert.Equal(6, c.Sections[3].Bars.Count);
        Assert.Equal(0, c.Differences);
        Assert.Equal("Save", c.Left.Where);
        Assert.Equal("Bank", c.Right.Where);
    }

    [Fact]
    public void TheHigherSideWinsEachStat()
    {
        var c = Compare(Mon(level: 60, stats: [200, 150, 115, 90, 105, 122]), Mon(level: 50));
        Assert.Equal(CompareEdge.Left, Row(c, "Profile", "Level").Edge);
        Assert.Equal(CompareEdge.Left, Bar(c, "Stats", "HP").Edge);
        Assert.Equal(CompareEdge.Right, Bar(c, "Stats", "Atk").Edge);
        Assert.Equal(CompareEdge.Same, Bar(c, "Stats", "Def").Edge);
        Assert.Equal(CompareEdge.Same, Bar(c, "Base stats", "Total").Edge);
        // Final stats share one scale, so neither bar is full and the higher is longer.
        var hp = Bar(c, "Stats", "HP");
        Assert.Equal(hp.LeftMax, hp.RightMax);
        Assert.True(hp.LeftFraction > hp.RightFraction && hp.LeftFraction < 1f);
    }

    [Fact]
    public void TextFieldsDifferWithoutAWinner()
    {
        var c = Compare(Mon(nature: "Jolly"), Mon(nature: "Adamant"));
        Assert.Equal(CompareEdge.Differs, Row(c, "Profile", "Nature").Edge);
        Assert.True(Row(c, "Profile", "Nature").Differs);
        Assert.Equal(CompareEdge.Same, Row(c, "Profile", "Ability").Edge);
    }

    [Fact]
    public void ASharedMoveInAnotherSlotIsNotADifference()
    {
        var c = Compare(Mon(moves: [89, 200]), Mon(moves: [200, 89]));
        Assert.All(c.Sections.Single(s => s.Title == "Moves").Rows, r => Assert.Equal(CompareEdge.Same, r.Edge));
        var d = Compare(Mon(moves: [89, 200, 14]), Mon(moves: [89, 200]));
        Assert.Equal(CompareEdge.Differs, Row(d, "Moves", "Move 3").Edge);
        Assert.Equal(MonComparison.Missing, Row(d, "Moves", "Move 3").Right);
        Assert.Equal(MonComparison.Missing, Row(d, "Moves", "Move 4").Left);
    }

    [Fact]
    public void WhatAFormatDoesNotStoreIsADashNeverAGuess()
    {
        var gen1 = Mon(generation: 1, nature: null, natureId: null, caps: new TrainingCaps(15, 65535), ball: 0, legal: null, ribbons: 0,
            ivs: [15, 15, 15, 15, 15, 15], evs: [0, 0, 0, 0, 0, 0]);
        var c = Compare(gen1, Mon(heldItem: 1, heldItemName: "Master Ball"));
        Assert.Equal(MonComparison.Missing, Row(c, "Profile", "Nature").Left);
        Assert.Equal(CompareEdge.None, Row(c, "Profile", "Nature").Edge);
        Assert.Equal(MonComparison.Missing, Row(c, "Profile", "Ability").Left);
        Assert.Equal(MonComparison.Missing, Row(c, "Profile", "Held item").Left);
        Assert.Equal("Master Ball", Row(c, "Profile", "Held item").Right);
        Assert.Equal(MonComparison.Missing, Row(c, "Profile", "Ball").Left);
        Assert.Equal(MonComparison.Missing, Row(c, "Origin", "Ribbons").Left);
        Assert.Equal(CompareEdge.None, Row(c, "Origin", "Ribbons").Edge);
        Assert.Equal(MonComparison.Missing, Row(c, "Origin", "Legality").Left);
        Assert.Equal(MonComparison.Missing, Row(c, "Origin", "Met on").Left);
        // DVs (0-15) and IVs (0-31) are not the same scale: shown, each against its own cap, never ranked.
        var hp = Bar(c, "IVs", "HP");
        Assert.Equal(CompareEdge.None, hp.Edge);
        Assert.Equal(15, hp.Left);
        Assert.Equal(1f, hp.LeftFraction);
        Assert.Equal(1f, hp.RightFraction);
    }

    [Fact]
    public void NatureMarksTheRaisedAndLoweredStat()
    {
        // Adamant: +Atk −SpA (display order HP, Atk, Def, SpA, SpD, Spe).
        var c = Compare(Mon(natureId: 3), Mon(natureId: 0)); // Hardy is neutral
        Assert.Equal(1, Bar(c, "Stats", "Atk").LeftNature);
        Assert.Equal(-1, Bar(c, "Stats", "SpA").LeftNature);
        Assert.All(c.Sections[1].Bars, b => Assert.Equal(0, b.RightNature));
    }

    [Fact]
    public void StatsMissingOnOneSideCompareToNothing()
    {
        var c = Compare(Mon(stats: []), Mon());
        Assert.Null(Bar(c, "Stats", "HP").Left);
        Assert.Equal(CompareEdge.None, Bar(c, "Stats", "HP").Edge);
        Assert.Equal(0f, Bar(c, "Stats", "HP").LeftFraction);
    }

    [Fact]
    public void ThePickHoldsTheFirstAndHandsItBackOnTheSecond()
    {
        var pick = new ComparePick<string>();
        Assert.Null(pick.Waiting);
        Assert.Null(pick.Choose("a", "Pikachu"));
        Assert.Equal("Pikachu", pick.Waiting);
        Assert.Equal("a", pick.Choose("b", "Eevee"));
        Assert.Null(pick.Waiting);
        pick.Choose("c", "Mew");
        pick.Clear();
        Assert.Null(pick.Waiting);
    }

    [Fact]
    public void ThePickKnowsWhereItsFirstChoiceLives()
    {
        var pick = new ComparePick<string>();
        Assert.False(pick.Holds("bank:1"));
        pick.Choose("a", "Pikachu", "bank:1");
        Assert.True(pick.Holds("bank:1"));
        Assert.False(pick.Holds("bank:2"));
        pick.Clear();
        Assert.False(pick.Holds("bank:1"));
        pick.Choose("b", "Eevee"); // a pick with no place is never "the same Pokémon"
        Assert.False(pick.Holds(""));
    }
}
