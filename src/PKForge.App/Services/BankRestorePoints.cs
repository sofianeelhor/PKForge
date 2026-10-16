using PKForge.Domain;
using PKForge.Infrastructure;
using PKForge.Sync;

namespace PKForge.App.Services;

/// <summary>
/// The Bank's own restore points (copies taken before Bank Sync replaced the whole Bank), listed beside the
/// save restore points. Restoring stops Bank Sync first: otherwise the old Bank would go out as new edits.
/// </summary>
public sealed class BankRestorePoints(FileBankSnapshots snapshots, FileBankService bank, BankDecor decor, SyncCoordinator sync)
{
    private const string IdPrefix = "bank:";

    public static bool IsBank(BackupInfo point) => point.BackupId.StartsWith(IdPrefix, StringComparison.Ordinal);

    public bool SyncIsOn => sync.Connection.IsConnected;

    public IReadOnlyList<BackupInfo> List() =>
        [.. snapshots.List().Select(s => new BackupInfo(IdPrefix + s.Id, s.CreatedUtc, "", "PKForge Bank", "Bank", 0, s.SizeBytes,
            s.PokemonCount is { } count ? $"{s.Reason} {count} Pokémon." : s.Reason))];

    /// <summary>Puts the Bank back as it was in <paramref name="point"/>; the current Bank becomes a restore point first.</summary>
    public async Task RestoreAsync(BackupInfo point)
    {
        if (!IsBank(point)) throw new ArgumentException("Not a Bank restore point.", nameof(point));
        if (sync.Connection.IsConnected)
        {
            sync.SignOut();
            await sync.WhenIdleAsync();
        }
        await Task.Run(() =>
        {
            snapshots.Restore(point.BackupId[IdPrefix.Length..], $"Before restoring the Bank from {point.CreatedUtc:yyyy-MM-dd HH:mm} UTC.");
            bank.Reload();
            decor.Reload();
        });
    }
}
