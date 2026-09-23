using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Sinnoh honey trees: the Munchlax-tree calculation must match PKHeX's
/// HoneyTreeUtil, and every tree write must survive a full serialize and reopen.</summary>
public sealed class HoneyTreeTests
{
    [Fact]
    public void MunchlaxTreesMatchPkhexDocumentedCollision()
    {
        // HoneyTreeUtil.AdjustOverlap's own example: 1935328924 => {10,6,9,10}.
        Assert.Equal([10, 6, 9], HoneyTreeService.GetMunchlaxTrees(1935328924u));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(12345u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x1234ABCDu)]
    public void MunchlaxTreesMatchHoneyTreeUtil(uint id32)
    {
        Span<byte> expected = stackalloc byte[4];
        HoneyTreeUtil.CalculateMunchlaxTrees(id32, expected);
        var trees = HoneyTreeService.GetMunchlaxTrees(id32);
        Assert.Equal(expected.ToArray().Distinct().Select(b => (int)b), trees);
        Assert.All(trees, t => Assert.InRange(t, 0, HoneyTreeService.TreeCount - 1));
    }

    [Theory]
    [InlineData(GameVersion.D)]
    [InlineData(GameVersion.Pt)]
    public void SessionUsesTrainerId32AndFlagsTrees(GameVersion version)
    {
        using var session = OpenBlank(version, tid: 54321, sid: 12345);
        var id32 = (12345u << 16) | 54321u;
        var expected = HoneyTreeService.GetMunchlaxTrees(id32);
        Assert.Equal(expected, HoneyTreeService.GetMunchlaxTrees(session));

        var trees = HoneyTreeService.GetTrees(session);
        Assert.Equal(21, trees.Count);
        Assert.Equal("Route 205, Floaroma Town side", trees[0].Location);
        Assert.Equal("Floaroma Meadow", trees[20].Location);
        Assert.Equal(expected.Order(), trees.Where(t => t.IsMunchlaxTree).Select(t => t.Index));
    }

    [Theory]
    [InlineData(GameVersion.D)]
    [InlineData(GameVersion.Pt)]
    public void MunchlaxReadySurvivesSerializeAndReopen(GameVersion version)
    {
        using var session = OpenBlank(version, tid: 1, sid: 2);
        var tree = HoneyTreeService.GetMunchlaxTrees(session)[0];

        var written = HoneyTreeService.SetMunchlaxReady(session, tree);
        Assert.Equal((ushort)Species.Munchlax, written.Species);
        Assert.True(written.IsReady);

        using var reloaded = Reopen(version, session);
        var after = HoneyTreeService.GetTrees(reloaded)[tree];
        Assert.Equal(1080u, after.Time);
        Assert.Equal(HoneyTreeService.GroupMunchlax, after.Group);
        Assert.Equal(3, after.Shakes);
        Assert.Equal((ushort)Species.Munchlax, after.Species);

        // HoneyTreeValue.Group's setter also writes SubTable = Group - 1.
        var raw = ((SAV4Sinnoh)reloaded.SaveFile).GetHoneyTree(tree);
        Assert.Equal(2, raw.SubTable);
    }

    [Fact]
    public void MunchlaxRefusedOnOrdinaryTree()
    {
        using var session = OpenBlank(GameVersion.Pt, tid: 1, sid: 2);
        var ordinary = Enumerable.Range(0, 21).First(i => !HoneyTreeService.GetMunchlaxTrees(session).Contains(i));
        Assert.Throws<InvalidOperationException>(() => HoneyTreeService.SetMunchlaxReady(session, ordinary));
    }

    [Fact]
    public void SlatherAndSetTreeRoundTripWithPkhexClamps()
    {
        using var session = OpenBlank(GameVersion.Pt, tid: 7, sid: 8);
        HoneyTreeService.SetTree(session, 4, time: 9999, group: 9, slot: 9, shakes: 9);
        var clamped = HoneyTreeService.GetTrees(session)[4];
        Assert.Equal((1440u, 3, 5, 3), (clamped.Time, clamped.Group, clamped.Slot, clamped.Shakes));

        HoneyTreeService.SetTree(session, 5, time: 0, group: 2, slot: 4, shakes: 1);
        HoneyTreeService.Slather(session, 5);

        using var reloaded = Reopen(GameVersion.Pt, session);
        var tree = HoneyTreeService.GetTrees(reloaded)[5];
        Assert.Equal((1080u, 2, 4, 1), (tree.Time, tree.Group, tree.Slot, tree.Shakes));
        Assert.Equal(((SAV4Sinnoh)reloaded.SaveFile).GetHoneyTreeSpecies(2, 4), tree.Species);
    }

    [Fact]
    public void NonSinnohSavesAreUnsupported()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        Assert.False(HoneyTreeService.IsSupported(session));
        Assert.Throws<NotSupportedException>(() => HoneyTreeService.GetTrees(session));
    }

    /// <summary>A full-size, zero-filled 512 KB Sinnoh image. PKHeX's in-memory blank
    /// (BlankSaveFile) has no backing file image, so SAV4.SetChecksums cannot write it;
    /// a real-size image goes through the same Write path a cartridge dump does.</summary>
    private static SaveEngineSession OpenBlank(GameVersion version, ushort tid, ushort sid)
    {
        var save = Parse(version, new byte[0x80000]);
        save.TID16 = tid;
        save.SID16 = sid;
        return new SaveEngineSession(save, null);
    }

    private static SaveEngineSession Reopen(GameVersion version, SaveEngineSession session) =>
        new(Parse(version, session.Serialize().ToArray()), null);

    private static SAV4Sinnoh Parse(GameVersion version, byte[] data) =>
        version == GameVersion.Pt ? new SAV4Pt(data) : new SAV4DP(data) { Version = version };
}
