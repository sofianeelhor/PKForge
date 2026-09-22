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

    private static SaveEngineSession Open(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);
}
