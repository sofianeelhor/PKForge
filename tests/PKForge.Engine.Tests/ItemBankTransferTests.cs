using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>The Item Bank screen's moves between the bank and any game: the bag and the bank
/// change together, and a failed save write puts the bank back.</summary>
public sealed class ItemBankTransferTests
{
    private static SaveEngineSession Blank(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);

    private static int IdOf(ISaveEngineSession session, string englishName) =>
        Array.FindIndex(GameInfo.GetStrings("en").GetItemStrings(((SaveEngineSession)session).SaveFile.Context, ((SaveEngineSession)session).SaveFile.Version),
            name => string.Equals(name, englishName, StringComparison.OrdinalIgnoreCase));

    private static int Held(ISaveEngineSession session, int id) =>
        session.GetBag().SelectMany(p => p.Items).Where(i => i.Id == id).Sum(i => i.Count);

    private static string PouchOf(ISaveEngineSession session, int id) =>
        session.GetBag().First(p => session.GetPouchLegalItems(p.Name).Contains(id) && p.Name != "PCItems").Name;

    private sealed class Store(ItemBank start) : IItemBankStore
    {
        private readonly HashSet<string> _applied = [];
        public ItemBank Bank { get; private set; } = start;
        public int Writes { get; private set; }
        public ItemBank Read() => Bank;
        public void Save(ItemBank bank) { Bank = bank; Writes++; }

        public ItemBank Update(Func<ItemBank, ItemBank> change)
        {
            Save(change(Bank));
            return Bank;
        }

        public bool UpdateOnce(string key, Func<ItemBank, ItemBank> change)
        {
            if (!_applied.Add(key)) return false;
            Update(change);
            return true;
        }
    }

    private static (string Key, int Count, string Origins)[] Shape(ItemBank bank) =>
        [.. bank.Entries.Select(e => (e.Key, e.Count, string.Join(",", e.Origins.Select(o => $"{o.Game}/{o.Generation}x{o.Count}"))))];

    [Fact]
    public async Task DepositFromPlatinumThenWithdrawIntoHeartGold()
    {
        var store = new Store(ItemBank.Empty);
        using var platinum = Blank(GameVersion.Pt);
        var potion = IdOf(platinum, "Potion");
        platinum.SetItemCount(PouchOf(platinum, potion), potion, 12);
        var commits = 0;

        var sent = await ItemBankTransfer.DepositAsync(platinum, store, () => { commits++; return Task.CompletedTask; },
            PouchOf(platinum, potion), potion, 5);
        Assert.True(sent.Success, sent.Message);
        Assert.Equal(5, sent.Moved);
        Assert.Equal(7, Held(platinum, potion));
        Assert.Equal(5, store.Bank.CountOf(ItemBankKeys.Item(17)));

        using var heartGold = Blank(GameVersion.HG);
        var back = await ItemBankTransfer.WithdrawAsync(heartGold, store, () => { commits++; return Task.CompletedTask; },
            ItemBankKeys.Item(17), 3, "Potion");
        Assert.True(back.Success, back.Message);
        Assert.Equal(3, Held(heartGold, IdOf(heartGold, "Potion")));
        Assert.Equal(2, store.Bank.CountOf(ItemBankKeys.Item(17)));
        Assert.Equal(2, commits);
    }

    [Fact]
    public async Task AFailedSaveWritePutsTheBankBack()
    {
        var start = ItemBank.Empty.Deposit(ItemBankKeys.Item(17), "Potion", 4, "Platinum", 4);
        var store = new Store(start);
        using var heartGold = Blank(GameVersion.HG);

        var result = await ItemBankTransfer.WithdrawAsync(heartGold, store,
            () => throw new IOException("The card was removed."), ItemBankKeys.Item(17), 4, "Potion");

        Assert.False(result.Success);
        Assert.Contains("stay banked", result.Message);
        Assert.Contains("card was removed", result.Message);
        Assert.Equal(Shape(start), Shape(store.Bank));
        Assert.Equal(2, store.Writes); // written, then put back
    }

