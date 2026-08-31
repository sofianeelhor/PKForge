using System.Security.Cryptography;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>Validates candidate bytes, creates a backup, and performs one platform write in that order.</summary>
public sealed class SafeSaveWriter(ISaveEngine engine, IBackupService backups, ISaveFileAccess access) : ISafeSaveWriter
{
    public async ValueTask<SaveWriteReceipt> WriteAsync(
        string documentId,
        SaveSnapshot original,
        ReadOnlyMemory<byte> candidate,
        string? changeDescription = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        // An unchanged candidate means the mutation produced identical bytes: writing the
        // same state again would create a meaningless restore point, so nothing happens.
        if (candidate.Span.SequenceEqual(original.OriginalBytes.Span))
            return new SaveWriteReceipt(
                string.Empty,
                Convert.ToHexString(SHA256.HashData(original.OriginalBytes.Span)),
                Convert.ToHexString(SHA256.HashData(candidate.Span)),
                DateTimeOffset.UtcNow,
                Changed: false);

        if (!engine.Validate(candidate))
            throw new InvalidDataException("The candidate save failed engine validation; the original was not touched.");

        var backup = await backups.CreateAsync(original, changeDescription, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await access.WriteAtomicallyAsync(documentId, candidate, cancellationToken).ConfigureAwait(false);

        return new SaveWriteReceipt(
            backup.BackupId,
            Convert.ToHexString(SHA256.HashData(original.OriginalBytes.Span)),
            Convert.ToHexString(SHA256.HashData(candidate.Span)),
            DateTimeOffset.UtcNow);
    }
}
