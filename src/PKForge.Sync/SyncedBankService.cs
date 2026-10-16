using System.Text.Json;
using PKForge.Domain;
using PKForge.Infrastructure;
using PKForge.Sync.Protocol;

namespace PKForge.Sync;

/// <summary>Every sync item's current local value, plus each Pokémon's blob id.</summary>
public sealed record LocalValues(IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyDictionary<string, string> Blobs);

/// <summary>A server value to apply locally (null value = released). <see cref="Data"/> carries bytes this device lacks.</summary>
public sealed record RemoteChange(string Key, JsonElement? Value, byte[]? Data = null);

public sealed record ApplyResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped, IReadOnlyList<(Guid Id, int Box, int Slot)> Relocated);

/// <summary>
/// The Bank while connected to a sync server: <see cref="FileBankService"/> underneath, so everything works
/// offline exactly as in Local mode, plus a note of which items each operation touched (pushed together
/// later) and the hooks the sync engine uses to read and apply items.
/// </summary>
public sealed class SyncedBankService : IBankService
{
    private readonly Lock _gate = new();
    private readonly FileBankService _inner;
    private readonly BankDecor _decor;
    private readonly SyncStateStore _state;
    private readonly TimeProvider _time;

    public SyncedBankService(FileBankService inner, BankDecor decor, SyncStateStore state, TimeProvider? time = null)
    {
        _inner = inner;
        _decor = decor;
        _state = state;
        _time = time ?? TimeProvider.System;
        // Box names, wallpapers and settings are edited straight on the decor, outside this service.
        decor.Names.Changed += (_, _) => Record([SyncValues.BoxesKey], _inner.BoxCount);
        decor.Wallpapers.Changed += (_, _) => Record([SyncValues.BoxesKey], _inner.BoxCount);
        decor.Settings.Changed += (_, _) => Record([SyncValues.SettingsKey], _inner.BoxCount);
    }

    /// <summary>Raised after changes from another device were written (any thread).</summary>
    public event EventHandler? RemoteChangesApplied;

    /// <summary>Raised after this device changed the Bank (any thread); the app schedules a sync.</summary>
    public event EventHandler? LocalChanged;

    public BankDecor Decor => _decor;

    /// <summary>The Bank's index could not be read: it looks empty, so nothing may be synced from it.</summary>
    public bool IsReadOnly => _inner.IsReadOnly;

    public IReadOnlyList<BankEntry> GetAll() => _inner.GetAll();
    public int BoxCount => _inner.BoxCount;
    public byte[] GetData(Guid id) => _inner.GetData(id);
    public int MigrationVersion => _inner.MigrationVersion;

    public BankEntry Add(byte[] data, BankEntryInfo info)
    {
        lock (_gate)
        {
            var boxes = _inner.BoxCount;
            var entry = _inner.Add(data, info);
            Record([SyncValues.MonKey(entry.Id)], boxes);
            return entry;
        }
    }

    public IReadOnlyList<BankEntry> AddMany(IReadOnlyList<BankDeposit> deposits)
    {
        lock (_gate)
        {
            var boxes = _inner.BoxCount;
            var entries = _inner.AddMany(deposits);
            Record(entries.Select(e => SyncValues.MonKey(e.Id)), boxes);
            return entries;
        }
    }

    public void Move(Guid id, int box, int slot)
    {
        lock (_gate)
        {
            var boxes = _inner.BoxCount;
            var occupant = _inner.GetAll().FirstOrDefault(e => e.Box == box && e.Slot == slot && e.Id != id);
            _inner.Move(id, box, slot);
            Guid[] moved = occupant is null ? [id] : [id, occupant.Id];
            Record(moved.Select(SyncValues.MonKey), boxes, moved: moved);
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            var existed = _inner.GetAll().Any(e => e.Id == id);
            _inner.Remove(id);
            if (existed) Forget([id]);
        }
    }

    public int RemoveMany(IReadOnlyList<Guid> ids)
    {
        lock (_gate)
        {
            var present = _inner.GetAll().Select(e => e.Id).Intersect(ids).ToList();
            var removed = _inner.RemoveMany(ids);
            Forget(present);
            return removed;
        }
    }

