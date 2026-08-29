using Android.Provider;
using PKForge.Domain;
using PKForge.Infrastructure;
using AndroidUri = Android.Net.Uri;

namespace PKForge.App;

/// <summary>
/// Walks a granted SAF tree looking for parseable Pokémon saves. File-name filters are only a
/// pre-filter; every candidate's bytes must pass engine validation before being reported.
/// </summary>
public sealed class AndroidEmulatorScanner(ISaveEngine engine) : IEmulatorDetectionService
{
    private const int EdenMaxDepth = 8;

    private int _filesSeen;
    private readonly List<string> _rejected = [];
    private readonly List<string> _diagnostics = [];

    private void Trace(string message)
    {
        const int maxLines = 2000;
        if (_diagnostics.Count < maxLines)
            _diagnostics.Add(message);
        else if (_diagnostics.Count == maxLines)
            _diagnostics.Add("DIAGNOSTICS TRUNCATED after 2000 lines");
    }

    public ValueTask<EmulatorScanResult> ScanAsync(string treeId, EmulatorKind kind, CancellationToken cancellationToken = default)
    {
        return new ValueTask<EmulatorScanResult>(Task.Run(() =>
        {
            var treeUri = AndroidUri.Parse(treeId) ?? throw new ArgumentException("Invalid tree URI.", nameof(treeId));
            var rootDocId = DocumentsContract.GetTreeDocumentId(treeUri)
                ?? throw new InvalidOperationException("The folder grant has no tree document id.");

            _filesSeen = 0;
            _rejected.Clear();
            _diagnostics.Clear();
            Trace($"Scanner={nameof(AndroidEmulatorScanner)} kind={kind}");
            Trace($"TreeUri={treeUri}");
            Trace($"RootDocId={rootDocId}");
            List<DetectedSave> found;
            try
            {
                found = kind switch
                {
                    EmulatorKind.RetroArch or EmulatorKind.MelonDS or EmulatorKind.Linkboy => ScanFlatFolder(treeUri, rootDocId, kind, cancellationToken),
                    EmulatorKind.Eden => ScanEden(treeUri, rootDocId, cancellationToken),
                    EmulatorKind.Azahar => ScanAzahar(treeUri, rootDocId, cancellationToken),
                    _ => [],
                };
            }
            catch (Exception error)
            {
                Trace($"SCAN EXCEPTION {error}");
                found = [];
            }
            Trace($"Scanner complete files={_filesSeen} saves={found.Count} rejected={_rejected.Count}");
            return new EmulatorScanResult(EmulatorSaveHeuristics.Normalize(found), _filesSeen, [.. _rejected], [.. _diagnostics]);
        }, cancellationToken));
    }

