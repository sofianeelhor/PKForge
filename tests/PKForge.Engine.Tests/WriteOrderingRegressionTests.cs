using PKForge.Domain;
using PKForge.Infrastructure;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Engine review findings 13 and 15: things recorded beside a save write happen once, and only
/// for writes that happened. A Pokéwalker homecoming cut short is finished without banking
/// anything twice; Pokémon History learns a trip only once the save holding it is written.
/// </summary>
public sealed class WriteOrderingRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-ordering-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static BankEntryInfo? Describe(byte[] data, string format) =>
        new(25, 0, false, "Catch", 5, 4, WalkerHomecomings.SourceName, format);

    private static WalkerState WalkWithFinds()
    {
        var state = WalkerRules.Start(new WalkerState(), WalkerCourses.All[0], "bank:x", "Pikachu", 25, 0, false, "fp",
            [ParkType.Electric], 42, Noon);
        return state with { Walk = state.Walk! with { Catches = [4, 5], Items = ["Potion", "Antidote"] } };
    }

    /// <summary>Finding 13: the walk used to be cleared only after the Bank and the Item Bank were
    /// written, so an interruption in between let the same walk come home again and bank its
    /// catches and finds a second time.</summary>
    [Fact]
    public void A_homecoming_cut_short_is_finished_once_never_banked_twice()
    {
        var bank = new FileBankService(Path.Combine(_root, "bank"));
        var items = new ItemBankStore(Path.Combine(_root, "item-bank"));
        var begun = WalkerHomecomings.Begin(WalkWithFinds(), WalkerService.TrainerFor(null, null, null));

        // The walk is over in the very state that records the homecoming: it cannot come home again.
        Assert.Null(begun.Walk);
        Assert.Equal(2, begun.Homecoming!.Catches.Count);
        Assert.Throws<InvalidOperationException>(() => WalkerHomecomings.Begin(begun with { Homecoming = null }, WalkerService.TrainerFor(null, null, null)));

        // The app dies right after the first catch reached the Bank, before that was noted.
        Assert.Throws<IOException>(() => WalkerHomecomings.Bank(begun, bank, Describe, items, _ => throw new IOException("killed")));
        Assert.Single(bank.GetAll());

        // Reopened, the homecoming on disk (the one before the crash) is finished.
        var saved = begun;
        var (done, lines) = WalkerHomecomings.Bank(begun, bank, Describe, items, state => saved = state);
        Assert.Equal(2, bank.GetAll().Count); // the first catch is recognised by its bytes, not banked again
        Assert.Equal(1, items.Read().CountOf(ItemBankKeys.Item(17)));
        Assert.Contains(lines, line => line.Contains("Item Bank", StringComparison.Ordinal));
        Assert.Same(done, saved);

        // Run again from a state that never recorded the finds: the Item Bank takes them once only.
        WalkerHomecomings.Bank(begun with { Homecoming = begun.Homecoming with { Catches = [.. done.Homecoming!.Catches] } },
            bank, Describe, items, _ => { });
        Assert.Equal(2, bank.GetAll().Count);
        Assert.Equal(1, items.Read().CountOf(ItemBankKeys.Item(17)));
        Assert.Equal(1, items.Read().CountOf(ItemBankKeys.Item(18)));
    }

    private static PK5 Pikachu()
    {
        var pk = new PK5
        {
            Species = (ushort)Species.Pikachu, CurrentLevel = 40, Version = GameVersion.W, Language = (int)LanguageID.English,
            OriginalTrainerName = "Sof", TID16 = 24680, SID16 = 13579, Ball = (byte)Ball.Poke, MetLevel = 20, PID = 0x2468_ACE1,
            Nature = (Nature)(0x2468_ACE1 % 25), OriginalTrainerFriendship = 120,
        };
        pk.Gender = EntityGender.GetFromPID((ushort)Species.Pikachu, pk.PID);
        pk.ClearNickname();
        pk.SetMoves([(ushort)Move.ThunderShock, (ushort)Move.QuickAttack]);
        pk.RefreshAbility(0);
        pk.ResetPartyStats();
        pk.HealPP();
        pk.RefreshChecksum();
        return pk;
    }

    /// <summary>Finding 15: Serialize() saved the pending trips. A transfer preview, the hex editor or
    /// a write that was then refused left a trip (and an arrival baseline) that never happened.</summary>
    [Fact]
    public void Pokemon_history_learns_a_trip_only_when_the_written_save_holds_it()
    {
        var store = new InMemoryPokemonHistoryStore();
        using var sword = new SaveEngineSession(BlankSaveFile.Get(GameVersion.SW, "Sof", LanguageID.English), null)
        {
            HistorySource = () => store,
        };
        var mon = Pikachu();
        Assert.NotNull(sword.ImportSlotWithReport(0, 0, mon.Data.ToArray(), out var refusal, EntityBytes.FormatOf(mon)) ?? throw new InvalidOperationException(refusal));
        Assert.Equal(1, sword.PendingHistoryCount);

        // A preview, the hex editor's baseline, a structural round-trip check: all serialize, none writes.
        _ = sword.Serialize();
        _ = sword.ValidateUnchangedRoundTrip();
        Assert.Empty(store.All());
        Assert.Equal(1, sword.PendingHistoryCount);

        // The write path calls this once the save is written.
        ((ISaveEngineSession)sword).CommitHistory();
        Assert.Single(store.All());
        Assert.Equal(0, sword.PendingHistoryCount);
    }
}
