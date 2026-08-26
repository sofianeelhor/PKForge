using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>Outcome of a bank-to-game or game-to-game transfer.</summary>
public sealed record TransferOutcome(bool Success, string Message, int Box = -1, int Slot = -1, string? BackupId = null);

/// <summary>
/// Moves one Pokémon into a game save without touching the currently connected session.
/// The target save is opened as a throwaway engine session, the entity is converted to
/// its format by the engine (Gen 1 to Gen 9 either way), and the write goes through the
/// same validate, backup, atomic-write pipeline as every other mutation.
/// </summary>
public sealed class TransferService(ISaveEngine engine, ISafeSaveWriter writer, ISaveFileAccess access)
{
    /// <summary>Places the entity into the first empty slot of the target save, across every box.</summary>
    public async Task<TransferOutcome> SendToGameAsync(
        ReadOnlyMemory<byte> entityBytes, string nickname, DetectedSave target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var bytes = await access.ReadAsync(target.DocumentId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        using var session = engine.OpenSession(bytes.ToArray(), target.GameLabel);
        var snapshot = session.Snapshot;

        var landing = snapshot.Slots.FirstOrDefault(s => s.Species is null);
        if (landing is null)
            return new TransferOutcome(false, $"{target.GameLabel} has no empty slot in any box.");

        if (!session.ImportSlot(landing.Box, landing.Slot, entityBytes.ToArray()))
            return new TransferOutcome(false, $"{nickname} cannot enter {target.GameLabel}'s format.");

        var receipt = await writer.WriteAsync(target.DocumentId, snapshot, session.Serialize(), cancellationToken).ConfigureAwait(false);
        return new TransferOutcome(true, $"{nickname} joined {target.GameLabel} (box {landing.Box + 1}).", landing.Box, landing.Slot, receipt.BackupId);
    }
}
