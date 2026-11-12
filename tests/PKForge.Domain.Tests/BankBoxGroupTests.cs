using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class BankBoxGroupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static BankEntryInfo Info(int species) => new(species, 0, false, $"M{species}", 5, 8, "t", "PK8");

    private static IReadOnlyList<BankBoxGroup> None => [];

    [Fact]
    public void CreateRenameDeleteAndAssign()
    {
        var groups = BankBoxGroups.Create(None, "  Shiny   hunt ", 3, 1);
        Assert.Equal("Shiny hunt", Assert.Single(groups).Name);
        Assert.Equal([1, 3], groups[0].Boxes);

        groups = BankBoxGroups.Create(groups, "Living dex", 3); // a box sits in one group only
        Assert.Equal([1], BankBoxGroups.Members(groups, "shiny HUNT"));
        Assert.Equal("Living dex", BankBoxGroups.GroupOf(groups, 3));

        Assert.Throws<InvalidOperationException>(() => BankBoxGroups.Create(groups, "LIVING DEX"));
        Assert.Throws<ArgumentException>(() => BankBoxGroups.Create(groups, "   "));

        groups = BankBoxGroups.Assign(groups, 1, "Living dex");
        Assert.Equal([1, 3], BankBoxGroups.Members(groups, "Living dex"));
        Assert.Empty(BankBoxGroups.Members(groups, "Shiny hunt")); // an empty group stays
        groups = BankBoxGroups.Assign(groups, 3, null);
        Assert.Null(BankBoxGroups.GroupOf(groups, 3));

        groups = BankBoxGroups.Rename(groups, "Shiny hunt", "Shinies");
        Assert.Equal(["Shinies", "Living dex"], groups.Select(g => g.Name));
        Assert.Throws<InvalidOperationException>(() => BankBoxGroups.Rename(groups, "Shinies", "living dex"));
        Assert.Equal("SHINIES", BankBoxGroups.Rename(groups, "Shinies", "SHINIES")[0].Name);

        groups = BankBoxGroups.Delete(groups, "living dex");
        Assert.Equal("Shinies", Assert.Single(groups).Name);
        Assert.Equal(new string('x', BankBoxGroups.MaxNameLength), BankBoxGroups.Clean(new string('x', 40)));
    }

    [Fact]
    public void NormalizeTidiesAnythingReadFromDisk()
    {
        var groups = BankBoxGroups.Normalize([
            new BankBoxGroup("A", [5, 2, 2, -1, 40]),
            new BankBoxGroup(" ", [7]),
            new BankBoxGroup("a", [9]),
            new BankBoxGroup("B", [2, 8]),
        ], boxCount: 10);
        Assert.Equal(["A", "B"], groups.Select(g => g.Name));
        Assert.Equal([2, 5, 9], groups[0].Boxes);
        Assert.Equal([8], groups[1].Boxes);
    }

    [Fact]
    public void GroupsFollowTheirBoxesThroughRemaps()
    {
        var groups = BankBoxGroups.Create(None, "G", 0, 2, 4);
        Assert.Equal([1, 3, 5], BankBoxGroups.Remap(groups, BankBoxRemap.Insert(5, 0))[0].Boxes);
        Assert.Equal([0, 3], BankBoxGroups.Remap(groups, BankBoxRemap.Remove(5, 2))[0].Boxes);
        Assert.Equal([1, 2, 4], BankBoxGroups.Remap(groups, BankBoxRemap.Swap(5, 0, 1))[0].Boxes);
        var empty = BankBoxGroups.Remap(BankBoxGroups.Create(None, "Solo", 1), BankBoxRemap.Remove(3, 1));
        Assert.Equal("Solo", Assert.Single(empty).Name); // the group outlives its last box
        Assert.Empty(empty[0].Boxes);
    }

    [Fact]
    public void StepWrapsWithinTheGroupAndFindsTheNearestFromOutside()
    {
        int[] members = [2, 5, 9];
        Assert.Equal(9, BankBoxGroups.Step(members, 5, 1));
        Assert.Equal(2, BankBoxGroups.Step(members, 9, 1));
        Assert.Equal(9, BankBoxGroups.Step(members, 2, -1));
        Assert.Equal(5, BankBoxGroups.Step(members, 3, 1));
        Assert.Equal(2, BankBoxGroups.Step(members, 3, -1));
        Assert.Equal(2, BankBoxGroups.Step(members, 10, 1));
        Assert.Null(BankBoxGroups.Step([], 0, 1));
    }

    [Fact]
    public void SectionsListGroupsThenTheRest()
    {
        Assert.Equal([0, 1, 2], Assert.Single(BankBoxGroups.Sections(None, 3)).Boxes);
        var groups = BankBoxGroups.Create(BankBoxGroups.Create(None, "A", 2), "Empty");
        var sections = BankBoxGroups.Sections(groups, 4);
        Assert.Equal(["A", "Empty", null], sections.Select(s => s.Name));
        Assert.Equal([0, 1, 3], sections[2].Boxes);
        var all = BankBoxGroups.Create(None, "All", 0, 1);
        Assert.Single(BankBoxGroups.Sections(all, 2)); // no empty "no group" run
    }

    [Fact]
    public void GroupsPersistInTheIndexAndFollowBoxRemaps()
    {
        var bank = new FileBankService(_root);
        bank.Add([1], Info(25));
        Assert.DoesNotContain("BoxGroups", File.ReadAllText(Path.Combine(_root, "index.json")));

        bank.SetBoxGroups(BankBoxGroups.Create(bank.BoxGroups, "Shiny", 1, 2));
        bank.RemapBoxes(BankBoxRemap.Insert(bank.BoxCount, 0));
        var reopened = new FileBankService(_root);
        Assert.Equal([2, 3], BankBoxGroups.Members(reopened.BoxGroups, "Shiny"));

        reopened.SetBoxGroups([]);
        Assert.DoesNotContain("BoxGroups", File.ReadAllText(Path.Combine(_root, "index.json")));
        Assert.Empty(new FileBankService(_root).BoxGroups);
    }

    [Fact]
    public void AnOldIndexWithoutGroupsLoadsAndKeepsEverything()
    {
        // The 3.1 index shape, written by the old service: no groups field at all.
        var old = new FileBankService(_root);
        var entry = old.Add([7], Info(25));
        old.Move(entry.Id, 2, 5);
        old.SetBoxLocked(1, true);
        var index = Path.Combine(_root, "index.json");
        Assert.DoesNotContain("BoxGroups", File.ReadAllText(index));

        var bank = new FileBankService(_root);
        Assert.Empty(bank.BoxGroups);
        Assert.Equal([1], bank.LockedBoxes);
        Assert.Equal((2, 5), (bank.GetAll().Single().Box, bank.GetAll().Single().Slot));

        bank.SetBoxGroups(BankBoxGroups.Create([], "Kanto", 2));
        Assert.Contains("\"BoxGroups\"", File.ReadAllText(index));
        var again = new FileBankService(_root);
        Assert.Equal([2], BankBoxGroups.Members(again.BoxGroups, "Kanto"));
        Assert.Equal([1], again.LockedBoxes);
        Assert.Equal([7], again.GetData(entry.Id));
    }

    [Fact]
    public void AnIndexWithMessyGroupsIsTidiedOnLoad()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "index.json"),
            """{"BoxCount":3,"Entries":[],"BoxGroups":[{"Name":"A","Boxes":[1,1,-2]},{"Name":"a","Boxes":[0]},{"Name":null,"Boxes":[2]},{"Name":"B","Boxes":null}]}""");
        var groups = new FileBankService(_root).BoxGroups;
        Assert.Equal(["A", "B"], groups.Select(g => g.Name));
        Assert.Equal([0, 1], groups[0].Boxes);
        Assert.Empty(groups[1].Boxes);
    }
}
