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
/// its format by the engine (Gen 1 to Gen 9 either way, downgrades included with
/// warnings), and the write goes through the
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
        ReadOnlyMemory<byte> entityBytes, string nickname, DetectedSave target, CancellationToken cancellationToken = default,
        string? format = null, SlotRef? startAt = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var session = await OpenTargetAsync(target, cancellationToken).ConfigureAwait(false);
        var snapshot = session.Snapshot;

        if (Landing(snapshot, startAt) is not { } landing)
            return new TransferPreviewOutcome(false, NoRoom(target, startAt));

        var preview = new TransferPreviewService(legality).Preview(session, landing.Box, landing.Slot, entityBytes.ToArray(), format);
        if (preview is null)
            return new TransferPreviewOutcome(false, Refusal(entityBytes, nickname, snapshot, target.GameLabel, format));
        return new TransferPreviewOutcome(true, $"{nickname} → {target.GameLabel} (box {landing.Box + 1}).",
            landing.Box, landing.Slot, preview);
    }

    /// <summary>
    /// Places the entity into the first empty box slot of the target save, across every box,
    /// or the first one at or after <paramref name="startAt"/> when the player chose where.
    /// </summary>
    public async Task<TransferOutcome> SendToGameAsync(
        ReadOnlyMemory<byte> entityBytes, string nickname, DetectedSave target, CancellationToken cancellationToken = default,
        string? format = null, SlotRef? startAt = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var session = await OpenTargetAsync(target, cancellationToken).ConfigureAwait(false);
        var snapshot = session.Snapshot;

        if (Landing(snapshot, startAt) is not { } landing)
            return new TransferOutcome(false, NoRoom(target, startAt));

        if (!TryImport(session, landing.Box, landing.Slot, entityBytes.ToArray(), out var refusal, format))
            return new TransferOutcome(false, refusal is null
                ? Refusal(entityBytes, nickname, snapshot, target.GameLabel, format)
                : $"{nickname} cannot go to {target.GameLabel}. {refusal}");

        var receipt = await WriteTargetAsync(target, snapshot, session, $"{nickname} arrived from a transfer", cancellationToken).ConfigureAwait(false);
        return new TransferOutcome(true, $"{nickname} joined {target.GameLabel} (box {landing.Box + 1}).", landing.Box, landing.Slot, receipt.BackupId);
    }

    /// <summary>
    /// Dry-run of an import into the connected game (the live session is never touched: a
    /// throwaway copy of its current bytes takes the import). Same diff, warnings and
    /// verdict as <see cref="PreviewAsync"/>, for the bank's and boxes' "send to this game" paths.
    /// </summary>
    public TransferPreviewOutcome PreviewIntoConnected(ReadOnlyMemory<byte> entityBytes, string nickname, int box, int slot, string? format = null)
    {
        var live = sessions.CurrentSession;
        if (live is null)
            return new TransferPreviewOutcome(false, "No game is connected.");
        const string label = "the connected game";
        using var scratch = engine.OpenSession(live.Serialize().ToArray(), sessions.Current?.Document.DisplayName);
        var preview = new TransferPreviewService(legality).Preview(scratch, box, slot, entityBytes.ToArray(), format);
        if (preview is null)
            return new TransferPreviewOutcome(false, Refusal(entityBytes, nickname, scratch.Snapshot, label, format));
        return new TransferPreviewOutcome(true, $"{nickname} → {label} (box {box + 1}).", box, slot, preview);
    }

    /// <summary>
    /// Imports through the engine's reporting path when available, so a refusal says why.
    /// Downgrades are allowed: only call this after the user confirmed the transfer preview.
    /// </summary>
    public static bool TryImport(ISaveEngineSession session, int box, int slot, byte[] bytes, out string? refusal, string? format = null) =>
        BankTransfers.TryImport(session, box, slot, bytes, out refusal, format);

    /// <summary>
    /// Places a whole batch in the target save's free slots (from <paramref name="startAt"/>) in
    /// one write, or writes nothing and says which Pokémon could not enter and why. Returns the
    /// slots the batch took, for <see cref="ReleaseFromGameAsync"/> to undo it.
    /// </summary>
    public async Task<(string? Refusal, IReadOnlyList<SlotRef> Landed)> SendManyToGameAsync(
        IReadOnlyList<TransferItem> items, DetectedSave target, string changeDescription,
        CancellationToken cancellationToken = default, SlotRef? startAt = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var session = await OpenTargetAsync(target, cancellationToken).ConfigureAwait(false);
        var snapshot = session.Snapshot;
        var rooms = SlotPlanning.FreeSlotsFrom(snapshot.Slots, startAt).Take(items.Count).ToList();
        var failed = BankTransfers.ImportAll(session, items, rooms, out var refusal);
        if (failed >= 0)
        {
            if (failed >= items.Count) return ($"{target.GameLabel} has room for {rooms.Count} of {items.Count} Pokémon.", []);
            var item = items[failed];
            return (refusal is null
                ? Refusal(item.Data, item.Nickname, snapshot, target.GameLabel, item.Format)
                : $"{item.Nickname} cannot go to {target.GameLabel}. {refusal}", []);
        }
        await WriteTargetAsync(target, snapshot, session, changeDescription, cancellationToken).ConfigureAwait(false);
        return (null, rooms);
    }

    /// <summary>Empties <paramref name="slots"/> of the target save in one write: the undo of
    /// a batch that reached it but could not leave its source.</summary>
    public async Task ReleaseFromGameAsync(DetectedSave target, IReadOnlyList<SlotRef> slots, string changeDescription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var session = await OpenTargetAsync(target, cancellationToken).ConfigureAwait(false);
        var snapshot = session.Snapshot;
        SlotReleases.ReleaseAll(session, [.. slots.Select(slot => (slot.Box, slot.Slot))]);
        await WriteTargetAsync(target, snapshot, session, changeDescription, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens any detected game as a throwaway session (read fresh from its file), for an
    /// edit that is not a Pokémon transfer (the Item Bank's bag moves). The caller disposes it.</summary>
    public Task<ISaveEngineSession> OpenGameAsync(DetectedSave target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return OpenTargetAsync(target, cancellationToken);
    }

    /// <summary>
    /// Writes a session opened by <see cref="OpenGameAsync"/> through the same safe path as every
    /// transfer (validate, restore point, atomic write). When the game written is the one open in
    /// the app, its live session is reopened from what was written, so it never writes stale bytes back.
    /// A failure once the save is written throws <see cref="SaveAlreadyWrittenException"/>, so a caller
    /// never undoes what went with the write (an Item Bank move) when the save already holds it.
    /// </summary>
    public async Task WriteGameAsync(DetectedSave target, SaveSnapshot snapshot, ISaveEngineSession session, string changeDescription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var receipt = await WriteTargetAsync(target, snapshot, session, changeDescription, cancellationToken).ConfigureAwait(false);
        try
        {
            if (receipt.Changed && sessions.Current?.Document.DocumentId == target.DocumentId) sessions.RevertToBaseline();
        }
        catch (Exception error)
        {
            throw new SaveAlreadyWrittenException($"The open game could not be reloaded from the file ({error.Message}): reopen it before editing.", error);
        }
    }

    /// <summary>Writes a throwaway target session back through the safe path (validate, backup, atomic write).</summary>
    private async Task<SaveWriteReceipt> WriteTargetAsync(DetectedSave target, SaveSnapshot snapshot, ISaveEngineSession session,
        string changeDescription, CancellationToken cancellationToken)
    {
        var candidate = session.Serialize();
        var receipt = await writer.WriteAsync(target.DocumentId, snapshot, candidate, changeDescription, cancellationToken).ConfigureAwait(false);
        if (receipt.Changed)
        {
            sessions.MarkWritten(target.DocumentId, candidate);
            session.CommitHistory(); // only now that the save holding the trips is written
        }
        return receipt;
    }

    /// <summary>Why the entity cannot enter the target (the species or file names itself), else the generic refusal.</summary>
    internal static string Refusal(ReadOnlyMemory<byte> entityBytes, string nickname, SaveSnapshot snapshot, string targetLabel, string? format = null) =>
        TransferCompatibility.ExplainRefusal(entityBytes.ToArray(), nickname, snapshot.Format, snapshot.Generation, targetLabel, format)
            ?? $"{nickname} can't go to {targetLabel}: PKForge can't convert it for that game.";

    /// <summary>The target save's slots, read fresh, so the player can choose where a batch starts.</summary>
    public async Task<IReadOnlyList<SlotSummary>> ReadSlotsAsync(DetectedSave target, CancellationToken cancellationToken = default)
    {
        using var session = await OpenTargetAsync(target, cancellationToken).ConfigureAwait(false);
        return session.Snapshot.Slots;
    }

    private static SlotRef? Landing(SaveSnapshot snapshot, SlotRef? startAt) =>
        SlotPlanning.FreeSlotsFrom(snapshot.Slots, startAt).Select(s => (SlotRef?)s).FirstOrDefault();

    private static string NoRoom(DetectedSave target, SlotRef? startAt) => startAt is { } from
        ? $"{target.GameLabel} has no empty slot from box {from.Box + 1} slot {from.Slot + 1} onward."
        : $"{target.GameLabel} has no empty slot in any box.";

    /// <summary>Re-reads the target save and opens it as a throwaway session; the caller disposes it.</summary>
    private async Task<ISaveEngineSession> OpenTargetAsync(DetectedSave target, CancellationToken cancellationToken)
    {
        var bytes = await access.ReadAsync(target.DocumentId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // The engine sees the chosen game and route, never the custom display name.
        return engine.OpenSession(bytes.ToArray(), target.EngineHint, target.Format);
    }
}
