using System.Net.Http;
using Microsoft.Extensions.Time.Testing;
using PKForge.Infrastructure;
using PKForge.Sync.Protocol;
using PKForge.Sync.Tests.Infrastructure;
using Xunit;

namespace PKForge.Sync.Tests;

public sealed class CoordinatorTests : IDisposable
{
    private sealed class MemorySecrets : ISyncSecrets
    {
        public string? DeviceKey { get; set; }
    }

    /// <summary>Fails every request while <see cref="Down"/> is set, like a server out of reach.</summary>
    private sealed class Outage : DelegatingHandler
    {
        public bool Down { get; set; }
        public int Attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            return Down ? throw new HttpRequestException("No route to host") : base.SendAsync(request, ct);
        }
    }

    /// <summary>Answers the change feed with a web page, like a captive portal or another service at that address.</summary>
    private sealed class NotAServer : DelegatingHandler
    {
        public bool On { get; set; }
        public int Attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            return On && request.RequestUri!.AbsolutePath.Contains("/v1/sync/", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html>Sign in to the Wi-Fi</html>", System.Text.Encoding.UTF8, "text/html") })
                : base.SendAsync(request, ct);
        }
    }

    /// <summary>Counts pushes, and holds them while <see cref="Hold"/> is set.</summary>
    private sealed class PushCounter : DelegatingHandler
    {
        public int Pushes;
        public TaskCompletionSource? Hold { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/v1/sync/push", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Pushes);
                Started.TrySetResult();
                if (Hold is { } hold) await hold.Task.ConfigureAwait(false);
            }
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }
    }

    private readonly List<string> _roots = [];
    private static readonly Uri Server = new("http://localhost/");
    private static readonly Dictionary<string, MemorySecrets> Secrets = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private (SyncCoordinator Coordinator, SyncedBankService Bank, FakeTimeProvider Time) Device(SyncServer server, DelegatingHandler? outage = null, string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "pkforge-tests", "coordinator-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var time = new FakeTimeProvider(TestData.T0);
        var store = new SyncStateStore(Path.Combine(root, "sync"));
        var bankRoot = Path.Combine(root, "bank");
        var bank = new SyncedBankService(new FileBankService(bankRoot), new BankDecor(bankRoot), store, time);
        // A handler can sit in only one client's pipeline, so the device keeps one client (as the app does).
        HttpClient? shared = null;
        var secrets = Secrets.GetValueOrDefault(root) ?? (Secrets[root] = new MemorySecrets());
        var connection = new SyncConnection(store, secrets, _ => shared ??= outage is null ? server.CreateClient() : server.CreateDefaultClient(outage));
        return (new SyncCoordinator(connection, bank, store, null, time), bank, time);
    }

    [Fact]
    public async Task WithoutAServerItStaysLocal()
    {
        using var server = new SyncServer();
        var (sync, _, _) = Device(server);

        Assert.Null(await sync.SyncNowAsync());
        Assert.Equal(SyncPhase.NotConnected, sync.Status.Phase);
    }

    [Fact]
    public async Task AfterSigningUpItAsksToJoinThenSyncs()
    {
        using var server = new SyncServer();
        var (sync, bank, _) = Device(server);
        bank.Add(TestData.Bytes(1), TestData.Info(25, "Sparky"));

        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");
        sync.Refresh();
        Assert.Equal(SyncPhase.NeedsJoin, sync.Status.Phase);
        Assert.False(await sync.ServerHasPokemonAsync());

        var report = await sync.JoinAsync(JoinMode.Merge);

        Assert.NotNull(report);
        Assert.Equal(SyncPhase.Idle, sync.Status.Phase);
        Assert.NotNull(sync.Status.LastSynced);
        Assert.Equal("PKForge Sync", sync.Store.Read(s => s.ServerName));
        Assert.True(await sync.ServerHasPokemonAsync());
    }

    [Fact]
    public async Task ItRemembersTheServersName()
    {
        using var server = new SyncServer();
        server.Settings["Sync:ServerName"] = "  Misty's Bank Box  ";
        var (sync, _, _) = Device(server);

        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");

        Assert.Equal("Misty's Bank Box", sync.Store.Read(s => s.ServerName));
        sync.SignOut();
        Assert.Null(sync.Store.Read(s => s.ServerName));
    }

    [Fact]
    public void AnOldStateFileWithAServerKindStillLoads()
    {
        var root = Path.Combine(Path.GetTempPath(), "pkforge-tests", "coordinator-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var store = new SyncStateStore(Path.Combine(root, "sync"));
        store.Update(s => { s.ServerUrl = "http://localhost/"; s.Joined = true; s.Cursor = 7; });
        store.Flush();
        var file = Directory.GetFiles(Path.Combine(root, "sync"), "*.json").Single();
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json.Remove("serverName");
        json["serverKind"] = "cloud";
        File.WriteAllText(file, json.ToJsonString());

        var reopened = new SyncStateStore(Path.Combine(root, "sync"));

        Assert.Equal(("http://localhost/", 7L, (string?)null), reopened.Read(s => (s.ServerUrl, s.Cursor, s.ServerName)));
    }

    [Fact]
    public async Task ARefusedPushShowsTheServersReasonUntilAPushIsAccepted()
    {
        using var server = new SyncServer();
        var (sync, bank, _) = Device(server);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");
        await sync.JoinAsync(JoinMode.Merge);
        server.Writes.Deny("Storage quota reached.");

        bank.Add(TestData.Bytes(1), TestData.Info(25, "Sparky"));
        await sync.SyncNowAsync();

        Assert.Equal(SyncPhase.Idle, sync.Status.Phase);
        Assert.True(sync.Status.WritesDisabled);
        Assert.Equal("Storage quota reached.", sync.Status.WritesDisabledMessage);
        Assert.True(sync.Status.PendingChanges);

        server.Writes.Allow();
        await sync.SyncNowAsync();

        Assert.False(sync.Status.WritesDisabled);
        Assert.Null(sync.Status.WritesDisabledMessage);
        Assert.False(sync.Status.PendingChanges);
        Assert.True(await sync.ServerHasPokemonAsync());
    }

    [Fact]
    public async Task ALocalChangeSyncsShortlyAfter()
    {
        using var server = new SyncServer();
        var (sync, bank, time) = Device(server);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");
        await sync.JoinAsync(JoinMode.Merge);

        var added = bank.Add(TestData.Bytes(1), TestData.Info(25, "Sparky"));
        time.Advance(SyncCoordinator.ChangeMaxWait);
        await sync.WhenIdleAsync();

        var deck = await server.SignInAsync();
        var changes = await server.Client(deck).GetChangesAsync(0, null, 100);
        Assert.Contains(changes.Items, i => i.Id == added.Id.ToString());
    }

    [Fact]
    public async Task ASingleChangePushesOneSecondLaterNotBefore()
    {
        using var server = new SyncServer();
        var counter = new PushCounter();
        var (sync, bank, time) = Device(server, counter);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");
        await sync.JoinAsync(JoinMode.Merge);
        await sync.WhenIdleAsync();
        var before = counter.Pushes;

        bank.Add(TestData.Bytes(1), TestData.Info(25, "Sparky"));
        time.Advance(SyncCoordinator.ChangeDebounce - TimeSpan.FromMilliseconds(1));
        await sync.WhenIdleAsync();
        Assert.Equal(before, counter.Pushes);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await sync.WhenIdleAsync();
        Assert.Equal(before + 1, counter.Pushes);
        Assert.False(bank.HasUnsyncedChanges());
    }

    [Fact]
    public async Task ABurstOfChangesIsPushedWithinTheMaxWaitAndTheRestFollows()
    {
        using var server = new SyncServer();
        var counter = new PushCounter();
        var (sync, bank, time) = Device(server, counter);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");
        await sync.JoinAsync(JoinMode.Merge);
        await sync.WhenIdleAsync();
        var before = counter.Pushes;

        var step = TimeSpan.FromMilliseconds(500);
        var elapsed = TimeSpan.Zero;
        TimeSpan? firstPush = null;
        for (var i = 0; i < 10; i++) // a change every 0.5 s for 5 s
        {
            bank.Add(TestData.Bytes((byte)(i + 1)), TestData.Info(25, "Sparky" + i));
            time.Advance(step);
            elapsed += step;
            await sync.WhenIdleAsync();
            if (firstPush is null && counter.Pushes > before) firstPush = elapsed;
        }
        Assert.NotNull(firstPush);
        Assert.True(firstPush <= SyncCoordinator.ChangeMaxWait, $"first push at {firstPush}");

        time.Advance(SyncCoordinator.ChangeDebounce);
        await sync.WhenIdleAsync();
        Assert.True(counter.Pushes >= before + 2);
        Assert.False(bank.HasUnsyncedChanges());
        var changes = await server.Client(await server.SignInAsync()).GetChangesAsync(0, null, 100);
        Assert.Equal(10, changes.Items.Count(i => i.Kind == ItemKinds.Mon));
    }

    [Fact]
    public async Task AChangeDuringASyncIsSentByAFollowUpRightAfter()
    {
        using var server = new SyncServer();
        var counter = new PushCounter();
        var (sync, bank, time) = Device(server, counter);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");
        await sync.JoinAsync(JoinMode.Merge);
        await sync.WhenIdleAsync();
        var before = counter.Pushes;

        counter.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bank.Add(TestData.Bytes(1), TestData.Info(25, "Sparky"));
        time.Advance(SyncCoordinator.ChangeDebounce);
        await counter.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = bank.Add(TestData.Bytes(2), TestData.Info(4, "Ember")); // while the sync is running
        var release = counter.Hold;
        counter.Hold = null;
        release.SetResult();
        await Task.Delay(50);
        await sync.WhenIdleAsync();

        // No clock time passed since the first sync began: the follow-up needed no debounce wait.
        Assert.Equal(before + 2, counter.Pushes);
        Assert.False(bank.HasUnsyncedChanges());
        var changes = await server.Client(await server.SignInAsync()).GetChangesAsync(0, null, 100);
        Assert.Contains(changes.Items, i => i.Id == second.Id.ToString());
    }

    [Fact]
    public async Task AnUnreachableServerIsRetriedWithGrowingDelays()
    {
        using var server = new SyncServer();
        var outage = new Outage();
        var (sync, bank, time) = Device(server, outage);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");
        await sync.JoinAsync(JoinMode.Merge);

        outage.Down = true;
        bank.Add(TestData.Bytes(1), TestData.Info(25, "Sparky"));
        time.Advance(SyncCoordinator.ChangeMaxWait);
        await sync.WhenIdleAsync();
        Assert.Equal(SyncPhase.Offline, sync.Status.Phase);
        Assert.True(sync.Status.PendingChanges);

        var before = outage.Attempts;
        time.Advance(SyncCoordinator.FirstRetry - TimeSpan.FromSeconds(1));
        await sync.WhenIdleAsync();
        Assert.Equal(before, outage.Attempts);
        time.Advance(TimeSpan.FromSeconds(1));
        await sync.WhenIdleAsync();
        Assert.True(outage.Attempts > before);

        outage.Down = false;
        time.Advance(SyncCoordinator.FirstRetry * 2);
        await sync.WhenIdleAsync();
        Assert.Equal(SyncPhase.Idle, sync.Status.Phase);
        Assert.False(sync.Status.PendingChanges);
    }

    [Fact]
    public async Task NetworkComingBackSyncsAtOnce()
    {
        using var server = new SyncServer();
        var outage = new Outage();
        var (sync, bank, time) = Device(server, outage);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");
        await sync.JoinAsync(JoinMode.Merge);
        outage.Down = true;
        bank.Add(TestData.Bytes(1), TestData.Info(25, "Sparky"));
        time.Advance(SyncCoordinator.ChangeMaxWait);
        await sync.WhenIdleAsync();

        outage.Down = false;
        sync.NetworkAvailable();
        await sync.WhenIdleAsync();

        Assert.Equal(SyncPhase.Idle, sync.Status.Phase);
    }

    [Fact]
    public async Task ARemovedDeviceIsToldItWasSignedOut()
    {
        using var server = new SyncServer();
        var (sync, _, _) = Device(server);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Deck");
        await sync.JoinAsync(JoinMode.Merge);
        var phone = server.Client(await server.SignInAsync(device: "Phone"));
        var deck = (await phone.GetDevicesAsync()).Single(d => d.Name == "Deck");

        await phone.RemoveDeviceAsync(deck.Id);
        await sync.SyncNowAsync();

        Assert.Equal(SyncPhase.SignedOut, sync.Status.Phase);
        Assert.False(string.IsNullOrWhiteSpace(sync.Status.Message));
    }

    [Fact]
    public async Task ResolvingAConflictSendsTheChoice()
    {
        using var server = new SyncServer();
        var (sync, bank, _) = Device(server);
        var shared = bank.Add(TestData.Bytes(1), TestData.Info(25, "Sparky"));
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Deck");
        await sync.JoinAsync(JoinMode.Merge);
        using var phone = new TestDevice(server, await server.SignInAsync(device: "Phone"));
        await phone.JoinAsync();
        phone.Bank.Replace(shared.Id, TestData.Bytes(1), shared.Info with { Nickname = "Phone name" });
        await phone.SyncAsync();
        bank.Replace(shared.Id, TestData.Bytes(1), shared.Info with { Nickname = "Deck name" });

        await sync.SyncNowAsync();
        Assert.Equal(1, sync.Status.Conflicts);
        await sync.ResolveAsync(sync.Store.Read(s => s.Conflicts.Single().Id), ConflictChoice.KeepThisDevice);
        await sync.WhenIdleAsync();
        await phone.SyncAsync();

        Assert.Equal(0, sync.Status.Conflicts);
        Assert.Equal("Deck name", phone.Bank.GetAll().Single().Info.Nickname);
    }

    [Fact]
    public async Task SigningOutKeepsTheBankAndGoesLocal()
    {
        using var server = new SyncServer();
        var (sync, bank, _) = Device(server);
        bank.Add(TestData.Bytes(1), TestData.Info(25, "Sparky"));
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");
        await sync.JoinAsync(JoinMode.Merge);

        sync.SignOut();

        Assert.Equal(SyncPhase.NotConnected, sync.Status.Phase);
        Assert.Single(bank.GetAll());
    }

    [Fact]
    public async Task OtherDevicesChangesArriveWhileTheAppSitsOpen()
    {
        using var server = new SyncServer();
        var (sync, bank, time) = Device(server);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");
        await sync.JoinAsync(JoinMode.Merge);
        using var deck = new TestDevice(server, await server.SignInAsync(device: "Deck"));
        await deck.JoinAsync();
        var arriving = deck.Deposit(7);
        await deck.SyncAsync();

        time.Advance(SyncCoordinator.PollInterval);
        await sync.WhenIdleAsync();

        Assert.Contains(bank.GetAll(), e => e.Id == arriving.Id);
    }

    [Fact]
    public async Task AnAnswerThatIsNotFromASyncServerFailsAndIsRetried()
    {
        using var server = new SyncServer();
        var portal = new NotAServer();
        var (sync, _, time) = Device(server, portal);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");
        await sync.JoinAsync(JoinMode.Merge);

        portal.On = true;
        await sync.SyncNowAsync();
        Assert.NotEqual(SyncPhase.Syncing, sync.Status.Phase);
        Assert.NotEqual(SyncPhase.Idle, sync.Status.Phase);

        var before = portal.Attempts;
        time.Advance(SyncCoordinator.FirstRetry);
        await sync.WhenIdleAsync();
        Assert.True(portal.Attempts > before);
    }

    [Fact]
    public async Task ChangesMadeJustBeforeLeavingTheAppAreSent()
    {
        using var server = new SyncServer();
        var (sync, bank, _) = Device(server);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");
        await sync.JoinAsync(JoinMode.Merge);

        var added = bank.Add(TestData.Bytes(1), TestData.Info(25, "Sparky"));
        sync.Suspend();
        await sync.WhenIdleAsync();

        var changes = await server.Client(await server.SignInAsync()).GetChangesAsync(0, null, 100);
        Assert.Contains(changes.Items, i => i.Id == added.Id.ToString());
    }

    [Fact]
    public async Task AFailedFirstJoinStillAsksToFinishSettingUp()
    {
        using var server = new SyncServer();
        var outage = new Outage();
        var (sync, _, _) = Device(server, outage);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Phone");

        outage.Down = true;
        var report = await sync.JoinAsync(JoinMode.Merge);

        Assert.Null(report);
        Assert.Equal(SyncPhase.NeedsJoin, sync.Status.Phase);
        Assert.False(string.IsNullOrWhiteSpace(sync.Status.Message));
    }

    [Fact]
    public async Task ARemovedDeviceStaysSignedOutAfterARestart()
    {
        using var server = new SyncServer();
        var root = Path.Combine(Path.GetTempPath(), "pkforge-tests", "coordinator-" + Guid.NewGuid().ToString("N"));
        var (sync, _, _) = Device(server, root: root);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Deck");
        await sync.JoinAsync(JoinMode.Merge);
        var phone = server.Client(await server.SignInAsync(device: "Phone"));
        await phone.RemoveDeviceAsync((await phone.GetDevicesAsync()).Single(d => d.Name == "Deck").Id);
        await sync.SyncNowAsync();

        var (restarted, _, _) = Device(server, root: root);

        Assert.Equal(SyncPhase.SignedOut, restarted.Status.Phase);
    }

    [Fact]
    public async Task ALocalChangeSyncsOnItsOwnAfterMovingToAnotherServer()
    {
        using var serverA = new SyncServer();
        using var serverB = new SyncServer();
        var root = Path.Combine(Path.GetTempPath(), "pkforge-tests", "coordinator-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var time = new FakeTimeProvider(TestData.T0);
        var store = new SyncStateStore(Path.Combine(root, "sync"));
        var bankRoot = Path.Combine(root, "bank");
        var bank = new SyncedBankService(new FileBankService(bankRoot), new BankDecor(bankRoot), store, time);
        var a = new Uri("http://server-a/");
        var b = new Uri("http://server-b/");
        HttpClient? clientA = null, clientB = null;
        var connection = new SyncConnection(store, new MemorySecrets(),
            uri => uri.Host == a.Host ? clientA ??= serverA.CreateClient() : clientB ??= serverB.CreateClient());
        using var sync = new SyncCoordinator(connection, bank, store, null, time);
        var first = bank.Add(TestData.Bytes(1), TestData.Info(25, "Sparky"));

        await sync.Connection.SignUpAsync(a, "ash@example.com", SyncServer.Password, "Odin");
        sync.Refresh();
        await sync.ServerHasPokemonAsync();
        await sync.JoinAsync(JoinMode.Merge);
        bank.Move(first.Id, 0, 3);
        time.Advance(SyncCoordinator.ChangeMaxWait);
        await sync.WhenIdleAsync();

        sync.SignOut();
        await sync.Connection.SignUpAsync(b, "ash@example.com", SyncServer.Password, "Odin");
        sync.Refresh();
        Assert.False(await sync.ServerHasPokemonAsync());
        await sync.JoinAsync(JoinMode.Merge);
        await sync.WhenIdleAsync();

        bank.Move(first.Id, 0, 7);
        Assert.True(bank.HasUnsyncedChanges());
        time.Advance(SyncCoordinator.ChangeMaxWait);
        await sync.WhenIdleAsync();

        Assert.False(bank.HasUnsyncedChanges());
        var deck = serverB.Client(await serverB.SignInAsync());
        var mon = (await deck.GetChangesAsync(0, null, 100)).Items.Single(i => i.Id == first.Id.ToString());
        Assert.Contains("\"slot\":7", mon.Value!.Value.GetRawText());
    }

    [Fact]
    public async Task WhileTheAppIsAwayChangesWaitAndOnceBackTheySyncOnTheirOwn()
    {
        // Suspend holds every scheduled sync until Resume: the app must always resume it (a recreated
        // activity never called OnResume, so changes stopped syncing until the app restarted).
        using var server = new SyncServer();
        var (sync, bank, time) = Device(server);
        await sync.Connection.SignUpAsync(Server, "ash@example.com", SyncServer.Password, "Odin");
        await sync.JoinAsync(JoinMode.Merge);
        sync.Suspend();
        await sync.WhenIdleAsync();

        var first = bank.Add(TestData.Bytes(1), TestData.Info(25, "Sparky"));
        time.Advance(SyncCoordinator.ChangeMaxWait);
        await sync.WhenIdleAsync();
        Assert.True(bank.HasUnsyncedChanges());

        sync.Resume();
        await sync.WhenIdleAsync();
        var second = bank.Add(TestData.Bytes(2), TestData.Info(4, "Ember"));
        time.Advance(SyncCoordinator.ChangeMaxWait);
        await sync.WhenIdleAsync();

        Assert.False(bank.HasUnsyncedChanges());
        var changes = await server.Client(await server.SignInAsync()).GetChangesAsync(0, null, 100);
        Assert.Contains(changes.Items, i => i.Id == first.Id.ToString());
        Assert.Contains(changes.Items, i => i.Id == second.Id.ToString());
    }
}
