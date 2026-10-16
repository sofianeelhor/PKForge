using PKForge.Sync.Protocol;

namespace PKForge.Sync;

/// <summary>Where the device key lives (the app keeps it in Android's secure storage).</summary>
public interface ISyncSecrets
{
    string? DeviceKey { get; set; }
}

/// <summary>Connecting this device to a sync server (sign up, sign in, pair) and disconnecting it.</summary>
public sealed class SyncConnection(SyncStateStore store, ISyncSecrets secrets, Func<Uri, HttpClient> httpFor)
{
    public bool IsConnected => store.Read(s => s.ServerUrl) is not null && secrets.DeviceKey is not null;

    public Task<ServerInfo> ProbeAsync(Uri server, CancellationToken ct = default) =>
        new SyncClient(httpFor(server)).GetServerInfoAsync(ct);

    public Task SignUpAsync(Uri server, string email, string password, string deviceName, CancellationToken ct = default) =>
        ConnectAsync(server, client => client.RegisterAsync(new RegisterRequest(email, password, deviceName), ct));

    public Task SignInAsync(Uri server, string email, string password, string deviceName, CancellationToken ct = default) =>
        ConnectAsync(server, client => client.LoginAsync(new LoginRequest(email, password, deviceName), ct));

    public Task PairAsync(Uri server, string code, string deviceName, CancellationToken ct = default) =>
        ConnectAsync(server, client => client.RedeemPairingAsync(new RedeemPairingRequest(code, deviceName), ct));

    /// <summary>A client signed in as this device.</summary>
    public SyncClient CreateClient()
    {
        var server = store.Read(s => s.ServerUrl) ?? throw new InvalidOperationException("This device is not connected to a sync server.");
        return new SyncClient(httpFor(new Uri(server))) { DeviceKey = secrets.DeviceKey };
    }

    /// <summary>Forgets the server, the account and what was synced. The local Bank is untouched.</summary>
    public void SignOut()
    {
        secrets.DeviceKey = null;
        store.Reset();
    }

    private async Task ConnectAsync(Uri server, Func<SyncClient, Task<DeviceKeyResponse>> connect)
    {
        var client = new SyncClient(httpFor(server));
        var key = await connect(client).ConfigureAwait(false);
        string? name = null;
        try { name = (await client.GetServerInfoAsync().ConfigureAwait(false)).Name; }
        catch (Exception error) when (error is HttpRequestException or SyncApiException or TaskCanceledException) { }
        store.Reset();
        store.Update(s =>
        {
            s.ServerUrl = server.ToString();
            s.AccountId = key.AccountId;
            s.DeviceId = key.DeviceId;
            s.ServerName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        });
        secrets.DeviceKey = key.DeviceKey;
    }
}
