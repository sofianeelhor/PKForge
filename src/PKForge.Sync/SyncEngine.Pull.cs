using PKForge.Sync.Protocol;

namespace PKForge.Sync;

public sealed partial class SyncEngine
{
    /// <summary>Downloads every change since the cursor and applies what this device has not changed itself.</summary>
    private async Task PullAsync(SyncReport report, CancellationToken ct)
    {
        var (since, epoch, behind) = _store.Read(s => (s.Cursor, s.Epoch, s.Behind.Values.ToList()));
        var (latest, cursor, newEpoch) = await DownloadAllAsync(since, epoch, ct).ConfigureAwait(false);
        foreach (var item in behind)
        {
            var key = SyncValues.Key(item.Kind, item.Id);
            if (!latest.TryGetValue(key, out var newer) || newer.Version < item.Version) latest[key] = item;
        }

        var local = Snapshot();
        var (known, held) = _store.Read(s => (s.Known.ToDictionary(k => k.Key, k => k.Value), s.Conflicts.Select(c => c.Key).ToHashSet()));
        var adopted = new List<SyncItem>();
        var stillBehind = new List<SyncItem>();
        var refreshed = new List<SyncItem>();
        var toApply = new List<SyncItem>();
        var expected = new Dictionary<string, string>();
        foreach (var (key, item) in latest)
        {
            var serverFingerprint = SyncValues.Fingerprint(item.Value);
            var localFingerprint = local.Fingerprint(key);
            if (held.Contains(key)) refreshed.Add(item);
            else if (serverFingerprint == localFingerprint) adopted.Add(item);
            else if (localFingerprint != (known.TryGetValue(key, out var k) ? k.Fingerprint : SyncValues.Deleted)) stillBehind.Add(item);
            else
            {
                toApply.Add(item);
                expected[key] = localFingerprint;
            }
        }

        _store.Update(s =>
        {
            foreach (var item in adopted) s.Adopt(SyncValues.Key(item.Kind, item.Id), item.Version, SyncValues.Fingerprint(item.Value));
            foreach (var item in stillBehind) s.Behind[SyncValues.Key(item.Kind, item.Id)] = item;
            foreach (var item in refreshed)
            {
                var key = SyncValues.Key(item.Kind, item.Id);
                var index = s.Conflicts.FindIndex(c => c.Key == key);
                if (index >= 0 && s.Conflicts[index].Server.Version < item.Version) s.Conflicts[index] = s.Conflicts[index] with { Server = item };
            }
        });

        var removals = toApply.Count(i => SyncValues.IsMon(SyncValues.Key(i.Kind, i.Id)) && i.Value is null && local.Values.ContainsKey(SyncValues.Key(i.Kind, i.Id)));
        if (removals > LargeRemoval) _snapshots?.Take($"Before releasing {removals} Pokémon released on another device.");
        report.Downloaded += await ApplyAsync(toApply, expected, local, report, ct).ConfigureAwait(false);
        _store.Update(s =>
        {
            s.Cursor = cursor;
            s.Epoch = newEpoch;
        });
    }

    /// <summary>Every change after <paramref name="since"/>, newest version per item.</summary>
    private async Task<(Dictionary<string, SyncItem> Latest, long Cursor, string Epoch)> DownloadAllAsync(long since, string? epoch, CancellationToken ct)
    {
        var latest = new Dictionary<string, SyncItem>();
        var cursor = since;
        var currentEpoch = epoch ?? "";
        while (true)
        {
            var page = await _client.GetChangesAsync(cursor, cursor > 0 ? epoch : null, PageSize, ct).ConfigureAwait(false);
            foreach (var item in page.Items) latest[SyncValues.Key(item.Kind, item.Id)] = item;
            cursor = page.Next;
            currentEpoch = page.Epoch;
            epoch = page.Epoch;
            if (!page.HasMore) break;
        }
        return (latest, cursor, currentEpoch);
    }

    /// <summary>Writes server items locally (with any bytes this device lacks); returns how many were applied.</summary>
    private async Task<int> ApplyAsync(List<SyncItem> items, Dictionary<string, string> expected, LocalSnapshot local, SyncReport report, CancellationToken ct)
    {
        if (items.Count == 0) return 0;
        var changes = await PrepareAsync(items, local, ct).ConfigureAwait(false);
        var result = _bank.ApplyRemote(changes, expected);
        var byKey = items.ToDictionary(i => SyncValues.Key(i.Kind, i.Id));
        // A Pokémon is remembered as this device renders it: facts a newer app version added (which this
        // one cannot store) must not look like a local edit and be stripped from the server.
        var rendered = result.Applied.Any(k => SyncValues.IsMon(k) && byKey[k].Value is not null) ? _bank.CurrentValues().Values : null;
        var at = _time.GetUtcNow();
        _store.Update(s =>
        {
            foreach (var key in result.Applied)
            {
                var item = byKey[key];
                var fingerprint = SyncValues.IsMon(key) && rendered is not null && rendered.TryGetValue(key, out var mine)
                    ? SyncValues.Fingerprint(mine)
                    : SyncValues.Fingerprint(item.Value);
                s.Adopt(key, item.Version, fingerprint);
            }
            foreach (var key in result.Skipped) s.Behind[key] = byKey[key];
            foreach (var (_, box, slot) in result.Relocated)
                s.AddActivity(at, $"Moved a Pokémon to box {box + 1}, slot {slot + 1}: another device put one where it was.");
        });
        report.Relocated += result.Relocated.Count;
        return result.Applied.Count;
    }

    /// <summary>Server items as local changes, downloading (and checking) bytes for Pokémon this device does not hold.</summary>
    private async Task<List<RemoteChange>> PrepareAsync(IReadOnlyList<SyncItem> items, LocalSnapshot local, CancellationToken ct)
    {
        var needed = new HashSet<string>();
        foreach (var item in items)
        {
            if (item.Kind != ItemKinds.Mon || item.Value is not { } value) continue;
            var blob = SyncValues.ReadMon(value).Blob;
            if (!local.Blobs.TryGetValue(SyncValues.Key(item.Kind, item.Id), out var held) || held != blob) needed.Add(blob);
        }
        var bytes = new Dictionary<string, byte[]>();
        foreach (var batch in needed.Chunk(BlobBatch))
        {
            foreach (var blob in await _client.DownloadBlobsAsync(batch, ct).ConfigureAwait(false))
            {
                if (BlobHash.Compute(blob.Data) != blob.Hash) throw new InvalidDataException("A Pokémon's bytes arrived damaged.");
                bytes[blob.Hash] = blob.Data;
            }
        }
        if (needed.Any(hash => !bytes.ContainsKey(hash))) throw new InvalidDataException("The server is missing a Pokémon's bytes.");

        return items.Select(item =>
        {
            byte[]? data = null;
            if (item.Kind == ItemKinds.Mon && item.Value is { } value) bytes.TryGetValue(SyncValues.ReadMon(value).Blob, out data);
            return new RemoteChange(SyncValues.Key(item.Kind, item.Id), item.Value, data);
        }).ToList();
    }
}
