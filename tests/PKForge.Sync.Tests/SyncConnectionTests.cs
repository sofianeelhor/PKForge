using PKForge.Sync.Tests.Infrastructure;
using Xunit;

namespace PKForge.Sync.Tests;

public sealed class SyncConnectionTests : IDisposable
{
    private sealed class MemorySecrets : ISyncSecrets
    {
        public string? DeviceKey { get; set; }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (SyncConnection Connection, SyncStateStore Store, MemorySecrets Secrets) Device(SyncServer server, string name)
    {
        var store = new SyncStateStore(Path.Combine(_root, name));
        var secrets = new MemorySecrets();
        return (new SyncConnection(store, secrets, _ => server.CreateClient()), store, secrets);
    }

    [Fact]
    public async Task SigningUpRemembersTheServerAndAccount()
    {
        using var server = new SyncServer();
        var (connection, store, secrets) = Device(server, "phone");

        var info = await connection.ProbeAsync(new Uri("http://localhost/"));
        await connection.SignUpAsync(new Uri("http://localhost/"), "ash@example.com", SyncServer.Password, "Phone");

        Assert.True(info.RegistrationOpen);
        Assert.True(connection.IsConnected);
        Assert.Equal("http://localhost/", store.Read(s => s.ServerUrl));
        Assert.NotNull(secrets.DeviceKey);
        Assert.NotEmpty(await connection.CreateClient().GetDevicesAsync());
    }

    [Fact]
    public async Task PairingJoinsTheSameAccount()
    {
        using var server = new SyncServer();
        var (phone, phoneStore, _) = Device(server, "phone");
        var (deck, deckStore, _) = Device(server, "deck");
        await phone.SignUpAsync(new Uri("http://localhost/"), "ash@example.com", SyncServer.Password, "Phone");
        var code = await phone.CreateClient().CreatePairingCodeAsync();

        await deck.PairAsync(new Uri("http://localhost/"), code.Code, "Deck");

        Assert.Equal(phoneStore.Read(s => s.AccountId), deckStore.Read(s => s.AccountId));
        Assert.NotEqual(phoneStore.Read(s => s.DeviceId), deckStore.Read(s => s.DeviceId));
    }

    [Fact]
    public async Task SigningOutForgetsEverything()
    {
        using var server = new SyncServer();
        var (connection, store, secrets) = Device(server, "phone");
        await connection.SignInAsync(new Uri("http://localhost/"), "ash@example.com", SyncServer.Password, "Phone")
            .ContinueWith(_ => { }); // no account yet: sign-in fails, nothing is stored
        Assert.False(connection.IsConnected);

        await connection.SignUpAsync(new Uri("http://localhost/"), "ash@example.com", SyncServer.Password, "Phone");
        connection.SignOut();

        Assert.False(connection.IsConnected);
        Assert.Null(secrets.DeviceKey);
        Assert.Null(store.Read(s => s.ServerUrl));
    }
}
