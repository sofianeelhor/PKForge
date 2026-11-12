using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>The Item Bank on real (blank) saves: items leave one game's bag and come
/// back in another generation under that game's own id, within its pouch caps.</summary>
public sealed class ItemBankTests
{
    private static SaveEngineSession Blank(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);


    private static int IdOf(ISaveEngineSession session, string englishName) =>
        Array.FindIndex(GameInfo.GetStrings("en").GetItemStrings(((SaveEngineSession)session).SaveFile.Context, ((SaveEngineSession)session).SaveFile.Version),
            name => string.Equals(name, englishName, StringComparison.OrdinalIgnoreCase));

    private static (string Pouch, int Count) Held(ISaveEngineSession session, int id) =>
        session.GetBag().SelectMany(p => p.Items.Select(i => (p.Name, i))).Where(x => x.i.Id == id).Select(x => (x.Name, x.i.Count)).FirstOrDefault();

    private static string PouchOf(ISaveEngineSession session, int id) =>
        session.GetBag().First(p => session.GetPouchLegalItems(p.Name).Contains(id) && p.Name != "PCItems").Name;

    [Theory]
    [InlineData(GameVersion.FR, GameVersion.Pt)]
    [InlineData(GameVersion.C, GameVersion.HG)]
    [InlineData(GameVersion.E, GameVersion.SW)]
    [InlineData(GameVersion.Pt, GameVersion.E)]
    [InlineData(GameVersion.SL, GameVersion.BD)]
    [InlineData(GameVersion.RD, GameVersion.Pt)]
    [InlineData(GameVersion.Pt, GameVersion.YW)]
    public void PotionsCrossGenerationsUnderEachGamesOwnId(GameVersion from, GameVersion to)
    {
        using var source = Blank(from);
        var potion = IdOf(source, "Potion");
        var pouch = PouchOf(source, potion);
        Assert.Equal(20, source.SetItemCount(pouch, potion, 20));

        var bank = ItemBankService.Deposit(source, ItemBank.Empty, pouch, potion, 7, out var deposit);
        Assert.Equal(ItemBankKeys.Item(17), deposit.Key); // Potion's modern index, whatever the source table says.
        Assert.Equal(7, bank.CountOf(deposit.Key));
        Assert.Equal(13, Held(source, potion).Count);

        using var target = Blank(to);
        var after = ItemBankService.Withdraw(target, bank, deposit.Key, 5, out var moved);
        Assert.Equal(5, moved);
        Assert.Equal(2, after.CountOf(deposit.Key));
        var landed = target;
        var targetPotion = IdOf(landed, "Potion");
        Assert.Equal(5, Held(landed, targetPotion).Count);
    }

    [Fact]
    public void DepositAndWithdrawSurviveTheSaveWrite()
    {
        // Blank saves of most formats cannot be written back, so the write is checked on PKHeX's own
        // Sun/Moon test save: Gen 3 Potions go in, the file is written and reopened, then they go back out.
        var engine = new SaveEngine();
        using var emerald = Blank(GameVersion.E);
        var gen3Potion = IdOf(emerald, "Potion");
        emerald.SetItemCount(PouchOf(emerald, gen3Potion), gen3Potion, 9);
        var bank = ItemBankService.Deposit(emerald, ItemBank.Empty, PouchOf(emerald, gen3Potion), gen3Potion, 9, out var deposit);

        using var moon = OpenSunMoon(engine);
        var potion = IdOf(moon, "Potion");
        var before = Held(moon, potion).Count;
        var after = ItemBankService.Withdraw(moon, bank, deposit.Key, 9, out var moved);
        Assert.Equal(9, moved);
        Assert.Empty(after.Entries);
        using var reopened = (SaveEngineSession)engine.OpenSession(moon.Serialize());
        Assert.Equal(before + 9, Held(reopened, potion).Count);

        var back = ItemBankService.Deposit(reopened, after, PouchOf(reopened, potion), potion, 9, out _);
        Assert.Equal(9, back.CountOf(deposit.Key));
        using var again = engine.OpenSession(reopened.Serialize());
        Assert.Equal(before, Held(again, potion).Count);
    }

