using System.Globalization;
using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// Things a folder sync took out of PKForge, kept 30 days so nothing pulled from the folder is
/// ever destroyed: Bank Pokémon (with their facts, to deposit again) and plain files (with the
/// private path to put back).
/// </summary>
public sealed class RecentlyRemovedStore(string directory)
{
    public static readonly TimeSpan Kept = TimeSpan.FromDays(30);

    public sealed record Item(string Id, DateTimeOffset RemovedUtc, string FolderPath, BankEntryInfo? Info, string? LocalPath);

    public string Directory { get; } = directory;

    public void Put(byte[] bytes, string folderPath, BankEntryInfo? info, string? localPath, DateTimeOffset now)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var id = $"{now:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
        // Bytes first, note last: a note is listed only once its bytes are whole.
        AtomicFile.Write(Path.Combine(Directory, id + ".bin"), bytes);
        AtomicFile.WriteText(Path.Combine(Directory, id + ".json"), JsonSerializer.Serialize(new Item(id, now, folderPath, info, localPath)));
    }

    public IReadOnlyList<Item> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var items = new List<Item>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                // A note names its own file: an id that does not (a path, another note's) is never followed.
                if (JsonSerializer.Deserialize<Item>(File.ReadAllText(path)) is { Id: { } id, FolderPath: not null } item
                    && id == Path.GetFileNameWithoutExtension(path)
                    && File.Exists(Path.Combine(Directory, id + ".bin")))
                    items.Add(item);
            }
            catch (Exception error) when (error is JsonException or NotSupportedException or IOException) { /* a damaged note is skipped; its bytes stay on disk */ }
        }
        return [.. items.OrderByDescending(item => item.RemovedUtc)];
    }

    public byte[] Read(Item item) => File.ReadAllBytes(Path.Combine(Directory, item.Id + ".bin"));

    /// <summary>Forgets an item (after it was restored, or once it is older than <see cref="Kept"/>).</summary>
    public void Forget(Item item)
    {
        File.Delete(Path.Combine(Directory, item.Id + ".bin"));
        File.Delete(Path.Combine(Directory, item.Id + ".json"));
    }

    public int Prune(DateTimeOffset now)
    {
        var old = List().Where(item => now - item.RemovedUtc > Kept).ToList();
        foreach (var item in old) Forget(item);
        return old.Count;
    }

    /// <summary>Puts an item back: a Pokémon into the first free Bank slot, a file at its private path.</summary>
    public void Restore(Item item, IBankService bank)
    {
        ArgumentNullException.ThrowIfNull(bank);
        var bytes = Read(item);
        if (item.Info is { } info)
        {
            // Back in the slot it left when that slot is still free, else the first free one.
            var (box, slot, _) = item.FolderPath.StartsWith(BankMirrorSource.Root, StringComparison.OrdinalIgnoreCase)
                ? BankMirrorSource.Place(item.FolderPath) : (-1, -1, null);
            var free = box >= 0 && slot >= 0 && !bank.LockedBoxes.Contains(box) && !bank.GetAll().Any(e => e.Box == box && e.Slot == slot);
            bank.AddMany([free ? new BankDeposit(bytes, info, box, slot) : new BankDeposit(bytes, info)]);
        }
        else if (item.LocalPath is { } path)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(File.Exists(path) ? UniqueBeside(path) : path, bytes);
        }
        Forget(item);
    }

    private static string UniqueBeside(string path)
    {
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        var extension = Path.GetExtension(path);
        for (var n = 2; ; n++)
            if (!File.Exists($"{stem} ({n}){extension}")) return $"{stem} ({n}){extension}";
    }
}

