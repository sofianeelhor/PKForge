using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>The bag showed the engine's pouch names ("KeyItems", "TMHMs"); it now shows the games' own.</summary>
public sealed class BagPouchLookTests
{
    [Theory]
    [InlineData("Items", "Items", "bag")]
    [InlineData("KeyItems", "Key Items", "key")]
    [InlineData("TMHMs", "TMs & HMs", "moves")]
    [InlineData("Balls", "Poké Balls", "ball")]
    [InlineData("BattleItems", "Battle Items", "battle")]
    [InlineData("ZCrystals", "Z-Crystals", "power")]
    public void Known_pouches_read_as_in_the_games(string pouch, string title, string icon)
    {
        var look = BagPouchLooks.Of(pouch);
        Assert.Equal(title, look.Title);
        Assert.Equal(icon, look.Icon);
    }

    [Theory]
    [InlineData("SomeNewPouch", "Some New Pouch")]
    [InlineData("", "Pouch")]
    public void Unknown_pouches_still_read_as_words(string pouch, string title) =>
        Assert.Equal(title, BagPouchLooks.Of(pouch).Title);
}
