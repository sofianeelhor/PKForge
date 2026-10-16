using System.Text.Json;
using PKForge.Sync.Protocol;

namespace PKForge.Sync;

public sealed partial class SyncEngine
{
    /// <summary>Sends changed items in their groups. Returns true when another round is worthwhile.</summary>
    private async Task<bool> PushAsync(SyncReport report, CancellationToken ct)
    {
        var local = Snapshot();
        var dirty = DirtyKeys(local).ToHashSet();
        if (dirty.Count == 0) return false;

        // An item is a background change only while no person's edit waits for it in another group
        // (read before grouping, which drops groups whose items another group already claimed).
        var userKeys = _store.Read(s => s.Pending.Where(g => !g.Background).SelectMany(g => g.Keys).ToHashSet());

        // Every dirty item belongs to exactly one group; changes no operation recorded form one more group.
        var groups = _store.Update(s =>
        {
            var covered = s.Pending.SelectMany(g => g.Keys).Where(dirty.Contains).ToHashSet();
            var leftovers = dirty.Where(k => !covered.Contains(k)).ToList();
            // No operation tied these together, so they need no atomicity: modest groups keep requests small.
            foreach (var part in leftovers.Chunk(LeftoverGroupSize)) s.RecordChange(part, background: false);
            var claimed = new HashSet<string>();
            var result = new List<(PendingGroup Group, List<string> Keys)>();
            foreach (var group in s.Pending.ToList())
            {
                var keys = group.Keys.Where(k => dirty.Contains(k) && claimed.Add(k)).ToList();
                if (keys.Count == 0) s.Pending.Remove(group);
                else result.Add((group, keys));
            }
            // The box layout goes first, so Pokémon placed in a new box find it on the server.
            var boxesFirst = result.FindIndex(g => g.Keys.Contains(SyncValues.BoxesKey));
            if (boxesFirst > 0)
            {
                var layout = result[boxesFirst];
                result.RemoveAt(boxesFirst);
                result.Insert(0, layout);
            }
            return result;
        });

        await UploadBlobsAsync(local, groups.SelectMany(g => g.Keys), ct).ConfigureAwait(false);

        var (since, epoch, versions) = _store.Read(s => (s.Cursor, s.Epoch, s.Known.ToDictionary(k => k.Key, k => k.Value.Version)));
        var again = false;
        foreach (var chunk in Chunk(groups))
        {
            var request = new PushRequest(
                chunk.Select(g => new ChangeGroup(g.Group.Id, g.Keys.Select(k => Write(k, local, versions)).ToList())).ToList(),
                since, epoch);
            _store.Update(s =>
            {
                foreach (var (group, keys) in chunk)
                {
                    group.Sent = true;
                    foreach (var key in keys) s.AddInFlight(key, local.Fingerprint(key));
                }
            });

            PushResponse response;
            try
            {
                response = await _client.PushAsync(request, ct).ConfigureAwait(false);
            }
            catch (SyncApiException error) when (error.Code == ErrorCodes.WritesDisabled)
            {
                report.WritesDisabled = true;
                report.WritesDisabledMessage = error.Message;
                return false;
            }

            // Every slot the server reported taken, so a relocated Pokémon never lands on another reported one.
            var taken = response.Results.Where(r => r.Status == GroupStatus.SlotTaken)
                .SelectMany(r => r.Items).Where(i => i.Value is not null)
                .Select(i => SyncValues.ReadMon(i.Value!.Value)).Select(m => (m.Box, m.Slot)).ToHashSet();
            // Server versions to take are gathered across the whole answer and written in one batch.
            var takeServer = new List<SyncItem>();
            foreach (var result in response.Results)
            {
                var sent = chunk.First(g => g.Group.Id == result.ClientGroupId);
                again |= HandleResult(result, sent, local, taken, userKeys, takeServer, report);
            }
            if (takeServer.Count > 0)
            {
                var expected = takeServer.ToDictionary(i => SyncValues.Key(i.Kind, i.Id), i => local.Fingerprint(SyncValues.Key(i.Kind, i.Id)));
                report.Downloaded += await ApplyAsync(takeServer, expected, local, report, ct).ConfigureAwait(false);
            }
            _store.Flush();
        }
        return again;
    }

