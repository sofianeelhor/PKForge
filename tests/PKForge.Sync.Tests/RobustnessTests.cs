using System.Text.Json;
using PKForge.Domain;
using PKForge.Sync.Protocol;
using PKForge.Sync.Tests.Infrastructure;
using Xunit;

namespace PKForge.Sync.Tests;

/// <summary>Sync under the things that go wrong on a real handheld: damaged files, killed apps, odd operation orders.</summary>
public sealed class RobustnessTests
{
    [Fact]
    public async Task AnUnreadableBankPausesSyncInsteadOfReleasingEverything()
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
        File.WriteAllText(Path.Combine(root, "bank", "index.json"), "{broken");
        File.WriteAllText(Path.Combine(root, "bank", "index.json.bak"), "{broken");

        using var damaged = new TestDevice(server, key, root: root);
        var report = await damaged.SyncAsync();
        using var deck = new TestDevice(server, await server.SignInAsync());
        await deck.JoinAsync();

        Assert.True(report.Paused);
        Assert.Equal(2, deck.Bank.GetAll().Count);
    }

    [Fact]
    public async Task AnEditToABanksBytesSurvivesTheAppBeingKilled()
    {
        using var server = new SyncServer();
        var key = await server.SignUpAsync();
        string root;
        Guid id;
        using (var phone = new TestDevice(server, key, keepFiles: true))
        {
            var entry = phone.Deposit(1);
            id = entry.Id;
            await phone.JoinAsync();
            phone.Bank.Replace(id, TestData.Bytes(99), entry.Info);   // new moves or IVs: same facts, new bytes
            root = phone.Root;                                         // killed before the next sync: nothing flushed
        }

        using (var restarted = new TestDevice(server, key, root: root)) await restarted.SyncAsync();
        using var deck = new TestDevice(server, await server.SignInAsync());
        await deck.JoinAsync();

        Assert.Equal(TestData.Bytes(99), deck.Bank.GetData(id));
    }

    [Fact]
    public async Task APokemonInANewBoxReachesOtherDevices()
    {
        using var server = new SyncServer();
        using var phone = new TestDevice(server, await server.SignUpAsync());
        await phone.JoinAsync();
        using var deck = new TestDevice(server, await server.SignInAsync());
        await deck.JoinAsync();

        phone.Bank.AddBox();
        var newcomer = phone.Deposit(1);
        phone.Bank.Move(newcomer.Id, phone.Bank.BoxCount - 1, 0);
        phone.Bank.AddBox();
        var report = await phone.SyncAsync();
        await deck.SyncAsync();

        Assert.False(report.PendingChanges);
        Assert.Contains(deck.Bank.GetAll(), e => e.Id == newcomer.Id);
        TestDevice.SameContents(phone, deck);
    }

    [Fact]
    public async Task ABackgroundRewriteNeverOverridesALaterEdit()
    {
        using var server = new SyncServer();
        using var phone = new TestDevice(server, await server.SignUpAsync());
        var shared = phone.Deposit(1, "Sparky");
        await phone.JoinAsync();
        using var deck = new TestDevice(server, await server.SignInAsync());
        await deck.JoinAsync();

        phone.Bank.Replace(shared.Id, TestData.Bytes(1), shared.Info with { Nickname = "Phone edit" });
        await phone.SyncAsync();
        deck.Bank.UpdateInfo([(shared.Id, shared.Info with { HeldItem = 3 })]);
        deck.Store.Update(s => s.Pending.ForEach(g => g.Sent = true));       // the background push was cut off
        deck.Bank.Replace(shared.Id, TestData.Bytes(1), shared.Info with { Nickname = "Deck edit", HeldItem = 3 });
        var report = await deck.SyncAsync();

        Assert.Equal(1, report.NewConflicts);                                 // two people edited it: ask, don't overwrite
        Assert.Equal("Deck edit", deck.Bank.GetAll().Single().Info.Nickname);
    }

    [Fact]
    public async Task FactsFromANewerAppVersionAreNotStrippedByThisOne()
    {
        using var server = new SyncServer();
        var key = await server.SignUpAsync();
        using var phone = new TestDevice(server, key);
        await phone.JoinAsync();
        var newer = server.Client(key);
        var bytes = TestData.Bytes(5);
        var hash = BlobHash.Compute(bytes);
        await newer.UploadBlobsAsync([new BlobData(hash, bytes)]);
        var id = Guid.NewGuid();
        var info = JsonSerializer.SerializeToElement(new
        {
            species = 25, form = 0, shiny = false, nickname = "Future", level = 5, generation = 9, sourceName = "SV", format = "PK9",
            futureFact = "kept",
        });
        var value = JsonSerializer.SerializeToElement(new MonValue(0, 5, TestData.T0, info, TestData.T0, hash), ProtocolJson.Options);
        await newer.PushAsync(new PushRequest([new ChangeGroup(Guid.NewGuid(), [new ItemWrite(ItemKinds.Mon, id.ToString(), 0, value)])]));

        await phone.SyncAsync();
        var again = await phone.SyncAsync();

        var stored = (await newer.GetChangesAsync(0, null, 100)).Items.Single(i => i.Id == id.ToString());
        Assert.Equal("Future", phone.Bank.GetAll().Single().Info.Nickname);
        Assert.Equal(0, again.Uploaded);
        Assert.Equal("kept", stored.Value!.Value.GetProperty("info").GetProperty("futureFact").GetString());
    }
}
