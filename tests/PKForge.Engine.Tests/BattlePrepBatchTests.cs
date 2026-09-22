using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Battle prep and the batch editor's instruction dialect: heal/PP ops follow PKHeX's
/// BatchMods semantics, the party (box -1) is a batch target, and explicit slot lists
/// touch exactly the requested mons.
/// </summary>
public sealed class BattlePrepBatchTests
{
    private static string CorpusPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", file);
    }

    private static SaveEngineSession OpenCorpus()
        => new(File.ReadAllBytes(CorpusPath("SM Project 802.main")));

    /// <summary>A blank session with one crafted Pikachu (Thunderbolt) in box 0, slot 0.</summary>
    private static SaveEngineSession Seed()
    {
        var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(7);
        var mon = new PK7 { Version = GameVersion.UM, Language = 2, Move1 = 85, CurrentLevel = 20 };
        mon.Species = 25;
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        Assert.True(session.ImportSlot(0, 0, bytes));
        return session;
    }

    [Fact]
    public void HealedMovesRefillToTheirMaximum()
    {
        using var session = Seed();
        session.ApplyMoveDetails(0, 0, new MoveDetailsEdit(PP: [3, 0, 0, 0], PPUps: [0, 0, 0, 0]));
        var drained = session.GetMoveDetails(0, 0).Moves[0];
        Assert.Equal(3, drained.PP);
        Assert.True(drained.PP < drained.MaxPP);

        var touched = session.BatchApply(["Heal"], [0]);

        Assert.Equal(1, touched);
        var healed = session.GetMoveDetails(0, 0).Moves[0];
        Assert.Equal(healed.MaxPP, healed.PP);
    }

    [Fact]
    public void PPUpsInstructionRaisesTheMaximumAndHealPPTopsItOff()
    {
        using var session = Seed();
        var before = session.GetMoveDetails(0, 0).Moves[0];
        Assert.Equal(0, before.PPUps); // Thunderbolt: 15 base, 24 with three ups.

        session.BatchApply(["Move1_PPUps=3", "HealPP"], [0]);

        var after = session.GetMoveDetails(0, 0).Moves[0];
        Assert.Equal(3, after.PPUps);
        Assert.True(after.MaxPP > before.MaxPP);
        Assert.Equal(after.MaxPP, after.PP);
    }

    [Fact]
    public void PPUpsInstructionClampsAtThree()
    {
        using var session = Seed();
        session.BatchApply(["Move1_PPUps=99"], [0]);
        Assert.Equal(3, session.GetMoveDetails(0, 0).Moves[0].PPUps);
    }

    [Fact]
    public void NicknameResetRestoresTheSpeciesName()
    {
        using var session = Seed();
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "SPARKY"));
        Assert.Equal("SPARKY", session.ReadEntity(0, 0).Nickname);

        session.BatchApply(["IsNicknamed=false"], [0]);

        var detail = session.ReadEntity(0, 0);
        Assert.Equal(detail.SpeciesName, detail.Nickname, ignoreCase: true);
    }

    [Fact]
    public void HyperTrainSuggestClearsFlagsBelowTheLevelGate()
    {
        using var session = Seed();
        session.ApplyEdit(0, 0, new EntityEdit(Level: 60, IVs: [30, 30, 30, 30, 30, 30]));

        // Gen 7 hyper training needs Lv100: the suggestion must clear, not force.
        session.BatchApply(["HyperTrain=$suggest"], [0]);

        Assert.DoesNotContain(session.GetPotential(0, 0).HyperTrained, trained => trained);
    }

    [Fact]
    public void HyperTrainSuggestTrainsFlawedIvsButKeepsDeliberateZeroSpeed()
    {
        using var session = Seed();
        session.ApplyEdit(0, 0, new EntityEdit(Level: 100, IVs: [30, 30, 30, 30, 30, 0]));

        session.BatchApply(["HyperTrain=$suggest"], [0]);

        // PKHeX's suggestion never hyper trains a speed IV of 2 or less (Trick Room
        // and low-speed builds are deliberate), everything else flawed gets trained.
        Assert.Equal([true, true, true, true, true, false], session.GetPotential(0, 0).HyperTrained);
    }

    [Fact]
    public void ForcedHyperTrainIgnoresTheLevelGate()
    {
        using var session = Seed();
        session.ApplyEdit(0, 0, new EntityEdit(Level: 60, IVs: [30, 30, 30, 30, 30, 30]));

        session.BatchApply(["HyperTrain"], [0]);

        Assert.All(session.GetPotential(0, 0).HyperTrained, Assert.True);
    }

    [Fact]
    public void BatchApplyReachesThePartyThroughBoxMinusOne()
    {
        using var session = OpenCorpus();
        Assert.False(session.ReadEntity(-1, 0).IsEmpty);

        var touched = session.BatchApply(["Friendship=200"], [-1]);

        Assert.True(touched > 0);
        Assert.Equal(200, session.ReadEntity(-1, 0).Friendship);
    }

    [Fact]
    public void BatchApplySlotsTouchesExactlyTheRequestedSlots()
    {
        using var session = OpenCorpus();
        var occupied = Enumerable.Range(0, 30)
            .Where(slot => !session.ReadEntity(0, slot).IsEmpty)
            .Take(3)
            .ToList();
        var untouched = occupied[2];
        // An empty slot and an out-of-range slot must be skipped, not thrown on.
        var empty = Enumerable.Range(0, 32 * 30)
            .Select(i => (Box: i / 30, Slot: i % 30))
            .First(s => session.ReadEntity(s.Box, s.Slot).IsEmpty);
        var touched = session.BatchApplySlots([(0, occupied[0]), (0, occupied[1]), (empty.Box, empty.Slot), (0, 99)],
            ["Nickname=BATCHED"]);
        Assert.Equal(2, touched);
        Assert.Equal("BATCHED", session.ReadEntity(0, occupied[0]).Nickname);
        Assert.Equal("BATCHED", session.ReadEntity(0, occupied[1]).Nickname);
        Assert.NotEqual("BATCHED", session.ReadEntity(0, untouched).Nickname);
    }
}
