using System.Text.Json;
using PKForge.Sync.Protocol;

namespace PKForge.Sync;

public enum JoinMode { Merge, UseServer }

/// <summary>What one sync did.</summary>
public sealed class SyncReport
{
    public int Uploaded { get; set; }
    public int Downloaded { get; set; }
    public int NewConflicts { get; set; }
    public int Relocated { get; set; }
    public bool Resynced { get; set; }
    /// <summary>The server refused this device's changes (403 writes_disabled); reads still worked.</summary>
    public bool WritesDisabled { get; set; }
    /// <summary>The server's explanation when <see cref="WritesDisabled"/>.</summary>
    public string? WritesDisabledMessage { get; set; }
    /// <summary>Nothing was sent or applied: this device's Bank could not be read (spec §10).</summary>
    public bool Paused { get; set; }
    /// <summary>Changes are still waiting (offline, refused, or more rounds needed).</summary>
    public bool PendingChanges { get; set; }
}

/// <summary>
/// Keeps the local Bank and the server's in step (spec §6). Each sync runs rounds of push-then-pull
/// until nothing is left to send; a 410 answer re-downloads everything and compares (§6.5). Only one
/// sync runs at a time.
/// </summary>
public sealed partial class SyncEngine(SyncClient client, SyncedBankService bank, SyncStateStore store, IBankSnapshots? snapshots = null, TimeProvider? time = null)
{
    public const int MaxRounds = 5;
    /// <summary>More remote releases than this take a Bank snapshot first (spec §8.3).</summary>
    public const int LargeRemoval = 30;
    private const int PageSize = 1000;
    private const int BlobBatch = 200;
    private const int MaxGroupsPerPush = 500;
    /// <summary>Requests carry about this many writes; one bigger group (a whole-Bank remap) still goes alone.</summary>
    private const int WritesPerRequest = 2_000;
    /// <summary>Size of the groups formed from changes no operation recorded.</summary>
    private const int LeftoverGroupSize = 1_000;

    private readonly SyncClient _client = client;
    private readonly SyncedBankService _bank = bank;
    private readonly SyncStateStore _store = store;
    private readonly IBankSnapshots? _snapshots = snapshots;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private enum ResyncMode { Merge, UseServer, Recover }

    /// <summary>First sync of this device: merges with the server's Bank, or replaces this one with it.</summary>
    public Task<SyncReport> JoinAsync(JoinMode mode, CancellationToken ct = default) =>
        RunAsync(mode == JoinMode.UseServer ? ResyncMode.UseServer : ResyncMode.Merge, ct);

