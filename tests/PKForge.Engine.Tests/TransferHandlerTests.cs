using PKForge.Domain;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// "When moving a Pokémon from a Generation 1 to 5 game to a Gen 6 onwards game, the handler
/// changes to a male trainer named PKHEX instead of the save's trainer." From Generation 6
/// on a Pokémon records who holds it now, and the one taking it in is the receiving save.
/// </summary>
[Collection(nameof(LegalityEraCollection))] // sets PKHeX's process-wide RecentTrainerCache
public sealed class TransferHandlerTests
{
    private static SaveFile Receiver(GameVersion version)
    {
        var save = BlankSaveFile.Get(version, "Serena", LanguageID.English);
        save.Gender = 1;
        return save;
    }

    private static PKM Caught(PKM mon)
    {
        mon.Species = 25;
        mon.CurrentLevel = 20;
        mon.OriginalTrainerName = "Ash";
        mon.TID16 = 4242;
        mon.Language = (int)LanguageID.English;
        mon.Nickname = "PIKACHU";
        mon.Move1 = 84; // Thunder Shock
        if (mon.Format >= 3) mon.PID = 0x1234_5678;
        if (mon.Format >= 3) { mon.MetLevel = 20; mon.Ball = 4; }
        mon.RefreshChecksum();
        return mon;
    }

    public static TheoryData<PKM, GameVersion> Arrivals => new()
    {
        { Caught(new PK1()), GameVersion.SN },
        { Caught(new PK2()), GameVersion.US },
        { Caught(new PK3 { Version = GameVersion.E }), GameVersion.X },
        { Caught(new PK4 { Version = GameVersion.HG }), GameVersion.OR },
        { Caught(new PK5 { Version = GameVersion.W2 }), GameVersion.X },
        { Caught(new PK5 { Version = GameVersion.W2 }), GameVersion.UM },
    };

    [Theory]
    [MemberData(nameof(Arrivals))]
    public void TheReceivingTrainerBecomesTheHandler(PKM source, GameVersion destination)
    {
        // Whatever PKHeX loaded last must not leak into the result.
        RecentTrainerCache.SetRecentTrainer(new SimpleTrainerInfo(GameVersion.SW) { OT = "Stranger" });
        var save = Receiver(destination);

        var conversion = CrossFormatConverter.Convert(source, save, out var refusal);

        Assert.True(conversion is not null, refusal);
        var arrived = conversion.Entity;
        Assert.Equal("Serena", arrived.HandlingTrainerName);
        Assert.Equal(1, arrived.HandlingTrainerGender);
        Assert.Equal(1, arrived.CurrentHandler);
        Assert.Equal("Ash", arrived.OriginalTrainerName);
    }

    private static SaveEngineSession Game(GameVersion version, string trainer, IPokemonHistoryStore? history = null)
    {
        var save = BlankSaveFile.Get(version, trainer, LanguageID.English);
        save.Gender = 1;
        var session = new SaveEngineSession(save, null);
        if (history is not null) session.HistorySource = () => history;
        return session;
    }

    /// <summary>Imports <paramref name="mon"/> into slot 1 of <paramref name="game"/>, the way every
    /// transfer, Bank withdrawal and .pk import lands, and returns what landed.</summary>
    private static PKM Land(SaveEngineSession game, PKM mon)
    {
        var conversion = game.ImportSlotWithReport(0, 0, mon.Data.ToArray(), out var refusal, EntityBytes.FormatOf(mon));
        Assert.True(conversion is not null, refusal);
        game.CommitHistory();
        return game.GetEntity(0, 0);
    }

    public static TheoryData<GameVersion, GameVersion> OnwardRoutes => new()
    {
        { GameVersion.X, GameVersion.Y },   // the same format, between two saves
        { GameVersion.X, GameVersion.SN },  // Gen 6 to Gen 7, a route PKHeX names no handler on
        { GameVersion.X, GameVersion.SW },  // on through HOME
        { GameVersion.UM, GameVersion.X },  // a downgrade
    };

    [Theory]
    [MemberData(nameof(OnwardRoutes))]
    public void TheNextSaveBecomesTheHandlerNotThePreviousOne(GameVersion first, GameVersion next)
    {
        // Ash's Pikachu enters Red's game (Red becomes its handler), then moves on to Blue's.
        var inRed = Land(Game(first, "Red"), Caught(new PK5 { Version = GameVersion.W2 }));
        Assert.Equal("Red", inRed.HandlingTrainerName);

        var inBlue = Land(Game(next, "Blue"), inRed);

        Assert.Equal("Blue", inBlue.HandlingTrainerName);
        Assert.Equal(1, inBlue.HandlingTrainerGender);
        Assert.Equal(1, inBlue.CurrentHandler);
        Assert.Equal("Ash", inBlue.OriginalTrainerName);
    }

    [Fact]
    public void APokemonHistoryRestoreDoesNotBringBackTheOldHandler()
    {
        // Red's Sword copy is remembered when it leaves for Blue's Violet; it then comes back to
        // Sword in Green's save, restored from that copy, which still names Red.
        var history = new InMemoryPokemonHistoryStore();
        var inRed = Land(Game(GameVersion.SW, "Red", history), Caught(new PK5 { Version = GameVersion.W2 }));
        var inBlue = Land(Game(GameVersion.VL, "Blue", history), inRed);

        var conversion = Game(GameVersion.SH, "Green", history).ImportSlotWithReport(0, 0, inBlue.Data.ToArray(), out var refusal, EntityBytes.FormatOf(inBlue));

        Assert.True(conversion is not null, refusal);
        Assert.Contains(conversion.Warnings, w => w.StartsWith("Restored from your", StringComparison.Ordinal));
        Assert.Equal("Green", conversion.Entity.HandlingTrainerName);
        Assert.Equal(1, conversion.Entity.CurrentHandler);
    }

    [Fact]
    public void ItsOwnTrainerTakesItBackAsTheOriginalTrainer()
    {
        // Serena's own Pokémon, handled by Blue in the meantime, comes back to her.
        var mine = Receiver(GameVersion.SW);
        var pk8 = (PK8)Caught(new PK8 { Version = GameVersion.SW });
        pk8.OriginalTrainerName = mine.OT;
        pk8.OriginalTrainerGender = mine.Gender;
        pk8.ID32 = mine.ID32;
        pk8.CurrentHandler = 1;
        pk8.HandlingTrainerName = "Blue";
        pk8.RefreshChecksum();

        var conversion = CrossFormatConverter.Convert(pk8, mine, out var refusal);

        Assert.True(conversion is not null, refusal);
        Assert.Equal(0, conversion.Entity.CurrentHandler);
        Assert.Equal(1, pk8.CurrentHandler); // the source is never modified
    }

    [Fact]
    public void APokemonStillWithItsOriginalTrainerIsNotStampedAsTraded()
    {
        var pk8 = (PK8)Caught(new PK8 { Version = GameVersion.SW });

        var conversion = CrossFormatConverter.Convert(pk8, Receiver(GameVersion.SH), out var refusal);

        Assert.True(conversion is not null, refusal);
        Assert.Equal(0, conversion.Entity.CurrentHandler);
        Assert.Equal("", conversion.Entity.HandlingTrainerName);
    }
}
