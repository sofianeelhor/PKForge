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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        if (!engine.Validate(candidate))
            throw new InvalidDataException("The candidate save failed engine validation; the original was not touched.");

        var backup = await backups.CreateAsync(original, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await access.WriteAtomicallyAsync(documentId, candidate, cancellationToken).ConfigureAwait(false);

        return new SaveWriteReceipt(
            backup.BackupId,
            Convert.ToHexString(SHA256.HashData(original.OriginalBytes.Span)),
            Convert.ToHexString(SHA256.HashData(candidate.Span)),
            DateTimeOffset.UtcNow);
    }
}
