using System.Text.Json;
using PKForge.Sync.Protocol;

namespace PKForge.Sync;

public enum SyncPhase { NotConnected, NeedsJoin, Idle, Syncing, Offline, SignedOut, UpdateRequired, EmailNotVerified, Paused, Failed }

/// <summary>What the app shows about sync.</summary>
/// <remarks><see cref="WritesDisabledMessage"/> is the server's reason when it isn't accepting this account's changes (null: it is).</remarks>
public sealed record SyncStatus(
    SyncPhase Phase, DateTimeOffset? LastSynced, int Conflicts, bool PendingChanges, string? WritesDisabledMessage, string? Message)
{
    public bool WritesDisabled => WritesDisabledMessage is not null;
}

/// <summary>
/// Runs sync for the app: syncs when it starts or resumes, shortly after a Bank change, when the
/// network returns and when asked; retries failures with growing delays (spec §10); and turns every
/// outcome into a <see cref="SyncStatus"/>. One sync runs at a time.
/// </summary>
public sealed class SyncCoordinator : IDisposable
{
    /// <summary>A local change syncs this long after the last change of a burst...</summary>
    public static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(1);
    /// <summary>...but never later than this after the burst's first change.</summary>
    public static readonly TimeSpan ChangeMaxWait = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(15);
    /// <summary>While the app is in front and idle, other devices' changes are fetched this often.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    private readonly SyncedBankService _bank;
    private readonly IBankSnapshots? _snapshots;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private DateTimeOffset _due;
    private bool _dueFromChange;
    private DateTimeOffset? _burstStart;
    private bool _changedWhileRunning;
    private TimeSpan _retry = TimeSpan.Zero;
    private string? _writesDisabled;
    private bool _suspended;
    private SyncEngine? _engine;
    private string? _engineFor;

    public SyncCoordinator(SyncConnection connection, SyncedBankService bank, SyncStateStore store, IBankSnapshots? snapshots = null, TimeProvider? time = null)
    {
        Connection = connection;
        Store = store;
        _bank = bank;
        _snapshots = snapshots;
        _time = time ?? TimeProvider.System;
        _bank.LocalChanged += (_, _) =>
        {
            Trace?.Invoke("Bank changed here");
            RequestChange();
        };
        Status = Settled(null);
    }

    public SyncConnection Connection { get; }
    public SyncStateStore Store { get; }
    public SyncStatus Status { get; private set; }
    public event EventHandler<SyncStatus>? StatusChanged;

    /// <summary>Diagnostics: what scheduling decided and why (the app writes it to its log).</summary>
    public Action<string>? Trace { get; set; }

    public int LocalPokemonCount => _bank.GetAll().Count;

    /// <summary>Schedules a sync after <paramref name="delay"/>; an earlier request wins. Ignored until connected and joined.</summary>
    public void RequestSync(TimeSpan delay = default) => Schedule($"RequestSync({delay.TotalSeconds}s)", delay, fromChange: false);

    /// <summary>
    /// A local change: syncs <see cref="ChangeDebounce"/> after the last change, but at most
    /// <see cref="ChangeMaxWait"/> after the first change of the burst.
    /// </summary>
    private void RequestChange()
    {
        lock (_gate)
        {
            if (_running.CurrentCount == 0) _changedWhileRunning = true;
            var now = _time.GetUtcNow();
            _burstStart ??= now;
            var due = now + ChangeDebounce;
            var latest = _burstStart.Value + ChangeMaxWait;
            if (due > latest) due = latest;
            Schedule($"Change debounce (burst began {(now - _burstStart.Value).TotalSeconds:0.0#}s ago)", due - now, fromChange: true);
        }
    }

