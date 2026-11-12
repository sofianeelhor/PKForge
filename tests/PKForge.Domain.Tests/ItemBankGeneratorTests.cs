using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>The Item Bank's generator: a stack at most, never past the bank's cap, recorded under
/// the family it was made for, and only the bank changes.</summary>
public sealed class ItemBankGeneratorTests
{
    private static readonly ItemBankRecipe RareCandy = new(ItemBankKeys.Item(50), "Rare Candy", "HeartGold / SoulSilver", 4, 999);
    private static readonly ItemBankRecipe Tm26 = new(ItemBankKeys.Machine("TM26", "Gen4"), "TM26", "Diamond / Pearl / Platinum", 4, 99);

    [Fact]
    public void MakingAddsTheAmountUnderTheFamilyItWasMadeFor()
    {
        var bank = ItemBankGenerator.Make(ItemBank.Empty, RareCandy, 30);

        var entry = Assert.Single(bank.Entries);
        Assert.Equal(RareCandy.Key, entry.Key);
        Assert.Equal("Rare Candy", entry.Name);
        Assert.Equal(30, entry.Count);
        var origin = Assert.Single(entry.Origins);
        Assert.Equal("HeartGold / SoulSilver (made)", origin.Game);
        Assert.Equal(4, origin.Generation);
        Assert.Equal(30, origin.Count);
    }

    [Fact]
    public void AMachineKeepsItsFamilyKeySoItOnlyGoesBackThere()
    {
        var bank = ItemBankGenerator.Make(ItemBank.Empty, Tm26, 1);

        Assert.Equal("Gen4", ItemBankKeys.MachineFamily(Assert.Single(bank.Entries).Key));
    }

    [Fact]
    public void MadeItemsJoinAnEntryDepositedFromARealSaveAsTheirOwnOrigin()
    {
        var bank = ItemBank.Empty.Deposit(RareCandy.Key, "Rare Candy", 4, "HeartGold", 4);

        bank = ItemBankGenerator.Make(bank, RareCandy, 6);

        var entry = Assert.Single(bank.Entries);
        Assert.Equal(10, entry.Count);
        Assert.Equal(["HeartGold", "HeartGold / SoulSilver (made)"], entry.Origins.Select(o => o.Game));
    }

    [Fact]
    public void OneGenerationIsAtMostAStackOfTheGame()
    {
        Assert.Equal(99, ItemBankGenerator.Most(ItemBank.Empty, Tm26));
        Assert.Equal(999, ItemBankGenerator.Most(ItemBank.Empty, RareCandy));
        Assert.Throws<InvalidOperationException>(() => ItemBankGenerator.Make(ItemBank.Empty, Tm26, 100));
        Assert.Equal(99, ItemBankGenerator.Make(ItemBank.Empty, Tm26, 99).CountOf(Tm26.Key));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NothingOrLessIsRefused(int amount)
    {
        Assert.Throws<InvalidOperationException>(() => ItemBankGenerator.Make(ItemBank.Empty, RareCandy, amount));
    }

    [Fact]
    public void TheBanksOwnCapStopsTheStackShort()
    {
        var bank = ItemBank.Empty.Deposit(RareCandy.Key, "Rare Candy", ItemBank.MaxPerEntry - 10, "HeartGold", 4);

        Assert.Equal(10, ItemBankGenerator.Most(bank, RareCandy));
        Assert.Throws<InvalidOperationException>(() => ItemBankGenerator.Make(bank, RareCandy, 11));
        Assert.Equal(ItemBank.MaxPerEntry, ItemBankGenerator.Make(bank, RareCandy, 10).CountOf(RareCandy.Key));
    }

    [Fact]
    public void AFullEntryIsRefusedWithItsReasonAndTheBankStaysAsItWas()
    {
        var bank = ItemBank.Empty.Deposit(RareCandy.Key, "Rare Candy", ItemBank.MaxPerEntry, "HeartGold", 4);

        Assert.Equal(0, ItemBankGenerator.Most(bank, RareCandy));
        Assert.NotNull(ItemBankGenerator.Refusal(bank, RareCandy));
        var refused = Assert.Throws<InvalidOperationException>(() => ItemBankGenerator.Make(bank, RareCandy, 1));
        Assert.Contains("most", refused.Message);
        Assert.Equal(ItemBank.MaxPerEntry, bank.CountOf(RareCandy.Key));
    }

    [Fact]
    public void AStackCapOfZeroStillAllowsOne()
    {
        var odd = RareCandy with { StackCap = 0 };

        Assert.Equal(1, ItemBankGenerator.Most(ItemBank.Empty, odd));
    }

    [Fact]
    public void TheSearchFoldsSpellingAndThePouchNarrows()
    {
        var recipes = new[] { ("Poké Ball", "Balls"), ("Potion", "Medicine"), ("Super Potion", "Medicine"), ("Rare Candy", "Medicine") };

        var potions = ItemBankGenerator.Filter(recipes, r => r.Item1, r => r.Item2, null, "POTION");
        Assert.Equal(["Potion", "Super Potion"], potions.Select(r => r.Item1));

        var balls = ItemBankGenerator.Filter(recipes, r => r.Item1, r => r.Item2, null, "poke ball");
        Assert.Equal(["Poké Ball"], balls.Select(r => r.Item1));

        var medicine = ItemBankGenerator.Filter(recipes, r => r.Item1, r => r.Item2, "Medicine", "");
        Assert.Equal(["Potion", "Rare Candy", "Super Potion"], medicine.Select(r => r.Item1));
    }
}
