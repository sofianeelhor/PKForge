using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class BankBoxLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    private static BankEntryInfo Info(int species) => new(species, 0, false, $"M{species}", 5, 8, "t", "PK8");

    private static BankEntry Entry(int box, int slot) =>
        new(Guid.NewGuid(), box, slot, Info(1), DateTimeOffset.UnixEpoch);

    [Fact]
    public void LocksPersistInTheIndexAndAnOldIndexLoadsWithNone()
    {
        var bank = new FileBankService(_root);
        bank.Add([1], Info(25));
        var index = Path.Combine(_root, "index.json");
        Assert.DoesNotContain("LockedBoxes", File.ReadAllText(index)); // no locks: the old shape

        bank.SetBoxLocked(1, true);
        Assert.Equal([1], new FileBankService(_root).LockedBoxes);

        bank.SetBoxLocked(1, false);
        Assert.Empty(new FileBankService(_root).LockedBoxes);
    }

    [Fact]
    public void CountFollowsTheBankWithoutCopyingIt()
    {
        var bank = new FileBankService(_root);
        Assert.Equal(0, bank.Count);
        var first = bank.Add([1], Info(25));
        bank.Add([2], Info(133));
        Assert.Equal(2, bank.Count);
        bank.Remove(first.Id);
        Assert.Equal(bank.GetAll().Count, bank.Count);
    }

    [Fact]
    public void ADropFromABoxLockedWhileCarryingIsRefusedWithTheReason()
    {
        // The Bank page refuses to lock the carried Pokémon's own box; the service still
        // refuses a move out of a locked box, and its message is what the page shows.
        var bank = new FileBankService(_root);
        var carried = bank.Add([1], Info(25));
        bank.SetBoxLocked(carried.Box, true);
        var refused = Assert.Throws<InvalidOperationException>(() => bank.Move(carried.Id, carried.Box + 1, 0));
        Assert.Equal(BankBoxLocks.Refusal(carried.Box), refused.Message);
    }

    [Fact]
    public void AutoPlacedDepositsStepOverALockedBox()
    {
        var bank = new FileBankService(_root);
        bank.SetBoxLocked(0, true);
        var single = bank.Add([1], Info(1));
        var many = bank.AddMany([new BankDeposit([2], Info(2)), new BankDeposit([3], Info(3))]);
        Assert.Equal((1, 0), (single.Box, single.Slot));
        Assert.Equal([(1, 1), (1, 2)], many.Select(e => (e.Box, e.Slot)));
        Assert.Throws<InvalidOperationException>(() => bank.AddMany([new BankDeposit([4], Info(4), 0, 5)]));
    }

    [Fact]
    public void NothingMovesIntoOrOutOfALockedBoxButEditsStillWork()
    {
        var bank = new FileBankService(_root);
        var inside = bank.Add([1], Info(1));   // box 0
        bank.Move(bank.Add([2], Info(2)).Id, 1, 0);
        var outside = bank.GetAll().Single(e => e.Box == 1);
        bank.SetBoxLocked(0, true);

        Assert.Throws<InvalidOperationException>(() => bank.Move(inside.Id, 2, 0));
        Assert.Throws<InvalidOperationException>(() => bank.Move(outside.Id, 0, 5));
        Assert.Throws<InvalidOperationException>(() => bank.Place([(inside.Id, 0, 9)]));
        Assert.Throws<InvalidOperationException>(() => bank.Remove(inside.Id));
        Assert.Equal(1, bank.RemoveMany([inside.Id, outside.Id])); // only the unlocked one leaves
        Assert.Equal(0, bank.Place([(inside.Id, 0, 0)]));          // staying put is not a move

        bank.Replace(inside.Id, [9], Info(7));
        Assert.Equal(7, bank.GetAll().Single().Info.Species);
        Assert.Equal((0, 0), (bank.GetAll().Single().Box, bank.GetAll().Single().Slot));
    }

    [Fact]
    public void LocksFollowTheirBoxAndALockedBoxCannotBeDeleted()
    {
        var bank = new FileBankService(_root);
        while (bank.BoxCount < 4) bank.AddBox();
        bank.SetBoxLocked(3, true);
        bank.RemapBoxes(BankBoxRemap.Move(bank.BoxCount, 3, 0));
        Assert.Equal([0], bank.LockedBoxes);
        Assert.Throws<InvalidOperationException>(() => bank.RemapBoxes(BankBoxRemap.Remove(bank.BoxCount, 0)));
        bank.RemapBoxes(BankBoxRemap.Remove(bank.BoxCount, 2));
        Assert.Equal([0], new FileBankService(_root).LockedBoxes);
    }

    [Fact]
    public void SplitKeepsLockedEntriesOutOfASelection()
    {
        var a = Entry(0, 0);
        var b = Entry(1, 0);
        var c = Entry(2, 4);
        var (free, held) = BankBoxLocks.Split([a, b, c], new HashSet<int> { 1 });
        Assert.Equal([a, c], free);
        Assert.Equal(1, held);
    }

    [Fact]
    public void FirstTouchedNamesTheLockedBoxAPlacementWouldDisturb()
    {
        var a = Entry(0, 0);
        var b = Entry(1, 3);
        var locked = new HashSet<int> { 1 };
        Assert.Null(BankBoxLocks.FirstTouched([(a.Id, 2, 0), (b.Id, 1, 3)], [a, b], locked));
        Assert.Equal(1, BankBoxLocks.FirstTouched([(a.Id, 1, 0)], [a, b], locked));
        Assert.Equal(1, BankBoxLocks.FirstTouched([(b.Id, 1, 4)], [a, b], locked));
        Assert.Null(BankBoxLocks.FirstTouched([(b.Id, 4, 4)], [a, b], new HashSet<int>()));
    }

    [Fact]
    public void AWholeBankReorderFlowsAroundLockedBoxes()
    {
        var ordered = Enumerable.Range(0, 35).Select(i => Entry(5, 0)).ToList();
        var placements = BankPlacement.Reorder(null, ordered, new HashSet<int> { 0, 2 });
        Assert.Equal((1, 0), (placements[0].Box, placements[0].Slot));
        Assert.Equal((1, 29), (placements[29].Box, placements[29].Slot));
        Assert.Equal((3, 0), (placements[30].Box, placements[30].Slot)); // box 2 is stepped over
        Assert.DoesNotContain(placements, p => p.Box is 0 or 2);
        Assert.Equal(BankPlacement.Reorder(null, ordered), BankPlacement.Reorder(null, ordered, new HashSet<int>()));
    }

    [Fact]
    public void RemapCarriesLocksAndDropsADeletedBox()
    {
        var remap = BankBoxRemap.Swap(3, 0, 2);
        Assert.Equal(new HashSet<int> { 2, 1 }, BankBoxLocks.Remap(new HashSet<int> { 0, 1 }, remap));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
