using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Storage sorting: every criteria must compact mons to the front in its order and
/// pool empties at the end, never losing or duplicating a mon.
/// </summary>
public sealed class SortTests
{
    private static string CorpusPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", file);
    }

    private static SaveEngineSession Open()
        => new(File.ReadAllBytes(CorpusPath("SM Project 802.main")));

    private static List<(int Species, int Level, bool Shiny, int IvSum)> ReadBox(SaveEngineSession session, int box, int slots)
    {
        var list = new List<(int, int, bool, int)>();
        for (var slot = 0; slot < slots; slot++)
        {
            var d = session.ReadEntity(box, slot);
            if (!d.IsEmpty)
                list.Add((d.Species, d.Level, d.IsShiny, d.IVs.Sum()));
        }
        return list;
    }

    private static List<(int Species, int Form)> ReadIdentityMultiset(SaveEngineSession session)
    {
        var result = new List<(int, int)>();
        for (var box = 0; box < 32; box++)
        for (var slot = 0; slot < 30; slot++)
        {
            var detail = session.ReadEntity(box, slot);
            if (!detail.IsEmpty) result.Add((detail.Species, detail.Form));
        }
        return result.OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();
    }

    [Fact]
    public void DexSortOrdersAndCompacts()
    {
        using var session = Open();
        var before = ReadBox(session, 0, 30).Select(m => m.Species).ToList();
        var placed = session.SortBoxes(Domain.SortCriteria.DexNumber, [0]);
        var after = ReadBox(session, 0, 30);

        Assert.Equal(before.Count, placed);
        Assert.Equal(after.Count, placed);
        Assert.Equal(after.Select(m => m.Species).OrderBy(s => s).ToList(), after.Select(m => m.Species).ToList());
    }

    [Fact]
    public void LevelSortIsStrongestFirst()
    {
        using var session = Open();
        var before = ReadBox(session, 0, 30).Count;
        Assert.True(before > 0);
        session.SortBoxes(Domain.SortCriteria.LevelDesc, [0]);
        var levels = ReadBox(session, 0, 30).Select(m => m.Level).ToList();
        Assert.Equal(levels.OrderByDescending(l => l).ToList(), levels);
    }

    [Fact]
    public void ShinyFirstSortPutsShiniesInFront()
    {
        using var session = Open();
        session.SortBoxes(Domain.SortCriteria.ShinyFirst, [0]);
        var shininess = ReadBox(session, 0, 30).Select(m => m.Shiny).ToList();
        var firstNonShiny = shininess.FindIndex(s => !s);
        if (firstNonShiny >= 0)
            Assert.DoesNotContain(shininess.Skip(firstNonShiny), s => s);
    }

    [Fact]
    public void GlobalSortKeepsEveryMon()
    {
        using var session = Open();
        int CountAll()
        {
            var total = 0;
            for (var box = 0; box < 32; box++)
                total += ReadBox(session, box, 30).Count;
            return total;
        }
        var before = CountAll();
        var placed = session.SortBoxes(Domain.SortCriteria.DexNumber, null);
        Assert.Equal(before, placed);
        Assert.Equal(before, CountAll());
    }

    [Theory]
    [InlineData(Domain.SortCriteria.DexNumber)]
    [InlineData(Domain.SortCriteria.Alphabetical)]
    [InlineData(Domain.SortCriteria.LevelDesc)]
    [InlineData(Domain.SortCriteria.IvTotalDesc)]
    [InlineData(Domain.SortCriteria.Type)]
    [InlineData(Domain.SortCriteria.AgeOldest)]
    [InlineData(Domain.SortCriteria.ShinyFirst)]
    public void GlobalSortPreservesEverySpeciesAndForm(Domain.SortCriteria criteria)
    {
        using var session = Open();
        var before = ReadIdentityMultiset(session);

        session.SortBoxes(criteria, null);

        Assert.Equal(before, ReadIdentityMultiset(session));
    }

    [Fact]
    public void AllBoxesSortCompactsFromBoxOne()
    {
        using var session = Open();
        session.SortBoxes(Domain.SortCriteria.DexNumber, null);
        // Mon density must be non-increasing across boxes: empties pool at the end.
        var last = 30;
        for (var box = 0; box < 32; box++)
        {
            var count = ReadBox(session, box, 30).Count;
            Assert.True(count <= last, $"box {box}: {count} after {last}");
            last = count;
        }
    }
}
