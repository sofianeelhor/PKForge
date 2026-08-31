using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class FileBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    private static SaveSnapshot Snapshot(byte[] bytes, string name) => new("PKM Test", 7, bytes, [], name);

    [Fact]
    public async Task CreateListReadRoundTrips()
    {
        var service = new FileBackupService(_root);
        var bytes = new byte[] { 1, 2, 3, 4 };

        var receipt = await service.CreateAsync(Snapshot(bytes, "save.srm"));
        var listed = await service.ListAsync();
        var read = await service.ReadAsync(receipt.BackupId);

        var info = Assert.Single(listed);
        Assert.Equal(receipt.BackupId, info.BackupId);
        Assert.Equal("save.srm", info.DisplayName);
        Assert.Equal(receipt.Sha256, info.Sha256);
        Assert.Equal(bytes, read.ToArray());
    }

    [Fact]
    public async Task ChangeDescriptionPersistsInTheSidecar()
    {
        var service = new FileBackupService(_root);

        var receipt = await service.CreateAsync(Snapshot([1, 2, 3], "save.sav"), "Deposited Pikachu in the Bank");
        var info = Assert.Single(await service.ListAsync());

        Assert.Equal(receipt.BackupId, info.BackupId);
        Assert.Equal("Deposited Pikachu in the Bank", info.ChangeDescription);
    }

    [Fact]
    public async Task ReadDetectsCorruptBytes()
    {
        var service = new FileBackupService(_root);
        var receipt = await service.CreateAsync(Snapshot([1, 2, 3], "save.sav"));

        await File.WriteAllBytesAsync(Path.Combine(_root, receipt.BackupId + ".bin"), [9, 9, 9]);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync(receipt.BackupId).AsTask());
    }

    [Fact]
    public async Task PrunesOldestBeyondMaxVersions()
    {
        var service = new FileBackupService(_root, maxVersions: 2);
        var first = await service.CreateAsync(Snapshot([1], "a"));
        await service.CreateAsync(Snapshot([2], "b"));
        await service.CreateAsync(Snapshot([3], "c"));

        var listed = await service.ListAsync();
        Assert.Equal(2, listed.Count);
        Assert.DoesNotContain(listed, x => x.BackupId == first.BackupId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
