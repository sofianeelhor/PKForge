using PKHeX.Core;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Gen 5 box mutations must survive the real write path: serialize to bytes, then
/// reopen those bytes the way the app does after reconnecting a game. A pass only in
/// the still-open session hides corruption that appears as invalid species after reload.
/// </summary>
public sealed class Gen5RoundTripTests
{
    [Fact]
    public void SeededBoxesSurviveWriteAndReload()
    {
        using var session = Open(GameVersion.B, seedMonCount: 45);
        var before = SpeciesMultiset(session);
        Assert.Equal(45, before.Count);

        using var reloaded = Reload(session);
        Assert.Equal(before, SpeciesMultiset(reloaded));
    }

    [Theory]
    [InlineData(GameVersion.B)]
    [InlineData(GameVersion.W)]
    [InlineData(GameVersion.B2)]
    [InlineData(GameVersion.W2)]
    public void EmptyAllBoxesSurvivesWriteAndReload(GameVersion version)
    {
        using var session = Open(version, seedMonCount: 45);
        Assert.Equal(45, SpeciesMultiset(session).Count);
        for (var box = 0; box < BoxCount(session); box++)
            session.ClearBox(box);

        using var reloaded = Reload(session);
        Assert.Empty(SpeciesMultiset(reloaded));
    }

    [Theory]
    [InlineData(GameVersion.B)]
    [InlineData(GameVersion.W2)]
    public void DeleteEveryBoxRescuesEverythingAfterReload(GameVersion version)
    {
        using var session = Open(version, seedMonCount: 60);
        var before = SpeciesMultiset(session);
        for (var box = BoxCount(session) - 1; box >= 0; box--)
            session.DeleteBox(box);

        using var reloaded = Reload(session);
        Assert.Equal(before, SpeciesMultiset(reloaded));
    }

    [Theory]
    [InlineData(GameVersion.B)]
    [InlineData(GameVersion.W2)]
    public void AdjacentSwapsSurviveWriteAndReload(GameVersion version)
    {
        using var session = Open(version, seedMonCount: 60);
        var before = SpeciesMultiset(session);
        for (var box = 0; box + 1 < BoxCount(session); box++)
            session.SwapBoxes(box, box + 1);

        using var reloaded = Reload(session);
        Assert.Equal(before, SpeciesMultiset(reloaded));
    }

    [Fact]
    public void SerializeIsStableAcrossRepeatedWrites()
    {
        using var session = Open(GameVersion.B, seedMonCount: 30);
        var first = session.Serialize().ToArray();
        var second = session.Serialize().ToArray();
        Assert.Equal(first, second);
    }

    private static SaveEngineSession Open(GameVersion version, int seedMonCount)
    {
        var save = BlankSaveFile.Get(version, "PKForge", LanguageID.English);
        var session = new SaveEngineSession(save, null);
        var seeded = 0;
        for (var box = 0; box < save.BoxCount && seeded < seedMonCount; box++)
        for (var slot = 0; slot < save.BoxSlotCount && seeded < seedMonCount; slot++)
        {
            var mon = new PK5 { Species = (ushort)(25 + seeded % 100), CurrentLevel = (byte)(5 + seeded % 90) };
            mon.RefreshChecksum();
            var bytes = new byte[mon.SIZE_STORED];
            mon.WriteDecryptedDataStored(bytes);
            Assert.True(session.ImportSlot(box, slot, bytes));
            seeded++;
        }
        return session;
    }

    private static SaveEngineSession Reload(SaveEngineSession session)
    {
        var bytes = session.Serialize().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var save), "serialized bytes must reopen as a save");
        return new SaveEngineSession(save!, null);
    }

    private static int BoxCount(SaveEngineSession session) => session.Snapshot.Slots.Max(s => s.Box) + 1;

    private static List<(int Species, int Form)> SpeciesMultiset(SaveEngineSession session)
    {
        var result = new List<(int, int)>();
        for (var box = 0; box < BoxCount(session); box++)
        for (var slot = 0; slot < 30; slot++)
        {
            var detail = session.ReadEntity(box, slot);
            if (!detail.IsEmpty) result.Add((detail.Species, detail.Form));
        }
        return result.OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();
    }
}