    /// <summary>Emulator folders whose contents can never be saves - pruned so a whole-RetroArch grant stays fast.</summary>
    private static readonly HashSet<string> PrunedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets", "autoconfig", "cheats", "config", "cores", "database", "downloads", "filters",
        "info", "logs", "overlays", "playlists", "remaps", "screenshots", "shaders", "states",
        "system", "thumbnails", "cache", "temp", "roms", "shader_cache",
    };

    /// <summary>
    /// RetroArch and melonDS saves are flat files, but RetroArch commonly sorts them into
    /// per-core subfolders (saves/&lt;core&gt;/*.srm). If the granted folder contains a
    /// "saves" directory (i.e. the user granted the whole emulator folder), only that
    /// subtree is walked; junk directories are pruned either way.
    /// </summary>
    private List<DetectedSave> ScanFlatFolder(AndroidUri treeUri, string rootDocId, EmulatorKind kind, CancellationToken cancellationToken)
    {
        const int maxDepth = 4;
        var results = new List<DetectedSave>();
        void OnFile(ChildDocument child)
        {
            _filesSeen++;
            if (!EmulatorSaveHeuristics.IsCandidateFileName(child.Name)) return;
            if (TryDetect(treeUri, child, kind, gameLabel: child.Name) is { } save)
                results.Add(save);
            else
                _rejected.Add(child.Name);
        }

        var rootChildren = ListChildren(treeUri, rootDocId);
        var savesDir = rootChildren.FirstOrDefault(x => x.IsDirectory && x.Name.Equals("saves", StringComparison.OrdinalIgnoreCase));
        if (savesDir is not null)
        {
            foreach (var file in rootChildren.Where(x => !x.IsDirectory))
                OnFile(file);
            FindFilesRecursive(treeUri, savesDir.DocId, maxDepth, cancellationToken, OnFile);
        }
        else
        {
            FindFilesRecursive(treeUri, rootDocId, maxDepth, cancellationToken, OnFile);
        }
        return results;
    }

    /// <summary>
    /// Eden (Switch): saves are files named "main" (or "*.bin" for BDSP) under nand/user/save/…
    /// The user may have granted the files root, nand/, or user/ - try each prefix.
    /// </summary>
    private List<DetectedSave> ScanEden(AndroidUri treeUri, string rootDocId, CancellationToken cancellationToken)
    {
        string[][] prefixes = [["nand", "user", "save"], ["user", "save"], ["save"]];
        string? saveDirDocId = null;
        foreach (var prefix in prefixes)
        {
            Trace($"Trying Eden prefix {string.Join('/', prefix)}");
            saveDirDocId = NavigatePath(treeUri, rootDocId, prefix, Trace);
            Trace(saveDirDocId is null ? "Prefix not found" : $"Save root found: {saveDirDocId}");
            if (saveDirDocId is not null)
                break;
        }
        if (saveDirDocId is null)
        {
            Trace("EDEN FAILURE: none of nand/user/save, user/save, or save exists under the granted root");
            return [];
        }

        var results = new List<DetectedSave>();
        FindFilesRecursive(treeUri, saveDirDocId, EdenMaxDepth, cancellationToken, child =>
        {
            _filesSeen++;
            if (!EmulatorSaveHeuristics.IsEdenSaveFileName(child.Name))
            {
                Trace($"SKIP file name={child.Name} docId={child.DocId}");
                return;
            }
            Trace($"CANDIDATE name={child.Name} modified={child.LastModified?.ToString("O") ?? "unknown"} docId={child.DocId}");
            var label = EmulatorSaveHeuristics.GuessSwitchGameLabel(child.DocId);
            if (TryDetect(treeUri, child, EmulatorKind.Eden, label) is { } save)
                results.Add(save);
            else
                _rejected.Add(child.Name);
        }, diagnostic: true);
        return results;
    }

    /// <summary>Azahar (3DS): root/sdmc/Nintendo 3DS/&lt;ID0&gt;/&lt;ID1&gt;/title/00040000/&lt;game&gt;/data/00000001/main.</summary>
    private List<DetectedSave> ScanAzahar(AndroidUri treeUri, string rootDocId, CancellationToken cancellationToken)
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
                if (TryDetect(treeUri, main, EmulatorKind.Azahar, gameLabel: $"3DS save ({game.Name})") is { } save)
                    results.Add(save);
            }
        }
        return results;
    }

    /// <summary>Reads a candidate's bytes and reports it only if the engine can parse them.</summary>
    private DetectedSave? TryDetect(AndroidUri treeUri, ChildDocument child, EmulatorKind kind, string gameLabel)
    {
        try
        {
            var documentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, child.DocId);
            if (documentUri is null)
            {
                Trace("REJECT document URI could not be built");
                return null;
            }
            Trace($"DocumentUri={documentUri}");

            // A file already parsed at this modification time never gets re-read: rescans are instant.
            var cacheKey = documentUri.ToString()!;
            var modifiedTicks = child.LastModified?.UtcTicks ?? 0;
#if !DEBUG && !DIAGNOSTIC
            if (ScanCache.TryGet(cacheKey, modifiedTicks, out var cached))
                return cached;
#else
            Trace("Diagnostic build: parse cache bypassed");
#endif

            using var stream = Platform.AppContext.ContentResolver?.OpenInputStream(documentUri);
            if (stream is null)
            {
                Trace("REJECT ContentResolver.OpenInputStream returned null");
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
                    Trace($"REJECT larger than {maxSaveBytes} bytes");
                    return null;
                }
            }
            if (buffer.Length == 0)
            {
                Trace("REJECT zero-byte file");
                return null;
            }

            var bytes = buffer.ToArray();
            var headerLength = Math.Min(16, bytes.Length);
            var bdspSize = bytes.Length is 956456 or 973856 or 978316 or 979108;
            Trace($"BYTES length={bytes.Length} header16={Convert.ToHexString(bytes.AsSpan(0, headerLength))} knownBdspSize={bdspSize}");

            // Describe consumes a copy: the engine decrypts Switch saves in place during parsing.
            var description = engine.TryDescribe(bytes);
            if (description is null)
            {
                Trace("PARSER REJECTED: ISaveEngine.TryDescribe returned null");
                ScanCache.Store(cacheKey, modifiedTicks, null);
                return null;
            }
            Trace($"PARSER ACCEPTED game={description.GameName} generation={description.Generation} trainer={description.TrainerName} playTime={description.PlayTime}");

            var detected = new DetectedSave(
                documentUri.ToString()!,
                child.Name,
                description.GameName is { Length: > 0 } name && !name.StartsWith("Generation", StringComparison.Ordinal)
                    ? $"Pokémon {name}"
                    : gameLabel,
                kind,
                EmulatorSaveHeuristics.RequiresExtraCare(kind),
                child.LastModified,
                description.Generation,
                description.TrainerName,
                description.PlayTime);
            ScanCache.Store(cacheKey, modifiedTicks, detected);
            return detected;
        }
        catch (Exception error)
        {
            Trace($"CANDIDATE EXCEPTION {error}");
            return null; // unreadable candidates are simply not saves
        }
    }

    private sealed record ChildDocument(string DocId, string Name, bool IsDirectory, DateTimeOffset? LastModified);

    /// <summary>
    /// Persistent parse cache keyed by document URI + last-modified time. A null entry
    /// records "this file is not a save" so rescans skip reading it entirely.
    /// </summary>
    private static class ScanCache
    {
        private const string Key = "scan_cache_v1";
        private const int MaxEntries = 512;
        private static Dictionary<string, CacheEntry>? _entries;
        private static readonly Lock Gate = new();

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
                Preferences.Default.Set(Key, System.Text.Json.JsonSerializer.Serialize(_entries));
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
        CancellationToken cancellationToken, Action<ChildDocument> onFile, bool diagnostic = false)
    {
        if (maxDepth <= 0)
        {
            if (diagnostic) Trace($"DEPTH LIMIT reached at {parentDocId}");
            return;
        }
        var children = ListChildren(treeUri, parentDocId);
        if (diagnostic) Trace($"WALK depthRemaining={maxDepth} parent={parentDocId} children={children.Count}");
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.IsDirectory)
            {
                if (diagnostic) Trace($"DIR name={child.Name} docId={child.DocId}");
                if (PrunedDirectories.Contains(child.Name))
                {
                    if (diagnostic) Trace($"PRUNED directory {child.Name}");
                    continue;
                }
                FindFilesRecursive(treeUri, child.DocId, maxDepth - 1, cancellationToken, onFile, diagnostic);
            }
            else
            {
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
