using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>Clone grouping, the legality sweep's write-generation cache key, and the
/// legal-ribbon album builder: the collection tools' pure decision logic.</summary>
public sealed class CollectionAuditTests
{
    private static MonFingerprint Mon(uint pid, uint? ec = null, string ot = "Red", int tid = 12345, string label = "B1-1") =>
        new(pid, ec, ot, tid, label, "Bulbasaur");

    // ── Clone grouping key ──

    [Fact]
    public void SameEncryptionConstantAndPidGroupAsClones()
    {
        var clones = CollectionAudit.GroupClones(
        [
            Mon(0x11223344, ec: 0xAABBCCDD, label: "B1-1"),
            Mon(0x11223344, ec: 0xAABBCCDD, label: "B2-7"),
            Mon(0x55667788, ec: 0x01020304, label: "B3-1"),
        ]);

        var group = Assert.Single(clones);
        Assert.Equal("EC + PID", group.KeyKind);
        Assert.Equal(2, group.Members.Count);
        Assert.Contains(group.Members, m => m.SlotLabel == "B1-1");
        Assert.Contains(group.Members, m => m.SlotLabel == "B2-7");
    }

    [Fact]
    public void SamePidWithDifferentEncryptionConstantsIsNotAClone()
    {
        var clones = CollectionAudit.GroupClones(
        [
            Mon(0x11223344, ec: 0xAABBCCDD),
            Mon(0x11223344, ec: 0x01020304),
        ]);

        Assert.Empty(clones);
    }

    [Fact]
    public void FormatsWithoutEcFallBackToPidAndTrainerIdentity()
    {
        var clones = CollectionAudit.GroupClones(
        [
            Mon(0x89ABCDEF, ec: null, ot: "Green", tid: 54321, label: "B1-2"),
            Mon(0x89ABCDEF, ec: null, ot: "Green", tid: 54321, label: "Party 3"),
            Mon(0x89ABCDEF, ec: null, ot: "Green", tid: 99999, label: "B4-1"),
            Mon(0x89ABCDEF, ec: null, ot: "Blue", tid: 54321, label: "B4-2"),
        ]);

        var group = Assert.Single(clones);
        Assert.Equal("PID + OT + TID", group.KeyKind);
        Assert.Equal(2, group.Members.Count);
    }

    [Fact]
    public void DistinctMonsProduceNoGroups()
    {
        Assert.Empty(CollectionAudit.GroupClones(
        [
            Mon(1, ec: 10), Mon(2, ec: 20), Mon(3, ec: 30),
        ]));
    }

    // ── Legality sweep cache invalidation key ──

    private static IReadOnlyList<SlotLegality> Verdicts(params (int Box, int Slot, bool Valid)[] slots) =>
        slots.Select(s => new SlotLegality(s.Box, s.Slot, s.Valid, s.Valid ? "" : "Move 1 invalid.", [])).ToList();

    [Fact]
    public void CacheStaysFreshOnlyForTheSameDocumentAndWriteGeneration()
    {
        var cache = new LegalitySweepCache();
        cache.Store("doc-a", 3, Verdicts((0, 0, true)));

        Assert.True(cache.IsFresh("doc-a", 3));
        Assert.False(cache.IsFresh("doc-a", 4), "a write happened: the sweep must rescan");
        Assert.False(cache.IsFresh("doc-b", 3), "a different save opened: the sweep must rescan");
    }

    [Fact]
    public void VerdictLookupWorksOnlyWhileFresh()
    {
        var cache = new LegalitySweepCache();
        cache.Store("doc", 7, Verdicts((-1, 0, true), (2, 5, false)));

        Assert.True(cache.TryGetVerdict("doc", 7, 2, 5, out var illegal));
        Assert.NotNull(illegal);
        Assert.False(illegal!.Valid);
        Assert.Equal("Move 1 invalid.", illegal.Problem);

        Assert.False(cache.TryGetVerdict("doc", 8, 2, 5, out _), "stale generation must not answer");
        Assert.False(cache.TryGetVerdict("doc", 7, 9, 9, out _), "unknown slot must not answer");
    }

    [Fact]
    public void StoringAgainReplacesThePreviousSweep()
    {
        var cache = new LegalitySweepCache();
        cache.Store("doc", 1, Verdicts((0, 0, true)));
        cache.Store("doc", 2, Verdicts((0, 0, false)));

        Assert.False(cache.IsFresh("doc", 1));
        Assert.True(cache.TryGetVerdict("doc", 2, 0, 0, out var verdict));
        Assert.False(verdict!.Valid);
    }

    // ── Legal-ribbon album builder ──

    [Fact]
    public void RibbonIsObtainableOnlyWhenItsLegalMaximumExceedsTheCurrentValue()
    {
        var ribbons = new List<RibbonEntry>
        {
            new("RibbonChampionKalos", "Champion Kalos", Value: 0, MaxValue: 1, IsMark: false),
            new("RibbonMaster", "Master", Value: 1, MaxValue: 1, IsMark: false),
            new("RibbonCountMemoryContest", "Contest Memory", Value: 2, MaxValue: 40, IsMark: false),
            new("RibbonEarth", "Earth", Value: 0, MaxValue: 1, IsMark: false),
        };
        var legalMaxima = new Dictionary<string, int>
        {
            ["RibbonChampionKalos"] = 1,
            ["RibbonMaster"] = 1,
            ["RibbonCountMemoryContest"] = 4,
            ["RibbonEarth"] = 0, // this species can never earn it
        };

        var album = RibbonAlbum.Build(ribbons, legalMaxima);

        Assert.Equal(4, album.Count);
        Assert.True(album[0].Obtainable, "unowned but legal: awardable");
        Assert.False(album[1].Obtainable, "already owned");
        Assert.True(album[2].Obtainable, "counted ribbon below its legal maximum");
        Assert.Equal(4 - 2, album.Count(e => e.Obtainable));
        var earth = album.Single(e => e.Id == "RibbonEarth");
        Assert.False(earth.Obtainable, "legal maximum 0: never awardable for this Pokémon");
    }

    [Fact]
    public void RibbonsMissingFromTheLegalMapAreNeverObtainable()
    {
        var ribbons = new List<RibbonEntry> { new("RibbonMarkLunchtime", "Lunchtime", 0, 1, true) };

        var album = RibbonAlbum.Build(ribbons, new Dictionary<string, int>());

        var mark = Assert.Single(album);
        Assert.True(mark.IsMark);
        Assert.False(mark.Obtainable);
    }
}