/// <summary>
/// The Bank as the folder shows it: "Bank/01 - Box name/012 - Pikachu.pk7" (box and slot
/// numbers from 1, the species, the exact format's extension), plus a hidden
/// <c>Bank/.pkforge-bank.json</c> mapping each file to its entry, slot, box locks and box groups (groups only there: the box folders stay flat). A file
/// the user drops, edits or moves there becomes a deposit, an update or a move.
/// </summary>
public sealed class BankMirrorSource(
    IBankService bank,
    BankBoxNames names,
    Func<byte[], string, string?, BankEntryInfo?> describe,
    Func<BankEntryInfo, string> label,
    RecentlyRemovedStore removed,
    Func<DateTimeOffset>? clock = null) : IMirrorSource
{
    public const string Root = "Bank/";
    public const string MappingPath = "Bank/.pkforge-bank.json";
    private Dictionary<string, Guid> _byPath = new(MirrorPlan.PathComparer);

    public string Prefix => Root;

    public string BoxFolder(int box) =>
        $"{box + 1:00} - {PkFileExport.SafeFileName(names.Get(box) is { Length: > 0 } name ? name : $"Box {box + 1}")}";

    public string PathOf(BankEntry entry) =>
        $"{Root}{BoxFolder(entry.Box)}/{PkFileExport.SafeFileName($"{entry.Slot + 1:000} - {label(entry.Info)}{BankEntryFiles.ExtensionFor(entry.Info)}")}";

    public ValueTask<IReadOnlyList<LocalMirrorFile>> SnapshotAsync(IReadOnlyDictionary<string, MirrorRecord> records, CancellationToken cancellationToken)
    {
        var files = new List<LocalMirrorFile>();
        var byPath = new Dictionary<string, Guid>(MirrorPlan.PathComparer);
        foreach (var entry in bank.GetAll().OrderBy(e => e.Box).ThenBy(e => e.Slot))
        {
            var data = bank.GetData(entry.Id);
            var path = PathOf(entry);
            byPath[path] = entry.Id;
            files.Add(new LocalMirrorFile(path, MirrorPlan.Hash(data), () => data));
        }
        _byPath = byPath;
        return ValueTask.FromResult<IReadOnlyList<LocalMirrorFile>>(files);
    }

    public ValueTask<MirrorApplied> ApplyAsync(IReadOnlyList<IncomingFile> pulled, IReadOnlyList<string> deleted, CancellationToken cancellationToken)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var leftAlone = new List<(string, string)>();
        var entries = bank.GetAll().ToDictionary(e => e.Id);

        // Files gone from the folder, by content, so a file moved there moves its Pokémon.
        var going = new List<(string Path, Guid Id, string Hash)>();
        foreach (var path in deleted)
            if (_byPath.TryGetValue(path, out var id) && entries.ContainsKey(id))
                going.Add((path, id, MirrorPlan.Hash(bank.GetData(id))));

        var namedBoxes = new List<(int, string?)>();
        foreach (var file in pulled)
        {
            try
            {
                var (box, slot, boxName) = Place(file.Path);
                if (_byPath.TryGetValue(file.Path, out var known) && entries.TryGetValue(known, out var current))
                {
                    if (Describe(file) is not { } edited) { leftAlone.Add((file.Path, "not a Pokémon file PKForge can read")); continue; }
                    // A locked box is a finished shelf: an edit there waits in the folder until it is unlocked.
                    if (bank.LockedBoxes.Contains(current.Box)) { leftAlone.Add((file.Path, BankBoxLocks.Refusal(current.Box))); continue; }
                    // The Pokémon it replaces is kept 30 days, like everything else a sync takes out.
                    removed.Put(bank.GetData(known), file.Path, current.Info, null, now);
                    bank.Replace(known, file.Bytes, edited);
                    continue;
                }

                var hash = MirrorPlan.Hash(file.Bytes);
                var moved = going.FindIndex(g => string.Equals(g.Hash, hash, StringComparison.OrdinalIgnoreCase));
                if (moved >= 0)
                {
                    var id = going[moved].Id;
                    going.RemoveAt(moved);
                    var target = FreeSlot(box, slot, except: id);
                    if (target is { } at) bank.Move(id, at.Box, at.Slot);
                    if (boxName is not null && target is not null) namedBoxes.Add((target.Value.Box, boxName));
                    continue;
                }

                if (Describe(file) is not { } info) { leftAlone.Add((file.Path, "not a Pokémon file PKForge can read")); continue; }
                var free = FreeSlot(box, slot, except: null);
                var added = bank.AddMany([free is { } spot ? new BankDeposit(file.Bytes, info, spot.Box, spot.Slot) : new BankDeposit(file.Bytes, info)]);
                if (boxName is not null && free is not null) namedBoxes.Add((added[0].Box, boxName));
            }
            catch (InvalidOperationException refused)
            {
                leftAlone.Add((file.Path, refused.Message)); // a locked box, a read-only bank
            }
        }

        var holding = 0;
        foreach (var (path, id, _) in going)
        {
            try
            {
                if (!bank.GetAll().Any(e => e.Id == id)) continue;
                var bytes = bank.GetData(id);
                var info = bank.GetAll().First(e => e.Id == id).Info;
                removed.Put(bytes, path, info, null, now);
                bank.Remove(id);
                holding++;
            }
            catch (InvalidOperationException refused)
            {
                leftAlone.Add((path, refused.Message));
            }
        }

        // A box folder the user named gives an unnamed box its name.
        var naming = namedBoxes.Where(n => names.Get(n.Item1) is null).Distinct().ToList();
        if (naming.Count > 0) names.SetMany(naming);
        return ValueTask.FromResult(new MirrorApplied(leftAlone, holding));
    }

    private BankEntryInfo? Describe(IncomingFile file)
    {
        var name = Path.GetFileName(file.Path);
        var extension = Path.GetExtension(name);
        var format = extension.Length > 1 ? extension[1..].ToUpperInvariant() : null;
        return describe(file.Bytes, name, format) ?? describe(file.Bytes, name, null);
    }

    /// <summary>Box and slot (from 0) a folder path names, -1 where it names none.</summary>
    public static (int Box, int Slot, string? BoxName) Place(string path)
    {
        var parts = path[Root.Length..].Split('/');
        if (parts.Length != 2) return (-1, -1, null);
        var (box, boxName) = Numbered(parts[0]);
        var (slot, _) = Numbered(parts[1]);
        return (box > 0 ? box - 1 : -1, slot is > 0 and <= IBankService.SlotsPerBox ? slot - 1 : -1, box > 0 ? boxName : null);
    }

    private static (int Number, string? Name) Numbered(string name)
    {
        var digits = 0;
        while (digits < name.Length && char.IsAsciiDigit(name[digits])) digits++;
        if (digits == 0 || !int.TryParse(name[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return (0, null);
        var rest = name[digits..].TrimStart(' ', '-').Trim();
        return (number, rest.Length == 0 || rest == $"Box {number}" ? null : rest);
    }

    /// <summary>The named slot when it is free, else the first free slot of the named box, else null (first free anywhere).</summary>
    private (int Box, int Slot)? FreeSlot(int box, int slot, Guid? except)
    {
        if (box < 0) return null;
        if (bank.LockedBoxes.Contains(box)) throw new InvalidOperationException(BankBoxLocks.Refusal(box));
        var taken = bank.GetAll().Where(e => e.Box == box && e.Id != except).Select(e => e.Slot).ToHashSet();
        if (slot >= 0 && !taken.Contains(slot)) return (box, slot);
        for (var s = 0; s < IBankService.SlotsPerBox; s++)
            if (!taken.Contains(s)) return (box, s);
        return null;
    }

    public IReadOnlyList<(string Path, byte[] Bytes)> Extras()
    {
        var entries = bank.GetAll().OrderBy(e => e.Box).ThenBy(e => e.Slot).ToList();
        var boxes = entries.Select(e => e.Box).Distinct()
            .Select(box => new { box = box + 1, name = names.Get(box), folder = BoxFolder(box) }).ToList();
        var mapping = new
        {
            version = 1,
            boxes,
            lockedBoxes = bank.LockedBoxes.Order().Select(box => box + 1).ToList(),
            // Groups live only here (and in the index): the box folders stay flat.
            groups = bank.BoxGroups.Select(g => new { name = g.Name, boxes = g.Boxes.Select(box => box + 1).ToList() }).ToList(),
            entries = entries.Select(e => new { path = PathOf(e)[Root.Length..], id = e.Id, box = e.Box + 1, slot = e.Slot + 1, format = e.Info.Format }).ToList(),
        };
        return [(MappingPath, JsonSerializer.SerializeToUtf8Bytes(mapping, new JsonSerializerOptions { WriteIndented = true }))];
    }
}

/// <summary>A private folder shown flat under a folder prefix ("Album/" ↔ pokepark-album/).
/// <paramref name="toFolder"/> / <paramref name="fromFolder"/> may rename files on the way (null:
/// the file is not mirrored / is left alone), and <paramref name="accepts"/> checks a pulled copy.</summary>
public sealed class DirectoryMirrorSource(string prefix, string localDirectory, RecentlyRemovedStore removed,
    Func<DateTimeOffset>? clock = null, Func<string, string?>? toFolder = null, Func<string, string?>? fromFolder = null,
    Func<byte[], bool>? accepts = null) : IMirrorSource
{
    private string? ToFolder(string name) => toFolder is null ? name : toFolder(name);

    private string? FromFolder(string relative) => fromFolder is null ? (relative.Contains('/') ? null : relative) : fromFolder(relative);

    public string Prefix => prefix;

    public ValueTask<IReadOnlyList<LocalMirrorFile>> SnapshotAsync(IReadOnlyDictionary<string, MirrorRecord> records, CancellationToken cancellationToken)
    {
        var files = new List<LocalMirrorFile>();
        if (Directory.Exists(localDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(localDirectory))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith('.') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(path);
                var stamp = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                if (ToFolder(name) is not { } mirrored) continue;
                var folderPath = prefix + mirrored;
                // An unchanged private file keeps the hash it had: only changed files are read.
                var hash = records.TryGetValue(folderPath, out var known) && known.LocalSize == info.Length && known.LocalModifiedMs == stamp
                    ? known.Hash
                    : MirrorPlan.Hash(File.ReadAllBytes(path));
                files.Add(new LocalMirrorFile(folderPath, hash, () => File.ReadAllBytes(path), info.Length, stamp));
            }
        }
        return ValueTask.FromResult<IReadOnlyList<LocalMirrorFile>>(files);
    }

    public ValueTask<MirrorApplied> ApplyAsync(IReadOnlyList<IncomingFile> pulled, IReadOnlyList<string> deleted, CancellationToken cancellationToken)
    {
        var leftAlone = new List<(string, string)>();
        Directory.CreateDirectory(localDirectory);
        foreach (var file in pulled)
        {
            if (FromFolder(file.Path[prefix.Length..]) is not { } name)
            {
                leftAlone.Add((file.Path, fromFolder is null ? "PKForge only reads files directly in this folder" : "PKForge does not use a file by this name here"));
                continue;
            }
            if (accepts is not null && !accepts(file.Bytes)) { leftAlone.Add((file.Path, "PKForge cannot read this file")); continue; }
            if (Inside(name) is not { } target) { leftAlone.Add((file.Path, "PKForge does not use a file by this name here")); continue; }
            if (File.Exists(target))
                removed.Put(File.ReadAllBytes(target), file.Path, null, target, (clock ?? (() => DateTimeOffset.UtcNow))());
            AtomicFile.Write(target, file.Bytes);
        }
        var holding = 0;
        foreach (var path in deleted)
        {
            if (FromFolder(path[prefix.Length..]) is not { } name || Inside(name) is not { } target) continue;
            if (!File.Exists(target)) continue;
            removed.Put(File.ReadAllBytes(target), path, null, target, (clock ?? (() => DateTimeOffset.UtcNow))());
            File.Delete(target);
            holding++;
        }
        return ValueTask.FromResult(new MirrorApplied(leftAlone, holding));
    }

    /// <summary>The private file a folder name maps to, or null when it would land outside the folder
    /// (a name the folder's provider allowed but this file system reads as a path).</summary>
    private string? Inside(string name)
    {
        var directory = Path.GetFullPath(localDirectory);
        var target = Path.GetFullPath(Path.Combine(directory, name));
        return Path.GetDirectoryName(target) == directory.TrimEnd(Path.DirectorySeparatorChar) ? target : null;
    }
}

/// <summary>One private file at one folder path ("Items/item-presets.json"). <paramref name="accepts"/>
/// checks a pulled copy before it replaces the private one.</summary>
public sealed class FileMirrorSource(string folderPath, string localPath, RecentlyRemovedStore removed,
    Func<byte[], bool>? accepts = null, Func<DateTimeOffset>? clock = null) : IMirrorSource
{
    public string Prefix => folderPath;

    public ValueTask<IReadOnlyList<LocalMirrorFile>> SnapshotAsync(IReadOnlyDictionary<string, MirrorRecord> records, CancellationToken cancellationToken)
    {
        if (!File.Exists(localPath)) return ValueTask.FromResult<IReadOnlyList<LocalMirrorFile>>([]);
        var bytes = File.ReadAllBytes(localPath);
        return ValueTask.FromResult<IReadOnlyList<LocalMirrorFile>>([new LocalMirrorFile(folderPath, MirrorPlan.Hash(bytes), () => bytes)]);
    }

    public ValueTask<MirrorApplied> ApplyAsync(IReadOnlyList<IncomingFile> pulled, IReadOnlyList<string> deleted, CancellationToken cancellationToken)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        foreach (var file in pulled)
        {
            if (accepts is not null && !accepts(file.Bytes))
                return ValueTask.FromResult(new MirrorApplied([(file.Path, "PKForge cannot read this file")], 0));
            if (File.Exists(localPath)) removed.Put(File.ReadAllBytes(localPath), file.Path, null, localPath, now);
            AtomicFile.Write(localPath, file.Bytes);
        }
        if (deleted.Count > 0 && File.Exists(localPath))
        {
            removed.Put(File.ReadAllBytes(localPath), folderPath, null, localPath, now);
            File.Delete(localPath);
            return ValueTask.FromResult(new MirrorApplied([], 1));
        }
        return ValueTask.FromResult(MirrorApplied.Nothing);
    }
}

