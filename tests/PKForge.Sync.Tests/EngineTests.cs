using PKForge.Domain;
using PKForge.Sync.Tests.Infrastructure;
using Xunit;

namespace PKForge.Sync.Tests;

public sealed class EngineTests
{
    private static async Task<(SyncServer Server, TestDevice Phone, TestDevice Deck)> TwoJoinedDevicesAsync()
    {
        var server = new SyncServer();
        var phone = new TestDevice(server, await server.SignUpAsync());
        await phone.JoinAsync();
        var deck = new TestDevice(server, await server.SignInAsync());
        await deck.JoinAsync();
        return (server, phone, deck);
    }

    [Fact]
    public async Task SyncingBeforeJoiningIsRefused()
    {
        using var server = new SyncServer();
        using var phone = new TestDevice(server, await server.SignUpAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => phone.SyncAsync());
    }

    [Fact]
    public async Task TheFirstDeviceUploadsAndTheSecondDownloads()
    {
        using var server = new SyncServer();
        using var phone = new TestDevice(server, await server.SignUpAsync());
        phone.Deposit(1);
        phone.Deposit(2);
        phone.Bank.Decor.Names.SetMany([(0, "Starters")]);
        phone.Bank.Decor.Settings.LivingDexStartBox = 2;

        var uploaded = await phone.JoinAsync();
        using var deck = new TestDevice(server, await server.SignInAsync());
        var downloaded = await deck.JoinAsync();

        Assert.Equal(4, uploaded.Uploaded); // two Pokémon, boxes, settings
        Assert.Equal(4, downloaded.Downloaded);
        Assert.False(uploaded.PendingChanges);
        TestDevice.SameContents(phone, deck);
    }

    [Fact]
    public async Task ChangesFlowBothWays()
    {
        var (server, phone, deck) = await TwoJoinedDevicesAsync();
        using var _ = server;
        using var __ = phone;
        using var ___ = deck;
        var a = phone.Deposit(1);
        var b = phone.Deposit(2);
        await phone.SyncAsync();
        await deck.SyncAsync();

        deck.Bank.Move(a.Id, 1, 4);
        deck.Bank.Remove(b.Id);
        deck.Bank.Replace(a.Id, TestData.Bytes(11), a.Info with { Nickname = "Evolved" });
        await deck.SyncAsync();
        var report = await phone.SyncAsync();

        Assert.Equal(2, report.Downloaded);
        TestDevice.SameContents(phone, deck);
        Assert.Equal("Evolved", phone.Bank.GetAll().Single().Info.Nickname);
    }

    [Fact]
    public async Task ASecondSyncWithNothingNewDoesNothing()
    {
        var (server, phone, deck) = await TwoJoinedDevicesAsync();
        using var _ = server;
        using var __ = phone;
        using var ___ = deck;
        phone.Deposit(1);
        await phone.SyncAsync();

        var again = await phone.SyncAsync();

        Assert.Equal((0, 0), (again.Uploaded, again.Downloaded));
    }

    [Fact]
    public async Task MergingKeepsBothBanks()
    {
        using var server = new SyncServer();
        using var phone = new TestDevice(server, await server.SignUpAsync());
        phone.Deposit(1);
        phone.Deposit(2);
        await phone.JoinAsync();
        using var deck = new TestDevice(server, await server.SignInAsync());
        deck.Deposit(3);
        deck.Deposit(4);

        await deck.JoinAsync(JoinMode.Merge);
        await phone.SyncAsync();

        Assert.Equal(4, deck.Bank.GetAll().Count);
        TestDevice.SameContents(phone, deck);
    }

    [Fact]
    public async Task UsingTheServersBankReplacesTheLocalOneAfterASnapshot()
    {
        using var server = new SyncServer();
        using var phone = new TestDevice(server, await server.SignUpAsync());
        phone.Deposit(1);
        await phone.JoinAsync();
        using var deck = new TestDevice(server, await server.SignInAsync());
        var local = deck.Deposit(9);

        await deck.JoinAsync(JoinMode.UseServer);

        TestDevice.SameContents(phone, deck);
        var snapshot = Assert.Single(Directory.GetDirectories(deck.Snapshots));
        Assert.True(File.Exists(Path.Combine(snapshot, local.Id.ToString("N") + ".bin")));
    }

    [Fact]
    public async Task ManyReleasesFromAnotherDeviceTakeASnapshotFirst()
    {
        var (server, phone, deck) = await TwoJoinedDevicesAsync();
        using var _ = server;
        using var __ = phone;
        using var ___ = deck;
        var ids = Enumerable.Range(1, SyncEngine.LargeRemoval + 1).Select(i => phone.Deposit(i).Id).ToList();
        await phone.SyncAsync();
        await deck.SyncAsync();

        phone.Bank.RemoveMany(ids);
        await phone.SyncAsync();
        await deck.SyncAsync();

        Assert.Empty(deck.Bank.GetAll());
        Assert.Single(Directory.GetDirectories(deck.Snapshots));
    }

    [Fact]
    public async Task AnUndoneLocalChangeStillReceivesTheServersVersion()
    {
        using var server = new SyncServer();
        using var phone = new TestDevice(server, await server.SignUpAsync());
        phone.Bank.Decor.Names.SetMany([(0, "Original")]);
        await phone.JoinAsync();
        // The deck cannot upload (writes refused), so its own change stays local and the pull must skip the server's.
        using var deck = new TestDevice(server, await server.SignInAsync());
        await deck.JoinAsync();

        phone.Bank.Decor.Names.SetMany([(0, "From the phone")]);
        await phone.SyncAsync();
        server.Writes.Deny();
        deck.Bank.Decor.Names.SetMany([(0, "Typed on the deck")]);
        Assert.True((await deck.SyncAsync()).WritesDisabled);
        Assert.Equal("Typed on the deck", deck.Bank.Decor.Names.Get(0));

        deck.Bank.Decor.Names.SetMany([(0, "Original")]);       // undone: nothing local is left to protect
        await deck.SyncAsync();

        Assert.Equal("From the phone", deck.Bank.Decor.Names.Get(0));
    }
}
