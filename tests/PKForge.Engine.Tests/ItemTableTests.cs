using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Item names are generation-specific: Gen 1 Rare Candy sits at a different index than the
/// modern list, which misnamed everything and hid real items. The session's item table must
/// match the open game's context, and the pouch legality must contain game-native items.
/// </summary>
public sealed class ItemTableTests
{
    private static string CorpusPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", file);
    }

    [Fact]
    public void Gen7SessionUsesModernItemTable()
    {
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));
        var names = session.GetItemNames();
        Assert.Contains("Master Ball", names);
        Assert.Contains("Rare Candy", names);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void OlderGamesUseTheirOwnItemTable(int generation)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(generation);
        var names = session.GetItemNames();
        Assert.Contains("Rare Candy", names);   // the reporter's missing item, present in every gen
        // The tables differ per generation: Gen 1 knows none of the modern balls.
        if (generation == 1)
            Assert.DoesNotContain(names, n => n.Contains("Dusk Ball"));
    }

    [Fact]
    public void PouchLegalityContainsRareCandyInGen1()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(1);
        var names = session.GetItemNames();
        var rareCandy = Array.IndexOf(names.ToArray(), "Rare Candy");
        Assert.True(rareCandy > 0, "Gen 1 table has no Rare Candy?");

        var allPouchItems = session.GetBag().SelectMany(p => session.GetPouchLegalItems(p.Name)).ToHashSet();
        Assert.Contains(rareCandy, allPouchItems);
    }
}
