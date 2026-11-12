using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>What an import would do, counted before anything is written.</summary>
public sealed record DataImportPreview(
    DataArchiveManifest Manifest,
    int BankNew,
    int BankDuplicates,
    bool BankKeepsLayout,
    int RestorePointsNew,
    int AlbumPhotosNew,
    int ItemPresetsNew,
    int SaveIdentitiesNew,
    int GiftsNew,
    int Preferences,
    IReadOnlyList<WatchedRoot> ToRelink)
{
    /// <summary>Item Bank entries the import would add or raise.</summary>
    public int ItemBankNew { get; init; }

    /// <summary>Personal Pokémon presets the import would add.</summary>
    public int MonPresetsNew { get; init; }

    /// <summary>Pokémon History records the import would add or extend.</summary>
    public int HistoryNew { get; init; }
}


/// <summary>What an import wrote.</summary>
public sealed record DataImportResult(
    int BankImported,
    int BankDuplicates,
    int RestorePoints,
    int AlbumPhotos,
    int ItemPresets,
    int SaveIdentities,
    int Gifts,
    int ItemBankItems = 0,
    int MonPresets = 0,
    int History = 0);

/// <summary>
/// The whole of PKForge's own data as one .zip, to move it to another device:
/// <c>manifest.json</c>, <c>preferences.json</c> and <c>data/</c>, a copy of the app-data
/// files that hold the player's work (the Bank, restore points, Poképark photos, item presets,
/// the Item Bank, personal Pokémon presets, save names and colors, the player's own game art, received-gift markers, Pokémon History). Caches that download again (sprites, item
/// art) and logs stay out.
/// <para>
/// Import never overwrites what is already here: the Bank merges (byte-identical Pokémon are
/// skipped), restore points, photos, presets, save names and gift markers are added where
/// missing, and the Item Bank merges item by item, keeping the larger count of each origin. Only preferences are replaced, and the caller keeps a safety archive first.
/// </para>
/// </summary>
public static class DataArchive
{
    public const string FormatName = "pkforge-data";
    public const int CurrentFormatVersion = 1;
    public const string ManifestEntry = "manifest.json";
    public const string PreferencesEntry = "preferences.json";
    private const string DataPrefix = "data/";

    private const string BankFolder = "bank";
    private const string BackupsFolder = "backups";
    private const string AlbumFolder = "pokepark-album";
    private const string ItemPresetsFile = "item-presets.json";
    private const string SaveIdentitiesFile = "save-identities.json";
    private const string GiftsFile = "injected-gifts.json";
    private const string MonPresetsFile = "mon-presets.json";
    private const string ItemBankFolder = "item-bank";
    private const string GameArtFolder = CustomGameArt.Folder;
    private const string HistoryFolder = FilePokemonHistoryStore.Folder;
    internal const string ItemBankEntry = ItemBankFolder + "/item-bank.json";

    private static readonly string[] Folders = [BankFolder, BackupsFolder, AlbumFolder, ItemBankFolder, GameArtFolder, HistoryFolder];
    private static readonly string[] Files = [ItemPresetsFile, SaveIdentitiesFile, GiftsFile, MonPresetsFile];
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    // ── Export ──────────────────────────────────────────────────────────────

    /// <summary>Writes the archive of everything under <paramref name="appDataRoot"/> worth keeping.</summary>
    public static DataArchiveManifest Export(Stream output, string appDataRoot, IReadOnlyList<PreferenceValue> preferences,
        string appVersion, DateTimeOffset now)
    {
        var files = KeptFiles(appDataRoot).ToArray();
        var manifest = new DataArchiveManifest(FormatName, CurrentFormatVersion, appVersion, now,
        [
            new(DataTransfer.BankSection, CountBank(Path.Combine(appDataRoot, BankFolder))),
            new(DataTransfer.RestorePointsSection, files.Count(f => f.Entry.StartsWith($"{DataPrefix}{BackupsFolder}/", StringComparison.Ordinal) && f.Entry.EndsWith(".json", StringComparison.Ordinal))),
            new(DataTransfer.AlbumSection, files.Count(f => f.Entry.StartsWith($"{DataPrefix}{AlbumFolder}/", StringComparison.Ordinal) && f.Entry.EndsWith(".jpg", StringComparison.Ordinal))),
            new(DataTransfer.ItemPresetsSection, CountJsonArray(Path.Combine(appDataRoot, ItemPresetsFile))),
            new(DataTransfer.ItemBankSection, CountItemBank(appDataRoot)),
            new(DataTransfer.MonPresetsSection, CountJsonArray(Path.Combine(appDataRoot, MonPresetsFile))),
            new(DataTransfer.SaveIdentitiesSection, CountJsonArray(Path.Combine(appDataRoot, SaveIdentitiesFile))),
            new(DataTransfer.GiftsSection, CountJsonArray(Path.Combine(appDataRoot, GiftsFile))),
            new(DataTransfer.HistorySection, files.Count(f => f.Entry.StartsWith($"{DataPrefix}{HistoryFolder}/", StringComparison.Ordinal) && f.Entry.EndsWith(".json", StringComparison.Ordinal))),
            new(DataTransfer.PreferencesSection, preferences.Count),
        ]);

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteJson(zip, ManifestEntry, manifest);
        WriteJson(zip, PreferencesEntry, preferences);
        foreach (var (path, entry) in files)
            zip.CreateEntryFromFile(path, entry, CompressionLevel.Optimal);
        return manifest;
    }

