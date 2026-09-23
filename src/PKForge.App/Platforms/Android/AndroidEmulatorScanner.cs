using Android.Provider;
using PKForge.Domain;
using PKForge.Infrastructure;
using AndroidUri = Android.Net.Uri;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace PKForge.App;

/// <summary>
/// Walks a granted SAF tree looking for parseable Pokémon saves. File-name filters are only a
/// pre-filter; every candidate's bytes must pass engine validation before being reported.
/// </summary>
public sealed class AndroidEmulatorScanner(ISaveEngine engine) : IIncrementalEmulatorDetectionService
{
    private const int EdenMaxDepth = 8;

    private sealed class ScanState
    {
        public int FilesSeen;
        /// <summary>Names in the folder currently being walked, for ROM-name hints.</summary>
        public IReadOnlyList<string>? Siblings;
        public readonly List<string> Rejected = [];
        public readonly List<string> Diagnostics = [];

        public void Trace(string message)
        {
            const int maxLines = 2000;
            if (Diagnostics.Count < maxLines)
                Diagnostics.Add(message);
            else if (Diagnostics.Count == maxLines)
                Diagnostics.Add("DIAGNOSTICS TRUNCATED after 2000 lines");
        }
    }

    public ValueTask<EmulatorScanResult> ScanAsync(string treeId, EmulatorKind kind, CancellationToken cancellationToken = default)
    {
        return new ValueTask<EmulatorScanResult>(Task.Run(() =>
            ScanCore(treeId, kind, cancellationToken, null), cancellationToken));
    }

    public async IAsyncEnumerable<EmulatorScanUpdate> ScanIncrementalAsync(
        string treeId, EmulatorKind kind,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<EmulatorScanUpdate>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        var producer = Task.Run(() =>
        {
            try
            {
                var result = ScanCore(treeId, kind, cancellationToken,
                    save => channel.Writer.TryWrite(new EmulatorScanUpdate(save, false)));
                channel.Writer.TryWrite(new EmulatorScanUpdate(
                    null, true, result.FilesSeen, result.Saves.Count,
                    result.RejectedCandidates, result.Diagnostics));
                ScanCache.Flush();
                channel.Writer.TryComplete();
            }
            catch (Exception error)
            {
                channel.Writer.TryComplete(error);
            }
        });

        await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
            yield return update;
        await producer;
    }

    private EmulatorScanResult ScanCore(
        string treeId, EmulatorKind kind, CancellationToken cancellationToken,
        Action<DetectedSave>? onSave)
    {
        var treeUri = AndroidUri.Parse(treeId) ?? throw new ArgumentException("Invalid tree URI.", nameof(treeId));
        var rootDocId = DocumentsContract.GetTreeDocumentId(treeUri)
            ?? throw new InvalidOperationException("The folder grant has no tree document id.");
        var state = new ScanState();
        state.Trace($"Scanner={nameof(AndroidEmulatorScanner)} kind={kind}");
        state.Trace($"TreeUri={treeUri}");
        state.Trace($"RootDocId={rootDocId}");
        List<DetectedSave> found;
        try
        {
            found = kind switch
            {
                EmulatorKind.RetroArch or EmulatorKind.MelonDS or EmulatorKind.Linkboy or
                EmulatorKind.DraStic or EmulatorKind.PizzaBoyGba or EmulatorKind.PizzaBoyGbc or EmulatorKind.Dolphin =>
                    ScanFlatFolder(treeUri, rootDocId, kind, cancellationToken, state, onSave),
                EmulatorKind.Eden => ScanEden(treeUri, rootDocId, cancellationToken, state, onSave),
                EmulatorKind.Azahar => ScanAzahar(treeUri, rootDocId, cancellationToken, state, onSave),
                _ => [],
            };
        }
        catch (Exception error)
        {
            if (error is OperationCanceledException)
                throw;
            state.Trace($"SCAN EXCEPTION {error}");
            found = [];
        }
        state.Trace($"Scanner complete files={state.FilesSeen} saves={found.Count} rejected={state.Rejected.Count}");
        ScanCache.Flush();
        return new EmulatorScanResult(EmulatorSaveHeuristics.Normalize(found), state.FilesSeen,
            [.. state.Rejected], [.. state.Diagnostics]);
    }

