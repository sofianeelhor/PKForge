using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class FileBankServiceRemoteTests : IDisposable
{
    private static readonly DateTimeOffset Added = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static BankEntryInfo Info(int species) => new(species, 0, false, $"#{species}", 5, 8, "t", "PK8");
    private static BankEntry Entry(Guid id, int box, int slot) => new(id, box, slot, Info(25), Added);
    private string BinPath(Guid id) => Path.Combine(_root, id.ToString("N") + ".bin");

    [Fact]
    public void ARemoteEntryKeepsItsIdPositionAndBytes()
    {
        var id = Guid.NewGuid();

        new FileBankService(_root).ApplyRemote([new RemoteEntry(Entry(id, 4, 7), [1, 2, 3])], []);

        var reopened = new FileBankService(_root);
        Assert.Equal(Entry(id, 4, 7), Assert.Single(reopened.GetAll()));
        Assert.Equal([1, 2, 3], reopened.GetData(id));
        Assert.Equal(5, reopened.BoxCount);
    }

    [Fact]
    public void MovesAndRemovalsLandInOneWrite()
    {
        var bank = new FileBankService(_root);
        var a = bank.Add([1], Info(1));
        var b = bank.Add([2], Info(2));
        var c = bank.Add([3], Info(3));

        bank.ApplyRemote([new RemoteEntry(a with { Slot = b.Slot }, null), new RemoteEntry(b with { Slot = a.Slot }, null)], [c.Id]);

        var after = new FileBankService(_root).GetAll().ToDictionary(e => e.Id);
        Assert.Equal(b.Slot, after[a.Id].Slot);
        Assert.Equal(a.Slot, after[b.Id].Slot);
        Assert.False(after.ContainsKey(c.Id));
        Assert.False(File.Exists(BinPath(c.Id)));
        Assert.Equal([1], bank.GetData(a.Id));
    }

    [Fact]
    public void ASlotClashIsRefusedAndChangesNothing()
    {
        var bank = new FileBankService(_root);
        var resident = bank.Add([1], Info(1));
        var newcomer = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() =>
            bank.ApplyRemote([new RemoteEntry(Entry(newcomer, resident.Box, resident.Slot), [9])], []));

        Assert.Equal(resident.Id, Assert.Single(new FileBankService(_root).GetAll()).Id);
        Assert.False(File.Exists(BinPath(newcomer)));
    }

    [Fact]
    public void NewEntriesNeedTheirBytes()
    {
        var bank = new FileBankService(_root);
        Assert.Throws<InvalidOperationException>(() => bank.ApplyRemote([new RemoteEntry(Entry(Guid.NewGuid(), 0, 0), null)], []));
        Assert.Empty(bank.GetAll());
    }

    [Fact]
    public void TheBoxCountFollowsTheServerButNeverStrandsAnEntry()
    {
        var bank = new FileBankService(_root);
        var id = Guid.NewGuid();

        bank.ApplyRemote([new RemoteEntry(Entry(id, 3, 0), [1])], [], boxCount: 2);
        Assert.Equal(4, bank.BoxCount);
        bank.ApplyRemote([], [], boxCount: 6);
        Assert.Equal(6, bank.BoxCount);
        bank.ApplyRemote([], [id], boxCount: 1);
        Assert.Equal(1, new FileBankService(_root).BoxCount);
    }

    [Fact]
    public void AFailedIndexWriteDeletesTheNewBytes()
    {
        var bank = new FileBankService(_root);
        Directory.CreateDirectory(Path.Combine(_root, "index.json.tmp"));
        var id = Guid.NewGuid();

        Assert.ThrowsAny<Exception>(() => bank.ApplyRemote([new RemoteEntry(Entry(id, 0, 0), [9])], []));

        Assert.Empty(bank.GetAll());
        Assert.False(File.Exists(BinPath(id)));
    }
}
