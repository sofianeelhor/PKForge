using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>The held-item filter, finder and marks: "I have 100s of Pokémon and can't find my Exp. Share".</summary>
public sealed class HeldItemSearchTests
{
    private const int ExpShare = 216;
    private const int Leftovers = 234;

    /// <summary>Uses the interface's default <c>HeldItem</c> (the index value), like an index-only view.</summary>
    private sealed class IndexFacts : IBankFacts
    {
        public string SpeciesName(int species) => species == 25 ? "Pikachu" : "Eevee";
        public bool IsEgg(BankEntry entry) => false;
        public IReadOnlyList<int> Types(int species, int form) => [];
        public int Gender(BankEntry entry) => 2;
        public int Ball(BankEntry entry) => 4;
    }

    /// <summary>Stands in for the byte probe: old entries (null in the index) resolve from "bytes".</summary>
    private sealed class ProbingFacts(Dictionary<Guid, int> bytes) : IBankFacts
    {
        public int Probes { get; private set; }
        public string SpeciesName(int species) => "";
        public bool IsEgg(BankEntry entry) => false;
        public IReadOnlyList<int> Types(int species, int form) => [];
        public int Gender(BankEntry entry) => 2;
        public int Ball(BankEntry entry) => 4;
        public int HeldItem(BankEntry entry)
        {
            if (entry.Info.HeldItem is { } known) return known;
            Probes++;
            return bytes.GetValueOrDefault(entry.Id);
        }
    }

    private static BankEntry Entry(int? item, int species = 25, int slot = 0) =>
        new(Guid.NewGuid(), 0, slot, new BankEntryInfo(species, 0, false, "", 50, 8, "Sword", "PK8", item),
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void HoldsItemFilterKeepsOnlyHolders()
    {
        var facts = new IndexFacts();
        var filter = BankFilter.None with { HoldsItemOnly = true };
        Assert.True(filter.Matches(Entry(ExpShare), facts));
        Assert.False(filter.Matches(Entry(0), facts));
        Assert.False(filter.Matches(Entry(null), facts)); // unknown and unprobed reads as empty
        Assert.True(filter.IsActive);
        Assert.Equal(1, filter.ActiveCount);
    }

    [Fact]
    public void ExactItemFilterMatchesOnlyThatItem()
    {
        var facts = new IndexFacts();
        var filter = BankFilter.None with { HoldsItemOnly = true, HeldItemId = ExpShare };
        Assert.True(filter.Matches(Entry(ExpShare), facts));
        Assert.False(filter.Matches(Entry(Leftovers), facts));
        Assert.False(filter.Matches(Entry(0), facts));
        Assert.Equal(2, filter.ActiveCount);
        Assert.False(BankFilter.None.IsActive);
    }

    [Fact]
    public void ItemFilterComposesWithTheOthers()
    {
        var facts = new IndexFacts();
        var filter = BankFilter.None with { HeldItemId = ExpShare, Query = "eevee" };
        Assert.True(filter.Matches(Entry(ExpShare, species: 133), facts));
        Assert.False(filter.Matches(Entry(ExpShare, species: 25), facts));
    }

    [Fact]
    public void OldEntriesResolveFromTheBytesAndOnlyWhenAsked()
    {
        var old = Entry(null);
        var facts = new ProbingFacts(new() { [old.Id] = ExpShare });
        Assert.True(BankFilter.None.Matches(old, facts));
        Assert.Equal(0, facts.Probes); // no item filter, no probe
        Assert.True((BankFilter.None with { HeldItemId = ExpShare }).Matches(old, facts));
        Assert.Equal(1, facts.Probes);
        Assert.True((BankFilter.None with { HeldItemId = ExpShare }).Matches(Entry(ExpShare), facts));
        Assert.Equal(1, facts.Probes); // indexed entries never read bytes
    }

    [Fact]
    public void TallyCountsHoldersMostCommonFirst()
    {
        var tally = HeldItemSearch.Tally([0, Leftovers, ExpShare, Leftovers, 0, -1]);
        Assert.Equal([(Leftovers, 2), (-1, 1), (ExpShare, 1)], tally);
    }

    [Fact]
    public void FindListsBoxesInOrderThenParty()
    {
        SlotSummary S(int box, int slot, int? species, int item) => new(box, slot, species, null, false, true, HeldItem: item);
        var slots = new[]
        {
            S(-1, 0, 4, ExpShare), S(3, 2, 25, ExpShare), S(0, 5, 1, Leftovers),
            S(0, 1, 7, ExpShare), S(1, 0, null, ExpShare), S(2, 2, 133, -1), S(2, 3, 133, 0),
        };
        Assert.Equal([(0, 1), (3, 2), (-1, 0)], HeldItemSearch.Find(slots, ExpShare).Select(s => (s.Box, s.Slot)));
        Assert.Equal([(0, 1), (0, 5), (2, 2), (3, 2), (-1, 0)], HeldItemSearch.Find(slots).Select(s => (s.Box, s.Slot)));
        Assert.True(slots[5].HasItem); // a ROM-only item still "holds something"
    }

    [Fact]
    public void NamesFallBackForRomOnlyAndOutOfRangeIds()
    {
        string[] names = ["", "Master Ball", ""];
        Assert.Equal("Master Ball", HeldItemSearch.NameOf(names, 1));
        Assert.Equal("Unknown item", HeldItemSearch.NameOf(names, -1));
        Assert.Equal("Unknown item", HeldItemSearch.NameOf(names, 2));
        Assert.Equal("Unknown item", HeldItemSearch.NameOf(names, 999));
        Assert.Equal("(none)", HeldItemSearch.NameOf(names, 0));
    }

    [Fact]
    public void MarkSlotsMarksOnlyOccupiedTargets()
    {
        var slots = new[]
        {
            new SlotSummary(0, 0, 25, null, false, true, HeldItem: ExpShare),
            new SlotSummary(0, 1, null, null, false, true),
            new SlotSummary(4, 9, 133, null, false, true, HeldItem: ExpShare),
        };
        var marks = new StorageMarks();
        marks.MarkSlots(slots, [(0, 0), (0, 1), (4, 9), (7, 7)]);
        Assert.Equal(2, marks.Count);
        Assert.True(marks.Contains(4, 9));
        Assert.False(marks.Contains(0, 1));
    }
}
