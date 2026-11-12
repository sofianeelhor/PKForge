using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class ItemBankTests
{
    private static readonly string Potion = ItemBankKeys.Item(17);

    [Fact]
    public void DepositsAddUpAndRememberEachOrigin()
    {
        var bank = ItemBank.Empty
            .Deposit(Potion, "Potion", 5, "FireRed", 3)
            .Deposit(Potion, "Potion", 3, "Platinum", 4)
            .Deposit(Potion, "Potion", 2, "FireRed", 3);

        var entry = Assert.Single(bank.Entries);
        Assert.Equal(10, entry.Count);
        Assert.Equal([new ItemBankOrigin("FireRed", 3, 7), new ItemBankOrigin("Platinum", 4, 3)], entry.Origins);
    }

    [Fact]
    public void WithdrawTakesTheLatestFirstAndDropsAnEmptiedEntry()
    {
        var bank = ItemBank.Empty.Deposit(Potion, "Potion", 5, "FireRed", 3).Deposit(Potion, "Potion", 3, "Platinum", 4);

        var less = bank.Withdraw(Potion, 4);
        Assert.Equal(4, less.CountOf(Potion));
        Assert.Equal([new ItemBankOrigin("FireRed", 3, 4)], less.Find(Potion)!.Origins);
        Assert.Empty(less.Withdraw(Potion, 4).Entries);
        // The bank it came from is untouched: a failed write can put it back as it was.
        Assert.Equal(8, bank.CountOf(Potion));
    }

    [Fact]
    public void RefusesWithdrawingMoreThanBankedAndOverflowingAnEntry()
    {
        var bank = ItemBank.Empty.Deposit(Potion, "Potion", 2, "Emerald", 3);
        Assert.Throws<InvalidOperationException>(() => bank.Withdraw(Potion, 3));
        Assert.Throws<InvalidOperationException>(() => bank.Withdraw(ItemBankKeys.Item(1), 1));
        Assert.Throws<InvalidOperationException>(() => bank.Deposit(Potion, "Potion", ItemBank.MaxPerEntry, "Emerald", 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => bank.Deposit(Potion, "Potion", 0, "Emerald", 3));
    }

    [Theory]
    [InlineData("POTION", "Potion")]
    [InlineData("POKé BALL", "Poké Ball")]
    [InlineData("BlackGlasses", "Black Glasses")]
    [InlineData("THUNDERSTONE", "Thunder Stone")]
    [InlineData("PARLYZ HEAL", "Paralyze Heal")]
    [InlineData("NeverMeltIce", "Never-Melt Ice")]
    public void OldSpellingsMeetTodaysNames(string old, string modern) =>
        Assert.Equal(ItemBankKeys.NameKey(modern), ItemBankKeys.NameKey(old));

    [Fact]
    public void MachinesKeepToTheirGameFamily()
    {
        Assert.True(ItemBankKeys.IsMachineName("TM01"));
        Assert.True(ItemBankKeys.IsMachineName("TR42"));
        Assert.True(ItemBankKeys.IsMachineName("HM05"));
        Assert.True(ItemBankKeys.IsHiddenMachineName("HM05"));
        Assert.False(ItemBankKeys.IsMachineName("TM Case"));
        Assert.False(ItemBankKeys.IsMachineName("Tiny Mushroom"));

        var gen5 = ItemBankKeys.Machine("TM01", "Gen5");
        Assert.NotEqual(gen5, ItemBankKeys.Machine("TM01", "Gen6"));
        Assert.Equal("Gen5", ItemBankKeys.MachineFamily(gen5));
        Assert.Null(ItemBankKeys.MachineFamily(Potion));
    }

    [Fact]
    public void OffersThePouchsItemsThenEveryItemThisGameCannotHold()
    {
        var tm = ItemBankKeys.Machine("TM01", "Gen5");
        var bank = ItemBank.Empty
            .Deposit(Potion, "Potion", 4, "Emerald", 3)
            .Deposit(ItemBankKeys.Item(4), "Poké Ball", 9, "Emerald", 3)
            .Deposit(ItemBankKeys.Item(1700), "Future Item", 1, "Legends Z-A", 9)
            .Deposit(tm, "TM01", 1, "Black", 5);
        var places = new Dictionary<string, ItemBankPlace>
        {
            [Potion] = new("Medicine", 17),
            [ItemBankKeys.Item(4)] = new("Balls", 4),
        };

        var offers = ItemBankOffers.ForPouch(bank, "Medicine", "Gen4", key => places.GetValueOrDefault(key));

        Assert.Equal(["Potion", "Future Item", "TM01"], offers.Select(o => o.Entry.Name));
        Assert.True(offers[0].CanWithdraw);
        Assert.False(offers[1].CanWithdraw);
        Assert.Equal("This game has no such item.", offers[1].Reason);
        Assert.Contains("Black", offers[2].Reason);
    }

    [Fact]
    public void StoreRoundTripsUnderItsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pkforge-item-bank-{Guid.NewGuid():N}");
        try
        {
            var store = new ItemBankStore(root);
            Assert.Empty(store.Read().Entries);
            var bank = ItemBank.Empty.Deposit(Potion, "Potion", 12, "Crystal", 2);
            store.Save(bank);

            Assert.True(File.Exists(Path.Combine(root, "item-bank.json")));
            var read = new ItemBankStore(root).Read();
            var entry = Assert.Single(read.Entries);
            Assert.Equal((Potion, "Potion", 12), (entry.Key, entry.Name, entry.Count));
            Assert.Equal([new ItemBankOrigin("Crystal", 2, 12)], entry.Origins);
            Assert.Single(Directory.GetFiles(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
