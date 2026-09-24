using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class FileBankServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FormatPersistsAndUpdateInfoRewritesFactsInOneIndexWrite()
    {
        var bank = new FileBankService(_root);
        var mesprit = bank.Add([1, 2, 3], new BankEntryInfo(481, 0, false, "Mesprit", 50, 8, "BD", "PB8"));
        var legacy = bank.Add([4, 5, 6], new BankEntryInfo(25, 0, false, "Pika", 30, 7, "Y"));
        Assert.Equal("PB8", new FileBankService(_root).GetAll().Single(e => e.Id == mesprit.Id).Info.Format);

        var changed = bank.UpdateInfo([
            (legacy.Id, legacy.Info with { Format = "PK6", Generation = 6 }),
            (mesprit.Id, mesprit.Info), // unchanged: not counted
            (Guid.NewGuid(), legacy.Info), // unknown: ignored
        ]);

        Assert.Equal(1, changed);
        var reloaded = new FileBankService(_root).GetAll().Single(e => e.Id == legacy.Id);
        Assert.Equal(("PK6", 6), (reloaded.Info.Format, reloaded.Info.Generation));
        Assert.Equal([4, 5, 6], bank.GetData(legacy.Id)); // bytes untouched
        Assert.Equal((legacy.Box, legacy.Slot), (reloaded.Box, reloaded.Slot));
    }

    [Fact]
    public void AFailedIndexWriteLeavesEveryMutationUndone()
    {
        var bank = new FileBankService(_root);
        var a = bank.Add([1], new BankEntryInfo(25, 0, false, "A", 5, 8, "t", "PK8"));
        var b = bank.Add([2], new BankEntryInfo(26, 0, false, "B", 5, 8, "t", "PK8"));
        var blocker = Path.Combine(_root, "index.json.tmp");
        Directory.CreateDirectory(blocker); // the index can no longer be written

        Assert.ThrowsAny<Exception>(() => bank.RemoveMany([a.Id, b.Id]));
        Assert.ThrowsAny<Exception>(() => bank.Add([3], new BankEntryInfo(27, 0, false, "C", 5, 8, "t", "PK8")));
        Assert.ThrowsAny<Exception>(() => bank.Move(a.Id, 2, 5));

        Assert.Equal([a, b], bank.GetAll());
        Assert.Equal([1], bank.GetData(a.Id));
        Assert.Equal([2], bank.GetData(b.Id));
        Assert.Equal(2, Directory.GetFiles(_root, "*.bin").Length); // no orphan from the failed Add
        Directory.Delete(blocker);
        Assert.Equal([a, b], new FileBankService(_root).GetAll());
    }

    [Fact]
    public void AnUnreadableIndexIsNeverOverwrittenByTheEmptyFallback()
    {
        Directory.CreateDirectory(_root);
        var index = Path.Combine(_root, "index.json");
        File.WriteAllText(index, "{ not json");

        var bank = new FileBankService(_root);
        Assert.Empty(bank.GetAll());
        Assert.Throws<InvalidOperationException>(() => bank.Add([1], new BankEntryInfo(25, 0, false, "A", 5, 8, "t")));
        Assert.Throws<InvalidOperationException>(bank.AddBox);
        Assert.Equal("{ not json", File.ReadAllText(index));
        Assert.False(File.Exists(index + ".bak"));
    }

    [Fact]
    public void TheMigrationMarkerPersistsWithTheFixesAndOlderIndexesReadAsZero()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "index.json"), """{"BoxCount":3,"Entries":[]}""");
        var bank = new FileBankService(_root);
        Assert.Equal(0, bank.MigrationVersion);
        var entry = bank.Add([1], new BankEntryInfo(25, 0, false, "A", 5, 8, "t"));
        var edited = entry.Info with { Nickname = "edited meanwhile" };
        var other = bank.Add([2], new BankEntryInfo(26, 0, false, "B", 5, 8, "t"));
        bank.Replace(entry.Id, [1], edited);

        var changed = bank.CompleteMigration(1, [
            (entry.Id, entry.Info, entry.Info with { Format = "PK8" }), // stale: the edit wins
            (other.Id, other.Info, other.Info with { Format = "PK8" }),
        ]);

        Assert.Equal(1, changed);
        var reloaded = new FileBankService(_root);
        Assert.Equal(1, reloaded.MigrationVersion);
        Assert.Equal(edited, reloaded.GetAll().Single(e => e.Id == entry.Id).Info);
        Assert.Equal("PK8", reloaded.GetAll().Single(e => e.Id == other.Id).Info.Format);
        Assert.Equal(0, reloaded.CompleteMigration(1, [(other.Id, other.Info, other.Info)])); // done once
    }

    [Fact]
    public void ExportFileNamesCarryTheRecordedFormat()
    {
        Assert.Equal(".pb8", BankEntryFiles.ExtensionFor(new BankEntryInfo(481, 0, false, "M", 50, 8, "BD", "PB8")));
        Assert.Equal(".pk6", BankEntryFiles.ExtensionFor(new BankEntryInfo(25, 0, false, "P", 5, 6, "Y", "PK6")));
        Assert.Equal(".pk7", BankEntryFiles.ExtensionFor(new BankEntryInfo(25, 0, false, "P", 5, 7, "old")));
        var bank = new FileBankService(_root);
        var entry = bank.Add([1], new BankEntryInfo(481, 0, false, "Mesprit", 50, 8, "BD", "PB8"));
        Assert.EndsWith(".pb8", BankArchive.FileNameFor(entry));
        Assert.True(BankArchive.IsPkFileName(BankArchive.FileNameFor(entry)));
    }

    [Fact]
    public void OldIndexStillLoads()
    {
        // The exact on-disk shape written before the search/archive features shipped:
        // raw JSON, PascalCase, one entry per stored mon. A user's bank must never
        // need re-creating because a feature touched the index schema.
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "index.json"), $$"""
            {
              "BoxCount": 5,
              "Entries": [
                {
                  "Id": "{{id}}",
                  "Box": 2,
                  "Slot": 7,
                  "Info": {
                    "Species": 25,
                    "Form": 1,
                    "Shiny": true,
                    "Nickname": "Sparky",
                    "Level": 50,
                    "Generation": 7,
                    "SourceName": "Emerald"
                  },
                  "AddedUtc": "2026-01-02T03:04:05+00:00"
                }
              ]
            }
            """);

        var bank = new FileBankService(_root);

        Assert.Equal(5, bank.BoxCount);
        var entry = Assert.Single(bank.GetAll());
        Assert.Equal(id, entry.Id);
        Assert.Equal(2, entry.Box);
        Assert.Equal(7, entry.Slot);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), entry.AddedUtc);
        Assert.Equal(25, entry.Info.Species);
        Assert.Equal(1, entry.Info.Form);
        Assert.True(entry.Info.Shiny);
        Assert.Equal("Sparky", entry.Info.Nickname);
        Assert.Equal("Emerald", entry.Info.SourceName);
        Assert.Null(entry.Info.Format); // written before the field existed: optional, awaits migration

        // And the loaded bank keeps working: mutations rewrite the index in today's shape.
        bank.Add([9, 9, 9], new BankEntryInfo(133, 0, false, "Eevee", 30, 4, "HeartGold"));
        Assert.Equal(2, new FileBankService(_root).GetAll().Count);
    }

    [Fact]
    public void IndexMissingOptionalFieldsStillLoads()
    {
        // Pins additive tolerance: a future field absent from an older index file must
        // not refuse the whole bank. AddedUtc omitted here deserializes to its default.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "index.json"), """
            {
              "BoxCount": 2,
              "Entries": [
                {
                  "Id": "01234567-89ab-cdef-0123-456789abcdef",
                  "Box": 0,
                  "Slot": 4,
                  "Info": {
                    "Species": 1,
                    "Form": 0,
                    "Shiny": false,
                    "Nickname": "Bulbasaur",
                    "Level": 5,
                    "Generation": 1,
                    "SourceName": "Red"
                  }
                }
              ]
            }
            """);

        var bank = new FileBankService(_root);

        Assert.Equal(2, bank.BoxCount);
        var entry = Assert.Single(bank.GetAll());
        Assert.Equal(1, entry.Info.Species);
        Assert.Equal(default, entry.AddedUtc);
    }

    [Fact]
    public void PlacePersistsASortThroughTheIndexAndReloadsIt()
    {
        // The organizer's write path end to end: an old-shape index (no fields the
        // search era added) loads, a sort's placements land through Place in one write,
        // and a fresh service reads the new layout back off disk.
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "index.json"), $$"""
            {
              "BoxCount": 1,
              "Entries": [
                {
                  "Id": "{{ids[2]}}", "Box": 0, "Slot": 0,
                  "Info": { "Species": 7, "Form": 0, "Shiny": false, "Nickname": "Squirtle", "Level": 9, "Generation": 1, "SourceName": "Blue" },
                  "AddedUtc": "2026-01-03T03:04:05+00:00"
                },
                {
                  "Id": "{{ids[0]}}", "Box": 0, "Slot": 1,
                  "Info": { "Species": 1, "Form": 0, "Shiny": false, "Nickname": "Bulbasaur", "Level": 5, "Generation": 1, "SourceName": "Red" },
                  "AddedUtc": "2026-01-01T03:04:05+00:00"
                },
                {
                  "Id": "{{ids[1]}}", "Box": 0, "Slot": 2,
                  "Info": { "Species": 4, "Form": 0, "Shiny": false, "Nickname": "Charmander", "Level": 7, "Generation": 1, "SourceName": "Red" },
                  "AddedUtc": "2026-01-02T03:04:05+00:00"
                }
              ]
            }
            """);

        var bank = new FileBankService(_root);
        var placements = BankPlacement.Reorder(null,
            BankSorting.Order(bank.GetAll(), BankSortOrder.DexNumber, new SearchFacts()));
        Assert.Equal(3, bank.Place(placements));

        var reloaded = new FileBankService(_root).GetAll().OrderBy(e => e.Info.Species).ToList();
        Assert.Equal(0, reloaded[0].Slot);
        Assert.Equal(1, reloaded[1].Slot);
        Assert.Equal(2, reloaded[2].Slot);
    }

    [Fact]
    public void PlaceRefusesOverlapsAndUnknownIdsWithoutTouchingTheBank()
    {
        var bank = new FileBankService(_root);
        var first = bank.Add([1], new BankEntryInfo(1, 0, false, "Bulbasaur", 5, 1, "Red"));
        var second = bank.Add([2], new BankEntryInfo(4, 0, false, "Charmander", 5, 1, "Red"));

        // Two entries may not share one slot.
        Assert.Throws<InvalidOperationException>(() =>
            bank.Place([(first.Id, 0, 0), (second.Id, 0, 0)]));
        // Unknown ids are a caller bug, not a silent skip.
        Assert.Throws<InvalidOperationException>(() =>
            bank.Place([(Guid.NewGuid(), 0, 0)]));
        // A target held by an entry that is not itself moving is refused: first may not take
        // second's slot while second stays put.
        Assert.Throws<InvalidOperationException>(() =>
            bank.Place([(first.Id, 0, 1)]));
        // Out-of-range slots are refused too.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            bank.Place([(first.Id, 0, FileBankService.SlotsPerBox)]));

        // The rejected batches left the vault exactly as it was.
        // (Looked up by id: ordering by Guid would make the expectation depend on random ids.)
        var intact = new FileBankService(_root).GetAll().ToDictionary(e => e.Id);
        Assert.Equal((first.Box, first.Slot), (intact[first.Id].Box, intact[first.Id].Slot));
        Assert.Equal((second.Box, second.Slot), (intact[second.Id].Box, intact[second.Id].Slot));
    }

    [Fact]
    public void PlaceAcceptsASwapBecauseBothOccupantsAreMoving()
    {
        var bank = new FileBankService(_root);
        var first = bank.Add([1], new BankEntryInfo(1, 0, false, "Bulbasaur", 5, 1, "Red"));
        var second = bank.Add([2], new BankEntryInfo(4, 0, false, "Charmander", 5, 1, "Red"));

        Assert.Equal(2, bank.Place([(first.Id, second.Box, second.Slot), (second.Id, 1, 0)]));
        var moved = new FileBankService(_root).GetAll().ToDictionary(e => e.Id);
        Assert.Equal((second.Box, second.Slot), (moved[first.Id].Box, moved[first.Id].Slot));
        Assert.Equal((1, 0), (moved[second.Id].Box, moved[second.Id].Slot));
    }

    [Fact]
    public void RemoveManyReleasesInOneWriteAndIgnoresUnknownIds()
    {
        var bank = new FileBankService(_root);
        var first = bank.Add([1], new BankEntryInfo(1, 0, false, "Bulbasaur", 5, 1, "Red"));
        var second = bank.Add([2], new BankEntryInfo(4, 0, false, "Charmander", 5, 1, "Red"));
        var keeper = bank.Add([3], new BankEntryInfo(7, 0, false, "Squirtle", 5, 1, "Blue"));

        Assert.Equal(2, bank.RemoveMany([first.Id, second.Id, Guid.NewGuid()]));
        var rest = new FileBankService(_root).GetAll();
        Assert.Equal(keeper.Id, Assert.Single(rest).Id);
        Assert.False(File.Exists(Path.Combine(_root, first.Id.ToString("N") + ".bin")));
    }

    /// <summary>The facts a sort needs over a loaded bank: names only, nothing byte-level.</summary>
    private sealed class SearchFacts : IBankFacts
    {
        public string SpeciesName(int species) => species switch { 1 => "Bulbasaur", 4 => "Charmander", _ => "Squirtle" };
        public bool IsEgg(BankEntry entry) => false;
        public IReadOnlyList<int> Types(int species, int form) => [];
        public int Gender(BankEntry entry) => 2;
        public int Ball(BankEntry entry) => 0;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
