using Microsoft.Extensions.Time.Testing;
using PKForge.Domain;
using PKForge.Infrastructure;
using PKForge.Sync.Protocol;

namespace PKForge.Sync.Tests.Infrastructure;

/// <summary>One simulated device: its own Bank folder, sync state and engine, signed in to the test server.</summary>
public sealed class TestDevice : IDisposable
{
    private readonly bool _keepFiles;

    /// <param name="root">Reuse an existing device folder (default: a new temp folder).</param>
    /// <param name="keepFiles">Leave the folder on dispose, to reopen it later.</param>
    /// <param name="handler">Intercepts this device's HTTP calls (e.g. an outage).</param>
    public TestDevice(SyncServer server, DeviceKeyResponse key, string? root = null, bool keepFiles = false, DelegatingHandler? handler = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "pkforge-tests", "device-" + Guid.NewGuid().ToString("N"));
        _keepFiles = keepFiles;
        var bankRoot = Path.Combine(Root, "bank");
        Store = new SyncStateStore(Path.Combine(Root, "sync"));
        Bank = new SyncedBankService(new FileBankService(bankRoot), new BankDecor(bankRoot), Store, Time);
        Snapshots = Path.Combine(Root, "snapshots");
        Engine = new SyncEngine(server.Client(key, handler is null ? [] : [handler]), Bank, Store, new FileBankSnapshots(bankRoot, Snapshots, Time), Time);
    }

    public string Root { get; }
    public string Snapshots { get; }
    public FakeTimeProvider Time { get; } = new(TestData.T0);
    public SyncStateStore Store { get; }
    public SyncedBankService Bank { get; }
    public SyncEngine Engine { get; }

    public BankEntry Deposit(int seed, string? nickname = null) => Bank.Add(TestData.Bytes(seed), TestData.Info(25, nickname ?? $"Mon{seed}"));
    public Task<SyncReport> SyncAsync() => Engine.SyncAsync();
    public Task<SyncReport> JoinAsync(JoinMode mode = JoinMode.Merge) => Engine.JoinAsync(mode);

    /// <summary>The Bank as comparable data: Pokémon (id, place, facts, bytes), box count, names, wallpapers, settings.</summary>
    public string Contents()
    {
        var mons = Bank.GetAll().OrderBy(e => e.Id)
            .Select(e => $"{e.Id}@{e.Box}/{e.Slot}:{e.Info}:{BlobHash.Compute(Bank.GetData(e.Id))}");
        var names = string.Join(",", Bank.Decor.Names.All().OrderBy(n => n.Key).Select(n => $"{n.Key}={n.Value}"));
        var walls = string.Join(",", Bank.Decor.Wallpapers.AllIds().OrderBy(n => n.Key).Select(n => $"{n.Key}={n.Value}"));
        return string.Join("\n", mons) + $"\nboxes={Bank.BoxCount}\nnames={names}\nwalls={walls}\nsettings={Bank.Decor.Settings.ToJson().GetRawText()}";
    }

    public static void SameContents(params TestDevice[] devices)
    {
        foreach (var device in devices.Skip(1)) Xunit.Assert.Equal(devices[0].Contents(), device.Contents());
    }

    public void Dispose()
    {
        if (_keepFiles) return;
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}
