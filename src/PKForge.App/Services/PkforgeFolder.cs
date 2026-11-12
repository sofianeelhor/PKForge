using System.Text.Json;
using PKForge.Domain;
using PKForge.Infrastructure;

namespace PKForge.App.Services;

/// <summary>
/// The PKForge folder: a folder the player picks (Android SAF tree) kept as a two-way mirror of
/// PKForge's private data. PKForge keeps working on its private copy; this keeps the folder in
/// step on launch, on resume, ~3 s after any data write and on "Sync now". One sync at a time,
/// always off the UI thread; a failure is reported and never touches the private data.
/// </summary>
public sealed class PkforgeFolder
{
    private const string TreeKey = "pkforge-folder.tree";
    private const string NameKey = "pkforge-folder.name";
    private const string ReportKey = "pkforge-folder.report";
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private readonly IServiceProvider _services;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _pending;
    private FileSystemWatcher? _watcher;
    private volatile bool _syncing;
    private bool _started;

    public PkforgeFolder(IServiceProvider services) => _services = services;

    /// <summary>Raised (on any thread) when a sync starts or ends.</summary>
    public event Action? Changed;

    private static string Root => FileSystem.AppDataDirectory;
    private static string StatePath => Path.Combine(Root, "mirror-state.json");
    public static RecentlyRemovedStore RecentlyRemoved { get; } = new(Path.Combine(Root, "mirror-removed"));

    public PickedFolder? Folder
    {
        get
        {
            var tree = Preferences.Default.Get(TreeKey, string.Empty);
            if (tree.Length == 0) return null;
            // SAF names a folder "primary:PKForge"; the player knows it as "PKForge".
            var name = Preferences.Default.Get(NameKey, "PKForge folder");
            return new PickedFolder(tree, name[(name.LastIndexOf(':') + 1)..] is { Length: > 0 } shown ? shown : name);
        }
    }

    public bool IsSyncing => _syncing;

    public record Stored(DateTimeOffset FinishedUtc, int Pushed, int Pulled, int Removed, List<string> Conflicts, List<string> LeftAlone, string? Error, string Summary);

