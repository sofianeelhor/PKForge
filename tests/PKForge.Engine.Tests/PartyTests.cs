using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The party is a first-class storage target (box -1): read, edit, move box-to-party and
/// party-to-box, release with compaction, and import with the 6-cap, exactly like the games.
/// </summary>
public sealed class PartyTests
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
    public void PartyRoundTripsThroughEveryOperation()
    {
        var engine = new SaveEngine();
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));

        var partySlots = session.Snapshot.Slots.Where(s => s.Box == -1).ToList();
        Assert.NotEmpty(partySlots);
        Assert.All(partySlots, s => Assert.NotNull(s.Species));

        var first = session.ReadEntity(-1, 0);
        Assert.False(first.IsEmpty);
        Assert.True(first.CurrentHp >= 0 && first.CurrentHp <= (first.Stats?[0] ?? 0));

        // Edit a party mon (live, like boxes).
        session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "PARTYMON"));
        Assert.Equal("PARTYMON", session.ReadEntity(-1, 0).Nickname);

        // The met and potential editors must work on party slots too (they used to hit
        // the box accessors directly and corrupt or throw).
        session.ApplyMetEdit(-1, 0, new MetEdit(MetLevel: 7));
        Assert.Equal(7, session.GetMetInfo(-1, 0).MetLevel);
        var potential = session.GetPotential(-1, 0);
        if (potential.SupportsHyperTrain)
        {
            session.ApplyPotentialEdit(-1, 0, new PotentialEdit(HyperTrained: [true, false, false, false, false, false]));
            Assert.True(session.GetPotential(-1, 0).HyperTrained![0]);
        }
        Assert.NotEmpty(session.GetShowdownText(-1, 0));

        // Snapshot is a construction-time cache; party state is read live after mutations.
        int LivePartyCount() => Enumerable.Range(0, 6).Count(i => !session.ReadEntity(-1, i).IsEmpty);

        // Box mon moves into the party (append semantics).
        var countBefore = LivePartyCount();
        var boxMon = session.Snapshot.Slots.First(s => s.Box >= 0 && s.Species is not null);
        session.MoveSlot(boxMon.Box, boxMon.Slot, -1, 0);
        Assert.Equal(countBefore + 1, LivePartyCount());
        Assert.True(session.ReadEntity(boxMon.Box, boxMon.Slot).IsEmpty);

        // Party mon moves back out to a box (party compacts).
        var partyCountNow = LivePartyCount();
        var emptyBox = session.Snapshot.Slots.First(s => s.Box >= 0 && s.Species is null);
        session.MoveSlot(-1, 0, emptyBox.Box, emptyBox.Slot);
        Assert.Equal(partyCountNow - 1, LivePartyCount());
        Assert.False(session.ReadEntity(emptyBox.Box, emptyBox.Slot).IsEmpty);

        // Release from party compacts instead of leaving a hole.
        var beforeRelease = LivePartyCount();
        session.ReleaseSlot(-1, 0);
        Assert.Equal(beforeRelease - 1, LivePartyCount());

        // Import into party respects the 6-cap.
        var exported = session.ExportSlot(emptyBox.Box, emptyBox.Slot);
        while (LivePartyCount() < 6)
            Assert.True(session.ImportSlot(-1, 0, exported.Data));
        Assert.False(session.ImportSlot(-1, 0, exported.Data));

        // Reorder inside a FULL party: a real swap, nothing lost, count unchanged.
        var firstBefore = session.ReadEntity(-1, 0).Nickname;
        var thirdBefore = session.ReadEntity(-1, 2).Nickname;
        session.MoveSlot(-1, 0, -1, 2);
        Assert.Equal(thirdBefore, session.ReadEntity(-1, 0).Nickname);
        Assert.Equal(firstBefore, session.ReadEntity(-1, 2).Nickname);
        Assert.Equal(6, LivePartyCount());

        // Move to an empty slot beyond the party count: inserts there, no throw, no loss.
        var countNow = LivePartyCount();
        var movingNick = session.ReadEntity(-1, 0).Nickname;
        session.MoveSlot(-1, 0, -1, Math.Min(5, countNow)); // first empty slot at the tail
        Assert.Equal(countNow, LivePartyCount());
        Assert.Equal(movingNick, session.ReadEntity(-1, Math.Min(5, countNow)).Nickname);

        // Adjacent columns (slot 0 <-> slot 1), then serialize/reload: the exact user flow.
        var a0 = session.ReadEntity(-1, 0).Nickname;
        var a1 = session.ReadEntity(-1, 1).Nickname;
        session.MoveSlot(-1, 0, -1, 1);
        Assert.Equal(a1, session.ReadEntity(-1, 0).Nickname);
        Assert.Equal(a0, session.ReadEntity(-1, 1).Nickname);
        var swappedBytes = session.Serialize().ToArray();
        using (var r2 = new SaveEngineSession(swappedBytes))
        {
            Assert.Equal(a1, r2.ReadEntity(-1, 0).Nickname);
            Assert.Equal(a0, r2.ReadEntity(-1, 1).Nickname);
        }

        // The full pipeline the app drives: edit, serialize, write, reopen from disk bytes.
        session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "PERSISTED"));
        var written = session.Serialize().ToArray();
        using (var reloaded = new SaveEngineSession(written))
        {
            Assert.Equal("PERSISTED", reloaded.ReadEntity(-1, 0).Nickname);
            Assert.Equal(6, Enumerable.Range(0, 6).Count(i => !reloaded.ReadEntity(-1, i).IsEmpty));
        }
    }
}
