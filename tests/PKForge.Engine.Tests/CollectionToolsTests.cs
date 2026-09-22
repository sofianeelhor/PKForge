using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Collection tooling behind the storage screen: the whole-save legality sweep,
/// one-mutation batch legalization, and PKHeX-driven ribbon awarding.
/// </summary>
public sealed class CollectionToolsTests
{
    private static string CorpusPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", file);
    }

    [Fact]
    public void SweepFlagsExactlyTheIllegalSlots()
    {
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));
        var occupied = session.Snapshot.Slots.Count(s => s.Species is not null);
        Assert.True(occupied > 0);

        // An impossible ball makes the party lead illegal without touching identity.
        session.ApplyEdit(-1, 0, new EntityEdit(Ball: 999));

        var verdicts = new LegalityService().Sweep(session);

        Assert.Equal(occupied, verdicts.Count);
        var flagged = verdicts.Where(v => !v.Valid).ToList();
        var party = Assert.Single(flagged, v => v.Box == -1 && v.Slot == 0);
        Assert.NotEmpty(party.Problem);
        Assert.Contains(party.Report, line => line.Length > 0);
    }

    [Fact]
    public void LegalizeSlotsRepairsEveryFlaggedSlotInOneCall()
    {
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));
        session.ApplyEdit(-1, 0, new EntityEdit(Ball: 999));
        var boxTarget = session.Snapshot.Slots.First(s => s.Species is not null && s.Box >= 0);
        session.ApplyEdit(boxTarget.Box, boxTarget.Slot, new EntityEdit(Ball: 999));

        var legality = new LegalityService();
        var flagged = legality.Sweep(session).Where(v => !v.Valid)
            .Select(v => (v.Box, v.Slot)).ToList();
        Assert.Contains((-1, 0), flagged);
        Assert.Contains((boxTarget.Box, boxTarget.Slot), flagged);

        // The batch contract is "the given slots": repair just the two we corrupted.
        var targets = new List<(int Box, int Slot)> { (-1, 0), (boxTarget.Box, boxTarget.Slot) };
        var outcome = new LegalizerService().LegalizeSlots(session, targets);

        Assert.True(outcome.Success, outcome.Message);
        foreach (var (box, slot) in targets)
        {
            Assert.True(legality.Analyze(session, box, slot).Valid);
            Assert.NotEqual(999 & 0xFF, session.ReadEntity(box, slot).Ball);
        }
    }

    [Fact]
    public void LegalizeSlotsOnNothingReportsNothingToDo()
    {
        using var session = new SaveEngine().OpenBlankSession(7);

        var outcome = new LegalizerService().LegalizeSlots(session, []);

        Assert.False(outcome.Success);
        Assert.Equal("Nothing to legalize.", outcome.Message);
    }

    [Fact]
    public void AwardAllObtainableRibbonsAwardsExactlyTheObtainableSetAndIsIdempotent()
    {
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));
        var target = session.Snapshot.Slots.First(s => s.Species is not null);
        var before = RibbonAlbum.Build(session.GetRibbons(target.Box, target.Slot),
            session.GetObtainableRibbonMaxima(target.Box, target.Slot));
        var obtainable = before.Count(e => e.Obtainable);
        Assert.True(obtainable > 0, "a modern mon should be able to legally earn something");

        var changed = session.AwardAllObtainableRibbons(target.Box, target.Slot);

        Assert.Equal(obtainable, changed);
        var after = RibbonAlbum.Build(session.GetRibbons(target.Box, target.Slot),
            session.GetObtainableRibbonMaxima(target.Box, target.Slot));
        Assert.DoesNotContain(after, e => e.Obtainable);
        Assert.Equal(0, session.AwardAllObtainableRibbons(target.Box, target.Slot));
    }

    [Fact]
    public void AwardedRibbonsStayWithinTheLegalMaxima()
    {
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));
        var target = session.Snapshot.Slots.First(s => s.Species is not null);
        var maxima = session.GetObtainableRibbonMaxima(target.Box, target.Slot);

        session.AwardAllObtainableRibbons(target.Box, target.Slot);

        foreach (var ribbon in session.GetRibbons(target.Box, target.Slot))
        {
            var legal = maxima.TryGetValue(ribbon.Id, out var max) ? max : 0;
            Assert.True(ribbon.Value <= legal,
                $"ribbon {ribbon.Id} holds {ribbon.Value} but only {legal} is legal");
        }
    }
}
