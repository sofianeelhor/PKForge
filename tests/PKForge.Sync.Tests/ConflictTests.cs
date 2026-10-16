using PKForge.Domain;
using PKForge.Sync.Tests.Infrastructure;
using Xunit;

namespace PKForge.Sync.Tests;

public sealed class ConflictTests
{
    private static async Task<(SyncServer Server, TestDevice Phone, TestDevice Deck, BankEntry Shared)> SharedPokemonAsync()
    {
        var server = new SyncServer();
        var phone = new TestDevice(server, await server.SignUpAsync());
        var shared = phone.Deposit(1, "Sparky");
        await phone.JoinAsync();
        var deck = new TestDevice(server, await server.SignInAsync());
        await deck.JoinAsync();
        return (server, phone, deck, shared);
    }

    private static string Nickname(TestDevice device, Guid id) => device.Bank.GetAll().Single(e => e.Id == id).Info.Nickname;

    [Theory]
    [InlineData(ConflictChoice.KeepThisDevice, "Deck name", 1)]
    [InlineData(ConflictChoice.KeepOtherDevice, "Phone name", 1)]
    [InlineData(ConflictChoice.KeepBoth, "Phone name", 2)]
    public async Task EditingTheSamePokemonOnTwoDevicesAsksWhichToKeep(ConflictChoice choice, string survivor, int count)
    {
        var (server, phone, deck, shared) = await SharedPokemonAsync();
        using var _ = server;
        using var __ = phone;
        using var ___ = deck;
        phone.Bank.Replace(shared.Id, TestData.Bytes(1), shared.Info with { Nickname = "Phone name" });
        deck.Bank.Replace(shared.Id, TestData.Bytes(1), shared.Info with { Nickname = "Deck name" });
        await phone.SyncAsync();

        var report = await deck.SyncAsync();
        var conflict = deck.Store.Read(s => s.Conflicts.Single());
        Assert.Equal(1, report.NewConflicts);
        Assert.Equal(ConflictKind.BothEdited, conflict.Kind);
        Assert.Equal("Deck name", Nickname(deck, shared.Id));

        await deck.Engine.ResolveAsync(conflict.Id, choice);
        await deck.SyncAsync();
        await phone.SyncAsync();

        Assert.Equal(survivor, Nickname(phone, shared.Id));
        Assert.Equal(count, phone.Bank.GetAll().Count);
        TestDevice.SameContents(phone, deck);
    }

    [Fact]
    public async Task TheLaterMoveWinsWithoutAsking()
    {
        var (server, phone, deck, shared) = await SharedPokemonAsync();
        using var _ = server;
        using var __ = phone;
        using var ___ = deck;
        phone.Time.Advance(TimeSpan.FromMinutes(1));
        phone.Bank.Move(shared.Id, 0, 5);
        deck.Time.Advance(TimeSpan.FromMinutes(2));
        deck.Bank.Move(shared.Id, 0, 9);

        await phone.SyncAsync();
        var report = await deck.SyncAsync();
        await phone.SyncAsync();

        Assert.Equal(0, report.NewConflicts);
        Assert.Equal(9, phone.Bank.GetAll().Single().Slot);
        TestDevice.SameContents(phone, deck);
    }

    [Fact]
    public async Task TheEarlierMoveGivesWayWithoutAsking()
    {
        var (server, phone, deck, shared) = await SharedPokemonAsync();
        using var _ = server;
        using var __ = phone;
        using var ___ = deck;
        deck.Time.Advance(TimeSpan.FromMinutes(1));
        deck.Bank.Move(shared.Id, 0, 9);
        phone.Time.Advance(TimeSpan.FromMinutes(2));
        phone.Bank.Move(shared.Id, 0, 5);

        await phone.SyncAsync();
        var report = await deck.SyncAsync();

        Assert.Equal(0, report.NewConflicts);
        Assert.Equal(5, deck.Bank.GetAll().Single().Slot);
    }

