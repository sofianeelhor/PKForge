using PKForge.Domain;

namespace PKForge.Engine;

/// <summary>One Pokémon of a batch bound for a save: its bytes, recorded format and name.</summary>
public sealed record TransferItem(byte[] Data, string? Format, string Nickname);

/// <summary>What a whole batch transfer did: it either all happened or none of it did.
/// <paramref name="Duplicated"/> is set only when the source could not let go after the
/// destination took everything and the destination could not give it back either: nothing
/// is lost, but both sides hold the batch.</summary>
public sealed record BatchTransferOutcome(bool Success, string Message, bool Duplicated = false);

/// <summary>
/// All-or-nothing transfers between saves and the Bank. The destination always takes the
/// whole batch first, in one durable write; the source lets go only after that, and if
/// letting go fails the destination gives its copies back, so a batch never ends half
/// moved and a Pokémon is never lost. Steps run on the caller's context (they drive UI-bound
/// save writes), so nothing here leaves it.
/// </summary>
public static class BankTransfers
{
    /// <summary>
    /// Imports every item into the given empty slots of <paramref name="session"/>, in order,
    /// or stops at the first one that cannot enter and returns its index (-1 when all landed).
    /// A refusal leaves the session partly written: the caller discards it rather than saving.
    /// A room that turns out to hold a Pokémon is a refusal too: the caller's picture of the
    /// save can be older than its last write, and an arrival must never replace what is stored.
    /// Downgrades are allowed, so only call this after the user confirmed the transfer preview.
    /// </summary>
    public static int ImportAll(ISaveEngineSession session, IReadOnlyList<TransferItem> items, IReadOnlyList<SlotRef> rooms, out string? refusal)
    {
        refusal = null;
        if (rooms.Count < items.Count)
        {
            refusal = $"there {(rooms.Count == 1 ? "is 1 empty slot" : $"are {rooms.Count} empty slots")} for {items.Count} Pokémon.";
            return rooms.Count;
        }
        for (var i = 0; i < items.Count; i++)
        {
            if (rooms[i].Box >= 0 && !session.ReadEntity(rooms[i].Box, rooms[i].Slot).IsEmpty)
            {
                refusal = $"box {rooms[i].Box + 1}, slot {rooms[i].Slot + 1} is no longer empty.";
                return i;
            }
            if (!TryImport(session, rooms[i].Box, rooms[i].Slot, items[i].Data, out refusal, items[i].Format))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Imports through the engine's reporting path when available, so a refusal says why.
    /// Downgrades are allowed: only call this after the user confirmed the transfer preview.
    /// </summary>
    public static bool TryImport(ISaveEngineSession session, int box, int slot, byte[] bytes, out string? refusal, string? format = null)
    {
        refusal = null;
        if (session is SaveEngineSession engineSession)
            return engineSession.ImportSlotWithReport(box, slot, bytes, out refusal, format) is not null;
        return session.ImportSlot(box, slot, bytes, format);
    }

    /// <summary>
    /// Moves (or copies, when <paramref name="releaseSource"/> is null) a whole batch of
    /// <paramref name="count"/> Pokémon. <paramref name="writeDestination"/> takes every one in
    /// a single durable write or none; only then does <paramref name="releaseSource"/> let them
    /// go, and when it cannot, <paramref name="undoDestination"/> takes the destination's copies
    /// back. Each step returns null on success, else why it failed (a throw counts as failing).
    /// </summary>
    public static async Task<BatchTransferOutcome> MoveAsync(int count, string destination,
        Func<Task<string?>> writeDestination, Func<Task<string?>>? releaseSource, Func<Task<string?>> undoDestination)
    {
        ArgumentNullException.ThrowIfNull(writeDestination);
        ArgumentNullException.ThrowIfNull(undoDestination);
        if (count == 0) return new BatchTransferOutcome(false, "Nothing to send.");
        if (await Step(writeDestination) is { } refused)
            return new BatchTransferOutcome(false, $"Nothing was sent: {refused}");
        if (releaseSource is null)
            return new BatchTransferOutcome(true, $"Copied {Count(count)} to {destination}.");
        if (await Step(releaseSource) is not { } stuck)
            return new BatchTransferOutcome(true, $"Moved {Count(count)} to {destination}.");
        return await Step(undoDestination) is { } undo
            ? new BatchTransferOutcome(false,
                $"They reached {destination} but could not leave where they were ({stuck}), and {destination} could not give " +
                $"them back ({undo}). Nothing is lost: {Count(count)} are now in both places.", Duplicated: true)
            : new BatchTransferOutcome(false, $"Nothing was sent: they could not leave where they were ({stuck}), so {destination} gave them back.");
    }

    /// <summary>Save → Bank: the Bank takes the batch in one index write before
    /// <paramref name="releaseFromSave"/> (null for a copy) empties the save's slots.</summary>
    public static Task<BatchTransferOutcome> DepositAsync(
        IBankService bank, IReadOnlyList<BankDeposit> deposits, Func<Task<string?>>? releaseFromSave)
    {
        ArgumentNullException.ThrowIfNull(bank);
        IReadOnlyList<BankEntry> added = [];
        return MoveAsync(deposits.Count, "the Bank",
            () =>
            {
                added = bank.AddMany(deposits);
                return Task.FromResult<string?>(null);
            },
            releaseFromSave,
            () =>
            {
                bank.RemoveMany([.. added.Select(e => e.Id)]);
                return Task.FromResult<string?>(null);
            });
    }

    /// <summary>Bank → save: <paramref name="writeSave"/> places the batch in one save write, then
    /// the entries leave the Bank in one index write; <paramref name="undoSave"/> empties the slots
    /// the batch took if the Bank cannot let go.</summary>
    public static Task<BatchTransferOutcome> WithdrawAsync(
        IBankService bank, IReadOnlyList<Guid> ids, string destination,
        Func<Task<string?>> writeSave, Func<Task<string?>> undoSave)
    {
        ArgumentNullException.ThrowIfNull(bank);
        return MoveAsync(ids.Count, destination, writeSave,
            () =>
            {
                bank.RemoveMany(ids);
                return Task.FromResult<string?>(null);
            },
            undoSave);
    }

    private static async Task<string?> Step(Func<Task<string?>> step)
    {
        try { return await step(); }
        catch (Exception error) { return error.Message; }
    }

    private static string Count(int n) => n == 1 ? "1 Pokémon" : $"{n} Pokémon";
}
