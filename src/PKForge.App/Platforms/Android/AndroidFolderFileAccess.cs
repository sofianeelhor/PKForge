using Android.Content;
using Android.Provider;
using PKForge.Domain;
using AndroidUri = Android.Net.Uri;

namespace PKForge.App;

/// <summary>
/// Reads and writes files inside a granted SAF folder (bank archive export/import). Names
/// are matched exactly: an existing file with the same display name is overwritten, so a
/// re-export updates the folder in place instead of accumulating "name (1)" copies.
/// </summary>
public sealed class AndroidFolderFileAccess : IFolderFileAccess, IFolderTreeAccess
{
    private static ContentResolver Resolver => Platform.AppContext.ContentResolver
        ?? throw new InvalidOperationException("Android ContentResolver is unavailable.");

    public ValueTask<IReadOnlyList<PickedDocument>> ListFilesAsync(string treeId, CancellationToken cancellationToken = default)
    {
        return new ValueTask<IReadOnlyList<PickedDocument>>(
            Task.Run<IReadOnlyList<PickedDocument>>(() => ListFiles(treeId), cancellationToken));
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var uri = Parse(documentId);
        await using var input = Resolver.OpenInputStream(uri)
            ?? throw new IOException($"The document provider could not open {uri} for reading.");
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public ValueTask WriteFileAsync(string treeId, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        return new ValueTask(Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var treeUri = Resolve(treeId);
            var target = FindDocument(treeUri, fileName) ?? CreateDocument(treeUri, fileName);
            using var output = Resolver.OpenOutputStream(target, "wt")
                ?? throw new IOException($"The document provider could not open {target} for writing.");
            await output.WriteAsync(bytes.ToArray(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken));
    }

    public ValueTask<string> WriteBesideAsync(string documentId, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        return new ValueTask<string>(Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = Parse(documentId);
            // Only a document reached through a folder grant has a folder PKForge may write in;
            // a path-shaped id ("primary:ROMs/nds/game.sav") names its parent.
            if (!DocumentsContract.IsDocumentUri(Platform.AppContext, uri)) throw new NotSupportedException("This file was not found through a linked folder.");
            string treeDocId, docId;
            try
            {
                treeDocId = DocumentsContract.GetTreeDocumentId(uri) ?? throw new NotSupportedException("This file was not found through a linked folder.");
                docId = DocumentsContract.GetDocumentId(uri) ?? throw new NotSupportedException("This file has no document id.");
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                throw new NotSupportedException("This file was not found through a linked folder.");
            }
            var slash = docId.LastIndexOf('/');
            var parentDocId = slash > 0 ? docId[..slash] : null;
            if (parentDocId is null || !(parentDocId == treeDocId || parentDocId.StartsWith(treeDocId.TrimEnd('/') + "/", StringComparison.Ordinal)
                || (treeDocId.EndsWith(':') && parentDocId.StartsWith(treeDocId, StringComparison.Ordinal))))
                throw new NotSupportedException("This file's folder is outside the linked folder.");

            var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(uri, parentDocId)
                ?? throw new IOException("The file's folder could not be listed.");
            var taken = new List<string>();
            var cursor = Resolver.Query(childrenUri, [DocumentsContract.Document.ColumnDisplayName], null, null, null);
            try
            {
                while (cursor?.MoveToNext() == true)
                    if (cursor.GetString(0) is { } name) taken.Add(name);
            }
            finally
            {
                ContentCursors.Release(cursor);
            }
            var free = SaveInsights.FreeFileName(fileName, taken);

            var parent = DocumentsContract.BuildDocumentUriUsingTree(uri, parentDocId)
                ?? throw new IOException("The file's folder could not be resolved.");
            var target = DocumentsContract.CreateDocument(Resolver, parent, "application/octet-stream", free)
                ?? throw new IOException($"The folder rejected a new file named {free}.");
            using var output = Resolver.OpenOutputStream(target, "wt")
                ?? throw new IOException($"The document provider could not open {target} for writing.");
            await output.WriteAsync(bytes.ToArray(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return free;
        }, cancellationToken));
    }

    private static List<PickedDocument> ListFiles(string treeId)
    {
        var treeUri = Resolve(treeId);
        return QueryChildren(treeUri)
            .Select(child => new PickedDocument(
                DocumentsContract.BuildDocumentUriUsingTree(treeUri, child.DocId)?.ToString()
                    ?? throw new IOException($"The document {child.Name} could not be resolved."),
                child.Name))
            .ToList();
    }

    /// <summary>The document URI of the exact-named file in the folder root, or null when absent.</summary>
    private static AndroidUri? FindDocument(AndroidUri treeUri, string fileName) =>
        QueryChildren(treeUri).FirstOrDefault(child => child.Name == fileName) is { } match
            ? DocumentsContract.BuildDocumentUriUsingTree(treeUri, match.DocId)
            : null;

    private static AndroidUri CreateDocument(AndroidUri treeUri, string fileName)
    {
        var rootDocId = DocumentsContract.GetTreeDocumentId(treeUri)
            ?? throw new InvalidOperationException("The folder grant has no tree document id.");
        var parent = DocumentsContract.BuildDocumentUriUsingTree(treeUri, rootDocId)
            ?? throw new IOException("The folder grant could not be resolved to a document.");
        var mime = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? "application/json"
            : "application/octet-stream";
        return DocumentsContract.CreateDocument(Resolver, parent, mime, fileName)
            ?? throw new IOException($"The folder rejected a new file named {fileName}.");
    }

    private sealed record Child(string DocId, string Name);

    private static List<Child> QueryChildren(AndroidUri treeUri)
    {
        var rootDocId = DocumentsContract.GetTreeDocumentId(treeUri)
            ?? throw new InvalidOperationException("The folder grant has no tree document id.");
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, rootDocId);
        if (childrenUri is null) return [];

        string[] projection =
        [
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType,
        ];
        var cursor = Resolver.Query(childrenUri, projection, null, null, null);
        if (cursor is null) return [];

        var children = new List<Child>();
        try
        {
            while (cursor.MoveToNext())
            {
                var docId = cursor.GetString(0);
                var name = cursor.GetString(1);
                if (docId is null || name is null) continue;
                if (cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir) continue;
                children.Add(new Child(docId, name));
            }
        }
        finally
        {
            ContentCursors.Release(cursor);
        }
        return children;
    }

    // ── The whole tree by relative path (the PKForge folder mirror) ──────────

    public ValueTask<IReadOnlyList<FolderEntry>> ListTreeAsync(string treeId, CancellationToken cancellationToken = default) =>
        new(Task.Run<IReadOnlyList<FolderEntry>>(() =>
        {
            var treeUri = Resolve(treeId);
            var found = new List<FolderEntry>();
            var pending = new Stack<(string DocId, string Prefix)>();
            pending.Push((RootDocId(treeUri), ""));
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (docId, prefix) = pending.Pop();
                foreach (var child in QueryEntries(treeUri, docId))
                {
                    if (child.IsFolder) pending.Push((child.DocId, prefix + child.Name + "/"));
                    else found.Add(new FolderEntry(prefix + child.Name, child.Size, DateTimeOffset.FromUnixTimeMilliseconds(child.Modified)));
                }
            }
            return found;
        }, cancellationToken));