    [Fact]
    public async Task TwoDevicesFillingTheSameSlotsKeepEveryPokemon()
    {
        var (server, phone, deck, _) = await SharedPokemonAsync();
        using var __ = server;
        using var ___ = phone;
        using var ____ = deck;
        var fromPhone = Enumerable.Range(10, 3).Select(i => phone.Deposit(i).Id).ToList();
        var fromDeck = Enumerable.Range(20, 3).Select(i => deck.Deposit(i).Id).ToList();

        await phone.SyncAsync();
        var report = await deck.SyncAsync();
        await phone.SyncAsync();

        Assert.Equal(3, report.Relocated);
        Assert.Equal(7, phone.Bank.GetAll().Count);
        Assert.Contains(deck.Store.Read(s => s.Activity), a => a.Message.Contains("filled on another device", StringComparison.Ordinal));
        TestDevice.SameContents(phone, deck);
    }

    [Fact]
    public async Task EditedHereButReleasedThereKeepsTheEditedCopyWhenChosen()
    {
        var (server, phone, deck, shared) = await SharedPokemonAsync();
        using var _ = server;
        using var __ = phone;
        using var ___ = deck;
        phone.Bank.Remove(shared.Id);
        deck.Bank.Replace(shared.Id, TestData.Bytes(1), shared.Info with { Nickname = "Still loved" });
        await phone.SyncAsync();

        await deck.SyncAsync();
        var conflict = deck.Store.Read(s => s.Conflicts.Single());
        Assert.Equal(ConflictKind.EditedHereReleasedThere, conflict.Kind);
        await deck.Engine.ResolveAsync(conflict.Id, ConflictChoice.KeepThisDevice);
        await deck.SyncAsync();
        await phone.SyncAsync();

        Assert.Equal("Still loved", Nickname(phone, shared.Id));
    }

    [Fact]
    public async Task ReleasedHereButEditedThereCanBringItBack()
    {
        var (server, phone, deck, shared) = await SharedPokemonAsync();
        using var _ = server;
        using var __ = phone;
        using var ___ = deck;
        phone.Bank.Replace(shared.Id, TestData.Bytes(1), shared.Info with { Nickname = "Edited" });
        deck.Bank.Remove(shared.Id);
        await phone.SyncAsync();

        await deck.SyncAsync();
        var conflict = deck.Store.Read(s => s.Conflicts.Single());
        Assert.Equal(ConflictKind.ReleasedHereEditedThere, conflict.Kind);
        await deck.Engine.ResolveAsync(conflict.Id, ConflictChoice.KeepOtherDevice);

        Assert.Equal("Edited", Nickname(deck, shared.Id));
    }

    [Fact]
    public async Task BackgroundFactRewritesNeverAsk()
    {
        var (server, phone, deck, shared) = await SharedPokemonAsync();
        using var _ = server;
        using var __ = phone;
        using var ___ = deck;
        phone.Bank.Replace(shared.Id, TestData.Bytes(1), shared.Info with { Nickname = "User edit" });
        deck.Bank.UpdateInfo([(shared.Id, shared.Info with { HeldItem = 9 })]);
        await phone.SyncAsync();

        var report = await deck.SyncAsync();

        Assert.Equal(0, report.NewConflicts);
        Assert.Equal("User edit", Nickname(deck, shared.Id));
    }

    [Fact]
    public async Task BoxLayoutChangedOnBothAsksWhichToKeep()
    {
        var (server, phone, deck, _) = await SharedPokemonAsync();
        using var __ = server;
        using var ___ = phone;
        using var ____ = deck;
        phone.Bank.Decor.Names.SetMany([(0, "Alpha")]);
        deck.Bank.Decor.Names.SetMany([(0, "Beta")]);
        await phone.SyncAsync();

        await deck.SyncAsync();
        var conflict = deck.Store.Read(s => s.Conflicts.Single());
        Assert.Equal(ConflictKind.Boxes, conflict.Kind);
        await deck.Engine.ResolveAsync(conflict.Id, ConflictChoice.KeepOtherDevice);

        Assert.Equal("Alpha", deck.Bank.Decor.Names.Get(0));
    }

