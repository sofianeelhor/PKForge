using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class GrandUndergroundTests
{
    [Fact]
    public void BdspUndergroundCountsAreBoundedAndSurviveSerialization()
    {
        using var session = new SaveEngineSession(new SAV8BS { OT = "PKForge", Version = GameVersion.BD }, null);
        var items = session.GetGrandUndergroundItems();
        Assert.NotEmpty(items);
        Assert.Equal("Red Sphere S", items[0].Name);
        Assert.Contains(items, item => item.Type == "Statue" && item.MaxCount == 99);

        var sphere = items[0];
        var statue = items.First(item => item.Type == "Statue");
        Assert.Equal(sphere.MaxCount, session.SetGrandUndergroundItemCount(sphere.Id, int.MaxValue));
        Assert.Equal(0, session.SetGrandUndergroundItemCount(statue.Id, -1));
        Assert.Equal(17, session.SetGrandUndergroundItemCount(statue.Id, 17));

        var bytes = session.Serialize().ToArray();
        using var reloaded = new SaveEngineSession(new SAV8BS(bytes), null);
        var after = reloaded.GetGrandUndergroundItems();
        Assert.Equal(sphere.MaxCount, after.Single(item => item.Id == sphere.Id).Count);
        Assert.Equal(17, after.Single(item => item.Id == statue.Id).Count);
    }

    [Fact]
    public void GrandUndergroundIsUnavailableOutsideBdsp()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        Assert.Empty(session.GetGrandUndergroundItems());
        Assert.Throws<NotSupportedException>(() => session.SetGrandUndergroundItemCount(1, 1));
    }
}
