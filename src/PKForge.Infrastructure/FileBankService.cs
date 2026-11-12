using System.Text.Json;
using System.Text.Json.Serialization;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// Durable bank store: one raw .bin per entity plus an atomically-written JSON index
/// (tmp → rename, previous index kept as .bak). Boxes are 30 slots and auto-grow.
/// </summary>
public sealed class FileBankService : IBankService
{
    /// <summary>Alias of <see cref="IBankService.SlotsPerBox"/>; the vault shape lives in the contract.</summary>
    public const int SlotsPerBox = IBankService.SlotsPerBox;

    private readonly string _root;
    private readonly Lock _gate = new();
    private List<BankEntry> _entries;
    private int _boxCount;
    private int _migrationVersion;
    // Locked boxes (BankBoxLocks): stored in the index itself so they travel with the bank.
    private HashSet<int> _locked;
    // Box groups (BankBoxGroups): in the index too, for the same reason.
    private IReadOnlyList<BankBoxGroup> _groups;
    // Set when an index exists on disk but neither it nor its backup could be read: the bank
    // then stays read-only rather than overwrite the real index with an empty fallback.
    private readonly bool _indexUnreadable;

    public FileBankService(string rootDirectory)
    {
        _root = rootDirectory;
        Directory.CreateDirectory(_root);
        (_entries, _boxCount, _migrationVersion, _locked, _groups, _indexUnreadable) = LoadIndex();
    }