    [Fact]
    public async Task SwappingBoxesMovesEveryPokemonAndName()
    {
        var (server, phone, deck, shared) = await SharedPokemonAsync();
        using var _ = server;
        using var __ = phone;
        using var ___ = deck;
        phone.Bank.Decor.Names.SetMany([(0, "First")]);
        await phone.SyncAsync();
        await deck.SyncAsync();

        var remap = BankBoxRemap.Swap(phone.Bank.BoxCount, 0, 1);
        phone.Bank.RemapBoxes(remap);
        phone.Bank.Decor.Remap(remap);
        await phone.SyncAsync();
        await deck.SyncAsync();

        Assert.Equal(1, deck.Bank.GetAll().Single(e => e.Id == shared.Id).Box);
        Assert.Equal("First", deck.Bank.Decor.Names.Get(1));
        TestDevice.SameContents(phone, deck);
    }

    [Fact]
    public async Task RefusedChangesAreNotSentAgainUntilTheyChange()
    {
        using var server = new SyncServer();
        server.Settings["Sync:MaxLiveMonsPerAccount"] = "1";
        using var phone = new TestDevice(server, await server.SignUpAsync());
        phone.Deposit(1);
        phone.Deposit(2);

        var first = await phone.JoinAsync();
        var second = await phone.SyncAsync();

        Assert.True(first.PendingChanges);
        Assert.Single(phone.Store.Read(s => s.Activity), a => a.Message.StartsWith("The server refused", StringComparison.Ordinal));
        Assert.Equal(0, second.Uploaded);
    }

    [Fact]
    public async Task ARefusedPushStillDownloadsAndCarriesTheServersReason()
    {
        using var server = new SyncServer();
        var key = await server.SignUpAsync();
        using var phone = new TestDevice(server, key);
        phone.Deposit(1);
        await phone.JoinAsync();
        server.Writes.Deny("Storage quota reached.");
        using var deck = new TestDevice(server, await server.SignInAsync());

        var report = await deck.JoinAsync();
        deck.Deposit(2);
        var push = await deck.SyncAsync();

        Assert.Equal(1, report.Downloaded); // the Pokémon; empty boxes and settings already match
        Assert.True(push.WritesDisabled);
        Assert.Equal("Storage quota reached.", push.WritesDisabledMessage);
        Assert.True(push.PendingChanges);
        Assert.Equal(0, push.Uploaded);
    }

    [Fact]
    public async Task ARefusalWithoutAReasonCarriesTheServersNeutralMessage()
    {
        using var server = new SyncServer();
        using var phone = new TestDevice(server, await server.SignUpAsync());
        await phone.JoinAsync();
        server.Writes.Deny();

        phone.Deposit(1);
        var push = await phone.SyncAsync();

        Assert.True(push.WritesDisabled);
        Assert.Equal(PKForge.Sync.Server.Sync.SyncService.WritesDisabledMessage, push.WritesDisabledMessage);
    }

    [Fact]
    public async Task OnceWritesAreAllowedAgainTheWaitingChangesGoUp()
    {
        using var server = new SyncServer();
        using var phone = new TestDevice(server, await server.SignUpAsync());
        await phone.JoinAsync();
        using var deck = new TestDevice(server, await server.SignInAsync());
        await deck.JoinAsync();
        server.Writes.Deny("Read-only for maintenance.");

        phone.Deposit(1);
        var refused = await phone.SyncAsync();
        server.Writes.Allow();
        var later = await phone.SyncAsync();
        await deck.SyncAsync();

        Assert.True(refused.WritesDisabled);
        Assert.False(later.WritesDisabled);
        Assert.Null(later.WritesDisabledMessage);
        Assert.Equal(1, later.Uploaded);
        Assert.False(later.PendingChanges);
        Assert.Single(deck.Bank.GetAll());
    }
}
