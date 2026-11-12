using System.Security.Cryptography;
using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// Durable backup store: raw save bytes plus a JSON metadata sidecar per version,
/// under an app-private directory. Each save file keeps its newest <see cref="_maxVersions"/>
/// versions, so a busy save never pushes out another save's restore points. Points the
/// player pinned are kept on top of that allowance and never dropped.
/// </summary>
public sealed class FileBackupService(string rootDirectory, int maxVersions = 20) : IBackupService
{
    private readonly string _root = rootDirectory;
    private readonly int _maxVersions = maxVersions;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, string? changeDescription = null, string? documentId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Directory.CreateDirectory(_root);

        var createdUtc = DateTimeOffset.UtcNow;
        var id = $"{createdUtc:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var bytes = source.OriginalBytes.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var info = new BackupInfo(id, createdUtc, sha, source.DisplayName, source.Format, source.Generation, bytes.LongLength, changeDescription, documentId);

        // Bytes first, sidecar last: a backup without a sidecar is ignored, never half-trusted.
        await AtomicFile.WriteAsync(BytesPath(id), bytes, cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteAsync(SidecarPath(id), System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info, JsonOptions)), cancellationToken).ConfigureAwait(false);

        Prune();
        return new BackupReceipt(id, createdUtc, sha);
    }

    public ValueTask<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<BackupInfo>>(ReadAll());
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(string backupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        if (backupId.Contains('/') || backupId.Contains('\\') || backupId.Contains(".."))
            throw new ArgumentException("Invalid backup id.", nameof(backupId));

        var info = ReadSidecar(SidecarPath(backupId))
            ?? throw new FileNotFoundException($"Backup {backupId} not found.");
        var bytes = await File.ReadAllBytesAsync(BytesPath(backupId), cancellationToken).ConfigureAwait(false);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(sha, info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Backup {backupId} is corrupt: stored hash does not match its bytes.");
        return bytes;
    }

    public ValueTask SetPinnedAsync(string backupId, bool pinned, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        if (backupId.Contains('/') || backupId.Contains('\\') || backupId.Contains(".."))
            throw new ArgumentException("Invalid backup id.", nameof(backupId));
        var info = ReadSidecar(SidecarPath(backupId))
            ?? throw new FileNotFoundException($"Backup {backupId} not found.");
        if (info.Pinned == pinned) return ValueTask.CompletedTask;
        // Written beside the sidecar and moved over it: a crash never leaves half a sidecar.
        cancellationToken.ThrowIfCancellationRequested();
        AtomicFile.WriteText(SidecarPath(backupId), JsonSerializer.Serialize(info with { Pinned = pinned }, JsonOptions));
        // Letting a point go puts it back under the allowance.
        if (!pinned) Prune();
        return ValueTask.CompletedTask;
    }

    private List<BackupInfo> ReadAll()
    {
        if (!Directory.Exists(_root)) return [];
        return Directory.EnumerateFiles(_root, "*.json")
            .Select(ReadSidecar)
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();
    }

    private void Prune()
    {
        // Points made before they recorded their file share one allowance, as before.
        foreach (var stale in ReadAll().Where(x => !x.Pinned).GroupBy(x => x.DocumentId).SelectMany(file => file.Skip(_maxVersions)))
        {
            File.Delete(BytesPath(stale.BackupId));
            File.Delete(SidecarPath(stale.BackupId));
        }
    }

    private static BackupInfo? ReadSidecar(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            // A sidecar names its own files: one whose id is not its file name (a path, another
            // point's) would make pruning delete something else, so it is not a restore point.
            var info = JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(path));
            return info is { BackupId: { } id, Sha256: not null } && id == Path.GetFileNameWithoutExtension(path) ? info : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private string BytesPath(string id) => Path.Combine(_root, id + ".bin");
    private string SidecarPath(string id) => Path.Combine(_root, id + ".json");
}
