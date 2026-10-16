using Microsoft.Extensions.Time.Testing;
using PKForge.Domain;
using PKForge.Infrastructure;
using PKForge.Sync.Tests.Infrastructure;
using Xunit;

namespace PKForge.Sync.Tests;

public sealed class BankSnapshotsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(TestData.T0);
    private string BankRoot => Path.Combine(_root, "bank");
    private string SnapshotRoot => Path.Combine(_root, "snapshots");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private FileBankSnapshots Snapshots(int keep = 5) => new(BankRoot, SnapshotRoot, _time, keep);

    [Fact]
    public void CopiesAreListedNewestFirstWithTheirReasonAndPokemonCount()
    {
        var bank = new FileBankService(BankRoot);
        bank.Add(TestData.Bytes(1), TestData.Info(1, "A"));
        Snapshots().Take("First");
        _time.Advance(TimeSpan.FromMinutes(5));
        bank.Add(TestData.Bytes(2), TestData.Info(2, "B"));
        Snapshots().Take("Second");

        var list = Snapshots().List();

        Assert.Equal(["Second", "First"], list.Select(s => s.Reason));
        Assert.Equal([2, 1], list.Select(s => s.PokemonCount));
        Assert.Equal(TestData.T0.AddMinutes(5), list[0].CreatedUtc);
    }

    [Fact]
    public void RestoringBringsBackTheCopiedBankAndKeepsTheCurrentOneFirst()
    {
        var bank = new FileBankService(BankRoot);
        var decor = new BankDecor(BankRoot);
        var kept = bank.Add(TestData.Bytes(1), TestData.Info(1, "Kept"));
        decor.Names.SetMany([(0, "Old name")]);
        Snapshots().Take("Before replacing");
        var copy = Snapshots().List().Single();

        _time.Advance(TimeSpan.FromMinutes(1));
        bank.Remove(kept.Id);
        var later = bank.Add(TestData.Bytes(2), TestData.Info(2, "Later"));
        decor.Names.SetMany([(0, "New name")]);

        Snapshots().Restore(copy.Id, "Before restoring");
        bank.Reload();
        decor.Reload();

        Assert.Equal([kept.Id], bank.GetAll().Select(e => e.Id));
        Assert.Equal(TestData.Bytes(1), bank.GetData(kept.Id));
        Assert.False(File.Exists(Path.Combine(BankRoot, later.Id.ToString("N") + ".bin")));
        Assert.Equal("Old name", decor.Names.Get(0));
        var safety = Snapshots().List().First();
        Assert.Equal("Before restoring", safety.Reason);
        Assert.Equal(1, safety.PokemonCount);
    }

    [Fact]
    public void TheSafetyCopyNeverPushesOutTheCopyBeingRestored()
    {
        new FileBankService(BankRoot).Add(TestData.Bytes(1), TestData.Info(1, "A"));
        Snapshots(keep: 2).Take("Oldest");
        var oldest = Snapshots(keep: 2).List().Single();
        _time.Advance(TimeSpan.FromMinutes(1));
        Snapshots(keep: 2).Take("Newer");

        _time.Advance(TimeSpan.FromMinutes(1));
        Snapshots(keep: 2).Restore(oldest.Id, "Before restoring");

        Assert.Contains(Snapshots(keep: 2).List(), s => s.Id == oldest.Id);
        Assert.Equal(TestData.Bytes(1), new FileBankService(BankRoot).GetData(new FileBankService(BankRoot).GetAll().Single().Id));
    }

    [Fact]
    public void AnUnknownCopyIsRefusedWithoutTouchingTheBank()
    {
        var bank = new FileBankService(BankRoot);
        var a = bank.Add(TestData.Bytes(1), TestData.Info(1, "A"));

        Assert.Throws<DirectoryNotFoundException>(() => Snapshots().Restore("20260101-000000-000", "Before restoring"));
        Assert.Throws<ArgumentException>(() => Snapshots().Restore("../bank", "Before restoring"));

        Assert.Equal([a.Id], new FileBankService(BankRoot).GetAll().Select(e => e.Id));
    }
}