    public Stored? LastReport
    {
        get
        {
            try { return JsonSerializer.Deserialize<Stored>(Preferences.Default.Get(ReportKey, string.Empty)); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>Uses <paramref name="folder"/> from now on. A new folder starts from a first sync
    /// (a union: everything here is copied in, nothing in either place is deleted).</summary>
    public void Use(PickedFolder folder)
    {
        if (Folder?.TreeId != folder.TreeId)
        {
            try { File.Delete(StatePath); } catch (IOException) { }
        }
        Preferences.Default.Set(TreeKey, folder.TreeId);
        Preferences.Default.Set(NameKey, folder.DisplayName);
        Preferences.Default.Remove(ReportKey);
        Start(syncNow: false); // the caller runs the first sync itself, with its progress shown
    }

    /// <summary>Stops syncing. Both copies stay as they are.</summary>
    public void Stop()
    {
        Preferences.Default.Remove(TreeKey);
        Preferences.Default.Remove(NameKey);
        Preferences.Default.Remove(ReportKey);
        try { File.Delete(StatePath); } catch (IOException) { }
        lock (_gate) _pending?.Cancel();
        Changed?.Invoke();
    }

    /// <summary>Watches the private data for writes and syncs once at launch and on every resume.</summary>
    public void Start(bool syncNow = true)
    {
        if (Folder is null) return;
        if (!_started)
        {
            _started = true;
            App.Resumed += () => RequestSync(TimeSpan.Zero);
            try
            {
                _watcher = new FileSystemWatcher(Root) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                _watcher.Changed += OnWrite;
                _watcher.Created += OnWrite;
                _watcher.Deleted += OnWrite;
                _watcher.Renamed += OnWrite;
            }
            catch (Exception error) when (error is IOException or ArgumentException or PlatformNotSupportedException)
            {
                AppLog.Warn("folder", $"Data writes cannot be watched; the folder syncs on launch, resume and Sync now ({error.Message})");
            }
        }
        if (syncNow) RequestSync(TimeSpan.Zero);
    }

    private void OnWrite(object sender, FileSystemEventArgs change)
    {
        if (_syncing || Folder is null) return;
        var path = Path.GetRelativePath(Root, change.FullPath).Replace('\\', '/');
        if (path.StartsWith("bank/") || path.StartsWith("pokepark-album/") || path.StartsWith("backups/") || path.StartsWith(CustomGameArt.Folder + "/") || path == "item-presets.json" || path == "item-bank/item-bank.json" || path == "mon-presets.json")
            RequestSync(Debounce);
    }

    /// <summary>Syncs after <paramref name="delay"/>; a newer request replaces a waiting one.</summary>
    public void RequestSync(TimeSpan delay)
    {
        if (Folder is null) return;
        CancellationTokenSource next;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending = next = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, next.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await SyncNowAsync().ConfigureAwait(false);
        });
    }

    /// <summary>Runs one sync now (waiting for one already running). Never throws.</summary>
    public async Task<Stored?> SyncNowAsync()
    {
        if (Folder is not { } folder) return null;
        await _one.WaitAsync().ConfigureAwait(false);
        _syncing = true;
        Changed?.Invoke();
        try
        {
            return await Task.Run(async () =>
            {
                MirrorReport report;
                try
                {
                    RecentlyRemoved.Prune(DateTimeOffset.UtcNow);
                    var access = (IFolderTreeAccess)_services.GetRequiredService<IFolderFileAccess>();
                    var (result, state) = await FolderMirrorSync.SyncAsync(access, folder.TreeId, Sources(), MirrorState.Load(StatePath), DateTimeOffset.Now).ConfigureAwait(false);
                    state.Save(StatePath);
                    report = result;
                    AppLog.Info("folder", $"PKForge folder synced: {report.Summary}");
                    foreach (var line in report.LeftAlone) AppLog.Info("folder", $"Left alone: {line}");
                }
                catch (Exception error)
                {
                    // A revoked grant, a provider error: the private data is untouched, the next sync retries.
                    AppLog.Error("folder", "Syncing the PKForge folder failed", error);
                    report = new MirrorReport(DateTimeOffset.Now, 0, 0, 0, 0, [], [], error.Message);
                }
                var stored = new Stored(report.FinishedUtc, report.Pushed, report.Pulled, report.DeletedRemote + report.MovedToRecentlyRemoved,
                    [.. report.Conflicts], [.. report.LeftAlone], report.Error, report.Summary);
                Preferences.Default.Set(ReportKey, JsonSerializer.Serialize(stored));
                return stored;
            }).ConfigureAwait(false);
        }
        finally
        {
            // Writes the sync itself made (pulled Pokémon, photos) must not schedule another.
            await Task.Delay(500).ConfigureAwait(false);
            _syncing = false;
            _one.Release();
            Changed?.Invoke();
        }
    }

    private IReadOnlyList<IMirrorSource> Sources()
    {
        var engine = _services.GetRequiredService<ISaveEngine>();
        var bank = _services.GetRequiredService<IBankService>();
        var names = new BankBoxNames(Path.Combine(Root, "bank"));
        var species = PKHeX.Core.GameInfo.GetStrings("en").specieslist;
        return
        [
            new BankMirrorSource(bank, names, (bytes, file, format) => engine.TryDescribeEntity(bytes, file, format),
                info => (uint)info.Species < (uint)species.Length ? species[info.Species] : $"Species {info.Species}", RecentlyRemoved),
            new DirectoryMirrorSource("Album/", PokeparkAlbum.Folder, RecentlyRemoved),
            // History records merge by Pokémon, never side by side as conflict copies.
            new HistoryMirrorSource(PokemonHistorySetting.FileStore, RecentlyRemoved),
            FileCustomGameArtStore.MirrorSource(Path.Combine(Root, CustomGameArt.Folder), RecentlyRemoved),
            new FileMirrorSource("Items/item-presets.json", Path.Combine(Root, "item-presets.json"), RecentlyRemoved, ItemPresetStore.IsLibrary),
            new FileMirrorSource("Pokemon/mon-presets.json", Path.Combine(Root, "mon-presets.json"), RecentlyRemoved, MonPresetStore.IsLibrary),
            // The Item Bank merges item by item through its store (and its lock), never replaced whole.
            new ItemBankMirrorSource(_services.GetRequiredService<ItemBankStore>(), Path.Combine(Root, "mirror-item-bank.json"), RecentlyRemoved),
            new FileMirrorSource("Walker/walk.json", PokewalkerStrolls.FilePath, RecentlyRemoved, IsJson),
            new PinnedSavesMirrorSource(_services.GetRequiredService<IBackupService>()),
        ];
    }

    /// <summary>A pulled walk is taken only when it is whole JSON; a half-synced file is left for the next pass.</summary>
    private static bool IsJson(byte[] bytes)
    {
        try { using var _ = System.Text.Json.JsonDocument.Parse(bytes); return true; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>Writes files into the folder's Exports/ (never replacing one there); how many.</summary>
    public async Task<int> ExportAsync(IReadOnlyList<PkFile> files, string? subfolder = null)
    {
        if (Folder is not { } folder) throw new InvalidOperationException("No PKForge folder is set.");
        var access = (IFolderTreeAccess)_services.GetRequiredService<IFolderFileAccess>();
        var prefix = "Exports/" + (subfolder is null ? "" : PkFileExport.SafeFileName(subfolder) + "/");
        var taken = new HashSet<string>((await access.ListTreeAsync(folder.TreeId).ConfigureAwait(false))
            .Where(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path[prefix.Length..]), StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            await access.WriteAsync(folder.TreeId, prefix + PkFileExport.Unique(PkFileExport.SafeFileName(file.FileName), taken), file.Data).ConfigureAwait(false);
        return files.Count;
    }
}