    private static IEnumerable<(string Path, string Entry)> KeptFiles(string root)
    {
        foreach (var name in Files)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) yield return (path, DataPrefix + name);
        }
        foreach (var folder in Folders)
        {
            var directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory)) continue;
            // Half-written temporaries and the bank's previous index are not data.
            foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                yield return (path, $"{DataPrefix}{folder}/{name}");
            }
        }
    }

    private static int CountBank(string directory) =>
        File.Exists(Path.Combine(directory, "index.json")) ? new FileBankService(directory).GetAll().Count : 0;

    private static int CountItemBank(string root)
    {
        try { return new ItemBankStore(Path.Combine(root, ItemBankFolder)).Read().Entries.Count; }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return 0; }
    }

    private static int CountJsonArray(string path)
    {
        try { return File.Exists(path) && JsonDocument.Parse(File.ReadAllText(path)).RootElement is { ValueKind: JsonValueKind.Array } array ? array.GetArrayLength() : 0; }
        catch (JsonException) { return 0; }
    }

    private static void WriteJson<T>(ZipArchive zip, string name, T value)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        JsonSerializer.Serialize(stream, value, Json);
    }

    // ── Reading ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens and checks an archive: it must be a zip with a PKForge manifest this version
    /// understands, and every entry must stay inside the places an archive may fill. Throws
    /// <see cref="DataArchiveException"/> with the reason otherwise.
    /// </summary>
    public static DataArchiveReader Open(Stream input)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new DataArchiveException("This file is not a zip archive."); }
        try
        {
            foreach (var entry in zip.Entries)
                if (UnsafeReason(entry.FullName) is { } reason)
                    throw new DataArchiveException($"The archive was refused: \"{entry.FullName}\" {reason}");

            var manifest = Read<DataArchiveManifest>(zip, ManifestEntry)
                ?? throw new DataArchiveException("This is not a PKForge data archive: it has no manifest.json.");
            if (manifest.Format != FormatName || manifest.Sections is null)
                throw new DataArchiveException("This is not a PKForge data archive: its manifest describes something else.");
            if (manifest.FormatVersion > CurrentFormatVersion)
                throw new DataArchiveException($"This archive was made by a newer PKForge ({manifest.AppVersion}). Update PKForge, then import it again.");
            if (manifest.FormatVersion < 1)
                throw new DataArchiveException("This archive's manifest is damaged: it has no format version.");
            var preferences = Read<List<PreferenceValue>>(zip, PreferencesEntry) ?? [];
            if (preferences.Any(p => string.IsNullOrEmpty(p.Key) || p.Type is not ("string" or "bool" or "int" or "long" or "float") || p.Value is null))
                throw new DataArchiveException("This archive's preferences are damaged.");
            return new DataArchiveReader(zip, manifest, preferences);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>Null when an entry name is safe to restore, else why it is not.</summary>
    internal static string? UnsafeReason(string name)
    {
        if (name.Length == 0 || name.Contains('\\') || name.Contains(':') || name.Contains('\0') || name.StartsWith('/'))
            return "has a path that points outside PKForge's data.";
        var parts = name.Split('/');
        if (parts.Any(p => p is "." or ".."))
            return "has a path that points outside PKForge's data.";
        if (name is ManifestEntry or PreferencesEntry) return null;
        if (parts.Length < 2 || parts[0] != "data") return "is not part of a PKForge data archive.";
        var known = parts.Length == 2 ? Files.Contains(parts[1]) : parts.Length == 3 && Folders.Contains(parts[1]);
        // Folder entries ("data/bank/") carry no data and are allowed when the folder is known.
        if (!known && !(parts.Length == 3 && parts[2].Length == 0 && Folders.Contains(parts[1])))
            return "is not part of a PKForge data archive.";
        return null;
    }

    private static T? Read<T>(ZipArchive zip, string name) where T : class
    {
        if (zip.GetEntry(name) is not { } entry) return null;
        try
        {
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<T>(stream, Json);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
        {
            throw new DataArchiveException($"The archive's {name} is damaged and cannot be read.");
        }
    }

    // ── Import ──────────────────────────────────────────────────────────────

    /// <summary>Counts what <see cref="Restore"/> would add, without writing anything here.</summary>
    public static DataImportPreview Preview(DataArchiveReader archive, string appDataRoot, IBankService bank,
        IReadOnlyList<WatchedRoot> waitingToRelink)
    {
        using var staged = StageBank(archive);
        var (fresh, duplicates) = SplitBank(staged.Bank, bank);
        var identities = ArchivedIdentities(archive);
        var local = LocalIdentityIds(appDataRoot);
        var presets = ArchivedPresets(archive);
        var localPresets = new ItemPresetStore(Path.Combine(appDataRoot, ItemPresetsFile)).Read();
        var localMonPresets = new MonPresetStore(Path.Combine(appDataRoot, MonPresetsFile)).Read();
        var gifts = ArchivedGifts(archive);
        var localGifts = new HashSet<string>((ReadJson<string[]>(Path.Combine(appDataRoot, GiftsFile)) ?? []).Where(key => key is not null), StringComparer.Ordinal);
        return new DataImportPreview(
            archive.Manifest,
            fresh.Count,
            duplicates,
            bank.GetAll().Count == 0,
            MissingFiles(archive, appDataRoot, BackupsFolder).Count(e => e.Name.EndsWith(".json", StringComparison.Ordinal)),
            MissingFiles(archive, appDataRoot, AlbumFolder).Count(e => e.Name.EndsWith(".jpg", StringComparison.Ordinal)),
            presets.Count(p => localPresets.All(l => l.Id != p.Id)),
            identities.Count(i => !local.Contains(i.DocumentId)),
            gifts.Count(g => !localGifts.Contains(g)),
            DataTransfer.ToApply(archive.Preferences).Count,
            DataTransfer.ToRelink(waitingToRelink, archive.Preferences))
        {
            ItemBankNew = ItemBankRaised(appDataRoot, archive),
            MonPresetsNew = ArchivedMonPresets(archive).Count(p => localMonPresets.All(l => l.Id != p.Id)),
            HistoryNew = HistoryChanges(archive, new FilePokemonHistoryStore(Path.Combine(appDataRoot, HistoryFolder))),
        };
    }

    /// <summary>
    /// Adds the archive's data to what is here; nothing already here is changed. The live
    /// stores are passed in so the running app sees the new data at once. Preferences are
    /// the caller's to apply (<see cref="DataTransfer.ToApply"/>).
    /// </summary>
    public static DataImportResult Restore(DataArchiveReader archive, string appDataRoot, IBankService bank,
        ISaveIdentityStore identities, InjectedGiftHistory gifts, BankBoxNames boxNames, BankBoxWallpapers boxWallpapers,
        ItemBankStore? itemBank = null)
    {
        // The Bank first: it is the one part that can refuse (a read-only bank), and then nothing else was touched.
        int imported, duplicates;
        using (var staged = StageBank(archive))
        {
            var wasEmpty = bank.GetAll().Count == 0;
            var (fresh, skipped) = SplitBank(staged.Bank, bank);
            duplicates = skipped;
            // An empty Bank takes the archive's boxes as they were: same slots, names and wallpapers.
            var deposits = fresh.Select(e => wasEmpty
                ? new BankDeposit(staged.Bank.GetData(e.Id), e.Info, e.Box, e.Slot)
                : new BankDeposit(staged.Bank.GetData(e.Id), e.Info)).ToArray();
            imported = bank.AddMany(deposits).Count;
            if (wasEmpty)
            {
                while (bank.BoxCount < staged.Bank.BoxCount) bank.AddBox();
                var archivedNames = new BankBoxNames(staged.Directory);
                var archivedWallpapers = new BankBoxWallpapers(staged.Directory);
                boxNames.SetMany(Enumerable.Range(0, staged.Bank.BoxCount).Select(box => (box, archivedNames.Get(box))).Where(n => n.Item2 is not null));
                for (var box = 0; box < staged.Bank.BoxCount; box++)
                    if (archivedWallpapers.Get(box) is { } wallpaper) boxWallpapers.Set(box, wallpaper);
            }
        }

        // Restore points: sidecar last, as FileBackupService writes them, so a half copy is ignored.
        var points = MissingFiles(archive, appDataRoot, BackupsFolder).ToArray();
        foreach (var entry in points.Where(e => !e.Name.EndsWith(".json", StringComparison.Ordinal)).Concat(points.Where(e => e.Name.EndsWith(".json", StringComparison.Ordinal))))
            Extract(entry, appDataRoot, BackupsFolder);
        var photos = MissingFiles(archive, appDataRoot, AlbumFolder).ToArray();
        foreach (var entry in photos) Extract(entry, appDataRoot, AlbumFolder);
        // Game art: only well-named PNGs, and never over the player's own image here.
        foreach (var entry in MissingFiles(archive, appDataRoot, GameArtFolder).Where(e => CustomGameArt.ParseFileName(e.Name) is not null).ToArray())
            if (IsPng(entry)) Extract(entry, appDataRoot, GameArtFolder);

        var presets = new ItemPresetStore(Path.Combine(appDataRoot, ItemPresetsFile)).Merge(ArchivedPresets(archive));
        var monPresets = new MonPresetStore(Path.Combine(appDataRoot, MonPresetsFile)).Merge(ArchivedMonPresets(archive));
        var items = (itemBank ?? new ItemBankStore(Path.Combine(appDataRoot, ItemBankFolder))).Merge(ArchivedItemBank(archive));
        // Pokémon History merges record by record: a Pokémon known on both sides keeps every copy and trip.
        var historyStore = new FilePokemonHistoryStore(Path.Combine(appDataRoot, HistoryFolder));
        var history = ArchivedHistory(archive).Count(historyStore.Merge);

        var names = 0;
        foreach (var identity in ArchivedIdentities(archive))
        {
            if (identities.Get(identity.DocumentId) is not null) continue;
            identities.Set(identity);
            names++;
        }

        var markers = 0;
        foreach (var key in ArchivedGifts(archive))
        {
            if (gifts.IsInjected(key)) continue;
            gifts.Record(key);
            markers++;
        }

        return new DataImportResult(imported, duplicates,
            points.Count(e => e.Name.EndsWith(".json", StringComparison.Ordinal)),
            photos.Count(e => e.Name.EndsWith(".jpg", StringComparison.Ordinal)),
            presets, names, markers, items, monPresets, history);
    }

    /// <summary>The archive's bank, unpacked to a temporary folder and opened read-only in spirit.</summary>
    private static StagedBank StageBank(DataArchiveReader archive)
    {
        var directory = Directory.CreateTempSubdirectory("pkforge-import-").FullName;
        try
        {
            foreach (var entry in archive.EntriesIn(BankFolder))
                Extract(entry, directory, null);
            return new StagedBank(directory, new FileBankService(directory));
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private static (List<BankEntry> Fresh, int Duplicates) SplitBank(FileBankService source, IBankService bank)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in bank.GetAll())
            known.Add(Sha256(bank.GetData(entry.Id)));
        var fresh = new List<BankEntry>();
        var duplicates = 0;
        foreach (var entry in source.GetAll().OrderBy(e => e.Box).ThenBy(e => e.Slot))
        {
            if (known.Add(Sha256(source.GetData(entry.Id)))) fresh.Add(entry);
            else duplicates++;
        }
        return (fresh, duplicates);
    }

    private static IEnumerable<ZipArchiveEntry> MissingFiles(DataArchiveReader archive, string root, string folder) =>
        archive.EntriesIn(folder).Where(e => !File.Exists(Path.Combine(root, folder, e.Name)));

    private static void Extract(ZipArchiveEntry entry, string root, string? folder)
    {
        var directory = Path.GetFullPath(folder is null ? root : Path.Combine(root, folder));
        var target = Path.GetFullPath(Path.Combine(directory, entry.Name));
        // Names were checked on open; this is the second lock on the same door.
        if (!target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new DataArchiveException($"The archive was refused: \"{entry.FullName}\" has a path that points outside PKForge's data.");
        Directory.CreateDirectory(directory);
        var temporary = target + ".tmp";
        entry.ExtractToFile(temporary, overwrite: true);
        File.Move(temporary, target, overwrite: false);
    }

    // Archived lists may hold nulls (a hand-edited or foreign archive): they are skipped, never dereferenced.
    private static IReadOnlyList<ItemPreset> ArchivedPresets(DataArchiveReader archive) =>
        ItemPresetStore.Clean(archive.ReadData<List<ItemPreset>>(ItemPresetsFile) ?? []);

    private static IReadOnlyList<MonPreset> ArchivedMonPresets(DataArchiveReader archive) =>
        [.. (archive.ReadData<List<MonPreset>>(MonPresetsFile) ?? []).Where(p => p is { Id: not null, Name: not null })];

    private static ItemBank ArchivedItemBank(DataArchiveReader archive) =>
        archive.ReadData<ItemBankFile>(ItemBankEntry) is { } file
            ? new ItemBank([.. (file.Entries ?? []).Where(e => e is not null && e.Count > 0 && !string.IsNullOrWhiteSpace(e.Key))
                .Select(e => e with { Origins = [.. (e.Origins ?? []).Where(o => o is not null)] })])
            : ItemBank.Empty;

    private static int ItemBankRaised(string root, DataArchiveReader archive)
    {
        new ItemBankStore(Path.Combine(root, ItemBankFolder)).Read().Merge(ArchivedItemBank(archive), out var raised);
        return raised;
    }

    private static IEnumerable<PokemonHistoryRecord> ArchivedHistory(DataArchiveReader archive)
    {
        foreach (var entry in archive.EntriesIn(HistoryFolder).Where(e => e.Name.EndsWith(".json", StringComparison.Ordinal)))
        {
            string text;
            try
            {
                using var reader = new StreamReader(entry.Open());
                text = reader.ReadToEnd();
            }
            catch (InvalidDataException) { continue; }
            if (FilePokemonHistoryStore.Parse(text) is { } record) yield return record;
        }
    }

    /// <summary>Archived records this device does not know yet, or knows with less in them.</summary>
    private static int HistoryChanges(DataArchiveReader archive, FilePokemonHistoryStore local) =>
        ArchivedHistory(archive).Count(record => local.Find(record.Souls) is not { } mine
            || FilePokemonHistoryStore.Serialize(PokemonHistoryRecord.Merge(mine, record)) != FilePokemonHistoryStore.Serialize(mine));

    private static IReadOnlyList<SaveIdentity> ArchivedIdentities(DataArchiveReader archive) =>
        [.. (archive.ReadData<List<SaveIdentity>>(SaveIdentitiesFile) ?? []).Where(i => i is not null && !string.IsNullOrWhiteSpace(i.DocumentId) && !i.IsEmpty)];

    private static IReadOnlyList<string> ArchivedGifts(DataArchiveReader archive) =>
        [.. (archive.ReadData<string[]>(GiftsFile) ?? []).Where(key => !string.IsNullOrEmpty(key))];

    private static HashSet<string> LocalIdentityIds(string root) =>
        [.. (ReadJson<List<SaveIdentity>>(Path.Combine(root, SaveIdentitiesFile)) ?? []).Where(i => i?.DocumentId is not null).Select(i => i.DocumentId)];

    private static T? ReadJson<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return null; }
    }

    private static bool IsPng(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            Span<byte> head = stackalloc byte[9];
            return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && CustomGameArt.LooksLikePng(head);
        }
        catch (InvalidDataException) { return false; }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed record StagedBank(string Directory, FileBankService Bank) : IDisposable
    {
        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException) { /* a temp folder left behind is cleared by the system */ }
        }
    }
}

