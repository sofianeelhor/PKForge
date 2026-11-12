using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>Pokémon History records: kept on disk one file per Pokémon, found by any identity they
/// carry, carried by the data export and merged on import without losing anything either side knew.</summary>
public sealed class PokemonHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Monday = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static PokemonHistoryRecord Trip(PokemonHistoryRecord? record, string[] souls, string from, string fromFormat, string to, string toFormat, DateTimeOffset at, byte seed) =>
        (record ?? PokemonHistoryRecord.Start(souls, 286)).WithTrip(souls, 286,
            HistoryCopy.Of(fromFormat, from, at, [seed, 1, 2]), HistoryCopy.Of(toFormat, to, at, [seed, 3, 4]), restored: false);

    [Fact]
    public void ARecordIsFoundByEveryIdentityItHasCarried()
    {
        var store = new FilePokemonHistoryStore(Path.Combine(_root, FilePokemonHistoryStore.Folder));
        var record = Trip(null, ["soul:a", "soul:b"], "White", "PK5", "Emerald", "PK3", Monday, 1);
        store.Save(record);

        Assert.Equal(record.Id, store.Find(["soul:b"])?.Id);
        Assert.Null(store.Find(["soul:z"]));
        var read = Assert.Single(store.All());
        Assert.Equal(new byte[] { 1, 1, 2 }, read.DepartureFor("PK5")!.Bytes());
        Assert.Equal("Emerald", read.ArrivalFor("PK3")!.Game);
    }

    [Fact]
    public void ADamagedFileIsSkippedNotFatal()
    {
        var directory = Path.Combine(_root, FilePokemonHistoryStore.Folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{ not json");
        Assert.Empty(new FilePokemonHistoryStore(directory).All());
    }

    [Fact]
    public void MergeKeepsEveryIdentityTheNewestCopyOfEachFormatAndEveryTrip()
    {
        var mine = Trip(null, ["soul:a"], "White", "PK5", "Emerald", "PK3", Monday, 1);
        var theirs = Trip(null, ["soul:a", "soul:c"], "White", "PK5", "Ruby", "PK3", Monday.AddDays(1), 2);
        var merged = PokemonHistoryRecord.Merge(mine, theirs);

        Assert.Equal(["soul:a", "soul:c"], merged.Souls);
        Assert.Equal(2, merged.DepartureFor("PK5")!.Bytes()[0]);
        Assert.Equal("Ruby", merged.ArrivalFor("PK3")!.Game);
        Assert.Equal(2, merged.Trips.Count);
        Assert.Equal(mine.Id, merged.Id);
        // Merging what is already known changes nothing.
        Assert.Equal(FilePokemonHistoryStore.Serialize(merged), FilePokemonHistoryStore.Serialize(PokemonHistoryRecord.Merge(merged, mine)));
    }

    [Fact]
    public void TheDataArchiveCarriesHistoryAndMergesItOnImport()
    {
        var old = Path.Combine(_root, "old");
        var here = Path.Combine(_root, "new");
        new FilePokemonHistoryStore(Path.Combine(old, FilePokemonHistoryStore.Folder))
            .Save(Trip(null, ["soul:a"], "White", "PK5", "Emerald", "PK3", Monday.AddDays(1), 2));
        var local = new FilePokemonHistoryStore(Path.Combine(here, FilePokemonHistoryStore.Folder));
        local.Save(Trip(null, ["soul:a"], "Black", "PK5", "Sapphire", "PK3", Monday, 1));

        using var output = new MemoryStream();
        var manifest = DataArchive.Export(output, old, [], "3.2.0", Monday);
        Assert.Equal(1, manifest.ItemsIn(DataTransfer.HistorySection));

        using var reader = DataArchive.Open(new MemoryStream(output.ToArray()));
        var bank = new FileBankService(Path.Combine(here, "bank"));
        Assert.Equal(1, DataArchive.Preview(reader, here, bank, []).HistoryNew);
        var result = DataArchive.Restore(reader, here, bank, new JsonSaveIdentityStore(Path.Combine(here, "save-identities.json")),
            new InjectedGiftHistory(Path.Combine(here, "injected-gifts.json")),
            new BankBoxNames(Path.Combine(here, "bank")), new BankBoxWallpapers(Path.Combine(here, "bank")));

        Assert.Equal(1, result.History);
        var merged = Assert.Single(local.All());
        Assert.Equal(2, merged.Trips.Count);
        Assert.Equal("Emerald", merged.ArrivalFor("PK3")!.Game);
        Assert.Equal(0, DataArchive.Preview(reader, here, bank, []).HistoryNew);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
