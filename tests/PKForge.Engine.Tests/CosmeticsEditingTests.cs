using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class CosmeticsEditingTests
{
    [Fact]
    public void Gen4MarkingsAndContestStatsRoundTrip()
    {
        using var session = Seed(4, new PK4 { Version = GameVersion.Pt });
        var before = session.GetCosmetics(0, 0);
        Assert.Equal(6, before.Markings.Count);
        Assert.All(before.Markings, marking => Assert.Equal(1, marking.MaxValue));
        Assert.Equal(6, before.ContestStats.Count);

        session.ApplyCosmeticEdit(0, 0, new CosmeticEdit(
            Markings: [1, 0, 1, 0, 1, 0],
            ContestStats: [1, 2, 3, 4, 5, 6]));

        var after = session.GetCosmetics(0, 0);
        Assert.Equal([1, 0, 1, 0, 1, 0], after.Markings.Select(m => m.Value));
        Assert.Equal([1, 2, 3, 4, 5, 6], after.ContestStats);
    }

    [Fact]
    public void Gen7ColorMarkingsAndCareDataRoundTrip()
    {
        using var session = Seed(7, new PK7 { Version = GameVersion.UM });
        var before = session.GetCosmetics(0, 0);
        Assert.Equal(6, before.Markings.Count);
        Assert.All(before.Markings, marking => Assert.Equal(2, marking.MaxValue));
        Assert.True(before.SupportsAffection);
        Assert.True(before.SupportsFullnessEnjoyment);

        session.ApplyCosmeticEdit(0, 0, new CosmeticEdit(
            Markings: [2, 1, 0, 2, 1, 0],
            OriginalTrainerAffection: 120,
            HandlingTrainerAffection: 80,
            Fullness: 40,
            Enjoyment: 200));

        var after = session.GetCosmetics(0, 0);
        Assert.Equal([2, 1, 0, 2, 1, 0], after.Markings.Select(m => m.Value));
        Assert.Equal(120, after.OriginalTrainerAffection);
        Assert.Equal(80, after.HandlingTrainerAffection);
        Assert.Equal(40, after.Fullness);
        Assert.Equal(200, after.Enjoyment);
    }

    [Fact]
    public void Gen8SizeFavoriteAndDynamaxDataRoundTrip()
    {
        using var session = Seed(8, new PK8 { Version = GameVersion.SW });
        var before = session.GetCosmetics(0, 0);
        Assert.True(before.SupportsSize);
        Assert.True(before.SupportsFavorite);
        Assert.True(before.SupportsDynamax);
        Assert.True(before.SupportsSociability);

        session.ApplyCosmeticEdit(0, 0, new CosmeticEdit(
            HeightScalar: 12,
            WeightScalar: 240,
            IsFavorite: true,
            DynamaxLevel: 10,
            CanGigantamax: true,
            Sociability: 123456));

        var after = session.GetCosmetics(0, 0);
        Assert.Equal(12, after.HeightScalar);
        Assert.Equal(240, after.WeightScalar);
        Assert.True(after.IsFavorite);
        Assert.Equal(10, after.DynamaxLevel);
        Assert.True(after.CanGigantamax);
        Assert.Equal(123456u, after.Sociability);
    }

    private static SaveEngineSession Seed(int generation, PKM mon)
    {
        var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(generation);
        mon.Species = 25;
        mon.CurrentLevel = 20;
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        Assert.True(session.ImportSlot(0, 0, bytes));
        return session;
    }
}
