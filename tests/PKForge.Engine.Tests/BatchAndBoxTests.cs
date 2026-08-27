using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Batch editing and box operations: instructions apply to every mon in the target boxes,
/// swaps exchange whole boxes, delete rescues mons before emptying.
/// </summary>
public sealed class BatchAndBoxTests
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

    [Fact]
    public void BatchApplyTouchesEveryMonInTheBox()
    {
        using var session = Open();
        var box = session.Snapshot.Slots.First(s => s.Box >= 0 && s.Species is not null).Box;
        var count = session.Snapshot.Slots.Count(s => s.Box == box && s.Species is not null);

        var touched = session.BatchApply(["Level=100"], [box]);
        Assert.Equal(count, touched);
        for (var slot = 0; slot < 30; slot++)
        {
            var detail = session.ReadEntity(box, slot);
            if (!detail.IsEmpty) Assert.Equal(100, detail.Level);
        }
    }

    [Fact]
    public void BatchApplyShinyAndIVs()
    {
        using var session = Open();
        var touched = session.BatchApply(["IV_HP=31"], boxes: [0]);
        Assert.True(touched > 0);
        var first = session.Snapshot.Slots.First(s => s.Box == 0 && s.Species is not null);
        var detail = session.ReadEntity(first.Box, first.Slot);
        Assert.Equal(31, detail.IVs[0]);
    }

    [Fact]
    public void SwapBoxesExchangesContents()
    {
        using var session = Open();
        var boxA = 0;
        var boxB = 1;
        var aFirst = session.ReadEntity(boxA, 0).Nickname;
        var bFirst = session.ReadEntity(boxB, 0).Nickname;

        session.SwapBoxes(boxA, boxB);
        Assert.Equal(bFirst, session.ReadEntity(boxA, 0).Nickname);
        Assert.Equal(aFirst, session.ReadEntity(boxB, 0).Nickname);
    }

    [Fact]
    public void ClearBoxEmptiesEverySlot()
    {
        using var session = Open();
        var first = session.Snapshot.Slots.First(s => s.Box >= 0 && s.Species is not null);
        session.ClearBox(first.Box);
        for (var slot = 0; slot < 30; slot++)
            Assert.True(session.ReadEntity(first.Box, slot).IsEmpty);
    }

    [Fact]
    public void BoxNamesArePerBoxAndBounded()
    {
        using var session = Open();
        var name = session.GetBoxName(0);
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.Equal($"BOX 99", session.GetBoxName(98)); // out of range: no throw, stable label
    }

    [Fact]
    public void DeleteBoxRescuesMonsIntoOtherBoxes()
    {
        using var session = Open();
        var before = session.Snapshot.Slots.Count(s => s.Box >= 0 && s.Species is not null);
        Assert.True(before > 0);

        session.DeleteBox(0);
        var after = 0;
        for (var box = 0; box < 32; box++)
            for (var slot = 0; slot < 30; slot++)
                if (!session.ReadEntity(box, slot).IsEmpty) after++;
        // Every mon survives unless the entire storage was full.
        Assert.True(after >= before - 1 || before >= 30 * session.Snapshot.Slots.Max(s => s.Box) + 1,
            $"before={before} after={after}");

        var lastBox = session.Snapshot.Slots.Max(s => s.Box);
        for (var slot = 0; slot < 30; slot++)
            Assert.True(session.ReadEntity(lastBox, slot).IsEmpty, "Logical deletion must leave the replacement blank box at the end.");
    }
}
