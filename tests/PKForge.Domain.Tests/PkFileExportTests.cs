using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class PkFileExportTests
{
    /// <summary>A picked folder in memory; a same-name write would overwrite, like SAF.</summary>
    private sealed class MemoryFolder(params string[] existing) : IFolderFileAccess
    {
        public Dictionary<string, byte[]> Files { get; } = existing.ToDictionary(n => n, _ => new byte[] { 0 });

        public ValueTask<IReadOnlyList<PickedDocument>> ListFilesAsync(string treeId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<PickedDocument>>([.. Files.Keys.Select(n => new PickedDocument("mem://" + n, n))]);

        public ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(string documentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask WriteFileAsync(string treeId, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Files[fileName] = bytes.ToArray();
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("PK3", 3, ".pk3")]
    [InlineData("PK4", 4, ".pk4")]
    [InlineData("PK9", 9, ".pk9")]
    [InlineData("PB8", 8, ".pb8")]
    [InlineData("PA9", 9, ".pa9")]
    [InlineData("PB7", 7, ".pb7")]
    [InlineData(null, 6, ".pk6")]
    [InlineData("", 2, ".pk2")]
    public void TheExtensionIsTheFormatPkhexNamesIt(string? format, int generation, string expected)
    {
        Assert.Equal(expected, PkFileExport.ExtensionFor(format, generation));
        Assert.Equal(expected, BankEntryFiles.ExtensionFor(new BankEntryInfo(1, 0, false, "x", 5, generation, "t", format)));
    }

    [Fact]
    public void FileNamesAreSpeciesThenNickname()
    {
        Assert.Equal("025 - Sparky.pk7", PkFileExport.FileNameFor(25, "Sparky", ".pk7"));
        Assert.Equal("025.pk7", PkFileExport.FileNameFor(25, " ", ".pk7"));
        Assert.Equal("006 - MrFire.pk3", PkFileExport.FileNameFor(6, "Mr/Fire", ".pk3"));
    }

    [Theory]
    [InlineData("025 - Pika:chu?.pk8", "025 - Pikachu.pk8")]
    [InlineData("a/b\\c*\"<>|.pk4", "abc.pk4")]
    [InlineData("..hidden.pk9", "hidden.pk9")]
    [InlineData("???.pk3", "pokemon.pk3")]
    [InlineData("tab\there.pa8", "tabhere.pa8")]
    public void SafeFileNamesDropHostileCharactersAndKeepTheExtension(string raw, string expected) =>
        Assert.Equal(expected, PkFileExport.SafeFileName(raw));

    [Fact]
    public void ALongNameIsCappedButKeepsItsExtension()
    {
        var name = PkFileExport.SafeFileName(new string('x', 200) + ".pk5");
        Assert.EndsWith(".pk5", name);
        Assert.True(name.Length <= 64);
    }

    [Fact]
    public void TakenNamesGetANumberedSuffixCaseInsensitively()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "025 - Pikachu.pk8", "025 - PIKACHU (2).pk8" };
        Assert.Equal("025 - Pikachu (3).pk8", PkFileExport.Unique("025 - Pikachu.pk8", taken));
        Assert.Equal("025 - Pikachu (4).pk8", PkFileExport.Unique("025 - Pikachu.pk8", taken));
        Assert.Equal("001.pk1", PkFileExport.Unique("001.pk1", taken));
        Assert.Contains("001.pk1", taken);
    }

    [Fact]
    public async Task WritingNeverOverwritesAndCountsEveryFile()
    {
        var folder = new MemoryFolder("025 - Pikachu.pk8");
        var written = await PkFileExport.WriteAsync(folder, "tree", [
            new PkFile("025 - Pikachu.pk8", new byte[] { 1 }),
            new PkFile("025 - pikachu.pk8", new byte[] { 2 }),
            new PkFile("150 - Mew:two.pk9", new byte[] { 3 }),
        ]);

        Assert.Equal(3, written);
        Assert.Equal([0], folder.Files["025 - Pikachu.pk8"]); // the existing file is untouched
        Assert.Equal([1], folder.Files["025 - Pikachu (2).pk8"]);
        Assert.Equal([2], folder.Files["025 - pikachu (3).pk8"]);
        Assert.Equal([3], folder.Files["150 - Mewtwo.pk9"]);
        Assert.Equal(0, await PkFileExport.WriteAsync(folder, "tree", []));
    }
}