    public Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        var (joined, needsResync) = _store.Read(s => (s.Joined, s.NeedsResync));
        if (!joined) throw new InvalidOperationException("Join the server's Bank before syncing.");
        return RunAsync(needsResync ? ResyncMode.Recover : null, ct);
    }

    /// <summary>Applies the person's decision about a conflict; the next sync sends the result.</summary>
    public async Task ResolveAsync(Guid conflictId, ConflictChoice choice, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conflict = _store.Read(s => s.Conflicts.FirstOrDefault(c => c.Id == conflictId))
                ?? throw new InvalidOperationException("That conflict was already resolved.");
            var server = conflict.Server;
            var serverFingerprint = SyncValues.Fingerprint(server.Value);
            switch (choice)
            {
                case ConflictChoice.KeepThisDevice:
                    // Ours stays as it is and is sent with the server's version as its base.
                    _store.Update(s => s.Adopt(conflict.Key, server.Version, serverFingerprint));
                    break;
                case ConflictChoice.KeepBoth when SyncValues.IsMon(conflict.Key) && conflict.Local is not null && server.Value is not null:
                    _bank.KeepCopy(SyncValues.MonId(conflict.Key));
                    await TakeServerVersionAsync(server, ct).ConfigureAwait(false);
                    break;
                case ConflictChoice.KeepBoth:
                    throw new ArgumentException("Keeping both only applies to a Pokémon edited on both devices.", nameof(choice));
                default:
                    await TakeServerVersionAsync(server, ct).ConfigureAwait(false);
                    break;
            }
            _store.Update(s => s.Conflicts.RemoveAll(c => c.Id == conflictId));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncReport> RunAsync(ResyncMode? resync, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var report = new SyncReport();
            if (_bank.IsReadOnly)
            {
                // An unreadable index lists no Pokémon; syncing it would release every one of them everywhere.
                report.Paused = true;
                var at = _time.GetUtcNow();
                _store.Update(s => s.AddActivity(at, "Sync paused: this device's Bank could not be read."));
                return report;
            }
            if (resync is { } mode) await ResyncAsync(mode, report, ct).ConfigureAwait(false);
            for (var round = 0; round < MaxRounds; round++)
            {
                try
                {
                    var again = await PushAsync(report, ct).ConfigureAwait(false);
                    await PullAsync(report, ct).ConfigureAwait(false);
                    if (!again) break;
                }
                catch (SyncApiException error) when (error.Code == ErrorCodes.ResyncRequired)
                {
                    await ResyncAsync(ResyncMode.Recover, report, ct).ConfigureAwait(false);
                }
            }
            report.PendingChanges = DirtyKeys(Snapshot(), includeRefused: true).Count > 0;
            return report;
        }
        finally
        {
            _store.Flush();
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-downloads the whole Bank and compares it with this device's (spec §6.3, §6.5). Items unchanged
    /// here since the last sync take the server's version; items changed here become conflicts (Merge and
    /// Recover) or are replaced (UseServer); Pokémon the server lacks stay and are uploaded as new.
    /// </summary>
    private async Task ResyncAsync(ResyncMode mode, SyncReport report, CancellationToken ct)
    {
        _store.Update(s => s.NeedsResync = true);
        var (latest, cursor, epoch) = await DownloadAllAsync(0, null, ct).ConfigureAwait(false);
        if (mode == ResyncMode.UseServer) _snapshots?.Take("Before replacing this Bank with the server's.");

        var local = Snapshot();
        var (oldKnown, held) = _store.Read(s => (
            s.Known.ToDictionary(k => k.Key, k => k.Value.Fingerprint),
            s.Conflicts.Select(c => c.Key).ToHashSet()));
        var adopted = new List<SyncItem>();
        var toApply = new List<SyncItem>();
        var expected = new Dictionary<string, string>();
        foreach (var (key, item) in latest)
        {
            if (held.Contains(key)) continue;
            var serverFingerprint = SyncValues.Fingerprint(item.Value);
            var localFingerprint = local.Fingerprint(key);
            if (serverFingerprint == localFingerprint)
            {
                adopted.Add(item);
                continue;
            }
            var changedHere = mode != ResyncMode.UseServer
                && localFingerprint != oldKnown.GetValueOrDefault(key, SyncValues.Deleted);
            var serverWins = !changedHere || (mode == ResyncMode.Merge && !SyncValues.IsMon(key));
            if (serverWins)
            {
                toApply.Add(item);
                expected[key] = localFingerprint;
            }
            else
            {
                RecordConflict(key, local.Value(key), item, report);
            }
        }
        if (mode == ResyncMode.UseServer)
        {
            foreach (var key in local.Values.Keys.Where(k => SyncValues.IsMon(k) && !latest.ContainsKey(k)))
            {
                var (kind, id) = SyncValues.SplitKey(key);
                toApply.Add(new SyncItem(kind, id, 0, true, null));
                expected[key] = local.Fingerprint(key);
            }
        }

        _store.Update(s =>
        {
            s.Known.Clear();
            s.Behind.Clear();
            s.InFlight.Clear();
            if (mode == ResyncMode.UseServer) s.Pending.Clear();
            foreach (var item in adopted) s.Adopt(SyncValues.Key(item.Kind, item.Id), item.Version, SyncValues.Fingerprint(item.Value));
        });
        var applied = await ApplyAsync(toApply, expected, local, report, ct).ConfigureAwait(false);
        _store.Update(s =>
        {
            s.Cursor = cursor;
            s.Epoch = epoch;
            s.Joined = true;
            s.NeedsResync = false;
        });
        report.Resynced = true;
        report.Downloaded += applied;
    }

    private async Task TakeServerVersionAsync(SyncItem server, CancellationToken ct)
    {
        var local = Snapshot();
        var changes = await PrepareAsync([server], local, ct).ConfigureAwait(false);
        var result = _bank.ApplyRemote(changes, new Dictionary<string, string>());
        if (result.Applied.Count > 0)
            _store.Update(s => s.Adopt(SyncValues.Key(server.Kind, server.Id), server.Version, SyncValues.Fingerprint(server.Value)));
    }

    private void RecordConflict(string key, JsonElement? local, SyncItem server, SyncReport report)
    {
        var kind = (server.Kind, local is not null, server.Value is not null) switch
        {
            (ItemKinds.Boxes, _, _) => ConflictKind.Boxes,
            (ItemKinds.Settings, _, _) => ConflictKind.Settings,
            (_, true, true) => ConflictKind.BothEdited,
            (_, true, false) => ConflictKind.EditedHereReleasedThere,
            _ => ConflictKind.ReleasedHereEditedThere,
        };
        var found = _time.GetUtcNow();
        _store.Update(s =>
        {
            s.Conflicts.RemoveAll(c => c.Key == key);
            s.Conflicts.Add(new SyncConflict(Guid.NewGuid(), key, kind, local, server, found));
            s.InFlight.Remove(key);
            s.Behind.Remove(key);
        });
        report.NewConflicts++;
    }

    /// <summary>Items whose local value differs from what was last synced (not held by a conflict; refused values only when asked).</summary>
    private List<string> DirtyKeys(LocalSnapshot local, bool includeRefused = false) => _store.Read(s =>
    {
        var keys = new HashSet<string>(local.Values.Keys);
        keys.UnionWith(s.Known.Keys);
        var held = s.Conflicts.Select(c => c.Key).ToHashSet();
        return keys.Where(key =>
        {
            var fingerprint = local.Fingerprint(key);
            if (held.Contains(key)) return false;
            if (fingerprint == (s.Known.TryGetValue(key, out var known) ? known.Fingerprint : SyncValues.Deleted)) return false;
            return includeRefused || !(s.Refused.TryGetValue(key, out var refused) && refused == fingerprint);
        }).ToList();
    });

    private LocalSnapshot Snapshot() => new(_bank.CurrentValues());

    /// <summary>The local values at one moment, with fingerprints computed once.</summary>
    private sealed class LocalSnapshot(LocalValues values)
    {
        private readonly Dictionary<string, string> _fingerprints = [];

        public IReadOnlyDictionary<string, JsonElement> Values => values.Values;
        public IReadOnlyDictionary<string, string> Blobs => values.Blobs;

        public JsonElement? Value(string key) => Values.TryGetValue(key, out var value) ? value : null;

        public string Fingerprint(string key) =>
            _fingerprints.TryGetValue(key, out var known) ? known : _fingerprints[key] = SyncValues.Fingerprint(Value(key));
    }
}
