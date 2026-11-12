using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>The Item Bank generator's catalogue, read from PKHeX: every family has items, keyed
/// as a deposit from that game would be, never a key item or an HM, each with its game's stack
/// cap; and a made item comes out in a real game through the bank's own withdrawal.</summary>
public sealed class ItemBankCatalogTests
{
    private static ItemBankFamily Family(string label) => ItemBankCatalog.Families.Single(f => f.Label == label);

    private static SaveEngineSession Blank(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);

    public static TheoryData<string> Families() => [.. ItemBankCatalog.Families.Select(f => f.Id)];

    [Theory]
    [MemberData(nameof(Families))]
    public void EveryFamilyOffersItemsThatMayLeaveItsBag(string id)
    {
        var family = ItemBankCatalog.Families.Single(f => f.Id == id);
        var items = ItemBankCatalog.Items(family);

        Assert.NotEmpty(items);
        Assert.All(items, item =>
        {
            Assert.NotEqual("KeyItems", item.Pouch);
            Assert.False(ItemBankKeys.IsHiddenMachineName(item.EnglishName), item.EnglishName);
            Assert.True(item.Recipe.StackCap >= 1);
            Assert.Equal(family.Label, item.Recipe.Game);
            Assert.Equal(family.Generation, item.Recipe.Generation);
            Assert.False(string.IsNullOrWhiteSpace(item.Recipe.Name));
        });
        Assert.Equal(items.Count, items.Select(i => i.Recipe.Key).Distinct().Count());
    }

    [Theory]
    [InlineData("Red / Blue / Yellow")]
    [InlineData("Gold / Silver / Crystal")]
    [InlineData("FireRed / LeafGreen")]
    [InlineData("HeartGold / SoulSilver")]
    [InlineData("Scarlet / Violet")]
    public void APotionIsKeyedByItsModernIndexInEveryGeneration(string label)
    {
        var potion = ItemBankCatalog.Items(Family(label)).Single(i => i.EnglishName.Equals("Potion", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ItemBankKeys.Item(17), potion.Recipe.Key);
    }

    [Fact]
    public void MachinesAreTiedToTheirFamily()
    {
        var machines = ItemBankCatalog.Items(Family("Diamond / Pearl / Platinum")).Where(i => ItemBankKeys.IsMachineKey(i.Recipe.Key)).ToList();

        Assert.NotEmpty(machines);
        Assert.All(machines, m => Assert.Equal("Gen4", ItemBankKeys.MachineFamily(m.Recipe.Key)));
    }

    [Fact]
    public void TheStackCapIsTheGamesOwn()
    {
        var candy = ItemBankCatalog.Items(Family("HeartGold / SoulSilver")).Single(i => i.EnglishName == "Rare Candy");
        using var save = Blank(GameVersion.HG);
        var id = Array.IndexOf(GameInfo.GetStrings("en").GetItemStrings(EntityContext.Gen4, GameVersion.HG), "Rare Candy");

        Assert.Equal(save.SaveFile.Inventory.GetMaxCount(InventoryType.Medicine, id), candy.Recipe.StackCap);
    }

    [Theory]
    [InlineData(GameVersion.D, "Diamond / Pearl / Platinum")]
    [InlineData(GameVersion.SS, "HeartGold / SoulSilver")]
    [InlineData(GameVersion.LG, "FireRed / LeafGreen")]
    [InlineData(GameVersion.E, "Ruby / Sapphire / Emerald")]
    [InlineData(GameVersion.YW, "Red / Blue / Yellow")]
    [InlineData(GameVersion.SH, "Sword / Shield")]
    [InlineData(GameVersion.SL, "Scarlet / Violet")]
    public void AGameFindsItsOwnFamily(GameVersion version, string label)
    {
        using var save = Blank(version);

        Assert.Equal(label, ItemBankCatalog.FamilyOf(save)?.Label);
    }

    [Fact]
    public void AMadeItemReachesAGameThroughTheBanksWithdrawal()
    {
        var candy = ItemBankCatalog.Items(Family("Scarlet / Violet")).Single(i => i.EnglishName == "Rare Candy");
        var bank = ItemBankGenerator.Make(ItemBank.Empty, candy.Recipe, 12);

        using var platinum = Blank(GameVersion.Pt);
        var after = ItemBankService.Withdraw(platinum, bank, candy.Recipe.Key, 12, out var moved);

        Assert.Equal(12, moved);
        Assert.Equal(0, after.CountOf(candy.Recipe.Key));
    }

    [Fact]
    public void AMadeMachineIsRefusedByAnotherFamily()
    {
        var tm = ItemBankCatalog.Items(Family("Diamond / Pearl / Platinum")).First(i => ItemBankKeys.IsMachineKey(i.Recipe.Key));
        var bank = ItemBankGenerator.Make(ItemBank.Empty, tm.Recipe, 1);

        using var black = Blank(GameVersion.B2);
        Assert.Throws<InvalidOperationException>(() => ItemBankService.Withdraw(black, bank, tm.Recipe.Key, 1, out _));
        using var heartGold = Blank(GameVersion.HG);
        ItemBankService.Withdraw(heartGold, bank, tm.Recipe.Key, 1, out var moved);
        Assert.Equal(1, moved);
    }
}