    private static SaveEngineSession OpenSunMoon(SaveEngine engine)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", "SM Project 802.main");
        return (SaveEngineSession)engine.OpenSession(File.ReadAllBytes(path));
    }

    [Fact]
    public void WithdrawStopsAtThePouchCapAndKeepsTheRestBanked()
    {
        using var game = Blank(GameVersion.Pt);
        var potion = IdOf(game, "Potion");
        var key = ItemBankService.KeyOf(game, potion)!;
        var bank = ItemBank.Empty.Deposit(key, "Potion", 5000, "Emerald", 3);

        var after = ItemBankService.Withdraw(game, bank, key, 5000, out var moved);

        Assert.InRange(moved, 1, 999);
        Assert.Equal(5000 - moved, after.CountOf(key));
        Assert.Equal(moved, Held(game, potion).Count);

        // Full: nothing more goes in, nothing leaves the bank.
        var again = ItemBankService.Withdraw(game, after, key, 10, out var none);
        Assert.Equal(0, none);
        Assert.Equal(after.CountOf(key), again.CountOf(key));
    }

    [Theory]
    [InlineData(GameVersion.Pt, "HM01")]
    [InlineData(GameVersion.E, "HM01")]
    [InlineData(GameVersion.C, "HM01")]
    public void HiddenMachinesNeverLeave(GameVersion version, string hm)
    {
        using var game = Blank(version);
        var id = IdOf(game, hm);
        var pouch = PouchOf(game, id);
        game.SetItemCount(pouch, id, 1);
        Assert.NotNull(ItemBankService.WhyKept(game, pouch, id));
        Assert.Throws<InvalidOperationException>(() => ItemBankService.PlanDeposit(game, pouch, id, 1));
    }

    [Fact]
    public void KeyItemsNeverLeave()
    {
        using var game = Blank(GameVersion.Pt);
        var bike = game.GetPouchLegalItems("KeyItems").First(id => id > 0);

        game.SetItemCount("KeyItems", bike, 1);
        Assert.Equal("Key items stay with their game.", ItemBankService.WhyKept(game, "KeyItems", bike));
        Assert.Throws<InvalidOperationException>(() => ItemBankService.Deposit(game, ItemBank.Empty, "KeyItems", bike, 1, out _));
        Assert.Equal(1, Held(game, bike).Count);
    }

    [Fact]
    public void MachinesOnlyGoBackToTheirOwnFamily()
    {
        using var platinum = Blank(GameVersion.Pt);
        var tm = IdOf(platinum, "TM01");
        var pouch = PouchOf(platinum, tm);
        platinum.SetItemCount(pouch, tm, 1);
        var bank = ItemBankService.Deposit(platinum, ItemBank.Empty, pouch, tm, 1, out var deposit);

        using var heartGold = Blank(GameVersion.HG);
        Assert.True(ItemBankService.Places(heartGold).ContainsKey(deposit.Key)); // Gen 4 shares TM01 (Focus Punch).
        using var black = Blank(GameVersion.B);
        Assert.False(ItemBankService.Places(black).ContainsKey(deposit.Key));
        var offer = Assert.Single(ItemBankOffers.ForPouch(bank, pouch, ItemBankService.Family(black), key => ItemBankService.Places(black).GetValueOrDefault(key)));
        Assert.False(offer.CanWithdraw);
        Assert.NotNull(offer.Reason);
    }

    [Fact]
    public void AnItemTheTargetLacksIsOfferedDimmed()
    {
        using var sv = Blank(GameVersion.SL);
        var tera = IdOf(sv, "Ability Patch");
        var pouch = PouchOf(sv, tera);
        sv.SetItemCount(pouch, tera, 2);
        var bank = ItemBankService.Deposit(sv, ItemBank.Empty, pouch, tera, 2, out _);

        using var emerald = Blank(GameVersion.E);
        var places = ItemBankService.Places(emerald);
        var offer = Assert.Single(ItemBankOffers.ForPouch(bank, "Items", ItemBankService.Family(emerald), key => places.GetValueOrDefault(key)));
        Assert.False(offer.CanWithdraw);
        Assert.Throws<InvalidOperationException>(() => ItemBankService.Withdraw(emerald, bank, offer.Entry.Key, 1, out _));
    }
}
