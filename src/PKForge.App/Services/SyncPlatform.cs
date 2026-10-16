using System.Net.Http.Headers;
using PKForge.Sync;

namespace PKForge.App.Services;

/// <summary>Where the Bank Sync device key lives: Android's secure storage, read once.</summary>
public sealed class SecureSyncSecrets : ISyncSecrets
{
    private const string Key = "pkforge.sync.device-key.v1";
    private readonly Lock _gate = new();
    private bool _loaded;
    private string? _value;

    public string? DeviceKey
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    try { _value = SecureStorage.Default.GetAsync(Key).GetAwaiter().GetResult(); }
                    catch (Exception error) { AppLog.Warn("sync", $"Secure storage unreadable: {error.GetType().Name}"); _value = null; }
                    _loaded = true;
                }
                return _value;
            }
        }
        set
        {
            lock (_gate)
            {
                _value = value;
                _loaded = true;
                if (value is null) SecureStorage.Default.Remove(Key);
                else SecureStorage.Default.SetAsync(Key, value).GetAwaiter().GetResult();
            }
        }
    }
}

/// <summary>One HttpClient per sync server address, reused for the life of the app.</summary>
public sealed class SyncHttp
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Uri, HttpClient> _clients = [];

    public HttpClient For(Uri server)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(server, out var client)) return client;
            client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            {
                BaseAddress = server,
                Timeout = TimeSpan.FromSeconds(100),
            };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PKForge", AppInfo.Current.VersionString));
            _clients[server] = client;
            return client;
        }
    }
}

public static class SyncPlatform
{
    /// <summary>How this device appears in the Devices list.</summary>
    public static string DeviceName
    {
        get
        {
            var name = DeviceInfo.Current.Name?.Trim();
            if (!string.IsNullOrEmpty(name)) return name.Length > 100 ? name[..100] : name;
            var model = $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}".Trim();
            return model.Length > 0 ? model : "PKForge device";
        }
    }
}