    public ValueTask<byte[]> ReadAsync(string treeId, string path, CancellationToken cancellationToken = default) =>
        new(Retrying(() => Task.Run(async () =>
        {
            var treeUri = Resolve(treeId);
            var docId = Walk(treeUri, path, create: false) ?? throw new FileNotFoundException($"{path} is not in the folder any more.");
            var uri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, docId)!;
            await using var input = Resolver.OpenInputStream(uri) ?? throw new IOException($"The document provider could not open {path}.");
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }, cancellationToken)));

    public ValueTask WriteAsync(string treeId, string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        new(Retrying(() => Task.Run(async () =>
        {
            var treeUri = Resolve(treeId);
            var docId = Walk(treeUri, path, create: true)!;
            var uri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, docId)!;
            using var output = Resolver.OpenOutputStream(uri, "wt") ?? throw new IOException($"The document provider could not open {path} for writing.");
            await output.WriteAsync(bytes.ToArray(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken)));

    public ValueTask<bool> DeleteAsync(string treeId, string path, CancellationToken cancellationToken = default) =>
        new(Retrying(() => Task.Run(() =>
        {
            var treeUri = Resolve(treeId);
            if (Walk(treeUri, path, create: false) is not { } docId) return false;
            return DocumentsContract.DeleteDocument(Resolver, DocumentsContract.BuildDocumentUriUsingTree(treeUri, docId)!);
        }, cancellationToken)));

    // Folder ids met on the way, so a sync's thousand writes do not list each parent again.
    // Forgotten whenever a call fails (the user may have moved or deleted a folder).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> FolderIds = new();

    private static async Task<T> Retrying<T>(Func<Task<T>> run)
    {
        try { return await run().ConfigureAwait(false); }
        catch (Exception) when (!FolderIds.IsEmpty)
        {
            FolderIds.Clear();
            return await run().ConfigureAwait(false);
        }
    }

    private sealed record Entry(string DocId, string Name, bool IsFolder, long Size, long Modified);

    private static string RootDocId(AndroidUri treeUri) => DocumentsContract.GetTreeDocumentId(treeUri)
        ?? throw new InvalidOperationException("The folder grant has no tree document id.");

    /// <summary>The document id at <paramref name="path"/>; with <paramref name="create"/>, missing
    /// folders and the file itself are made (names matched exactly, so nothing becomes "name (1)").</summary>
    private static string? Walk(AndroidUri treeUri, string path, bool create)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = RootDocId(treeUri);
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            var key = $"{treeUri}|{string.Join('/', parts[..(i + 1)])}";
            if (!last && FolderIds.TryGetValue(key, out var cached)) { current = cached; continue; }
            var match = QueryEntries(treeUri, current).FirstOrDefault(e => e.Name == parts[i] && e.IsFolder != last);
            if (match is not null)
            {
                current = match.DocId;
                if (!last) FolderIds[key] = current;
                continue;
            }
            if (!create) return null;
            var parent = DocumentsContract.BuildDocumentUriUsingTree(treeUri, current)
                ?? throw new IOException("The folder could not be resolved.");
            var made = DocumentsContract.CreateDocument(Resolver, parent,
                last ? "application/octet-stream" : DocumentsContract.Document.MimeTypeDir, parts[i])
                ?? throw new IOException($"The folder rejected {string.Join('/', parts[..(i + 1)])}.");
            current = DocumentsContract.GetDocumentId(made) ?? throw new IOException("The new document has no id.");
            if (!last) FolderIds[key] = current;
        }
        return current;
    }

    private static List<Entry> QueryEntries(AndroidUri treeUri, string parentDocId)
    {
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentDocId);
        if (childrenUri is null) return [];
        string[] projection =
        [
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType,
            DocumentsContract.Document.ColumnSize,
            DocumentsContract.Document.ColumnLastModified,
        ];
        var cursor = Resolver.Query(childrenUri, projection, null, null, null)
            ?? throw new IOException("The folder cannot be read (was its permission removed?).");
        var entries = new List<Entry>();
        try
        {
            while (cursor.MoveToNext())
            {
                var docId = cursor.GetString(0);
                var name = cursor.GetString(1);
                if (docId is null || name is null) continue;
                entries.Add(new Entry(docId, name, cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir,
                    cursor.IsNull(3) ? 0 : cursor.GetLong(3), cursor.IsNull(4) ? 0 : cursor.GetLong(4)));
            }
        }
        finally
        {
            ContentCursors.Release(cursor);
        }
        return entries;
    }

    private static AndroidUri Resolve(string treeId) => AndroidUri.Parse(treeId)
        ?? throw new ArgumentException("The folder grant is not a valid tree URI.", nameof(treeId));

    private static AndroidUri Parse(string documentId) => AndroidUri.Parse(documentId)
        ?? throw new ArgumentException("The document identifier is not a valid content URI.", nameof(documentId));
}