/// <summary>
/// Pinned restore points as real saves: "Saves/&lt;game&gt;/2026-11-10 1432 - before evolving.sav".
/// The folder copy is for the user to take away; PKForge never imports a save from it. Deleting
/// one there only unpins the restore point here (it stays among the ordinary ones).
/// </summary>
public sealed class PinnedSavesMirrorSource(IBackupService backups) : IMirrorSource
{
    public const string Root = "Saves/";
    private Dictionary<string, string> _byPath = new(MirrorPlan.PathComparer);

    public string Prefix => Root;

    public async ValueTask<IReadOnlyList<LocalMirrorFile>> SnapshotAsync(IReadOnlyDictionary<string, MirrorRecord> records, CancellationToken cancellationToken)
    {
        var points = (await backups.ListAsync(cancellationToken).ConfigureAwait(false)).Where(p => p.Pinned).OrderBy(p => p.CreatedUtc).ToList();
        var taken = new HashSet<string>(MirrorPlan.PathComparer);
        var byPath = new Dictionary<string, string>(MirrorPlan.PathComparer);
        var files = new List<LocalMirrorFile>();
        foreach (var point in points)
        {
            var game = PkFileExport.SafeFileName(point.DisplayName is { Length: > 0 } name ? name : point.Format);
            var file = PkFileExport.SafeFileName($"{point.CreatedUtc.ToLocalTime():yyyy-MM-dd HHmm} - {point.ChangeDescription ?? "restore point"}.sav");
            var path = PkFileExport.Unique($"{Root}{game}/{file}", taken);
            byPath[path] = point.BackupId;
            var id = point.BackupId;
            files.Add(new LocalMirrorFile(path, point.Sha256, () => backups.ReadAsync(id).AsTask().GetAwaiter().GetResult().ToArray()));
        }
        _byPath = byPath;
        return files;
    }

