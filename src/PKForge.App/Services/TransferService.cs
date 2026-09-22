using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Services;

/// <summary>Outcome of a bank-to-game or game-to-game transfer.</summary>
public sealed record TransferOutcome(bool Success, string Message, int Box = -1, int Slot = -1, string? BackupId = null);

/// <summary>
/// Pre-flight answer for a transfer: what the conversion will change and a legality
/// verdict for the converted entity. <see cref="Preview"/> is null when the transfer
/// cannot happen at all. Nothing was written anywhere when this returns.
/// </summary>
public sealed record TransferPreviewOutcome(
    bool Success, string Message, int Box = -1, int Slot = -1, TransferPreview? Preview = null);

/// <summary>
/// Moves one Pokémon into a game save without touching the currently connected session.
/// The target save is opened as a throwaway engine session, the entity is converted to
/// its format by the engine (Gen 1 to Gen 9 either way), and the write goes through the
/// same validate, backup, atomic-write pipeline as every other mutation.
/// </summary>
public sealed class TransferService(
    ISaveEngine engine, ISafeSaveWriter writer, ISaveFileAccess access, ISaveSessionService sessions,
    ILegalityService? legality = null)
{
    /// <summary>
    /// Dry-run of <see cref="SendToGameAsync"/>: converts the entity into the target
    /// save's format inside a throwaway session and reports the conversion diff and a
    /// legality verdict, without writing anything anywhere. Callers gate the real
    /// transfer on the user confirming this preview.
    /// </summary>
    public async Task<TransferPreviewOutcome> PreviewAsync(
        ReadOnlyMemory<byte> entityBytes, string nickname, DetectedSave target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var session = await OpenTargetAsync(target, cancellationToken).ConfigureAwait(false);
        var snapshot = session.Snapshot;

        var landing = snapshot.Slots.FirstOrDefault(s => s.Species is null);
        if (landing is null)
            return new TransferPreviewOutcome(false, $"{target.GameLabel} has no empty slot in any box.");

        var preview = new TransferPreviewService(legality).Preview(session, landing.Box, landing.Slot, entityBytes.ToArray());
        if (preview is null)
            return new TransferPreviewOutcome(false, $"{nickname} cannot enter {target.GameLabel}'s format.");
        return new TransferPreviewOutcome(true, $"{nickname} → {target.GameLabel} (box {landing.Box + 1}).",
            landing.Box, landing.Slot, preview);
    }

    /// <summary>Places the entity into the first empty slot of the target save, across every box.</summary>
    public async Task<TransferOutcome> SendToGameAsync(
        ReadOnlyMemory<byte> entityBytes, string nickname, DetectedSave target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var session = await OpenTargetAsync(target, cancellationToken).ConfigureAwait(false);
        var snapshot = session.Snapshot;

        var landing = snapshot.Slots.FirstOrDefault(s => s.Species is null);
        if (landing is null)
            return new TransferOutcome(false, $"{target.GameLabel} has no empty slot in any box.");

        if (!session.ImportSlot(landing.Box, landing.Slot, entityBytes.ToArray()))
            return new TransferOutcome(false, $"{nickname} cannot enter {target.GameLabel}'s format.");

        var candidate = session.Serialize();
        var receipt = await writer.WriteAsync(target.DocumentId, snapshot, candidate,
            $"{nickname} arrived from a transfer", cancellationToken).ConfigureAwait(false);
        if (receipt.Changed)
            sessions.MarkWritten(target.DocumentId, candidate);
        return new TransferOutcome(true, $"{nickname} joined {target.GameLabel} (box {landing.Box + 1}).", landing.Box, landing.Slot, receipt.BackupId);
    }

    /// <summary>Re-reads the target save and opens it as a throwaway session; the caller disposes it.</summary>
    private async Task<ISaveEngineSession> OpenTargetAsync(DetectedSave target, CancellationToken cancellationToken)
    {
        var bytes = await access.ReadAsync(target.DocumentId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return engine.OpenSession(bytes.ToArray(), target.GameLabel);
    }
}
