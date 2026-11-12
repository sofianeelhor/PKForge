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
    public async Task APinnedPointOutlivesTheAllowance()
    {
        var service = new FileBackupService(_root, maxVersions: 3);
        var first = await service.CreateAsync(Snapshot([1], "save.sav"), "Before the League", "doc");
        await service.SetPinnedAsync(first.BackupId, pinned: true);

        for (byte i = 2; i <= 6; i++)
        {
            await Task.Delay(5); // ids are timestamps
            await service.CreateAsync(Snapshot([i], "save.sav"), $"Edit {i}", "doc");
        }

        var listed = await service.ListAsync();
        Assert.Equal(4, listed.Count); // the three newest and the pinned one
        var pinned = Assert.Single(listed, info => info.Pinned);
        Assert.Equal(first.BackupId, pinned.BackupId);
        Assert.Equal(new byte[] { 1 }, (await service.ReadAsync(first.BackupId)).ToArray());
    }

    [Fact]
    public async Task APointLetGoFallsBackUnderTheAllowance()
    {
        var service = new FileBackupService(_root, maxVersions: 2);
        var first = await service.CreateAsync(Snapshot([1], "save.sav"), "Before the League", "doc");
        await service.SetPinnedAsync(first.BackupId, pinned: true);
        for (byte i = 2; i <= 3; i++)
        {
            await Task.Delay(5);
            await service.CreateAsync(Snapshot([i], "save.sav"), $"Edit {i}", "doc");
        }

        await service.SetPinnedAsync(first.BackupId, pinned: false);

        var listed = await service.ListAsync();
        Assert.Equal(2, listed.Count);
        Assert.DoesNotContain(listed, info => info.BackupId == first.BackupId);
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

    [Fact]
    public async Task EachSaveKeepsItsOwnRestorePoints()
    {
        var service = new FileBackupService(_root, maxVersions: 2);
        var emerald = await service.CreateAsync(Snapshot([1], "emerald.sav"), documentId: "doc:emerald");
        for (var i = 0; i < 3; i++)
            await service.CreateAsync(Snapshot([(byte)(2 + i)], "platinum.sav"), documentId: "doc:platinum");

        var listed = await service.ListAsync();
        // Three writes to Platinum keep its newest two and never push out Emerald's only point.
        Assert.Contains(listed, x => x.BackupId == emerald.BackupId && x.DocumentId == "doc:emerald");
        Assert.Equal(2, listed.Count(x => x.DocumentId == "doc:platinum"));
    }

    [Fact]
    public void ARestorePointBelongsOnlyToItsOwnSave()
    {
        var open = Snapshot([1], "emerald.sav");
        var own = new BackupInfo("a", DateTimeOffset.UtcNow, "", "emerald.sav", "PKM Test", 7, 1, DocumentId: "doc:emerald");
        var other = own with { DocumentId = "doc:other" };
        Assert.True(own.BelongsTo("doc:emerald", open));
        Assert.False(other.BelongsTo("doc:emerald", open));

        // A point from before restore points recorded their file matches by format, generation and name.
        var legacy = own with { DocumentId = null };
        Assert.True(legacy.BelongsTo("doc:emerald", open));
        Assert.False((legacy with { Generation = 4 }).BelongsTo("doc:emerald", open));
        Assert.False((legacy with { DisplayName = "platinum.sav" }).BelongsTo("doc:emerald", open));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