    public async ValueTask<MirrorApplied> ApplyAsync(IReadOnlyList<IncomingFile> pulled, IReadOnlyList<string> deleted, CancellationToken cancellationToken)
    {
        foreach (var path in deleted)
            if (_byPath.TryGetValue(path, out var id))
                await backups.SetPinnedAsync(id, false, cancellationToken).ConfigureAwait(false);
        return new MirrorApplied(
            [.. pulled.Select(file => (file.Path, "saves in the folder are not imported; open one from Games and folders › Open a save file"))], 0);
    }
}

/// <summary>
/// The Item Bank as <c>Items/item-bank.json</c>. Unlike a plain file it is never replaced by
/// the folder's copy: what comes in is merged item by item and origin by origin through
/// <see cref="ItemBankStore.MergeIn"/>, under the store's lock, against the copy both sides last
/// agreed on (kept at <paramref name="commonPath"/>). Two devices that each banked items offline
/// both keep everything, a withdrawal made on one is not undone by the other, and a change
/// both sides made is merged in place: no conflict copy nobody reads.
/// </summary>
public sealed class ItemBankMirrorSource(ItemBankStore store, string commonPath, RecentlyRemovedStore removed,
    Func<DateTimeOffset>? clock = null) : IMirrorSource
{
    public const string FolderPath = "Items/item-bank.json";

    private byte[]? _snapshot;
    private string? _agreed;

    public string Prefix => FolderPath;

    public ValueTask<IReadOnlyList<LocalMirrorFile>> SnapshotAsync(IReadOnlyDictionary<string, MirrorRecord> records, CancellationToken cancellationToken)
    {
        _agreed = records.TryGetValue(FolderPath, out var record) ? record.Hash : null;
        var bytes = _snapshot = store.ReadBytes();
        return ValueTask.FromResult<IReadOnlyList<LocalMirrorFile>>(bytes is null ? [] : [new LocalMirrorFile(FolderPath, MirrorPlan.Hash(bytes), () => bytes)]);
    }

    public ValueTask<MirrorApplied> ApplyAsync(IReadOnlyList<IncomingFile> pulled, IReadOnlyList<string> deleted, CancellationToken cancellationToken)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var leftAlone = new List<(string, string)>();
        foreach (var file in pulled)
        {
            if (!ItemBankStore.IsItemBank(file.Bytes)) { leftAlone.Add((file.Path, "not an Item Bank file PKForge can read")); continue; }
            try
            {
                // A pull means this side had not changed since the last sync: the snapshot is the
                // common copy, so a deposit made while the sync ran is kept too.
                if (store.ReadBytes() is { } mine) removed.Put(mine, file.Path, null, store.FilePath, now);
                store.MergeIn(_snapshot is { } common && Agreed(common) ? common : null, file.Bytes);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or IOException)
            {
                leftAlone.Add((file.Path, $"the Item Bank here could not be read ({error.Message})"));
            }
        }
        var holding = 0;
        if (deleted.Count > 0 && store.ReadBytes() is { } gone)
        {
            removed.Put(gone, FolderPath, null, store.FilePath, now);
            store.Save(ItemBank.Empty);
            holding++;
        }
        return ValueTask.FromResult(new MirrorApplied(leftAlone, holding));
    }

    public ValueTask<byte[]?> MergeConflictAsync(string path, byte[] theirs, CancellationToken cancellationToken)
    {
        // Something that is not an Item Bank is kept as a conflict copy, never merged.
        if (!ItemBankStore.IsItemBank(theirs)) return ValueTask.FromResult<byte[]?>(null);
        byte[]? common = null;
        try { common = File.Exists(commonPath) ? File.ReadAllBytes(commonPath) : null; }
        catch (IOException) { }
        try
        {
            store.MergeIn(common is not null && Agreed(common) ? common : null, theirs);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            return ValueTask.FromResult<byte[]?>(null); // the bank here is damaged: keep both files
        }
        return ValueTask.FromResult(store.ReadBytes());
    }

    /// <summary>Remembers the copy both sides now hold, the common ancestor of the next merge.</summary>
    public void Settled(IReadOnlyDictionary<string, MirrorRecord> records)
    {
        if (!records.TryGetValue(FolderPath, out var record)) return;
        try
        {
            if (store.ReadBytes() is { } bytes && string.Equals(MirrorPlan.Hash(bytes), record.Hash, StringComparison.OrdinalIgnoreCase))
                AtomicFile.Write(commonPath, bytes);
        }
        catch (IOException) { /* without it the next conflict merges by keeping the larger counts */ }
    }

    private bool Agreed(byte[] bytes) => _agreed is not null && string.Equals(MirrorPlan.Hash(bytes), _agreed, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Pokémon History as <c>History/&lt;id&gt;.json</c>. A record that comes in is merged into the one
/// this device holds for the same Pokémon (by any identity), and a record changed on both sides is
/// merged in place, so no conflict copy ever stands beside (and hides) the real record.
/// </summary>
public sealed class HistoryMirrorSource(FilePokemonHistoryStore store, RecentlyRemovedStore removed, Func<DateTimeOffset>? clock = null) : IMirrorSource
{
    public const string Root = "History/";

    private readonly DirectoryMirrorSource _files = new(Root, store.Directory, removed, clock);

    public string Prefix => Root;

    public ValueTask<IReadOnlyList<LocalMirrorFile>> SnapshotAsync(IReadOnlyDictionary<string, MirrorRecord> records, CancellationToken cancellationToken) =>
        _files.SnapshotAsync(records, cancellationToken);

    public async ValueTask<MirrorApplied> ApplyAsync(IReadOnlyList<IncomingFile> pulled, IReadOnlyList<string> deleted, CancellationToken cancellationToken)
    {
        var leftAlone = new List<(string, string)>();
        foreach (var file in pulled)
        {
            if (file.Path[Root.Length..].Contains('/'))
            {
                leftAlone.Add((file.Path, "PKForge only reads files directly in this folder"));
                continue;
            }
            if (FilePokemonHistoryStore.Parse(System.Text.Encoding.UTF8.GetString(file.Bytes)) is not { } record)
            {
                leftAlone.Add((file.Path, "not a Pokémon History record PKForge can read"));
                continue;
            }
            store.Merge(record);
        }
        var gone = await _files.ApplyAsync([], deleted, cancellationToken).ConfigureAwait(false);
        return new MirrorApplied([.. leftAlone, .. gone.LeftAlone], gone.MovedToRecentlyRemoved);
    }

    public ValueTask<byte[]?> MergeConflictAsync(string path, byte[] theirs, CancellationToken cancellationToken)
    {
        if (path[Root.Length..].Contains('/') || FilePokemonHistoryStore.Parse(System.Text.Encoding.UTF8.GetString(theirs)) is not { } record)
            return ValueTask.FromResult<byte[]?>(null);
        store.Merge(record);
        var local = Path.Combine(store.Directory, path[Root.Length..]);
        return ValueTask.FromResult(File.Exists(local) ? File.ReadAllBytes(local) : null);
    }
}
