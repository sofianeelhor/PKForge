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
public sealed class AndroidFolderFileAccess : IFolderFileAccess
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
        using var cursor = Resolver.Query(childrenUri, projection, null, null, null);
        if (cursor is null) return [];

        var children = new List<Child>();
        while (cursor.MoveToNext())
        {
            var docId = cursor.GetString(0);
            var name = cursor.GetString(1);
            if (docId is null || name is null) continue;
            if (cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir) continue;
            children.Add(new Child(docId, name));
        }
        return children;
    }

    private static AndroidUri Resolve(string treeId) => AndroidUri.Parse(treeId)
        ?? throw new ArgumentException("The folder grant is not a valid tree URI.", nameof(treeId));

    private static AndroidUri Parse(string documentId) => AndroidUri.Parse(documentId)
        ?? throw new ArgumentException("The document identifier is not a valid content URI.", nameof(documentId));
}