    public IReadOnlyList<BankEntry> GetAll()
    {
        lock (_gate) return _entries.ToList();
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public int BoxCount
    {
        get { lock (_gate) return _boxCount; }
    }

    public BankEntry Add(byte[] data, BankEntryInfo info)
    {
        lock (_gate)
        {
            EnsureWritable();
            return Commit(() =>
            {
                var (box, slot) = FirstEmpty();
                var entry = new BankEntry(Guid.NewGuid(), box, slot, info, DateTimeOffset.UtcNow);
                File.WriteAllBytes(DataPath(entry.Id), data);
                _entries.Add(entry);
                try { SaveIndex(); }
                catch
                {
                    try { File.Delete(DataPath(entry.Id)); }
                    catch { /* an orphan .bin is harmless: the index never lists it */ }
                    throw;
                }
                return entry;
            });
        }
    }

    public IReadOnlyList<BankEntry> AddMany(IReadOnlyList<BankDeposit> deposits)
    {
        lock (_gate)
        {
            if (deposits.Count == 0) return [];
            EnsureWritable();

            // Every chosen slot is checked before a byte is written: a rejected batch changes nothing.
            var occupied = _entries.Select(e => (e.Box, e.Slot)).ToHashSet();
            foreach (var deposit in deposits.Where(d => d.Box >= 0 || d.Slot >= 0))
            {
                if (deposit.Box < 0 || deposit.Slot < 0 || deposit.Slot >= SlotsPerBox)
                    throw new ArgumentOutOfRangeException(nameof(deposits), "Box or slot out of range.");
                if (_locked.Contains(deposit.Box))
                    throw new InvalidOperationException(BankBoxLocks.Refusal(deposit.Box));
                if (!occupied.Add((deposit.Box, deposit.Slot)))
                    throw new InvalidOperationException("A deposit's slot is already taken.");
            }

            var written = new List<Guid>(deposits.Count);
            try
            {
                return Commit(() =>
                {
                    var added = new List<BankEntry>(deposits.Count);
                    var (box, slot) = (0, 0);
                    foreach (var deposit in deposits)
                    {
                        var at = (deposit.Box, deposit.Slot);
                        if (deposit.Box < 0)
                        {
                            // The free-slot scan resumes where the previous deposit landed and
                            // steps over locked boxes, which never fill up behind the user's back.
                            while (occupied.Contains((box, slot)) || _locked.Contains(box))
                                (box, slot) = slot + 1 == SlotsPerBox ? (box + 1, 0) : (box, slot + 1);
                            occupied.Add((box, slot));
                            at = (box, slot);
                        }
                        var entry = new BankEntry(Guid.NewGuid(), at.Box, at.Slot, deposit.Info, DateTimeOffset.UtcNow);
                        written.Add(entry.Id);
                        File.WriteAllBytes(DataPath(entry.Id), deposit.Data);
                        _entries.Add(entry);
                        _boxCount = Math.Max(_boxCount, at.Box + 1);
                        added.Add(entry);
                    }
                    SaveIndex();
                    return added;
                });
            }
            catch
            {
                foreach (var id in written)
                {
                    try { File.Delete(DataPath(id)); }
                    catch { /* an orphan .bin is harmless: the index never lists it */ }
                }
                throw;
            }
        }
    }

    public byte[] GetData(Guid id)
    {
        lock (_gate)
        {
            if (_entries.All(e => e.Id != id))
                throw new InvalidOperationException("Unknown bank entry.");
            return File.ReadAllBytes(DataPath(id));
        }
    }

    public void Move(Guid id, int box, int slot)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) throw new InvalidOperationException("Unknown bank entry.");
            EnsureWritable();
            var occupant = _entries.FindIndex(e => e.Box == box && e.Slot == slot);
            var moving = _entries[index];
            if ((moving.Box, moving.Slot) == (box, slot)) return;
            if (_locked.Contains(moving.Box)) throw new InvalidOperationException(BankBoxLocks.Refusal(moving.Box));
            if (_locked.Contains(box)) throw new InvalidOperationException(BankBoxLocks.Refusal(box));
            Commit(() =>
            {
                if (occupant >= 0 && occupant != index)
                {
                    // Swap: the occupant takes the mover's old place.
                    _entries[occupant] = _entries[occupant] with { Box = moving.Box, Slot = moving.Slot };
                }
                _entries[index] = moving with { Box = box, Slot = slot };
                _boxCount = Math.Max(_boxCount, box + 1);
                SaveIndex();
                return 0;
            });
        }
    }

    public void Replace(Guid id, byte[] data, BankEntryInfo info)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) throw new InvalidOperationException("Unknown bank entry.");
            EnsureWritable();
            // The new bytes replace the old ones whole (a crash leaves one or the other), and go
            // back if the index cannot follow, so bytes and facts never disagree.
            var previous = File.ReadAllBytes(DataPath(id));
            AtomicFile.Write(DataPath(id), data);
            try
            {
                Commit(() =>
                {
                    _entries[index] = _entries[index] with { Info = info };
                    SaveIndex();
                    return 0;
                });
            }
            catch
            {
                try { AtomicFile.Write(DataPath(id), previous); }
                catch { /* the index still describes the old Pokémon; the new bytes are what is on disk */ }
                throw;
            }
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
            if (_locked.Contains(_entries[index].Box))
                throw new InvalidOperationException(BankBoxLocks.Refusal(_entries[index].Box));
            EnsureWritable();
            Commit(() =>
            {
                _entries.RemoveAt(index);
                SaveIndex();
                return 0;
            });
            // Bytes go only once the index no longer lists them.
            try { File.Delete(DataPath(id)); }
            catch { /* index is authoritative; orphan bytes are harmless */ }
        }
    }

    public int RemoveMany(IReadOnlyList<Guid> ids)
    {
        lock (_gate)
        {
            if (ids.Count == 0) return 0;
            var wanted = ids.ToHashSet();
            // Entries in a locked box stay: the count returned tells the caller who left.
            wanted.ExceptWith(_entries.Where(e => _locked.Contains(e.Box)).Select(e => e.Id));
            var releasing = _entries.Where(e => wanted.Contains(e.Id)).Select(e => e.Id).ToList();
            if (releasing.Count == 0) return 0;
            EnsureWritable();
            // All or nothing: a failed index write leaves every entry (and its bytes) in place.
            Commit(() =>
            {
                _entries.RemoveAll(e => wanted.Contains(e.Id));
                SaveIndex();
                return 0;
            });
            foreach (var id in releasing)
            {
                try { File.Delete(DataPath(id)); }
                catch { /* index is authoritative; orphan bytes are harmless */ }
            }
            return releasing.Count;
        }
    }

    public int Place(IReadOnlyList<(Guid Id, int Box, int Slot)> placements)
    {
        lock (_gate)
        {
            if (placements.Count == 0) return 0;

            // Validate the whole batch before touching anything: a rejected batch must leave
            // the vault exactly as it was.
            var rows = new Dictionary<Guid, int>(placements.Count);
            var targets = new HashSet<(int Box, int Slot)>();
            foreach (var (id, box, slot) in placements)
            {
                if (!rows.TryAdd(id, _entries.FindIndex(e => e.Id == id)) || rows[id] < 0)
                    throw new InvalidOperationException("Place requires distinct, known bank entries.");
                if (box < 0 || slot < 0 || slot >= SlotsPerBox)
                    throw new ArgumentOutOfRangeException(nameof(placements), "Box or slot out of range.");
                if (!targets.Add((box, slot)))
                    throw new InvalidOperationException("Two entries would share one slot.");
            }
            foreach (var (_, box, slot) in placements)
            {
                var occupant = _entries.FirstOrDefault(e => e.Box == box && e.Slot == slot);
                if (occupant is not null && !rows.ContainsKey(occupant.Id))
                    throw new InvalidOperationException("Target slot is held by an entry that is not moving.");
            }
            if (BankBoxLocks.FirstTouched(placements, _entries, _locked) is { } locked)
                throw new InvalidOperationException(BankBoxLocks.Refusal(locked));

            EnsureWritable();
            return Commit(() =>
            {
                var moved = 0;
                foreach (var (id, box, slot) in placements)
                {
                    var index = rows[id];
                    var entry = _entries[index];
                    if (entry.Box == box && entry.Slot == slot) continue;
                    _entries[index] = entry with { Box = box, Slot = slot };
                    moved++;
                }
                _boxCount = Math.Max(_boxCount, placements.Max(p => p.Box) + 1);
                SaveIndex();
                return moved;
            });
        }
    }

    public int UpdateInfo(IReadOnlyList<(Guid Id, BankEntryInfo Info)> updates)
    {
        lock (_gate)
        {
            if (!updates.Any(u => _entries.FindIndex(e => e.Id == u.Id) is var i && i >= 0 && _entries[i].Info != u.Info))
                return 0;
            EnsureWritable();
            return Commit(() =>
            {
                var changed = 0;
                foreach (var (id, info) in updates)
                {
                    var index = _entries.FindIndex(e => e.Id == id);
                    if (index < 0 || _entries[index].Info == info) continue;
                    _entries[index] = _entries[index] with { Info = info };
                    changed++;
                }
                SaveIndex();
                return changed;
            });
        }
    }

    public int MigrationVersion
    {
        get { lock (_gate) return _migrationVersion; }
    }

    public int CompleteMigration(int version, IReadOnlyList<(Guid Id, BankEntryInfo Expected, BankEntryInfo Info)> updates)
    {
        lock (_gate)
        {
            if (version <= _migrationVersion) return 0;
            EnsureWritable();
            var previous = _migrationVersion;
            try
            {
                return Commit(() =>
                {
                    var changed = 0;
                    foreach (var (id, expected, info) in updates)
                    {
                        var index = _entries.FindIndex(e => e.Id == id);
                        if (index < 0 || _entries[index].Info != expected || expected == info) continue;
                        _entries[index] = _entries[index] with { Info = info };
                        changed++;
                    }
                    _migrationVersion = version;
                    SaveIndex();
                    return changed;
                });
            }
            catch
            {
                _migrationVersion = previous;
                throw;
            }
        }
    }

    public void AddBox()
    {
        lock (_gate)
        {
            EnsureWritable();
            Commit(() =>
            {
                _boxCount++;
                SaveIndex();
                return 0;
            });
        }
    }

    public void RemapBoxes(BankBoxRemap remap)
    {
        ArgumentNullException.ThrowIfNull(remap);
        lock (_gate)
        {
            EnsureWritable();
            remap.Validate(_boxCount);
            if (_entries.FirstOrDefault(e => remap.Map(e.Box) is null) is { } stranded)
                throw new InvalidOperationException($"Box {stranded.Box + 1} is not empty.");
            // A locked box can change position (it moves whole) but cannot be deleted.
            if (_locked.FirstOrDefault(box => remap.Map(box) is null, -1) is var gone and >= 0)
                throw new InvalidOperationException(BankBoxLocks.Refusal(gone));
            if (remap.IsIdentity) return;
            Commit(() =>
            {
                for (var i = 0; i < _entries.Count; i++)
                    _entries[i] = _entries[i] with { Box = remap.Map(_entries[i].Box)!.Value };
                _boxCount = remap.NewCount;
                _locked = BankBoxLocks.Remap(_locked, remap);
                _groups = BankBoxGroups.Remap(_groups, remap);
                SaveIndex();
                return 0;
            });
        }
    }

    public IReadOnlySet<int> LockedBoxes
    {
        get { lock (_gate) return _locked.ToHashSet(); }
    }

    public void SetBoxLocked(int box, bool locked)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(box);
        lock (_gate)
        {
            if (_locked.Contains(box) == locked) return;
            EnsureWritable();
            Commit(() =>
            {
                if (locked) _locked.Add(box);
                else _locked.Remove(box);
                SaveIndex();
                return 0;
            });
        }
    }

    public IReadOnlyList<BankBoxGroup> BoxGroups
    {
        get { lock (_gate) return _groups; }
    }

    public void SetBoxGroups(IReadOnlyList<BankBoxGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        lock (_gate)
        {
            var tidy = BankBoxGroups.Normalize(groups);
            EnsureWritable();
            Commit(() =>
            {
                _groups = tidy;
                SaveIndex();
                return 0;
            });
        }
    }

    private (int Box, int Slot) FirstEmpty()
    {
        var occupied = _entries.Select(e => (e.Box, e.Slot)).ToHashSet();
        for (var box = 0; box < _boxCount; box++)
        {
            if (_locked.Contains(box)) continue; // a locked box never takes an unplaced deposit
            for (var slot = 0; slot < SlotsPerBox; slot++)
            {
                if (!occupied.Contains((box, slot)))
                    return (box, slot);
            }
        }
        _boxCount++;
        while (_locked.Contains(_boxCount - 1)) _boxCount++; // a stale lock past the end
        return (_boxCount - 1, 0);
    }

    private string DataPath(Guid id) => Path.Combine(_root, id.ToString("N") + ".bin");
    private string IndexPath => Path.Combine(_root, "index.json");

    // MigrationVersion, LockedBoxes and BoxGroups are absent from older indexes and read as
    // 0 / null (no locks, no groups); older app versions ignore all three, so none needs a migration.
    private sealed record IndexFile(int BoxCount, List<BankEntry> Entries, int MigrationVersion = 0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<int>? LockedBoxes = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<IndexGroup>? BoxGroups = null);

    /// <summary>A group as the index stores it: its name and its boxes (0-based, like entries).</summary>
    private sealed record IndexGroup(string Name, List<int> Boxes);

    /// <summary>Runs one mutation of the in-memory index (the caller holds the gate); if it throws,
    /// typically because the index write failed, memory is put back to match the disk.</summary>
    private T Commit<T>(Func<T> mutate)
    {
        var entries = _entries.ToList();
        var boxCount = _boxCount;
        var locked = _locked.ToHashSet();
        var groups = _groups;
        try { return mutate(); }
        catch
        {
            _entries = entries;
            _boxCount = boxCount;
            _locked = locked;
            _groups = groups;
            throw;
        }
    }

    private void EnsureWritable()
    {
        if (_indexUnreadable)
            throw new InvalidOperationException("The bank index could not be read, so the bank is read-only to protect it. Check the bank folder's index.json.");
    }

    private void SaveIndex()
    {
        EnsureWritable();
        var json = JsonSerializer.Serialize(new IndexFile(_boxCount, _entries, _migrationVersion,
            _locked.Count == 0 ? null : [.. _locked.Order()],
            _groups.Count == 0 ? null : [.. _groups.Select(g => new IndexGroup(g.Name, [.. g.Boxes]))]));
        if (File.Exists(IndexPath))
            File.Copy(IndexPath, IndexPath + ".bak", overwrite: true);
        AtomicFile.WriteText(IndexPath, json, IndexPath + ".tmp");
    }

    private (List<BankEntry>, int, int, HashSet<int>, IReadOnlyList<BankBoxGroup>, bool Unreadable) LoadIndex()
    {
        var anyIndex = false;
        foreach (var candidate in new[] { IndexPath, IndexPath + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                anyIndex = true;
                var loaded = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(candidate));
                // Every index this service writes has an entry list: a file without one (another
                // JSON written over it) is not an empty bank but an unreadable index.
                if (loaded is { Entries: not null } && loaded.Entries.All(e => e is { Info: not null }))
                    return (loaded.Entries, Math.Max(1, loaded.BoxCount), loaded.MigrationVersion,
                        [.. (loaded.LockedBoxes ?? []).Where(box => box >= 0)],
                        BankBoxGroups.Normalize((loaded.BoxGroups ?? []).Where(g => g is not null)
                            .Select(g => new BankBoxGroup(g.Name ?? "", g.Boxes ?? []))), false);
            }
            catch
            {
                // Try the backup index next.
            }
        }
        // A fresh bank opens with three inviting boxes; an index that exists but cannot be
        // read is never replaced by that empty fallback.
        return ([], 3, 0, [], [], anyIndex);
    }
}
