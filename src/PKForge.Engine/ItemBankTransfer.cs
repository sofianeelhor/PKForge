using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Engine;

/// <summary>What one Item Bank move did: whether it landed, what to tell the player, how many moved.</summary>
public sealed record ItemBankMoveResult(bool Success, string Message, int Moved);

/// <summary>
/// An Item Bank move between the bank and any game's bag, in one order every caller shares: the
/// bank is read, the bag is edited in the session through <see cref="ItemBankService"/> (same
/// rules as the bag's own deposit and withdrawal), the bank takes the change, then the save is
/// committed by <c>commit</c>. Only a failed save write undoes the bank's change, and it undoes
/// that change alone, on the bank as it is by then (<see cref="ItemBank.ThreeWay"/>), so anything
/// else the bank took meanwhile stays. A failure after the save was written
/// (<see cref="SaveAlreadyWrittenException"/>) leaves the bank as it is: the save holds the move.
/// The session is the caller's to throw away.
/// </summary>
public static class ItemBankTransfer
{
    public static async Task<ItemBankMoveResult> DepositAsync(ISaveEngineSession session, IItemBankStore bank,
        Func<Task> commit, string pouch, int itemId, int amount)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(commit);
        ItemBank before;
        ItemBank after;
        ItemBankDeposit deposit;
        try
        {
            before = bank.Read();
            after = ItemBankService.Deposit(session, before, pouch, itemId, amount, out deposit);
        }
        catch (InvalidOperationException refused) { return new ItemBankMoveResult(false, refused.Message, 0); }
        return await CommitAsync(bank, commit, before, after, deposit.Moved,
            $"Saved: {deposit.Name} ×{deposit.Moved} is in the Item Bank.",
            "Not saved: the file was left as it was, and the Item Bank too.").ConfigureAwait(false);
    }

    public static async Task<ItemBankMoveResult> WithdrawAsync(ISaveEngineSession session, IItemBankStore bank,
        Func<Task> commit, string key, int amount, string name)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(commit);
        ItemBank before;
        ItemBank after;
        int moved;
        try
        {
            before = bank.Read();
            after = ItemBankService.Withdraw(session, before, key, amount, out moved);
        }
        catch (InvalidOperationException refused) { return new ItemBankMoveResult(false, refused.Message, 0); }
        if (moved <= 0) return new ItemBankMoveResult(false, $"No room for {name}: the pouch is full or at this game's cap.", 0);
        var wanted = Math.Min(amount, before.CountOf(key));
        return await CommitAsync(bank, commit, before, after, moved,
            moved < wanted ? $"{name} ×{moved} came out: this game stops there. The rest stays banked." : $"Saved: {name} ×{moved} from the Item Bank.",
            "Not saved: the file was left as it was, the items stay banked.").ConfigureAwait(false);
    }

    /// <summary>Throws banked items away: the bank alone changes, no save is touched.</summary>
    public static ItemBankMoveResult Discard(IItemBankStore bank, string key, int amount, string name)
    {
        ArgumentNullException.ThrowIfNull(bank);
        var gone = 0;
        bank.Update(current =>
        {
            gone = Math.Min(Math.Max(1, amount), current.CountOf(key));
            return gone <= 0 ? current : current.Withdraw(key, gone);
        });
        return gone <= 0
            ? new ItemBankMoveResult(false, "The Item Bank holds none.", 0)
            : new ItemBankMoveResult(true, $"{name} ×{gone} thrown away.", gone);
    }

    private static async Task<ItemBankMoveResult> CommitAsync(IItemBankStore bank, Func<Task> commit, ItemBank before, ItemBank after,
        int moved, string done, string undone)
    {
        try { bank.Update(current => ItemBank.ThreeWay(before, current, after)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return new ItemBankMoveResult(false, $"The Item Bank could not be written: {error.Message}", 0);
        }
        try
        {
            await commit().ConfigureAwait(false);
        }
        catch (SaveAlreadyWrittenException written)
        {
            // The save on disk holds the move: undoing the bank now would double or lose the items.
            return new ItemBankMoveResult(true, $"{done} {written.Message}", moved);
        }
        catch (Exception error)
        {
            try { bank.Update(current => ItemBank.ThreeWay(after, current, before)); }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                return new ItemBankMoveResult(false, $"{undone} The Item Bank could not be put back: {restore.Message}", 0);
            }
            return new ItemBankMoveResult(false, $"{undone} {error.Message}", 0);
        }
        return new ItemBankMoveResult(true, done, moved);
    }
}
