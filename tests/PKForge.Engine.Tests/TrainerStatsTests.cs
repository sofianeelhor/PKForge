using PKHeX.Core;
using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Playtime, Battle Points, and coins live at format-specific offsets with
/// format-specific widths: every writer must clamp to what the storage holds,
/// report the maxima it clamps to, and never pretend a counter exists where the
/// format does not keep one.
/// </summary>
public sealed class TrainerStatsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void EveryGenerationExposesEditablePlayTime(int generation)
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(generation);
        Assert.True(session.GetTrainerStats().SupportsPlayTime);

        session.SetTrainerStats(new TrainerStatsEdit(PlayTimeHours: 123, PlayTimeMinutes: 45));

        var stats = session.GetTrainerStats();
        Assert.Equal(123, stats.PlayTimeHours);
        Assert.Equal(45, stats.PlayTimeMinutes);
        Assert.Equal(generation == 1 ? byte.MaxValue : ushort.MaxValue, stats.PlayTimeHoursMax);
    }

    [Fact]
    public void Gen1PlayTimeClampsToOneByteHoursAndSixtyMinutes()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(1);

        session.SetTrainerStats(new TrainerStatsEdit(PlayTimeHours: 999, PlayTimeMinutes: 99));

        var stats = session.GetTrainerStats();
        Assert.Equal(byte.MaxValue, stats.PlayTimeHoursMax);
        Assert.Equal(255, stats.PlayTimeHours);
        Assert.Equal(59, stats.PlayTimeMinutes);
    }

    [Theory]
    [InlineData(3, GameVersion.E, true)]
    [InlineData(4, GameVersion.Pt, true)]
    [InlineData(5, GameVersion.B2, true)]
    [InlineData(6, GameVersion.AS, true)]
    [InlineData(7, GameVersion.UM, true)]
    [InlineData(8, GameVersion.SW, true)]
    [InlineData(9, GameVersion.VL, false)]
    public void BattlePointsFollowTheFormat(int generation, GameVersion version, bool supported)
    {
        using var session = Open(version);
        Assert.Equal(generation, session.Generation);
        var stats = session.GetTrainerStats();
        Assert.Equal(supported, stats.SupportsBP);
        if (!supported)
            return;

        Assert.Equal(9999, stats.BPMax);
        session.SetTrainerStats(new TrainerStatsEdit(BP: 4321));
        Assert.Equal(4321, session.GetTrainerStats().BP);

        session.SetTrainerStats(new TrainerStatsEdit(BP: 99999));
        Assert.Equal(9999, session.GetTrainerStats().BP);
    }

    [Fact]
    public void EmeraldBattlePointsLiveInTheSmallBlock()
    {
        using var session = Open(GameVersion.E);
        var save = Assert.IsType<SAV3E>(session.SaveFile);

        session.SetTrainerStats(new TrainerStatsEdit(BP: 777));

        Assert.Equal(777, save.SmallBlock.BP);
    }

    [Fact]
    public void BrilliantDiamondBattlePointsLiveInTheBattleTowerBlock()
    {
        using var session = Open(GameVersion.BD);
        var save = Assert.IsType<SAV8BS>(session.SaveFile);

        session.SetTrainerStats(new TrainerStatsEdit(BP: 555));

        Assert.Equal(555u, save.BattleTower.BP);
    }

    [Fact]
    public void FireRedHasNoBattlePoints()
    {
        using var session = Open(GameVersion.FR);
        Assert.False(session.GetTrainerStats().SupportsBP);
        // The ignored field must not throw or invent a counter.
        session.SetTrainerStats(new TrainerStatsEdit(BP: 100));
        Assert.False(session.GetTrainerStats().SupportsBP);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void GameBoyAndGbaGenerationsExposeCoins(int generation)
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(generation);
        var stats = session.GetTrainerStats();
        Assert.True(stats.SupportsCoins);
        Assert.Equal(generation == 4 ? 50_000 : 9999, stats.CoinsMax);

        session.SetTrainerStats(new TrainerStatsEdit(Coins: 4242));

        Assert.Equal(4242, session.GetTrainerStats().Coins);
    }

    [Fact]
    public void Gen4CoinCaseHoldsFiftyThousand()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(4);

        session.SetTrainerStats(new TrainerStatsEdit(Coins: 60000));

        var stats = session.GetTrainerStats();
        Assert.Equal(50_000, stats.CoinsMax);
        Assert.Equal(50_000, stats.Coins);
    }

    [Fact]
    public void Gen1CoinCaseIsBinaryCodedDecimal()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(1);
        var save = Assert.IsType<SAV1>(session.SaveFile);

        session.SetTrainerStats(new TrainerStatsEdit(Coins: 1234));

        Assert.Equal(1234, (int)save.Coin);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    public void ModernFormatsDroppedTheCoinCase(int generation)
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(generation);
        Assert.False(session.GetTrainerStats().SupportsCoins);
        session.SetTrainerStats(new TrainerStatsEdit(Coins: 100));
        Assert.False(session.GetTrainerStats().SupportsCoins);
    }

    [Fact]
    public void PartialEditLeavesOtherCountersAlone()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(7);
        session.SetTrainerStats(new TrainerStatsEdit(PlayTimeHours: 10, PlayTimeMinutes: 30, BP: 400));

        session.SetTrainerStats(new TrainerStatsEdit(BP: 500));

        var stats = session.GetTrainerStats();
        Assert.Equal(10, stats.PlayTimeHours);
        Assert.Equal(30, stats.PlayTimeMinutes);
        Assert.Equal(500, stats.BP);
    }

    public enum CurrencyKind { BP, Coins, Watts, FestaCoins, LeaguePoints, BlueberryPoints }

    /// <summary>Every currency on every game that keeps it: set, serialize, reload, read back,
    /// and an over-cap value lands on the game's cap.</summary>
    [Theory]
    [InlineData(GameVersion.Pt, CurrencyKind.BP, 9999)]
    [InlineData(GameVersion.Pt, CurrencyKind.Coins, 50_000)]
    [InlineData(GameVersion.HG, CurrencyKind.BP, 9999)]
    [InlineData(GameVersion.HG, CurrencyKind.Coins, 50_000)]
    [InlineData(GameVersion.HG, CurrencyKind.Watts, 9_999_999)]
    [InlineData(GameVersion.B2, CurrencyKind.BP, 9999)]
    [InlineData(GameVersion.Y, CurrencyKind.BP, 9999)]
    [InlineData(GameVersion.OR, CurrencyKind.BP, 9999)]
    [InlineData(GameVersion.SN, CurrencyKind.BP, 9999)]
    [InlineData(GameVersion.SN, CurrencyKind.FestaCoins, 9_999_999)]
    [InlineData(GameVersion.UM, CurrencyKind.FestaCoins, 9_999_999)]
    [InlineData(GameVersion.SW, CurrencyKind.BP, 9999)]
    [InlineData(GameVersion.SW, CurrencyKind.Watts, 9_999_999)]
    [InlineData(GameVersion.BD, CurrencyKind.BP, 9999)]
    [InlineData(GameVersion.SL, CurrencyKind.LeaguePoints, 9_999_999)]
    [InlineData(GameVersion.VL, CurrencyKind.BlueberryPoints, 9_999_999)]
    public void CurrencyRoundTripsThroughTheSaveFileAndClamps(GameVersion version, CurrencyKind kind, int max)
    {
        using var session = OpenWritable(version);
        if (session.SaveFile is SAV9SV sv)
            TypeAsPlayedSave(sv);
        var stats = session.GetTrainerStats();
        Assert.True(Supports(stats, kind));
        Assert.Equal(max, Max(stats, kind));

        session.SetTrainerStats(Edit(kind, 1234));
        using (var reloaded = Reload(session))
            Assert.Equal(1234, Value(reloaded.GetTrainerStats(), kind));

        session.SetTrainerStats(Edit(kind, int.MaxValue));
        using (var reloaded = Reload(session))
            Assert.Equal(max, Value(reloaded.GetTrainerStats(), kind));

        session.SetTrainerStats(Edit(kind, -5));
        Assert.Equal(0, Value(session.GetTrainerStats(), kind));
    }

    [Fact]
    public void ScarletVioletPointsNeedTheirTypedBlock()
    {
        // A blank save carries the blocks untyped: there is no value to show or write.
        using var session = Open(GameVersion.SL);
        var stats = session.GetTrainerStats();
        Assert.False(stats.SupportsLeaguePoints);
        Assert.False(stats.SupportsBlueberryPoints);
        session.SetTrainerStats(new TrainerStatsEdit(LeaguePoints: 5, BlueberryPoints: 5));
        Assert.False(session.GetTrainerStats().SupportsLeaguePoints);
    }

    /// <summary>A played Scarlet/Violet save stores League and Blueberry Points as typed u32
    /// blocks; PKHeX's blank one leaves them untyped, so the test types them the same way.</summary>
    private static void TypeAsPlayedSave(SAV9SV save)
    {
        foreach (var key in new[] { SaveBlockAccessor9SV.KLeaguePoints, SaveBlockAccessor9SV.KBlueberryPoints })
        {
            Assert.True(save.Blocks.TryGetBlock(key, out var block));
            if (block.Type == SCTypeCode.None)
                block.ChangeStoredType(SCTypeCode.UInt32);
        }
    }

    [Fact]
    public void SwordWattsKeepTheLifetimeTotalAtLeastTheHeldAmount()
    {
        using var session = Open(GameVersion.SW);
        var save = Assert.IsType<SAV8SWSH>(session.SaveFile);

        session.SetTrainerStats(new TrainerStatsEdit(Watts: 50_000));

        Assert.Equal(50_000u, save.MyStatus.Watt);
        Assert.True(save.GetRecord(Record8.WattTotal) >= 50_000);
    }

    [Theory]
    [InlineData(GameVersion.Pt)]
    [InlineData(GameVersion.B2)]
    [InlineData(GameVersion.Y)]
    [InlineData(GameVersion.BD)]
    [InlineData(GameVersion.FR)]
    public void GamesWithoutTheNewerCurrenciesDoNotPretend(GameVersion version)
    {
        using var session = Open(version);
        session.SetTrainerStats(new TrainerStatsEdit(Watts: 1, FestaCoins: 1, LeaguePoints: 1, BlueberryPoints: 1));
        var stats = session.GetTrainerStats();
        Assert.False(stats.SupportsWatts);
        Assert.False(stats.SupportsFestaCoins);
        Assert.False(stats.SupportsLeaguePoints);
        Assert.False(stats.SupportsBlueberryPoints);
    }

    private static SaveEngineSession Reload(SaveEngineSession session)
    {
        // Blank saves lack the footers format detection keys on, so each reopens as its own
        // type. PKHeX's Switch blanks carry untyped blocks a real file never has, so their
        // encrypted image cannot be decrypted again; as in DexRoundTripTests they reload from a
        // deep copy of the block list, which is exactly what SwishCrypto.Encrypt writes out.
        if (session.SaveFile is ISCBlockArray)
            return new SaveEngineSession(session.SaveFile.Clone(), null);
        Memory<byte> bytes = session.Serialize().ToArray();
        SaveFile save = session.SaveFile switch
        {
            SAV4HGSS => new SAV4HGSS(bytes),
            SAV4Pt => new SAV4Pt(bytes),
            SAV5B2W2 => new SAV5B2W2(bytes),
            SAV6XY => new SAV6XY(bytes),
            SAV6AO => new SAV6AO(bytes),
            SAV7USUM => new SAV7USUM(bytes),
            SAV7SM => new SAV7SM(bytes),
            SAV8SWSH => new SAV8SWSH(bytes),
            SAV8BS => new SAV8BS(bytes),
            SAV9SV => new SAV9SV(bytes),
            var other => throw new NotSupportedException(other.GetType().Name),
        };
        return new SaveEngineSession(save, null);
    }

    private static bool Supports(TrainerStats s, CurrencyKind kind) => kind switch
    {
        CurrencyKind.BP => s.SupportsBP,
        CurrencyKind.Coins => s.SupportsCoins,
        CurrencyKind.Watts => s.SupportsWatts,
        CurrencyKind.FestaCoins => s.SupportsFestaCoins,
        CurrencyKind.LeaguePoints => s.SupportsLeaguePoints,
        _ => s.SupportsBlueberryPoints,
    };

    private static int Value(TrainerStats s, CurrencyKind kind) => kind switch
    {
        CurrencyKind.BP => s.BP,
        CurrencyKind.Coins => s.Coins,
        CurrencyKind.Watts => s.Watts,
        CurrencyKind.FestaCoins => s.FestaCoins,
        CurrencyKind.LeaguePoints => s.LeaguePoints,
        _ => s.BlueberryPoints,
    };

    private static int Max(TrainerStats s, CurrencyKind kind) => kind switch
    {
        CurrencyKind.BP => s.BPMax,
        CurrencyKind.Coins => s.CoinsMax,
        CurrencyKind.Watts => s.WattsMax,
        CurrencyKind.FestaCoins => s.FestaCoinsMax,
        CurrencyKind.LeaguePoints => s.LeaguePointsMax,
        _ => s.BlueberryPointsMax,
    };

    private static TrainerStatsEdit Edit(CurrencyKind kind, int value) => kind switch
    {
        CurrencyKind.BP => new(BP: value),
        CurrencyKind.Coins => new(Coins: value),
        CurrencyKind.Watts => new(Watts: value),
        CurrencyKind.FestaCoins => new(FestaCoins: value),
        CurrencyKind.LeaguePoints => new(LeaguePoints: value),
        _ => new(BlueberryPoints: value),
    };

    /// <summary>PKHeX's blank Gen 4 save is a bare partition that cannot be exported; a
    /// zeroed full-size image of the same game can, so the round trip starts from one.</summary>
    private static SaveEngineSession OpenWritable(GameVersion version) => version switch
    {
        GameVersion.HG or GameVersion.SS => new(new SAV4HGSS(new byte[0x80000]), null),
        GameVersion.Pt => new(new SAV4Pt(new byte[0x80000]), null),
        _ => Open(version),
    };

    private static SaveEngineSession Open(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);
}