    private void Schedule(string what, TimeSpan delay, bool fromChange)
    {
        if (!Connection.IsConnected || !Store.Read(s => s.Joined && !s.DeviceRemoved))
        {
            Trace?.Invoke($"{what} ignored: connected={Connection.IsConnected} joined={Store.Read(s => s.Joined)} removed={Store.Read(s => s.DeviceRemoved)}");
            return;
        }
        lock (_gate)
        {
            if (_suspended)
            {
                Trace?.Invoke($"{what} ignored: suspended");
                return;
            }
            var due = _time.GetUtcNow() + delay;
            // A change may move its own pending timer later (the debounce); anything else only moves a timer earlier.
            var replaceable = fromChange && _dueFromChange;
            if (_timer is not null && _due <= due && !replaceable)
            {
                Trace?.Invoke($"{what} kept the sync already due at {_due:HH:mm:ss.fff}");
                return;
            }
            Trace?.Invoke($"{what} scheduled for {due:HH:mm:ss.fff} (in {delay.TotalSeconds:0.0#}s)");
            _timer?.Dispose();
            _due = due;
            _dueFromChange = fromChange;
            // Stored before it is started: a timer that fires at once (on this thread, under this lock)
            // must still find itself here to clear, or a dead timer would hold back every later request.
            ITimer? timer = null;
            timer = _time.CreateTimer(_ =>
            {
                lock (_gate)
                {
                    // A timer replaced by an earlier one may still fire once; the replacement runs instead.
                    if (timer is null || !ReferenceEquals(_timer, timer)) return;
                    timer.Dispose();
                    _timer = null;
                    _dueFromChange = false;
                    _burstStart = null;
                }
                Trace?.Invoke("Scheduled sync starting");
                _ = RunAsync(engine => engine.SyncAsync(), CancellationToken.None);
            }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer = timer;
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The app went to the background: no scheduled syncs until <see cref="Resume"/>.</summary>
    public void Suspend()
    {
        Trace?.Invoke("Suspend");
        // Changes made just before leaving are sent now, as far as Android's background window allows.
        if (Connection.IsConnected && Store.Read(s => s.Joined && !s.DeviceRemoved) && _bank.HasUnsyncedChanges())
            _ = RunAsync(engine => engine.SyncAsync(), CancellationToken.None);
        lock (_gate)
        {
            _suspended = true;
            _timer?.Dispose();
            _timer = null;
            _burstStart = null;
        }
        Store.Flush();
    }

    /// <summary>The app is in front again: sync now, then keep polling.</summary>
    public void Resume()
    {
        Trace?.Invoke("Resume");
        lock (_gate) _suspended = false;
        RequestSync();
    }

    /// <summary>The network is back: try now instead of waiting for the next retry.</summary>
    public void NetworkAvailable()
    {
        if (Status.Phase is SyncPhase.Offline or SyncPhase.Failed || Status.PendingChanges) RequestSync();
    }

    /// <summary>Re-reads connection state (after connecting or disconnecting elsewhere).</summary>
    public void Refresh() => Publish(Settled(null));

    public Task<SyncReport?> SyncNowAsync(CancellationToken ct = default) => RunAsync(engine => engine.SyncAsync(ct), ct);

    public Task<SyncReport?> JoinAsync(JoinMode mode, CancellationToken ct = default) => RunAsync(engine => engine.JoinAsync(mode, ct), ct);

    /// <summary>Whether the server's Bank already holds Pokémon (deciding whether joining needs a choice).</summary>
    public async Task<bool> ServerHasPokemonAsync(CancellationToken ct = default)
    {
        var client = Connection.CreateClient();
        long since = 0;
        while (true)
        {
            var page = await client.GetChangesAsync(since, null, 1000, ct).ConfigureAwait(false);
            if (page.Items.Any(i => i.Kind == ItemKinds.Mon && !i.Deleted)) return true;
            if (!page.HasMore) return false;
            since = page.Next;
        }
    }

    public async Task ResolveAsync(Guid conflictId, ConflictChoice choice, CancellationToken ct = default)
    {
        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Engine().ResolveAsync(conflictId, choice, ct).ConfigureAwait(false);
        }
        finally
        {
            _running.Release();
        }
        Publish(Settled(null));
        RequestSync();
    }

    /// <summary>Stops syncing on this device; the Bank stays as it is.</summary>
    public void SignOut()
    {
        Trace?.Invoke("SignOut");
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _burstStart = null;
        }
        Connection.SignOut();
        _engine = null;
        _engineFor = null;
        _retry = TimeSpan.Zero;
        _writesDisabled = null;
        Publish(Settled(null));
    }

    /// <summary>Saves sync state now (the app is going to the background).</summary>
    public void Flush() => Store.Flush();

    /// <summary>Completes when no sync is running (tests, and the app before showing results).</summary>
    public async Task WhenIdleAsync()
    {
        await _running.WaitAsync().ConfigureAwait(false);
        _running.Release();
    }

    public void Dispose()
    {
        lock (_gate) _timer?.Dispose();
        _running.Dispose();
    }

    private async Task<SyncReport?> RunAsync(Func<SyncEngine, Task<SyncReport>> work, CancellationToken ct)
    {
        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Connection.IsConnected)
            {
                Publish(Settled(null));
                return null;
            }
            lock (_gate) _changedWhileRunning = false;
            Trace?.Invoke("Sync running");
            Publish(Status with { Phase = SyncPhase.Syncing, Message = null });
            try
            {
                var report = await work(Engine()).ConfigureAwait(false);
                _retry = TimeSpan.Zero;
                Trace?.Invoke($"Sync done: sent {report.Uploaded}, received {report.Downloaded}");
                var at = _time.GetUtcNow();
                Store.Update(s => s.LastSyncedUtc = at);
                Publish(Settled(report));
                bool followUp;
                lock (_gate) { followUp = _changedWhileRunning; _changedWhileRunning = false; }
                if (followUp) Trace?.Invoke("Bank changed during the sync: syncing again at once");
                RequestSync(followUp ? TimeSpan.Zero : PollInterval);
                return report;
            }
            catch (SyncApiException error) when (error.Status == 401)
            {
                Store.Update(s => s.DeviceRemoved = true);
                Publish(Settled(null));
            }
            catch (SyncApiException error) when (error.Status == 426)
            {
                Fail(SyncPhase.UpdateRequired, error.Message, retry: false);
            }
            catch (SyncApiException error) when (error.Code == ErrorCodes.EmailNotVerified)
            {
                Fail(SyncPhase.EmailNotVerified, "Confirm your email address to start syncing: check your inbox for the link.", retry: true);
            }
            catch (SyncApiException error)
            {
                Fail(SyncPhase.Failed, error.Message, retry: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Publish(Settled(null)); // the person cancelled: nothing failed
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException)
            {
                Fail(SyncPhase.Offline, "Not synced: the server can't be reached. Trying again on its own.", retry: true);
            }
            catch (JsonException)
            {
                Fail(SyncPhase.Failed, "The address answered, but not as a PKForge Sync server (a Wi-Fi sign-in page, perhaps). Trying again on its own.", retry: true);
            }
            catch (Exception error)
            {
                // Anything else still ends the sync cleanly: never stuck on "Syncing", always retried.
                Fail(SyncPhase.Failed, $"Sync failed: {error.Message}", retry: true);
            }
            return null;
        }
        finally
        {
            Store.Flush();
            _running.Release();
        }
    }

