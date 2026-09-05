using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class SafeSaveWriterTests
{
    private static SaveSnapshot Snapshot(byte[] original) => new(
        "PKM Test",
        9,
        original,
        [],
        "test.sav");

    [Fact]
    public async Task InvalidCandidateDoesNotCreateBackupOrWrite()
    {
        var engine = new FakeSaveEngine(valid: false);
        var backup = new FakeBackupService();
        var access = new FakeFileAccess();
        var writer = new SafeSaveWriter(engine, backup, access);

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync("content://save", Snapshot(new byte[] { 1, 2, 3 }), new byte[] { 4, 5, 6 }).AsTask());

        Assert.Equal(0, backup.Calls);
        Assert.Equal(0, access.Writes);
    }

    [Fact]
    public async Task ValidCandidateBacksUpBeforeSingleWrite()
    {
        var engine = new FakeSaveEngine(valid: true);
        var backup = new FakeBackupService();
        var access = new FakeFileAccess();
        var writer = new SafeSaveWriter(engine, backup, access);

        var receipt = await writer.WriteAsync("content://save", Snapshot(new byte[] { 1, 2, 3 }), new byte[] { 4, 5, 6 });

        Assert.Equal(1, backup.Calls);
        Assert.Equal(1, access.Writes);
        Assert.Equal(backup.CreatedOrder, access.WriteOrder - 1);
        Assert.Equal("backup-1", receipt.BackupId);
    }

    [Fact]
    public async Task UnchangedCandidateCreatesNoBackupAndWritesNothing()
    {
        var engine = new FakeSaveEngine(valid: true);
        var backup = new FakeBackupService();
        var access = new FakeFileAccess();
        var writer = new SafeSaveWriter(engine, backup, access);

        var receipt = await writer.WriteAsync("content://save", Snapshot(new byte[] { 1, 2, 3 }), new byte[] { 1, 2, 3 });

        Assert.False(receipt.Changed);
        Assert.Equal(string.Empty, receipt.BackupId);
        Assert.Equal(0, backup.Calls);
        Assert.Equal(0, access.Writes);
    }

    [Fact]
    public async Task ChangeDescriptionFlowsIntoTheBackup()
    {
        var engine = new FakeSaveEngine(valid: true);
        var backup = new FakeBackupService();
        var writer = new SafeSaveWriter(engine, backup, new FakeFileAccess());

        await writer.WriteAsync("content://save", Snapshot(new byte[] { 1, 2, 3 }), new byte[] { 4, 5, 6 },
            "Edit Garchomp (Box 3, Slot 12)");

        Assert.Equal("Edit Garchomp (Box 3, Slot 12)", backup.LastDescription);
    }

    private sealed class FakeSaveEngine(bool valid) : ISaveEngine
    {
        public SaveSnapshot Open(ReadOnlyMemory<byte> bytes, string? displayName = null) => throw new NotImplementedException();
        public ISaveEngineSession OpenSession(ReadOnlyMemory<byte> bytes, string? displayName = null) => throw new NotImplementedException();
        public ReadOnlyMemory<byte> Serialize(SaveSnapshot snapshot) => throw new NotImplementedException();
        public bool Validate(ReadOnlyMemory<byte> bytes) => valid;
        public SaveDescription? TryDescribe(ReadOnlyMemory<byte> bytes, string? displayName = null) => throw new NotImplementedException();
        public BankEntryInfo? TryDescribeEntity(byte[] bytes, string sourceName) => throw new NotImplementedException();
        public ISaveEngineSession? OpenEntitySession(byte[] entityBytes, string? displayName = null) => throw new NotImplementedException();
        public ISaveEngineSession OpenBlankSession(int generation, string? displayName = null) => throw new NotImplementedException();
    }

    private sealed class FakeBackupService : IBackupService
    {
        public int Calls { get; private set; }
        public int CreatedOrder { get; private set; } = -1;
        public string? LastDescription { get; private set; }

        public ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, string? changeDescription = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastDescription = changeDescription;
            CreatedOrder = Order++;
            return ValueTask.FromResult(new BackupReceipt($"backup-{Calls}", DateTimeOffset.UtcNow, "sha"));
        }

        public ValueTask<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string backupId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class FakeFileAccess : ISaveFileAccess
    {
        public int Writes { get; private set; }
        public int WriteOrder { get; private set; } = -1;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Writes++;
            WriteOrder = Order++;
            return ValueTask.CompletedTask;
        }
    }

    private static int Order;
}
