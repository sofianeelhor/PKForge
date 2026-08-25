using Android.Content;
using Android.Net;
using Android.OS;
using PKForge.Domain;

namespace PKForge.App;

/// <summary>Reads and writes save documents exclusively through Android's Storage Access Framework.</summary>
public sealed class AndroidSafFileAccess : ISaveFileAccess
{
    private static ContentResolver Resolver => Platform.AppContext.ContentResolver
        ?? throw new InvalidOperationException("Android ContentResolver is unavailable.");

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var uri = Parse(documentId);
        await using var input = Resolver.OpenInputStream(uri)
            ?? throw new IOException($"The document provider could not open {uri} for reading.");
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var uri = Parse(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        // SAF providers do not expose a portable rename-and-swap primitive. The descriptor must support
        // reliable truncate/write/flush; providers that reject this mode fail closed.
        using var descriptor = Resolver.OpenFileDescriptor(uri, "rwt")
            ?? throw new IOException($"The document provider does not support safe truncate/write access for {uri}.");
        var output = new ParcelFileDescriptor.AutoCloseOutputStream(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        output.Write(bytes.ToArray());
        output.Flush();
        output.Close();
        return ValueTask.CompletedTask;
    }

    public static void PersistPermission(Android.Net.Uri uri, Intent intent)
    {
        var grants = intent.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        if (grants == 0)
            throw new InvalidOperationException("The picker result did not grant document access.");
        Resolver.TakePersistableUriPermission(uri, grants);
    }

    private static Android.Net.Uri Parse(string documentId) => Android.Net.Uri.Parse(documentId)
        ?? throw new ArgumentException("The document identifier is not a valid content URI.", nameof(documentId));
}