    /// <summary>
    /// Publishes a failure. Before the first join the device stays at "finish setting up" (with the reason),
    /// since scheduled syncs only run once joined.
    /// </summary>
    private void Fail(SyncPhase phase, string message, bool retry)
    {
        if (!Store.Read(s => s.Joined))
        {
            Publish(Settled(null) with { Message = message });
            return;
        }
        Publish(Settled(null) with { Phase = phase, Message = message });
        if (retry) ScheduleRetry();
    }

    private void ScheduleRetry()
    {
        _retry = _retry == TimeSpan.Zero ? FirstRetry : TimeSpan.FromTicks(Math.Min(_retry.Ticks * 2, MaxRetry.Ticks));
        RequestSync(_retry);
    }

    private SyncEngine Engine()
    {
        // One engine per connection: two would each run "one sync at a time" on the same state.
        lock (_gate)
        {
            var identity = Store.Read(s => $"{s.ServerUrl}|{s.DeviceId}");
            if (_engine is null || _engineFor != identity)
            {
                _engine = new SyncEngine(Connection.CreateClient(), _bank, Store, _snapshots, _time);
                _engineFor = identity;
            }
            return _engine;
        }
    }

    /// <summary>The resting state: from the connection, the last report (if any) and the stored state.</summary>
    private SyncStatus Settled(SyncReport? report)
    {
        if (!Connection.IsConnected) return new SyncStatus(SyncPhase.NotConnected, null, 0, false, null, null);
        var (joined, last, conflicts, removed) = Store.Read(s => (s.Joined, s.LastSyncedUtc, s.Conflicts.Count, s.DeviceRemoved));
        if (removed)
            return new SyncStatus(SyncPhase.SignedOut, last, conflicts, _bank.HasUnsyncedChanges(), null,
                "This device was removed from the account, or its sign-in expired. Sign in again to keep syncing.");
        if (report is not null)
            _writesDisabled = report.WritesDisabled ? report.WritesDisabledMessage ?? "This server isn't accepting changes for this account." : null;
        if (!joined) return new SyncStatus(SyncPhase.NeedsJoin, last, conflicts, false, null, null);
        var pending = report?.PendingChanges ?? _bank.HasUnsyncedChanges();
        return report?.Paused == true
            ? new SyncStatus(SyncPhase.Paused, last, conflicts, pending, _writesDisabled, "Sync paused: this device's Bank could not be read.")
            : new SyncStatus(SyncPhase.Idle, last, conflicts, pending, _writesDisabled, null);
    }

    private void Publish(SyncStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
