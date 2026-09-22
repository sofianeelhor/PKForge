using System.Security.Cryptography;
using System.Text.Json;
using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class BankArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    private static BankEntryInfo Info(int species, string nickname = "Sparky", bool shiny = false, int generation = 7) =>
        new(species, 0, shiny, nickname, 50, generation, "Emerald");

    /// <summary>The archive's folder surface, in memory: same-name writes overwrite, like SAF.</summary>
    private sealed class MemoryFolder : IFolderFileAccess
    {
        private readonly List<(string Id, string Name, byte[] Bytes)> _files = [];

        public IReadOnlyList<string> Names => _files.Select(f => f.Name).ToArray();

        public void Add(string name, byte[] bytes) => _files.Add(($"mem://{_files.Count}", name, bytes));

        public byte[] ReadByName(string name) => _files.First(f => f.Name == name).Bytes;

        public ValueTask<IReadOnlyList<PickedDocument>> ListFilesAsync(string treeId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<PickedDocument> documents = _files.Select(f => new PickedDocument(f.Id, f.Name)).ToArray();
            return ValueTask.FromResult(documents);
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(string documentId, CancellationToken cancellationToken = default)
        {
            var file = _files.FirstOrDefault(f => f.Id == documentId);
            if (file.Id is null) throw new IOException("Unknown document.");
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(file.Bytes);
        }

        public ValueTask WriteFileAsync(string treeId, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            var existing = _files.FindIndex(f => f.Name == fileName);
            if (existing >= 0) _files[existing] = (_files[existing].Id, fileName, bytes.ToArray());
            else _files.Add(($"mem://{_files.Count}", fileName, bytes.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ExportWritesPkFilesAndManifestRoundTrips()
    {
        var bank = new FileBankService(_root);
        var sparky = bank.Add([1, 2, 3, 4], Info(25, "Sparky"));
        var eevee = bank.Add([5, 6, 7, 8], Info(133, "Eevee", shiny: true, generation: 4));

        var folder = new MemoryFolder();
        var exported = await BankArchive.ExportAsync(bank, folder, "tree");

        Assert.Equal(2, exported);
        Assert.Equal(
        [
            BankArchive.FileNameFor(sparky),
            BankArchive.FileNameFor(eevee),
            BankArchive.ManifestFileName,
        ], folder.Names.Order());
        Assert.Equal([1, 2, 3, 4], folder.ReadByName(BankArchive.FileNameFor(sparky)));
        Assert.Equal([5, 6, 7, 8], folder.ReadByName(BankArchive.FileNameFor(eevee)));

        // The manifest is an interchange format: parse it back exactly as any tool would.
        var manifest = JsonSerializer.Deserialize<BankArchiveManifest>(
            folder.ReadByName(BankArchive.ManifestFileName),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(manifest);
        Assert.Equal("PKForge", manifest!.App);
        Assert.Equal(BankArchive.CurrentSchemaVersion, manifest.SchemaVersion);
        var sparkyEntry = Assert.Single(manifest.Entries, e => e.Species == 25);
        Assert.Equal(BankArchive.FileNameFor(sparky), sparkyEntry.File);
        Assert.Equal("Sparky", sparkyEntry.Nickname);
        Assert.False(sparkyEntry.Shiny);
        Assert.Equal(7, sparkyEntry.Generation);
        Assert.Equal(0, sparkyEntry.Box);
        Assert.Equal(0, sparkyEntry.Slot);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])), sparkyEntry.Sha256);
        var eeveeEntry = Assert.Single(manifest.Entries, e => e.Species == 133);
        Assert.True(eeveeEntry.Shiny);
        Assert.Equal(4, eeveeEntry.Generation);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([5, 6, 7, 8])), eeveeEntry.Sha256);
    }

    [Fact]
    public async Task ExportNamesAreUniqueAndPathSafe()
    {
        var bank = new FileBankService(_root);
        bank.Add([1], Info(25, "Sparky"));
        bank.Add([2], Info(25, "Sparky")); // an exact sibling: the short id must split them
        bank.Add([3], Info(6, "Bad/File*Name?"));

        var folder = new MemoryFolder();
        await BankArchive.ExportAsync(bank, folder, "tree");

        var names = folder.Names.Where(n => n != BankArchive.ManifestFileName).ToList();
        Assert.Equal(3, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
        foreach (var name in names)
            Assert.All(name, c => Assert.DoesNotContain(c, Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public async Task ImportMergesSkippingExactDuplicates()
    {
        var bank = new FileBankService(_root);
        bank.Add([1, 1, 1], Info(25)); // this mon is already stored: bytes [1,1,1]

        var folder = new MemoryFolder();
        folder.Add("stored.pk7", [1, 1, 1]); // exact copy of a banked mon -> skipped
        folder.Add("fresh.pk7", [2, 2, 2]); // new -> imported
        folder.Add("fresh copy.pk7", [2, 2, 2]); // same bytes again inside the batch -> skipped once imported
        folder.Add("broken.pk7", [9, 9]); // not a recognizable entity -> rejected
        folder.Add("readme.txt", [0, 0, 0]); // not a .pk file -> never even read

        static BankEntryInfo? Describe(byte[] bytes, string name) => bytes.Length == 3 ? Info(25) : null;
        var reports = new List<(int Done, int Total)>();
        var summary = await BankArchive.ImportAsync(bank, Describe, folder, "tree", (done, total) => reports.Add((done, total)));

        Assert.Equal(1, summary.Imported);
        Assert.Equal(2, summary.SkippedDuplicates);
        Assert.Equal(1, summary.Rejected);
        Assert.Equal(2, bank.GetAll().Count);
        Assert.Equal((4, 4), reports[^1]); // the scan ends on a complete report
    }

    [Fact]
    public void PkFileNamePrefilterMatchesLooseEntityExtensions()
    {
        foreach (var name in new[] { "mon.pk", "mon.PK", "mon.pk1", "mon.Pk9", "025 - Sparky ab12cd34.pk7" })
            Assert.True(BankArchive.IsPkFileName(name), name);
        foreach (var name in new[] { "manifest.json", "mon.txt", "mon.pk10", "mon.ek7", "mon", "mon.pkx" })
            Assert.False(BankArchive.IsPkFileName(name), name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