    public void Replace(Guid id, byte[] data, BankEntryInfo info)
    {
        lock (_gate)
        {
            // Forgotten durably before the bytes change: a stale hash would hide this edit for good.
            _state.Update(s => s.BlobHashes.Remove(id));
            _inner.Replace(id, data, info);
            Record([SyncValues.MonKey(id)], _inner.BoxCount);
        }
    }

    public void AddBox()
    {
        lock (_gate)
        {
            _inner.AddBox();
            Record([SyncValues.BoxesKey], _inner.BoxCount);
        }
    }

    public void RemapBoxes(BankBoxRemap remap)
    {
        lock (_gate)
        {
            var before = _inner.GetAll().ToDictionary(e => e.Id, e => e.Box);
            _inner.RemapBoxes(remap);
            var moved = _inner.GetAll().Where(e => before.TryGetValue(e.Id, out var box) && box != e.Box).Select(e => e.Id).ToList();
            Record([SyncValues.BoxesKey, .. moved.Select(SyncValues.MonKey)], _inner.BoxCount, moved: moved);
        }
    }

    public int UpdateInfo(IReadOnlyList<(Guid Id, BankEntryInfo Info)> updates)
    {
        lock (_gate)
        {
            var before = _inner.GetAll().ToDictionary(e => e.Id, e => e.Info);
            var changed = _inner.UpdateInfo(updates);
            RecordInfoChanges(before);
            return changed;
        }
    }

    public int CompleteMigration(int version, IReadOnlyList<(Guid Id, BankEntryInfo Expected, BankEntryInfo Info)> updates)
    {
        lock (_gate)
        {
            var before = _inner.GetAll().ToDictionary(e => e.Id, e => e.Info);
            var changed = _inner.CompleteMigration(version, updates);
            RecordInfoChanges(before);
            return changed;
        }
    }

    public int Place(IReadOnlyList<(Guid Id, int Box, int Slot)> placements)
    {
        lock (_gate)
        {
            var boxes = _inner.BoxCount;
            var before = _inner.GetAll().ToDictionary(e => e.Id, e => (e.Box, e.Slot));
            var count = _inner.Place(placements);
            var moved = _inner.GetAll().Where(e => before.TryGetValue(e.Id, out var at) && at != (e.Box, e.Slot)).Select(e => e.Id).ToList();
            Record(moved.Select(SyncValues.MonKey), boxes, moved: moved);
            return count;
        }
    }

    /// <summary>True when an operation recorded a change that no sync has sent yet.</summary>
    public bool HasUnsyncedChanges() => _state.Read(s => s.Pending.Count > 0);

    /// <summary>Every item's current value, computed under the write lock.</summary>
    public LocalValues CurrentValues()
    {
        lock (_gate) return CurrentValuesLocked();
    }

