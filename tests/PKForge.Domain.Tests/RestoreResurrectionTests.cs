using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>Hardcore restore check: which Pokémon an older restore point would bring back.</summary>
public sealed class RestoreResurrectionTests
{
    private static SlotSummary Mon(int box, int slot, int species, bool shiny = false, string? nick = null) =>
        new(box, slot, species, nick, shiny, true);

    private static SlotSummary Empty(int box, int slot) => new(box, slot, null, null, false, true);

    private static BankEntryInfo Banked(int species, bool shiny = false) =>
        new(species, 0, shiny, "X", 5, 4, "save");

    [Fact]
    public void Identical_boxes_are_safe()
    {
        var boxes = new[] { Mon(0, 0, 25), Mon(0, 1, 1) };
        var result = RestoreResurrection.Detect(boxes, boxes, []);
        Assert.True(result.IsSafe);
    }

    [Fact]
    public void Box_moves_and_sorting_are_not_departures()
    {
        var then = new[] { Mon(0, 0, 25), Mon(0, 1, 1), Empty(0, 2) };
        var now = new[] { Empty(0, 0), Mon(3, 7, 1), Mon(0, 2, 25) };
        Assert.True(RestoreResurrection.Detect(then, now, []).IsSafe);
    }

    [Fact]
    public void A_mon_moved_to_the_bank_is_reported_as_in_bank()
    {
        var then = new[] { Mon(0, 0, 25, shiny: true), Mon(0, 1, 1) };
        var now = new[] { Empty(0, 0), Mon(0, 1, 1) };
        var result = RestoreResurrection.Detect(then, now, [Banked(25, shiny: true)]);
        Assert.False(result.IsSafe);
        Assert.Single(result.Reappearing);
        Assert.Equal(1, result.InBank);
        Assert.Equal(0, result.Elsewhere);
    }

    [Fact]
    public void A_mon_gone_elsewhere_is_counted_outside_the_bank()
    {
        var then = new[] { Mon(0, 0, 25), Mon(0, 1, 150) };
        var now = new[] { Mon(0, 0, 25), Empty(0, 1) };
        var result = RestoreResurrection.Detect(then, now, [Banked(25)]);
        Assert.Equal(0, result.InBank);
        Assert.Equal(1, result.Elsewhere);
    }

    [Fact]
    public void Duplicates_are_matched_one_for_one()
    {
        var then = new[] { Mon(0, 0, 25), Mon(0, 1, 25), Mon(0, 2, 25) };
        var now = new[] { Mon(0, 0, 25) };
        var result = RestoreResurrection.Detect(then, now, [Banked(25)]);
        Assert.Equal(2, result.Reappearing.Count);
        Assert.Equal(1, result.InBank);
    }

    [Fact]
    public void Nickname_distinguishes_same_species()
    {
        var then = new[] { Mon(0, 0, 25, nick: "Sparky") };
        var now = new[] { Mon(0, 0, 25) };
        Assert.False(RestoreResurrection.Detect(then, now, []).IsSafe);
    }
}
