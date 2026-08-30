using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Regression coverage for Gen 7's two separate item stores: the normal
/// Bag and Poké Pelago's Bean counters. Both must survive a full save write.</summary>
public sealed class Gen7BagAndBeansTests
{
    [Fact]
    public void USUMBagEditSurvivesSerializeAndReopen()
    {
        var engine = new SaveEngine();
        using var session = OpenGen7Session(engine);
        var pouch = session.GetBag().Single(p => p.Name == "Items");
        var item = session.GetPouchLegalItems(pouch.Name).First(id => id > 0);

        var stored = session.SetItemCount(pouch.Name, item, 17);
        Assert.Equal(17, stored);

        using var reloaded = engine.OpenSession(session.Serialize());
        var after = reloaded.GetBag().Single(p => p.Name == pouch.Name).Items.Single(i => i.Id == item);
        Assert.Equal(17, after.Count);
    }

    [Fact]
    public void PokeBeansAreStoredSeparatelyAndSurviveSerializeAndReopen()
    {
        var engine = new SaveEngine();
        using var session = OpenGen7Session(engine);
        var beans = session.GetPokeBeans();
        Assert.Equal(15, beans.Count);
        Assert.Equal("Red Bean", beans[0].Name);
        Assert.Equal("Rainbow Bean", beans[^1].Name);

        Assert.Equal(255, session.SetPokeBeanCount(0, 999));
        Assert.Equal(0, session.SetPokeBeanCount(1, -1));
        Assert.Equal(37, session.SetPokeBeanCount(14, 37));

        using var reloaded = engine.OpenSession(session.Serialize());
        var after = reloaded.GetPokeBeans();
        Assert.Equal(255, after[0].Count);
        Assert.Equal(0, after[1].Count);
        Assert.Equal(37, after[14].Count);
    }

    private static SaveEngineSession OpenGen7Session(SaveEngine engine)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", "SM Project 802.main");
        return (SaveEngineSession)engine.OpenSession(File.ReadAllBytes(path));
    }
}
