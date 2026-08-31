using System.Security.Cryptography;
using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// Durable backup store: raw save bytes plus a JSON metadata sidecar per version,
/// under an app-private directory. Oldest versions beyond <see cref="_maxVersions"/> are pruned.
/// </summary>
public sealed class FileBackupService(string rootDirectory, int maxVersions = 20) : IBackupService
{
    private readonly string _root = rootDirectory;
    private readonly int _maxVersions = maxVersions;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, string? changeDescription = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Directory.CreateDirectory(_root);

        var createdUtc = DateTimeOffset.UtcNow;
        var id = $"{createdUtc:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var bytes = source.OriginalBytes.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var info = new BackupInfo(id, createdUtc, sha, source.DisplayName, source.Format, source.Generation, bytes.LongLength, changeDescription);

        // Bytes first, sidecar last: a backup without a sidecar is ignored, never half-trusted.
        await File.WriteAllBytesAsync(BytesPath(id), bytes, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(SidecarPath(id), JsonSerializer.Serialize(info, JsonOptions), cancellationToken).ConfigureAwait(false);

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
        foreach (var stale in ReadAll().Skip(_maxVersions))
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
            return JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string BytesPath(string id) => Path.Combine(_root, id + ".bin");
    private string SidecarPath(string id) => Path.Combine(_root, id + ".json");
}
