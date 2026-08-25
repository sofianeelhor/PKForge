using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// Durable bank store: one raw .bin per entity plus an atomically-written JSON index
/// (tmp → rename, previous index kept as .bak). Boxes are 30 slots and auto-grow.
/// </summary>
public sealed class FileBankService : IBankService
{
    public const int SlotsPerBox = 30;

    private readonly string _root;
    private readonly Lock _gate = new();
    private List<BankEntry> _entries;
    private int _boxCount;

    public FileBankService(string rootDirectory)
    {
        _root = rootDirectory;
        Directory.CreateDirectory(_root);
        (_entries, _boxCount) = LoadIndex();
    }

    public IReadOnlyList<BankEntry> GetAll()
    {
        lock (_gate) return _entries.ToList();
    }

    public int BoxCount
    {
        get { lock (_gate) return _boxCount; }
    }

    public BankEntry Add(byte[] data, BankEntryInfo info)
    {
        lock (_gate)
        {
            var (box, slot) = FirstEmpty();
            var entry = new BankEntry(Guid.NewGuid(), box, slot, info, DateTimeOffset.UtcNow);
            File.WriteAllBytes(DataPath(entry.Id), data);
            _entries.Add(entry);
            SaveIndex();
            return entry;
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
            var occupant = _entries.FindIndex(e => e.Box == box && e.Slot == slot);
            var moving = _entries[index];
            if (occupant >= 0 && occupant != index)
            {
                // Swap: the occupant takes the mover's old place.
                _entries[occupant] = _entries[occupant] with { Box = moving.Box, Slot = moving.Slot };
            }
            _entries[index] = moving with { Box = box, Slot = slot };
            _boxCount = Math.Max(_boxCount, box + 1);
            SaveIndex();
        }
    }

    public void Replace(Guid id, byte[] data, BankEntryInfo info)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) throw new InvalidOperationException("Unknown bank entry.");
            File.WriteAllBytes(DataPath(id), data);
            _entries[index] = _entries[index] with { Info = info };
            SaveIndex();
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
            _entries.RemoveAt(index);
            try { File.Delete(DataPath(id)); }
            catch { /* index is authoritative; orphan bytes are harmless */ }
            SaveIndex();
        }
    }

    public void AddBox()
    {
        lock (_gate)
        {
            _boxCount++;
            SaveIndex();
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

    private sealed record IndexFile(int BoxCount, List<BankEntry> Entries);

    private void SaveIndex()
    {
        var json = JsonSerializer.Serialize(new IndexFile(_boxCount, _entries));
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(IndexPath))
            File.Copy(IndexPath, IndexPath + ".bak", overwrite: true);
        File.Move(tmp, IndexPath, overwrite: true);
    }

    private (List<BankEntry>, int) LoadIndex()
    {
        foreach (var candidate in new[] { IndexPath, IndexPath + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var loaded = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(candidate));
                if (loaded is not null)
                    return (loaded.Entries, Math.Max(1, loaded.BoxCount));
            }
            catch
            {
                // Try the backup index next.
            }
        }
        return ([], 3); // a fresh bank opens with three inviting boxes
    }
}
