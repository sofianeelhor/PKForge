using System.Text.Json;
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
    // Set when an index exists on disk but neither it nor its backup could be read: the bank
    // then stays read-only rather than overwrite the real index with an empty fallback.
    private bool _indexUnreadable;

    public FileBankService(string rootDirectory)
    {
        _root = rootDirectory;
        Directory.CreateDirectory(_root);
        (_entries, _boxCount, _migrationVersion, _indexUnreadable) = LoadIndex();
    }

    public IReadOnlyList<BankEntry> GetAll()
    {
        lock (_gate) return _entries.ToList();
    }

    /// <summary>Reads the index from disk again, after its files were put back from a copy.</summary>
    public void Reload()
    {
        lock (_gate) (_entries, _boxCount, _migrationVersion, _indexUnreadable) = LoadIndex();
    }

    /// <summary>The index could not be read, so the bank refuses every write and lists nothing.</summary>
    public bool IsReadOnly => _indexUnreadable;

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
                            // The free-slot scan resumes where the previous deposit landed.
                            while (occupied.Contains((box, slot)))
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
            File.WriteAllBytes(DataPath(id), data);
            Commit(() =>
            {
                _entries[index] = _entries[index] with { Info = info };
                SaveIndex();
                return 0;
            });
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
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
            if (remap.IsIdentity) return;
            Commit(() =>
            {
                for (var i = 0; i < _entries.Count; i++)
                    _entries[i] = _entries[i] with { Box = remap.Map(_entries[i].Box)!.Value };
                _boxCount = remap.NewCount;
                SaveIndex();
                return 0;
            });
        }
    }

    /// <summary>
    /// Applies changes that arrived from a sync server in one index write. Entries keep the server's ids,
    /// boxes and slots. The whole batch is checked first (bytes for every new entry, slots in range, one
    /// entry per slot afterwards), so a refused batch changes nothing. Bytes are written before the index,
    /// as <see cref="Replace"/> does; removed entries' bytes are deleted after it. The box count follows
    /// <paramref name="boxCount"/> but always fits every entry.
    /// </summary>
    public void ApplyRemote(IReadOnlyList<RemoteEntry> upserts, IReadOnlyList<Guid> removals, int? boxCount = null)
    {
        lock (_gate)
        {
            if (upserts.Count == 0 && removals.Count == 0 && (boxCount is null || boxCount == _boxCount)) return;
            EnsureWritable();

            var removing = removals.ToHashSet();
            var known = _entries.Select(e => e.Id).ToHashSet();
            var incoming = new Dictionary<Guid, RemoteEntry>(upserts.Count);
            foreach (var upsert in upserts)
            {
                var entry = upsert.Entry;
                if (removing.Contains(entry.Id) || !incoming.TryAdd(entry.Id, upsert))
                    throw new InvalidOperationException("A remote batch names an entry twice.");
                if (entry.Box < 0 || entry.Slot < 0 || entry.Slot >= SlotsPerBox)
                    throw new ArgumentOutOfRangeException(nameof(upserts), "Box or slot out of range.");
                if (upsert.Data is null && !known.Contains(entry.Id))
                    throw new InvalidOperationException("A new remote entry arrived without its bytes.");
            }

            var result = _entries
                .Where(e => !removing.Contains(e.Id) && !incoming.ContainsKey(e.Id))
                .Concat(incoming.Values.Select(u => u.Entry))
                .ToList();
            var slots = new HashSet<(int, int)>();
            if (result.Any(e => !slots.Add((e.Box, e.Slot))))
                throw new InvalidOperationException("Two entries would share one slot.");

            var created = new List<Guid>();
            try
            {
                foreach (var upsert in incoming.Values.Where(u => u.Data is not null))
                {
                    if (!known.Contains(upsert.Entry.Id)) created.Add(upsert.Entry.Id);
                    File.WriteAllBytes(DataPath(upsert.Entry.Id), upsert.Data!);
                }
                Commit(() =>
                {
                    _entries = result;
                    var needed = result.Count == 0 ? 1 : result.Max(e => e.Box) + 1;
                    _boxCount = Math.Max(Math.Max(1, boxCount ?? _boxCount), needed);
                    SaveIndex();
                    return 0;
                });
            }
            catch
            {
                foreach (var id in created)
                {
                    try { File.Delete(DataPath(id)); }
                    catch { /* an orphan .bin is harmless: the index never lists it */ }
                }
                throw;
            }

            foreach (var id in removing.Where(known.Contains))
            {
                try { File.Delete(DataPath(id)); }
                catch { /* a leftover .bin is harmless: the index no longer lists it */ }
            }
        }
    }

    private (int Box, int Slot) FirstEmpty()
    {
        var occupied = _entries.Select(e => (e.Box, e.Slot)).ToHashSet();
        for (var box = 0; box < _boxCount; box++)
        {
            for (var slot = 0; slot < SlotsPerBox; slot++)
            {
                if (!occupied.Contains((box, slot)))
                    return (box, slot);
            }
        }
        _boxCount++;
        return (_boxCount - 1, 0);
    }

    private string DataPath(Guid id) => Path.Combine(_root, id.ToString("N") + ".bin");
    private string IndexPath => Path.Combine(_root, "index.json");

    // MigrationVersion is absent from older indexes and reads as 0; older app versions ignore it.
    private sealed record IndexFile(int BoxCount, List<BankEntry> Entries, int MigrationVersion = 0);

    /// <summary>Runs one mutation of the in-memory index (the caller holds the gate); if it throws,
    /// typically because the index write failed, memory is put back to match the disk.</summary>
    private T Commit<T>(Func<T> mutate)
    {
        var entries = _entries.ToList();
        var boxCount = _boxCount;
        try { return mutate(); }
        catch
        {
            _entries = entries;
            _boxCount = boxCount;
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
        var json = JsonSerializer.Serialize(new IndexFile(_boxCount, _entries, _migrationVersion));
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(IndexPath))
            File.Copy(IndexPath, IndexPath + ".bak", overwrite: true);
        File.Move(tmp, IndexPath, overwrite: true);
    }

    private (List<BankEntry>, int, int, bool Unreadable) LoadIndex()
    {
        var anyIndex = false;
        foreach (var candidate in new[] { IndexPath, IndexPath + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                anyIndex = true;
                var loaded = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(candidate));
                if (loaded is not null)
                    return (loaded.Entries ?? [], Math.Max(1, loaded.BoxCount), loaded.MigrationVersion, false);
            }
            catch
            {
                // Try the backup index next.
            }
        }
        // A fresh bank opens with three inviting boxes; an index that exists but cannot be
        // read is never replaced by that empty fallback.
        return ([], 3, 0, anyIndex);
    }
}
