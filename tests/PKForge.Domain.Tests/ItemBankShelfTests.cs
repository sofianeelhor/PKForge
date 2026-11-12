using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>The Item Bank screen's left side: tabs, search, sorts, and what a game cannot take.</summary>
public sealed class ItemBankShelfTests
{
    private static readonly string Potion = ItemBankKeys.Item(17);
    private static readonly string PokeBall = ItemBankKeys.Item(4);
    private static readonly string RareCandy = ItemBankKeys.Item(50);
    private static readonly string Tm01 = ItemBankKeys.Machine("TM01", "Gen4");

    private static ItemBank Bank() => ItemBank.Empty
        .Deposit(Potion, "Potion", 5, "Platinum", 4)
        .Deposit(PokeBall, "Poké Ball", 20, "FireRed", 3)
        .Deposit(RareCandy, "Rare Candy", 2, "HeartGold", 4)
        .Deposit(Tm01, "TM01", 1, "Platinum", 4);

    private static ItemBankPlace? Emerald(string key) =>
        key == Potion ? new ItemBankPlace("Medicine", 13)
        : key == PokeBall ? new ItemBankPlace("Balls", 4)
        : key == RareCandy ? new ItemBankPlace("Medicine", 68)
        : null;

    [Fact]
    public void WithoutAGameEveryItemShowsAndNoneIsRefused()
    {
        var rows = ItemBankShelf.Rows(Bank(), null, "", ItemBankSort.Name);
        Assert.Equal(["Poké Ball", "Potion", "Rare Candy", "TM01"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.Null(r.Reason));
        Assert.All(rows, r => Assert.False(r.CanWithdraw));
    }

    [Fact]
    public void APouchTabListsWhatGoesThereThenWhatTheGameCannotHold()
    {
        var rows = ItemBankShelf.Rows(Bank(), "Medicine", "", ItemBankSort.Name, Emerald, "Gen3");
        Assert.Equal(["Potion", "Rare Candy", "TM01"], rows.Select(r => r.Name));
        Assert.True(rows[0].CanWithdraw);
        Assert.False(rows[2].CanWithdraw);
        Assert.Contains("goes back to Platinum", rows[2].Reason);
    }

    [Fact]
    public void TheAllTabKeepsRefusedItemsLastWhateverTheSort()
    {
        var rows = ItemBankShelf.Rows(Bank(), null, "", ItemBankSort.Count, Emerald, "Gen3");
        Assert.Equal(["Poké Ball", "Potion", "Rare Candy", "TM01"], rows.Select(r => r.Name));
        Assert.Equal("TM01", rows[^1].Name);
    }

    [Fact]
    public void SortsByLatestDepositAndByGame()
    {
        Assert.Equal(["TM01", "Rare Candy", "Poké Ball", "Potion"],
            ItemBankShelf.Rows(Bank(), null, "", ItemBankSort.Recent).Select(r => r.Name));
        Assert.Equal(["Poké Ball", "Rare Candy", "Potion", "TM01"],
            ItemBankShelf.Rows(Bank(), null, "", ItemBankSort.Game).Select(r => r.Name));
    }

    [Fact]
    public void SearchIgnoresAccentsCaseAndSpacesAndUsesTheGamesOwnName()
    {
        Assert.Equal(["Poké Ball"], ItemBankShelf.Rows(Bank(), null, "POKE ba", ItemBankSort.Name).Select(r => r.Name));
        var renamed = ItemBankShelf.Rows(Bank(), null, "candy", ItemBankSort.Name, nameOf: (e, _) => e.Key == RareCandy ? "RARE CANDY" : e.Name);
        Assert.Equal(["RARE CANDY"], renamed.Select(r => r.Name));
    }

    [Fact]
    public void OriginsAddUpPerGame()
    {
        var bank = Bank().Deposit(Potion, "Potion", 3, "HeartGold", 4).Deposit(Potion, "Potion", 1, "Platinum", 4);
        Assert.Equal("Platinum ×6 · HeartGold ×3", ItemBankShelf.Origins(bank.Find(Potion)!));
        Assert.Equal("HeartGold", ItemBankShelf.LatestGame(bank.Find(Potion)!));
    }

    [Theory]
    [InlineData(1, -1, 5, 5)]
    [InlineData(5, 1, 5, 1)]
    [InlineData(2, 1, 5, 3)]
    [InlineData(1, 1, 0, 1)]
    public void AmountArrowsWrapWithinWhatIsHeld(int amount, int delta, int most, int expected) =>
        Assert.Equal(expected, ItemBankShelf.Step(amount, delta, most));

    [Fact]
    public void SortsCycle()
    {
        var sort = ItemBankSort.Name;
        for (var i = 0; i < 4; i++) sort = ItemBankShelf.Next(sort);
        Assert.Equal(ItemBankSort.Name, sort);
    }
}