/// <summary>An opened, checked PKForge data archive.</summary>
public sealed class DataArchiveReader : IDisposable
{
    private readonly ZipArchive _zip;

    internal DataArchiveReader(ZipArchive zip, DataArchiveManifest manifest, IReadOnlyList<PreferenceValue> preferences)
    {
        _zip = zip;
        Manifest = manifest;
        Preferences = preferences;
    }

    public DataArchiveManifest Manifest { get; }

    public IReadOnlyList<PreferenceValue> Preferences { get; }

    internal IEnumerable<ZipArchiveEntry> EntriesIn(string folder) =>
        _zip.Entries.Where(e => e.Name.Length > 0 && e.FullName == $"data/{folder}/{e.Name}");

    /// <summary>One of the archive's JSON data files (the item presets and save names ones are
    /// the app's own serialization, PascalCase), or null when absent or unreadable.</summary>
    internal T? ReadData<T>(string file) where T : class
    {
        if (_zip.GetEntry("data/" + file) is not { } entry) return null;
        try
        {
            using var stream = entry.Open();
            return file switch
            {
                "item-presets.json" => JsonSerializer.Deserialize(stream, ItemPresetJsonContext.Default.ListItemPreset) as T,
                "mon-presets.json" => JsonSerializer.Deserialize(stream, MonPresetJsonContext.Default.ListMonPreset) as T,
                DataArchive.ItemBankEntry => JsonSerializer.Deserialize(stream, ItemBankJsonContext.Default.ItemBankFile) as T,
                _ => JsonSerializer.Deserialize<T>(stream),
            };
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
        {
            throw new DataArchiveException($"The archive's {file} is damaged and cannot be read.");
        }
    }

    public void Dispose() => _zip.Dispose();
}
