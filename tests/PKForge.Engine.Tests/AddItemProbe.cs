
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class AddItemProbe
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    public void EveryPouchOffersAddableItems(int generation)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(generation);
        var names = session.GetItemNames();
        foreach (var pouch in session.GetBag())
        {
            var legal = session.GetPouchLegalItems(pouch.Name);
            var named = legal.Where(id => id < names.Count && names[id].Length > 0).ToList();
            Assert.True(named.Count > 0, $"gen{generation} pouch {pouch.Name}: {legal.Count} legal, {named.Count} named");
        }
    }

    [Fact]
    public void ItemCountIsClampedToTheGamesRealLimit()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(1);
        var pouch = session.GetBag().First();
        var item = session.GetPouchLegalItems(pouch.Name).First(id => id > 0);

        var stored = session.SetItemCount(pouch.Name, item, 999);

        Assert.Equal(99, stored);
        Assert.Equal(stored, session.GetBag().Single(p => p.Name == pouch.Name).Items.Single(i => i.Id == item).Count);
    }
}