    /// <summary>Review finding 16: the save was written, then reopening the open game failed. The bank
    /// was put back anyway, so a withdrawal was doubled (save and bank both held the items).</summary>
    [Fact]
    public async Task AFailureAfterTheSaveWasWrittenNeverRollsTheBankBack()
    {
        var store = new Store(ItemBank.Empty.Deposit(ItemBankKeys.Item(17), "Potion", 4, "Platinum", 4));
        using var heartGold = Blank(GameVersion.HG);

        var result = await ItemBankTransfer.WithdrawAsync(heartGold, store,
            () => throw new SaveAlreadyWrittenException("The open game could not be reloaded.", new IOException("gone")),
            ItemBankKeys.Item(17), 3, "Potion");

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, result.Moved);
        Assert.Contains("could not be reloaded", result.Message);
        Assert.Equal(1, store.Bank.CountOf(ItemBankKeys.Item(17))); // the save holds 3: the bank keeps 1, not 4
        Assert.Equal(3, Held(heartGold, IdOf(heartGold, "Potion")));
    }

    /// <summary>Review finding 16: a failed write put the whole earlier bank back, erasing what the bank
    /// took meanwhile (a folder sync, a Pokéwalker find). Only this move is undone now.</summary>
    [Fact]
    public async Task AFailedWriteUndoesOnlyItsOwnMove()
    {
        var store = new Store(ItemBank.Empty.Deposit(ItemBankKeys.Item(17), "Potion", 4, "Platinum", 4));
        using var platinum = Blank(GameVersion.Pt);
        var potion = IdOf(platinum, "Potion");
        platinum.SetItemCount(PouchOf(platinum, potion), potion, 10);

        var result = await ItemBankTransfer.DepositAsync(platinum, store, () =>
        {
            // Meanwhile another device's Master Ball and two more Potions arrive through the folder.
            store.Update(bank => bank.Deposit(ItemBankKeys.Item(1), "Master Ball", 1, "Emerald", 3)
                .Deposit(ItemBankKeys.Item(17), "Potion", 2, "Crystal", 2));
            throw new IOException("The card was removed.");
        }, PouchOf(platinum, potion), potion, 5);

        Assert.False(result.Success);
        Assert.Equal(1, store.Bank.CountOf(ItemBankKeys.Item(1)));
        Assert.Equal(6, store.Bank.CountOf(ItemBankKeys.Item(17))); // 4 + 2 from the sync; the 5 deposited went back
    }

    [Fact]
    public async Task KeyItemsAreRefusedBeforeAnythingIsWritten()
    {
        var store = new Store(ItemBank.Empty);
        using var platinum = Blank(GameVersion.Pt);
        var bike = IdOf(platinum, "Bicycle");
        platinum.SetItemCount("KeyItems", bike, 1);
        var committed = false;

        var result = await ItemBankTransfer.DepositAsync(platinum, store, () => { committed = true; return Task.CompletedTask; },
            "KeyItems", bike, 1);

        Assert.False(result.Success);
        Assert.Equal("Key items stay with their game.", result.Message);
        Assert.False(committed);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task AnItemTheGameLacksIsRefused()
    {
        var store = new Store(ItemBank.Empty.Deposit(ItemBankKeys.Item(1606), "Ability Patch", 1, "Sword", 8));
        using var heartGold = Blank(GameVersion.HG);
        var result = await ItemBankTransfer.WithdrawAsync(heartGold, store, () => Task.CompletedTask,
            ItemBankKeys.Item(1606), 1, "Ability Patch");
        Assert.False(result.Success);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void ThrowingAwayTakesOnlyFromTheBank()
    {
        var store = new Store(ItemBank.Empty.Deposit(ItemBankKeys.Item(17), "Potion", 4, "Platinum", 4));
        var result = ItemBankTransfer.Discard(store, ItemBankKeys.Item(17), 9, "Potion");
        Assert.True(result.Success);
        Assert.Equal(4, result.Moved);
        Assert.Empty(store.Bank.Entries);
    }
}
