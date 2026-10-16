using Microsoft.EntityFrameworkCore;
using PKForge.Sync.Tests.Infrastructure;
using Xunit;

namespace PKForge.Sync.Tests;

public sealed class ResyncTests
{
    /// <summary>Puts the server back to how it was at <paramref name="version"/>, as restoring an old backup would.</summary>
    private static async Task RestoreServerAsync(SyncServer server, long version)
    {
        var (scope, db) = server.Database();
        using (scope)
        {
            await db.Items.Where(i => i.Version > version).ExecuteDeleteAsync();
            await db.Accounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.Version, version));
        }
    }

    [Fact]
    public async Task ARestoredServerGetsBackWhatDevicesStillHold()
    {
        using var server = new SyncServer();
        using var phone = new TestDevice(server, await server.SignUpAsync());
        await phone.JoinAsync();                                   // v1 boxes, v2 settings
        using var deck = new TestDevice(server, await server.SignInAsync());
        await deck.JoinAsync();
        phone.Deposit(1);
        await phone.SyncAsync();                                   // v3
        phone.Deposit(2);
        phone.Deposit(3);
        await phone.SyncAsync();                                   // v4, v5
        await deck.SyncAsync();

        await RestoreServerAsync(server, 3);
        var recovered = await phone.SyncAsync();
        await deck.SyncAsync();
        await phone.SyncAsync();

        Assert.True(recovered.Resynced);
        Assert.Equal(3, deck.Bank.GetAll().Count);
        TestDevice.SameContents(phone, deck);
        Assert.Empty(phone.Store.Read(s => s.Conflicts));
    }

    [Fact]
    public async Task AnUnreadableStateFileRejoinsWithoutDuplicates()
    {
        using var server = new SyncServer();
        var key = await server.SignUpAsync();
        string root;
        using (var phone = new TestDevice(server, key, keepFiles: true))
        {
            phone.Deposit(1);
            phone.Deposit(2);
            await phone.JoinAsync();
            root = phone.Root;
        }
        File.WriteAllText(Path.Combine(root, "sync", "sync-state.json"), "{broken");
        File.WriteAllText(Path.Combine(root, "sync", "sync-state.json.bak"), "{broken");

        using var reopened = new TestDevice(server, key, root: root);
        Assert.True(reopened.Store.Read(s => s.NeedsResync));
        var report = await reopened.JoinAsync();
        using var fresh = new TestDevice(server, await server.SignInAsync());
        await fresh.JoinAsync();

        Assert.Equal(0, report.NewConflicts);
        Assert.Equal(2, reopened.Bank.GetAll().Count);
        TestDevice.SameContents(fresh, reopened);
    }
}