    /// <summary>Emulator folders whose contents can never be saves - pruned so a whole-RetroArch grant stays fast.</summary>
    private static readonly HashSet<string> PrunedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets", "autoconfig", "cheats", "config", "cores", "database", "downloads", "filters",
        "info", "logs", "overlays", "playlists", "remaps", "screenshots", "shaders", "states",
        "system", "thumbnails", "cache", "temp", "roms", "shader_cache",
    };

    /// <summary>
    /// Battery saves and GCI files can be nested: RetroArch uses per-core folders,
    /// DraStic uses backup/, and Dolphin uses GC/&lt;region&gt;/Card A or Card B.
    /// Prefer the emulator's save subtrees when its whole data folder was granted.
    /// Direct save-folder grants and custom locations are also supported.
    /// </summary>
    private List<DetectedSave> ScanFlatFolder(AndroidUri treeUri, string rootDocId, EmulatorKind kind,
        CancellationToken cancellationToken, ScanState state, Action<DetectedSave>? onSave)
    {
        const int maxDepth = 8;
        var results = new List<DetectedSave>();
        void OnFile(ChildDocument child)
        {
            state.FilesSeen++;
            if (kind == EmulatorKind.Dolphin && EmulatorSaveHeuristics.IsDolphinMemoryCard(child.Name))
            {
                state.Rejected.Add($"{child.Name}: export Colosseum/XD as GCI with Dolphin's Memory Card Manager, or use GCI Folder for the card slot.");
                state.Trace($"RAW MEMORY CARD {child.Name}: individual GCI export required");
                return;
            }
            if (!EmulatorSaveHeuristics.IsCandidateFileName(child.Name, kind)) return;
            if (TryDetect(treeUri, child, kind, gameLabel: child.Name, state: state) is { } save)
            {
                results.Add(save);
                onSave?.Invoke(save);
            }
            else
                state.Rejected.Add(child.Name);
        }

        var rootChildren = ListChildren(treeUri, rootDocId);
        string[] preferredFolders = kind switch
        {
            EmulatorKind.Dolphin => ["GC"],
            EmulatorKind.DraStic => ["backup"],
            EmulatorKind.PizzaBoyGba or EmulatorKind.PizzaBoyGbc => ["save", "saves"],
            _ => ["saves"],
        };
        var savesDirs = rootChildren.Where(x => x.IsDirectory && preferredFolders.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (savesDirs.Length > 0)
        {
            state.Siblings = rootChildren.Where(x => !x.IsDirectory).Select(x => x.Name).ToArray();
            foreach (var file in rootChildren.Where(x => !x.IsDirectory))
                OnFile(file);
            foreach (var savesDir in savesDirs)
                FindFilesRecursive(treeUri, savesDir.DocId, maxDepth, cancellationToken, OnFile, state);
        }
        else
        {
            FindFilesRecursive(treeUri, rootDocId, maxDepth, cancellationToken, OnFile, state);
        }
        return results;
    }

    /// <summary>
    /// Eden (Switch): saves are files named "main" (or "*.bin" for BDSP) under nand/user/save/…
    /// The user may have granted the files root, nand/, or user/ - try each prefix.
    /// </summary>
    private List<DetectedSave> ScanEden(AndroidUri treeUri, string rootDocId, CancellationToken cancellationToken,
        ScanState state, Action<DetectedSave>? onSave)
    {
        string[][] prefixes = [["nand", "user", "save"], ["user", "save"], ["save"]];
        string? saveDirDocId = null;
        foreach (var prefix in prefixes)
        {
            state.Trace($"Trying Eden prefix {string.Join('/', prefix)}");
            saveDirDocId = NavigatePath(treeUri, rootDocId, prefix, state.Trace);
            state.Trace(saveDirDocId is null ? "Prefix not found" : $"Save root found: {saveDirDocId}");
            if (saveDirDocId is not null)
                break;
        }
        if (saveDirDocId is null)
        {
            state.Trace("EDEN FAILURE: none of nand/user/save, user/save, or save exists under the granted root");
            return [];
        }

        var results = new List<DetectedSave>();
        FindFilesRecursive(treeUri, saveDirDocId, EdenMaxDepth, cancellationToken, child =>
        {
            state.FilesSeen++;
            if (!EmulatorSaveHeuristics.IsEdenSaveFileName(child.Name))
            {
                state.Trace($"SKIP file name={child.Name} docId={child.DocId}");
                return;
            }
            state.Trace($"CANDIDATE name={child.Name} modified={child.LastModified?.ToString("O") ?? "unknown"} docId={child.DocId}");
            var label = EmulatorSaveHeuristics.GuessSwitchGameLabel(child.DocId);
            if (TryDetect(treeUri, child, EmulatorKind.Eden, label, state) is { } save)
            {
                results.Add(save);
                onSave?.Invoke(save);
            }
            else
                state.Rejected.Add(child.Name);
        }, state, diagnostic: true);
        return results;
    }

    /// <summary>Azahar (3DS): root/sdmc/Nintendo 3DS/&lt;ID0&gt;/&lt;ID1&gt;/title/00040000/&lt;game&gt;/data/00000001/main.</summary>
    private List<DetectedSave> ScanAzahar(AndroidUri treeUri, string rootDocId, CancellationToken cancellationToken,
        ScanState state, Action<DetectedSave>? onSave)
    {
        var results = new List<DetectedSave>();
        var n3ds = NavigatePath(treeUri, rootDocId, ["sdmc", "Nintendo 3DS"]);
        if (n3ds is null) return results;

        foreach (var id0 in ListChildren(treeUri, n3ds).Where(x => x.IsDirectory))
        foreach (var id1 in ListChildren(treeUri, id0.DocId).Where(x => x.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var retail = NavigatePath(treeUri, id1.DocId, ["title", "00040000"]);
            if (retail is null) continue;

            foreach (var game in ListChildren(treeUri, retail).Where(x => x.IsDirectory))
            {
                var data = NavigatePath(treeUri, game.DocId, ["data", "00000001"]);
                if (data is null) continue;
                var main = ListChildren(treeUri, data).FirstOrDefault(x => !x.IsDirectory && x.Name == "main");
                if (main is null) continue;
                if (TryDetect(treeUri, main, EmulatorKind.Azahar, gameLabel: $"3DS save ({game.Name})", state: state) is { } save)
                {
                    results.Add(save);
                    onSave?.Invoke(save);
                }
            }
        }
        return results;
    }

    /// <summary>Reads a candidate's bytes and reports it only if the engine can parse them.</summary>
    private DetectedSave? TryDetect(AndroidUri treeUri, ChildDocument child, EmulatorKind kind,
        string gameLabel, ScanState state)
    {
        try
        {
            var documentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, child.DocId);
            if (documentUri is null)
            {
                state.Trace("REJECT document URI could not be built");
                return null;
            }
            state.Trace($"DocumentUri={documentUri}");

            // A file already parsed at this modification time never gets re-read: rescans
            // are instant. The install epoch in the key makes each fresh APK re-read every
            // file exactly once, so detection improvements (new romhack labels, say) reach
            // saves whose timestamps have not changed since the previous install.
            var cacheKey = documentUri + "#" + kind + "#" + InstallEpoch;
            var modifiedTicks = child.LastModified?.UtcTicks ?? 0;
            if (ScanCache.TryGet(cacheKey, modifiedTicks, out var cached))
                return cached;

            using var stream = Platform.AppContext.ContentResolver?.OpenInputStream(documentUri);
            if (stream is null)
            {
                state.Trace("REJECT ContentResolver.OpenInputStream returned null");
                return null;
            }
            // Saves are small; a matching extension on a ROM/archive must not stall the scan.
            const long maxSaveBytes = 32 * 1024 * 1024;
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxSaveBytes)
                {
                    state.Trace($"REJECT larger than {maxSaveBytes} bytes");
                    return null;
                }
            }
            if (buffer.Length == 0)
            {
                state.Trace("REJECT zero-byte file");
                return null;
            }

            var bytes = buffer.ToArray();
            var headerLength = Math.Min(16, bytes.Length);
            var bdspSize = bytes.Length is 956456 or 973856 or 978316 or 979108;
            state.Trace($"BYTES length={bytes.Length} header16={Convert.ToHexString(bytes.AsSpan(0, headerLength))} knownBdspSize={bdspSize}");

            // Describe consumes a copy: the engine decrypts Switch saves in place during parsing.
            var description = engine.TryDescribe(bytes, child.Name);
            if (description is null)
            {
                state.Trace("PARSER REJECTED: ISaveEngine.TryDescribe returned null");
                ScanCache.Store(cacheKey, modifiedTicks, null);
                return null;
            }
            state.Trace($"PARSER ACCEPTED game={description.GameName} generation={description.Generation} trainer={description.TrainerName} playTime={description.PlayTime}");

            // The label comes from the bytes alone; the save/ROM name is only a Rename prefill.
            var romName = EmulatorSaveHeuristics.FindSiblingRom(child.Name, state.Siblings);
            var guess = SaveIdentityRules.Guess(description.GameName, description.Generation, child.Name, romName, gameLabel);
            state.Trace($"IDENTITY label={guess.Label} family={guess.Family} format={guess.Format} language={description.Language ?? "-"} rom={romName ?? "-"}");
            var detected = new DetectedSave(
                documentUri.ToString()!,
                child.Name,
                guess.Label,
                kind,
                EmulatorSaveHeuristics.RequiresExtraCare(kind),
                child.LastModified,
                description.Generation,
                description.TrainerName,
                description.PlayTime,
                guess,
                romName,
                EmulatorSaveHeuristics.FolderHint(child.DocId),
                Language: description.Language);
            ScanCache.Store(cacheKey, modifiedTicks, detected);
            return detected;
        }
        catch (Exception error)
        {
            state.Trace($"CANDIDATE EXCEPTION {error}");
            return null; // unreadable candidates are simply not saves
        }
    }

    private sealed record ChildDocument(string DocId, string Name, bool IsDirectory, DateTimeOffset? LastModified);

    /// <summary>Android's last package update time: zero-once per install, stable after.</summary>
    private static long _installEpoch = -1;

    private static long InstallEpoch
    {
        get
        {
            if (_installEpoch >= 0) return _installEpoch;
            try
            {
                var context = Platform.AppContext;
                _installEpoch = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)?.LastUpdateTime ?? 0;
            }
            catch
            {
                _installEpoch = 0; // unbustable cache is still better than a crashing scan
            }
            return _installEpoch;
        }
    }

    /// <summary>
    /// Persistent parse cache keyed by document URI + last-modified time. A null entry
    /// records "this file is not a save" so rescans skip reading it entirely.
    /// </summary>
    private static class ScanCache
    {
        // Detection support can expand between releases. Keep cached negative
        // results versioned so a newly supported save is always retried once.
        private const string Key = "scan_cache_v4"; // v4: identity comes from bytes only, plus the save language
        private const int MaxEntries = 512;
        private static Dictionary<string, CacheEntry>? _entries;
        private static readonly Lock Gate = new();
        private static bool _dirty;

        private sealed record CacheEntry(long ModifiedTicks, DetectedSave? Save);

        public static bool TryGet(string documentId, long modifiedTicks, out DetectedSave? save)
        {
            lock (Gate)
            {
                Load();
                if (_entries!.TryGetValue(documentId, out var entry) && entry.ModifiedTicks == modifiedTicks && modifiedTicks != 0)
                {
                    save = entry.Save;
                    return true;
                }
            }
            save = null;
            return false;
        }

        public static void Store(string documentId, long modifiedTicks, DetectedSave? save)
        {
            if (modifiedTicks == 0) return; // no timestamp, no safe caching
            lock (Gate)
            {
                Load();
                _entries![documentId] = new CacheEntry(modifiedTicks, save);
                while (_entries.Count > MaxEntries)
                    _entries.Remove(_entries.Keys.First());
                _dirty = true;
            }
        }

        public static void Flush()
        {
            lock (Gate)
            {
                if (!_dirty || _entries is null) return;
                Preferences.Default.Set(Key, System.Text.Json.JsonSerializer.Serialize(_entries));
                _dirty = false;
            }
        }

        private static void Load()
        {
            if (_entries is not null) return;
            try
            {
                var raw = Preferences.Default.Get(Key, string.Empty);
                _entries = string.IsNullOrEmpty(raw)
                    ? []
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(raw) ?? [];
            }
            catch
            {
                _entries = [];
            }
        }
    }

    private void FindFilesRecursive(AndroidUri treeUri, string parentDocId, int maxDepth,
        CancellationToken cancellationToken, Action<ChildDocument> onFile, ScanState state,
        bool diagnostic = false)
    {
        if (maxDepth <= 0)
        {
            if (diagnostic) state.Trace($"DEPTH LIMIT reached at {parentDocId}");
            return;
        }
        var children = ListChildren(treeUri, parentDocId);
        var siblings = children.Where(x => !x.IsDirectory).Select(x => x.Name).ToArray();
        if (diagnostic) state.Trace($"WALK depthRemaining={maxDepth} parent={parentDocId} children={children.Count}");
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.IsDirectory)
            {
                if (diagnostic) state.Trace($"DIR name={child.Name} docId={child.DocId}");
                if (PrunedDirectories.Contains(child.Name))
                {
                    if (diagnostic) state.Trace($"PRUNED directory {child.Name}");
                    continue;
                }
                FindFilesRecursive(treeUri, child.DocId, maxDepth - 1, cancellationToken, onFile, state, diagnostic);
            }
            else
            {
                state.Siblings = siblings;
                onFile(child);
            }
        }
    }

    private static string? NavigatePath(AndroidUri treeUri, string fromDocId, string[] segments, Action<string>? trace = null)
    {
        var current = fromDocId;
        foreach (var segment in segments)
        {
            var children = ListChildren(treeUri, current);
            trace?.Invoke($"At {current}: {children.Count} children [{string.Join(", ", children.Select(x => x.IsDirectory ? x.Name + "/" : x.Name))}]");
            var next = children.FirstOrDefault(x => x.IsDirectory && x.Name == segment);
            if (next is null)
            {
                trace?.Invoke($"Missing directory segment '{segment}'");
                return null;
            }
            trace?.Invoke($"Matched '{segment}' -> {next.DocId}");
            current = next.DocId;
        }
        return current;
    }

    private static List<ChildDocument> ListChildren(AndroidUri treeUri, string parentDocId)
    {
        var results = new List<ChildDocument>();
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentDocId);
        if (childrenUri is null) return results;

        string[] projection =
        [
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType,
            DocumentsContract.Document.ColumnLastModified,
        ];
        using var cursor = Platform.AppContext.ContentResolver?.Query(childrenUri, projection, null, null, null);
        if (cursor is null) return results;

        while (cursor.MoveToNext())
        {
            var docId = cursor.GetString(0);
            var name = cursor.GetString(1);
            if (docId is null || name is null) continue;
            var isDirectory = cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir;
            var modifiedMs = cursor.GetLong(3);
            results.Add(new ChildDocument(docId, name, isDirectory,
                modifiedMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(modifiedMs) : null));
        }
        return results;
    }
}
