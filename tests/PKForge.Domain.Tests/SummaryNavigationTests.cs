using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The summary screen's L/R walk and page turns: previous/next skip empty slots, wrap
/// around the box ends, and stay put when the mon is alone in its box. Also the Gen 4+
/// characteristic phrase, derived from the highest IV and the personality tie-break.
/// </summary>
public sealed class SummaryNavigationTests
{
    // A 30-slot box with mons in 2, 3, 10 and 29.
    private static readonly HashSet<int> Occupied = [2, 3, 10, 29];
    private static bool IsOccupied(int slot) => Occupied.Contains(slot);

    [Theory]
    [InlineData(2, 1, 3)]
    [InlineData(3, 1, 10)]   // skips the empty 4..9
    [InlineData(10, 1, 29)]
    [InlineData(29, 1, 2)]   // wraps past the end
    [InlineData(2, -1, 29)]  // wraps past the start
    [InlineData(10, -1, 3)]
    [InlineData(5, 1, 10)]   // from an empty cursor slot, the next occupied one
    [InlineData(5, -1, 3)]
    public void StepSkipsEmptySlotsAndWraps(int current, int direction, int expected)
    {
        Assert.Equal(expected, SummaryNavigation.Step(current, direction, 30, IsOccupied));
    }

    [Fact]
    public void StepStaysPutWhenTheMonIsAlone()
    {
        Assert.Null(SummaryNavigation.Step(4, 1, 30, s => s == 4));
        Assert.Null(SummaryNavigation.Step(4, -1, 30, s => s == 4));
        Assert.Null(SummaryNavigation.Step(0, 1, 30, _ => false));
        Assert.Null(SummaryNavigation.Step(0, 1, 0, _ => true));
        Assert.Null(SummaryNavigation.Step(0, 0, 30, _ => true));
    }

    [Fact]
    public void StepWorksForThePartyAndOversizedDirections()
    {
        // The party: six slots, 0..2 filled.
        Assert.Equal(0, SummaryNavigation.Step(2, 1, 6, s => s < 3));
        Assert.Equal(2, SummaryNavigation.Step(0, -1, 6, s => s < 3));
        // Only the sign of the direction counts.
        Assert.Equal(3, SummaryNavigation.Step(2, 7, 30, IsOccupied));
    }

    [Fact]
    public void PositionCountsOnlyOccupiedSlots()
    {
        Assert.Equal((1, 4), SummaryNavigation.Position(2, 30, IsOccupied));
        Assert.Equal((3, 4), SummaryNavigation.Position(10, 30, IsOccupied));
        Assert.Equal((4, 4), SummaryNavigation.Position(29, 30, IsOccupied));
        Assert.Equal((0, 4), SummaryNavigation.Position(5, 30, IsOccupied));
    }

    [Fact]
    public void PagesTurnInOrderAndWrap()
    {
        Assert.Equal(SummaryPage.Stats, SummaryNavigation.Turn(SummaryPage.Info, 1));
        Assert.Equal(SummaryPage.Legality, SummaryNavigation.Turn(SummaryPage.Info, -1));
        Assert.Equal(SummaryPage.Info, SummaryNavigation.Turn(SummaryPage.Legality, 1));
        Assert.Equal(SummaryPage.Origin, SummaryNavigation.Turn(SummaryPage.Legality, -1));
    }

    [Fact]
    public void CharacteristicFollowsTheHighestIvAndItsRemainder()
    {
        // Display order HP, Atk, Def, SpA, SpD, Spe.
        Assert.Equal("Likes to run", Characteristics.Describe(4, [10, 10, 10, 10, 10, 30], 0));        // Spe 30 % 5 = 0
        Assert.Equal("Proud of its power", Characteristics.Describe(6, [1, 30, 2, 3, 4, 5], 0));      // Atk 30 % 5 = 0
        Assert.Equal("Takes plenty of siestas", Characteristics.Describe(5, [31, 1, 1, 1, 1, 1], 0));   // HP 31 % 5 = 1
        Assert.Equal("Very finicky", Characteristics.Describe(9, [0, 0, 0, 29, 0, 0], 0));             // SpA 29 % 5 = 4
        Assert.Null(Characteristics.Describe(3, [31, 31, 31, 31, 31, 31], 0));                         // no characteristics before Gen 4
        Assert.Null(Characteristics.Describe(7, [31, 31], 0));
    }

    [Fact]
    public void CharacteristicTiesStartFromThePersonalityStat()
    {
        IReadOnlyList<int> perfect = [31, 31, 31, 31, 31, 31];
        // Games' order HP, Atk, Def, Spe, SpA, SpD: personality % 6 picks the first stat checked.
        Assert.Equal("Takes plenty of siestas", Characteristics.Describe(6, perfect, 0));
        Assert.Equal("Likes to thrash about", Characteristics.Describe(6, perfect, 1));
        Assert.Equal("Capable of taking hits", Characteristics.Describe(6, perfect, 2));
        Assert.Equal("Alert to sounds", Characteristics.Describe(6, perfect, 3));
        Assert.Equal("Mischievous", Characteristics.Describe(6, perfect, 4));
        Assert.Equal("Somewhat vain", Characteristics.Describe(6, perfect, 11));
    }
}