    /// <summary>
    /// Writes server values. A change is skipped when its item no longer matches the fingerprint in
    /// <paramref name="expected"/> (someone edited it after the engine looked), so a download never
    /// overwrites a fresh edit. Local Pokémon sitting where an arriving one lands move to free slots.
    /// </summary>
    public ApplyResult ApplyRemote(IReadOnlyList<RemoteChange> changes, IReadOnlyDictionary<string, string> expected)
    {
        ApplyResult result;
        lock (_gate)
        {
            var current = CurrentValuesLocked().Values;
            var applied = new List<string>();
            var skipped = new List<string>();
            var upserts = new List<RemoteEntry>();
            var removals = new List<Guid>();
            var arriving = new Dictionary<Guid, MonValue>();
            int? boxCount = null;
            BoxesValue? boxes = null;
            JsonElement? settings = null;

            foreach (var change in changes)
            {
                var now = current.TryGetValue(change.Key, out var value) ? SyncValues.Fingerprint(value) : SyncValues.Deleted;
                if (expected.TryGetValue(change.Key, out var seen) && seen != now)
                {
                    skipped.Add(change.Key);
                    continue;
                }
                var (kind, _) = SyncValues.SplitKey(change.Key);
                switch (kind)
                {
                    case ItemKinds.Mon when change.Value is { } mon:
                        var id = SyncValues.MonId(change.Key);
                        var read = SyncValues.ReadMon(mon);
                        upserts.Add(new RemoteEntry(SyncValues.ToEntry(id, read), change.Data));
                        arriving[id] = read;
                        break;
                    case ItemKinds.Mon:
                        removals.Add(SyncValues.MonId(change.Key));
                        break;
                    case ItemKinds.Boxes when change.Value is { } box:
                        boxes = SyncValues.ReadBoxes(box);
                        boxCount = boxes.Count;
                        break;
                    case ItemKinds.Settings when change.Value is { } values:
                        settings = values;
                        break;
                    default:
                        skipped.Add(change.Key);
                        continue;
                }
                applied.Add(change.Key);
            }

            var relocated = MakeRoom(upserts, removals, boxCount);
            var rewritten = upserts.Where(u => u.Data is not null).Select(u => u.Entry.Id).ToList();
            if (rewritten.Count > 0) _state.Update(s => { foreach (var id in rewritten) s.BlobHashes.Remove(id); });
            _inner.ApplyRemote(upserts, removals, boxCount);
            if (boxes is not null)
            {
                _decor.Names.ReplaceAll(SyncValues.FromIndexed(boxes.Names));
                _decor.Wallpapers.ReplaceAllIds(SyncValues.FromIndexed(boxes.Wallpapers));
            }
            if (settings is { } newSettings) _decor.Settings.Replace(newSettings);

            var movedAt = _time.GetUtcNow();
            _state.Update(s =>
            {
                foreach (var (id, mon) in arriving)
                {
                    s.MovedUtc[id] = mon.MovedUtc;
                    s.BlobHashes[id] = mon.Blob;
                }
                foreach (var id in removals)
                {
                    s.MovedUtc.Remove(id);
                    s.BlobHashes.Remove(id);
                }
                foreach (var (id, _, _) in relocated) s.MovedUtc[id] = movedAt;
                s.RecordChange(relocated.Select(r => SyncValues.MonKey(r.Id)).ToList(), background: false);
            }, persist: false);
            result = new ApplyResult(applied, skipped, relocated);
        }
        if (result.Applied.Count > 0 || result.Relocated.Count > 0) RemoteChangesApplied?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>
    /// Moves a Pokémon to the first free slot (adding a box if needed), skipping <paramref name="avoid"/>:
    /// slots the server reported taken, which may look free here until the next download. Null when it is gone.
    /// </summary>
    public (int Box, int Slot)? MoveToFreeSlot(Guid id, IReadOnlyCollection<(int Box, int Slot)>? avoid = null)
    {
        lock (_gate)
        {
            var entries = _inner.GetAll();
            if (entries.All(e => e.Id != id)) return null;
            var boxes = _inner.BoxCount;
            var count = boxes;
            var occupied = entries.Select(e => (e.Box, e.Slot)).ToHashSet();
            occupied.UnionWith(avoid ?? []);
            var free = FirstFree(occupied, ref count);
            _inner.Place([(id, free.Box, free.Slot)]);
            Record([SyncValues.MonKey(id)], boxes, moved: [id]);
            return free;
        }
    }

    /// <summary>Adds a copy of a Pokémon under a new id (a conflict resolved by keeping both).</summary>
    public Guid KeepCopy(Guid id)
    {
        lock (_gate)
        {
            var boxes = _inner.BoxCount;
            var entry = _inner.GetAll().First(e => e.Id == id);
            var copy = _inner.Add(_inner.GetData(id), entry.Info);
            Record([SyncValues.MonKey(copy.Id)], boxes);
            return copy.Id;
        }
    }

    private LocalValues CurrentValuesLocked()
    {
        var (hashes, moved) = _state.Read(s => (new Dictionary<Guid, string>(s.BlobHashes), new Dictionary<Guid, DateTimeOffset>(s.MovedUtc)));
        var values = new Dictionary<string, JsonElement>();
        var blobs = new Dictionary<string, string>();
        var computed = new Dictionary<Guid, string>();
        foreach (var entry in _inner.GetAll())
        {
            if (!hashes.TryGetValue(entry.Id, out var hash)) computed[entry.Id] = hash = BlobHash.Compute(_inner.GetData(entry.Id));
            var key = SyncValues.MonKey(entry.Id);
            values[key] = SyncValues.Mon(entry, hash, moved.TryGetValue(entry.Id, out var at) ? at : entry.AddedUtc);
            blobs[key] = hash;
        }
        if (computed.Count > 0)
            _state.Update(s => { foreach (var (id, hash) in computed) s.BlobHashes[id] = hash; }, persist: false);
        values[SyncValues.BoxesKey] = SyncValues.Boxes(_inner.BoxCount, _decor.Names.All(), _decor.Wallpapers.AllIds());
        values[SyncValues.SettingsKey] = _decor.Settings.ToJson();
        return new LocalValues(values, blobs);
    }

    /// <summary>Adds moves for local Pokémon that sit where arriving ones land; returns where they went.</summary>
    private List<(Guid Id, int Box, int Slot)> MakeRoom(List<RemoteEntry> upserts, List<Guid> removals, int? boxCount)
    {
        var arrivingIds = upserts.Select(u => u.Entry.Id).ToHashSet();
        var leaving = removals.ToHashSet();
        var landing = upserts.Select(u => (u.Entry.Box, u.Entry.Slot)).ToHashSet();
        var staying = _inner.GetAll().Where(e => !arrivingIds.Contains(e.Id) && !leaving.Contains(e.Id)).ToList();
        var occupied = new HashSet<(int, int)>(landing);
        foreach (var entry in staying.Where(e => !landing.Contains((e.Box, e.Slot)))) occupied.Add((entry.Box, entry.Slot));

        var count = Math.Max(boxCount ?? _inner.BoxCount, 1);
        var relocated = new List<(Guid, int, int)>();
        foreach (var entry in staying.Where(e => landing.Contains((e.Box, e.Slot))))
        {
            var free = FirstFree(occupied, ref count);
            occupied.Add(free);
            upserts.Add(new RemoteEntry(entry with { Box = free.Box, Slot = free.Slot }, null));
            relocated.Add((entry.Id, free.Box, free.Slot));
        }
        return relocated;
    }

    private static (int Box, int Slot) FirstFree(HashSet<(int, int)> occupied, ref int count)
    {
        for (var box = 0; box < count; box++)
        {
            for (var slot = 0; slot < IBankService.SlotsPerBox; slot++)
            {
                if (!occupied.Contains((box, slot))) return (box, slot);
            }
        }
        return (count++, 0);
    }

    private void Record(IEnumerable<string> keys, int boxCountBefore, bool background = false, IEnumerable<Guid>? moved = null)
    {
        var list = keys.ToList();
        if (_inner.BoxCount != boxCountBefore) list.Add(SyncValues.BoxesKey);
        var at = _time.GetUtcNow();
        _state.Update(s =>
        {
            foreach (var id in moved ?? []) s.MovedUtc[id] = at;
            s.RecordChange(list, background);
        }, persist: false);
        LocalChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RecordInfoChanges(Dictionary<Guid, BankEntryInfo> before)
    {
        var changed = _inner.GetAll().Where(e => before.TryGetValue(e.Id, out var info) && info != e.Info).Select(e => SyncValues.MonKey(e.Id)).ToList();
        Record(changed, _inner.BoxCount, background: true);
    }

    private void Forget(IReadOnlyCollection<Guid> ids)
    {
        _state.Update(s =>
        {
            foreach (var id in ids)
            {
                s.BlobHashes.Remove(id);
                s.MovedUtc.Remove(id);
            }
            s.RecordChange(ids.Select(SyncValues.MonKey).ToList(), background: false);
        }, persist: false);
        LocalChanged?.Invoke(this, EventArgs.Empty);
    }
}
