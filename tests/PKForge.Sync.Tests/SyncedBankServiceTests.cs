using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PKForge.Domain;
using PKForge.Infrastructure;
using PKForge.Sync.Protocol;
using PKForge.Sync.Tests.Infrastructure;
using Xunit;

namespace PKForge.Sync.Tests;

public sealed class SyncedBankServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(TestData.T0);
    private readonly SyncStateStore _store;
    private readonly FileBankService _inner;
    private readonly SyncedBankService _bank;

    public SyncedBankServiceTests()
    {
        _inner = new FileBankService(Path.Combine(_root, "bank"));
        _store = new SyncStateStore(Path.Combine(_root, "sync"));
        _bank = new SyncedBankService(_inner, new BankDecor(Path.Combine(_root, "bank")), _store, _time);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private List<HashSet<string>> Groups() => _store.Read(s => s.Pending.Select(g => g.Keys.ToHashSet()).ToList());

    [Fact]
    public void EachOperationRecordsTheItemsItTouched()
    {
        var a = _bank.Add(TestData.Bytes(1), TestData.Info(1, "A"));
        var b = _bank.Add(TestData.Bytes(2), TestData.Info(2, "B"));
        _store.Update(s => s.Pending.Clear());

        _time.Advance(TimeSpan.FromMinutes(1));
        _bank.Move(a.Id, b.Box, b.Slot);

        Assert.Equal(new[] { SyncValues.MonKey(a.Id), SyncValues.MonKey(b.Id) }.Order(), Groups().Single().Order());
        Assert.Equal(_time.GetUtcNow(), _store.Read(s => s.MovedUtc[a.Id]));
    }

    [Fact]
    public void BoxRemapsRecordTheBoxesAndEveryPokemonThatMoved()
    {
        var a = _bank.Add(TestData.Bytes(1), TestData.Info(1, "A"));
        _store.Update(s => s.Pending.Clear());

        _bank.RemapBoxes(BankBoxRemap.Insert(_bank.BoxCount, 0));

        Assert.Equal([SyncValues.BoxesKey, SyncValues.MonKey(a.Id)], Groups().Single().Order());
    }

    [Fact]
    public void AutomaticFactRewritesAreBackgroundChanges()
    {
        var a = _bank.Add(TestData.Bytes(1), TestData.Info(1, "A"));
        _store.Update(s => s.Pending.Clear());

        _bank.UpdateInfo([(a.Id, a.Info with { HeldItem = 5 })]);

        Assert.True(_store.Read(s => s.Pending.Single().Background));
    }

    [Fact]
    public void CurrentValuesDescribeEveryItem()
    {
        var a = _bank.Add(TestData.Bytes(1), TestData.Info(1, "A"));
        _bank.Decor.Names.SetMany([(1, "Shinies")]);
        _bank.Decor.Settings.LivingDexStartBox = 2;

        var local = _bank.CurrentValues();

        Assert.Equal(a, SyncValues.ToEntry(a.Id, SyncValues.ReadMon(local.Values[SyncValues.MonKey(a.Id)])));
        Assert.Equal(BlobHash.Compute(TestData.Bytes(1)), local.Blobs[SyncValues.MonKey(a.Id)]);
        Assert.Equal("Shinies", SyncValues.ReadBoxes(local.Values[SyncValues.BoxesKey]).Names![1]);
        Assert.Equal(2, local.Values[SyncValues.SettingsKey].GetProperty("livingDexStartBox").GetInt32());
    }

    [Fact]
    public void ApplyingRemoteChangesWritesThemAndRaisesTheEvent()
    {
        var raised = 0;
        _bank.RemoteChangesApplied += (_, _) => raised++;
        var id = Guid.NewGuid();
        var entry = new BankEntry(id, 2, 5, TestData.Info(25, "Sparky"), TestData.T0);
        var blob = BlobHash.Compute(TestData.Bytes(9));
        var boxes = SyncValues.Boxes(4, new Dictionary<int, string> { [0] = "Starters" }, new Dictionary<int, string>());

        var result = _bank.ApplyRemote(
            [new RemoteChange(SyncValues.MonKey(id), SyncValues.Mon(entry, blob, TestData.T0), TestData.Bytes(9)),
             new RemoteChange(SyncValues.BoxesKey, boxes)],
            new Dictionary<string, string>());

        Assert.Equal(2, result.Applied.Count);
        Assert.Equal(entry, _bank.GetAll().Single());
        Assert.Equal(4, _bank.BoxCount);
        Assert.Equal("Starters", _bank.Decor.Names.Get(0));
        Assert.Equal(blob, _store.Read(s => s.BlobHashes[id]));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ApplyRemoteSkipsItemsChangedSinceTheEngineLooked()
    {
        var a = _bank.Add(TestData.Bytes(1), TestData.Info(1, "A"));
        var seen = SyncValues.Fingerprint(_bank.CurrentValues().Values[SyncValues.MonKey(a.Id)]);
        _bank.Replace(a.Id, TestData.Bytes(1), a.Info with { Nickname = "Edited just now" });
        var remote = SyncValues.Mon(a with { Info = a.Info with { Nickname = "From the server" } }, BlobHash.Compute(TestData.Bytes(1)), TestData.T0);

        var result = _bank.ApplyRemote([new RemoteChange(SyncValues.MonKey(a.Id), remote)],
            new Dictionary<string, string> { [SyncValues.MonKey(a.Id)] = seen });

        Assert.Equal([SyncValues.MonKey(a.Id)], result.Skipped);
        Assert.Equal("Edited just now", _bank.GetAll().Single().Info.Nickname);
    }

    [Fact]
    public void ALocalPokemonInTheWayOfAnArrivingOneMovesAside()
    {
        var resident = _bank.Add(TestData.Bytes(1), TestData.Info(1, "Resident"));
        _store.Update(s => s.Pending.Clear());
        var newcomer = new BankEntry(Guid.NewGuid(), resident.Box, resident.Slot, TestData.Info(2, "Newcomer"), TestData.T0);

        var result = _bank.ApplyRemote(
            [new RemoteChange(SyncValues.MonKey(newcomer.Id), SyncValues.Mon(newcomer, BlobHash.Compute(TestData.Bytes(2)), TestData.T0), TestData.Bytes(2))],
            new Dictionary<string, string>());

        var moved = _bank.GetAll().Single(e => e.Id == resident.Id);
        Assert.NotEqual((resident.Box, resident.Slot), (moved.Box, moved.Slot));
        Assert.Equal((resident.Id, moved.Box, moved.Slot), Assert.Single(result.Relocated));
        Assert.Contains(SyncValues.MonKey(resident.Id), Groups().Single());
    }

    [Fact]
    public void MoveToFreeSlotLeavesTheCurrentSlot()
    {
        var a = _bank.Add(TestData.Bytes(1), TestData.Info(1, "A"));

        var moved = _bank.MoveToFreeSlot(a.Id);

        Assert.NotNull(moved);
        Assert.NotEqual((a.Box, a.Slot), moved!.Value);
        Assert.Equal(moved.Value, (_bank.GetAll().Single().Box, _bank.GetAll().Single().Slot));
    }

    [Fact]
    public void MoveToFreeSlotAvoidsSlotsTheServerSaidAreTaken()
    {
        var a = _bank.Add(TestData.Bytes(1), TestData.Info(1, "A"));     // (0,0)
        var b = _bank.Add(TestData.Bytes(2), TestData.Info(2, "B"));     // (0,1)
        _bank.MoveToFreeSlot(a.Id, avoid: [(0, 0), (0, 2)]);

        var moved = _bank.MoveToFreeSlot(b.Id, avoid: [(0, 0), (0, 1), (0, 2)]);

        Assert.Equal((0, 4), moved);
        Assert.Equal((0, 3), (_bank.GetAll().Single(e => e.Id == a.Id).Box, _bank.GetAll().Single(e => e.Id == a.Id).Slot));
    }

    [Fact]
    public void KeepCopyAddsAnIdenticalPokemonUnderANewId()
    {
        var a = _bank.Add(TestData.Bytes(1), TestData.Info(1, "A"));

        var copy = _bank.KeepCopy(a.Id);

        Assert.NotEqual(a.Id, copy);
        Assert.Equal(a.Info, _bank.GetAll().Single(e => e.Id == copy).Info);
        Assert.Equal(TestData.Bytes(1), _bank.GetData(copy));
    }

    [Fact]
    public void LocalChangesAreAnnouncedButRemoteOnesAreNot()
    {
        var local = 0;
        _bank.LocalChanged += (_, _) => local++;
        var a = _bank.Add(TestData.Bytes(1), TestData.Info(1, "A"));
        _bank.Move(a.Id, 0, 5);
        var id = Guid.NewGuid();
        var entry = new BankEntry(id, 1, 0, TestData.Info(2, "B"), TestData.T0);

        _bank.ApplyRemote([new RemoteChange(SyncValues.MonKey(id), SyncValues.Mon(entry, BlobHash.Compute(TestData.Bytes(2)), TestData.T0), TestData.Bytes(2))],
            new Dictionary<string, string>());

        Assert.Equal(2, local);
    }
    [Fact]
    public void BoxDecorEditsAreRecordedAndAnnouncedLikeAnyChange()
    {
        var local = 0;
        _bank.LocalChanged += (_, _) => local++;

        _bank.Decor.Wallpapers.Set(0, BankWallpaper.Color(3));
        _bank.Decor.Names.SetMany([(1, "Shiny hunts")]);

        Assert.Equal(2, local);
        Assert.Contains(SyncValues.BoxesKey, Groups().Single());
    }

    [Fact]
    public void SettingsEditsAreRecordedAndAnnounced()
    {
        var local = 0;
        _bank.LocalChanged += (_, _) => local++;

        _bank.Decor.Settings.LivingDexStartBox = 2;

        Assert.Equal(1, local);
        Assert.Contains(SyncValues.SettingsKey, Groups().Single());
    }

    [Fact]
    public void DecorFromAnotherDeviceIsNotAnnounced()
    {
        var local = 0;
        _bank.LocalChanged += (_, _) => local++;
        var boxes = SyncValues.Boxes(1, new Dictionary<int, string> { [0] = "Far" }, new Dictionary<int, string> { [0] = "color:2" });
        using var settings = JsonDocument.Parse("""{"livingDexStartBox":1}""");

        _bank.ApplyRemote([new RemoteChange(SyncValues.BoxesKey, boxes), new RemoteChange(SyncValues.SettingsKey, settings.RootElement.Clone())],
            new Dictionary<string, string>());

        Assert.Equal(0, local);
        Assert.Equal("Far", _bank.Decor.Names.Get(0));
    }
}