    private bool HandleResult(GroupResult result, (PendingGroup Group, List<string> Keys) sent, LocalSnapshot local,
        IReadOnlyCollection<(int Box, int Slot)> reportedTaken, IReadOnlySet<string> userKeys, List<SyncItem> takeServer, SyncReport report)
    {
        // State is saved once per answer (the caller flushes); fingerprints in flight were saved before sending.
        _store.Update(s => s.Pending.Remove(sent.Group), persist: false);
        switch (result.Status)
        {
            case GroupStatus.Accepted:
                _store.Update(s =>
                {
                    foreach (var item in result.Items) s.Adopt(SyncValues.Key(item.Kind, item.Id), item.Version, SyncValues.Fingerprint(item.Value));
                }, persist: false);
                report.Uploaded += result.Items.Count;
                return false;

            case GroupStatus.Conflict:
                foreach (var item in result.Items)
                    ResolveAutomatically(item, sent.Group.Background && !userKeys.Contains(SyncValues.Key(item.Kind, item.Id)), local, takeServer, report);
                return true;

            case GroupStatus.SlotTaken:
                foreach (var occupant in result.Items)
                {
                    if (occupant.Value is not { } taken) continue;
                    var spot = SyncValues.ReadMon(taken);
                    var ours = sent.Keys.FirstOrDefault(k => SyncValues.IsMon(k) && local.Value(k) is { } mine
                        && SyncValues.ReadMon(mine) is var m && (m.Box, m.Slot) == (spot.Box, spot.Slot));
                    if (ours is null || _bank.MoveToFreeSlot(SyncValues.MonId(ours), reportedTaken) is not { } moved) continue;
                    report.Relocated++;
                    var at = _time.GetUtcNow();
                    _store.Update(s => s.AddActivity(at,
                        $"Moved a Pokémon to box {moved.Box + 1}, slot {moved.Slot + 1}: box {spot.Box + 1}, slot {spot.Slot + 1} was filled on another device."), persist: false);
                }
                return true;

            default:
                if (result.Error == ErrorCodes.MissingBlob) return true; // uploaded again next round
                var refusedAt = _time.GetUtcNow();
                _store.Update(s =>
                {
                    foreach (var key in sent.Keys)
                    {
                        s.Refused[key] = local.Fingerprint(key);
                        s.RefusedCodes[key] = result.Error ?? ErrorCodes.InvalidItem;
                        s.InFlight.Remove(key);
                    }
                    s.AddActivity(refusedAt, $"The server refused a change: {result.Message}");
                });
                return false;
        }
    }

    /// <summary>
    /// Settles a conflicting item without asking when the answer is clear (spec §8.1): our own earlier
    /// write, identical values, background fact rewrites, or a Pokémon only moved on both devices.
    /// Everything else becomes a recorded conflict.
    /// </summary>
    private void ResolveAutomatically(SyncItem server, bool background, LocalSnapshot local, List<SyncItem> takeServer, SyncReport report)
    {
        var key = SyncValues.Key(server.Kind, server.Id);
        var serverFingerprint = SyncValues.Fingerprint(server.Value);
        if (serverFingerprint == local.Fingerprint(key) || _store.Read(s => s.IsInFlight(key, serverFingerprint)))
        {
            _store.Update(s => s.Adopt(key, server.Version, serverFingerprint), persist: false);
            return;
        }

        var mine = local.Value(key);
        if (mine is null && server.Value is null)
        {
            _store.Update(s => s.Adopt(key, server.Version, serverFingerprint), persist: false);
            return;
        }
        if (background && server.Value is not null)
        {
            takeServer.Add(server);
            return;
        }
        if (mine is { } ours && server.Value is { } theirs && SyncValues.IsMon(key) && SyncValues.SameExceptPosition(ours, theirs))
        {
            if (SyncValues.ReadMon(ours).MovedUtc > SyncValues.ReadMon(theirs).MovedUtc)
                _store.Update(s => s.Adopt(key, server.Version, serverFingerprint), persist: false); // our later move is sent again on top
            else
                takeServer.Add(server);
            return;
        }
        RecordConflict(key, mine, server, report);
    }

    private async Task UploadBlobsAsync(LocalSnapshot local, IEnumerable<string> keys, CancellationToken ct)
    {
        var owners = new Dictionary<string, Guid>();
        foreach (var key in keys)
        {
            if (local.Blobs.TryGetValue(key, out var hash)) owners.TryAdd(hash, SyncValues.MonId(key));
        }
        foreach (var batch in owners.Keys.Chunk(500))
        {
            var missing = await _client.MissingBlobsAsync(batch, ct).ConfigureAwait(false);
            foreach (var upload in missing.Chunk(BlobBatch))
                await _client.UploadBlobsAsync(upload.Select(hash => new BlobData(hash, _bank.GetData(owners[hash]))).ToList(), ct).ConfigureAwait(false);
        }
    }

    private static ItemWrite Write(string key, LocalSnapshot local, IReadOnlyDictionary<string, long> versions)
    {
        var (kind, id) = SyncValues.SplitKey(key);
        var baseVersion = versions.TryGetValue(key, out var version) ? version : 0;
        return local.Value(key) is { } value
            ? new ItemWrite(kind, id, baseVersion, value)
            : new ItemWrite(kind, id, baseVersion, null, Deleted: true);
    }

    private static IEnumerable<List<(PendingGroup Group, List<string> Keys)>> Chunk(List<(PendingGroup Group, List<string> Keys)> groups)
    {
        var chunk = new List<(PendingGroup Group, List<string> Keys)>();
        var writes = 0;
        foreach (var group in groups)
        {
            if (chunk.Count > 0 && (chunk.Count == MaxGroupsPerPush || writes + group.Keys.Count > WritesPerRequest))
            {
                yield return chunk;
                chunk = [];
                writes = 0;
            }
            chunk.Add(group);
            writes += group.Keys.Count;
        }
        if (chunk.Count > 0) yield return chunk;
    }
}
