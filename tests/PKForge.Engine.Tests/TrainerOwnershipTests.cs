using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Ownership quality-of-life: generated mons use the save identity, and
/// Make Mine rewrites ownership without silently making the result illegal.</summary>
public sealed class TrainerOwnershipTests
{
    private sealed class OwnershipSettings(bool enabled) : IGenerationOwnershipSettings
    {
        public bool UseCurrentTrainerForGeneration { get; } = enabled;
    }

    private static SaveEngineSession BlankGen5()
    {
        var engine = new SaveEngine();
        var session = (SaveEngineSession)engine.OpenBlankSession(5);
        session.SetTrainer(new TrainerInfo("Alice", 111, 222, 12345, 1));
        return session;
    }

    [Fact]
    public void GenerationUsesTheOpenSaveTrainerIdentity()
    {
        using var session = BlankGen5();
        var legalizer = new LegalizerService(new OwnershipSettings(enabled: true));

        var outcome = legalizer.Generate(session, 0, 0, new GenerationRequest(25, 20, Shiny: false,
            Nature: null, Ability: null, Ball: null, Moves: null));

        Assert.True(outcome.Success, outcome.Message);
        var mon = session.GetEntity(0, 0);
        Assert.Equal("Alice", mon.OriginalTrainerName);
        Assert.Equal(111, mon.TID16);
        Assert.Equal(222, mon.SID16);
        Assert.Equal(1, mon.OriginalTrainerGender);
        Assert.True(TrainerInfoExtensions.IsFromTrainerNoVersion(session.SaveFile, mon),
            "Generated OT identity did not match the save");
    }

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
    public void MakeMineMatchesTheSaveIdentityAcrossGenerations(int generation)
    {
        var engine = new SaveEngine();
        using var raw = engine.OpenBlankSession(generation);
        var session = (SaveEngineSession)raw;
        // The Gen 1 blank is Japanese Blue: its OT charset rejects Latin letters.
        var initialName = generation == 1 ? "タケル" : $"Gen{generation}";
        var ownerName = generation == 1 ? "サトシ" : "Owner";
        session.SetTrainer(new TrainerInfo(initialName, 100 + generation, 200 + generation, 0, generation % 2));
        Assert.Equal(initialName, session.GetTrainer().Name);
        var legalizer = new LegalizerService(new OwnershipSettings(enabled: true));
        Assert.True(legalizer.Generate(session, 0, 0, new GenerationRequest(25, 20, Shiny: false,
            Nature: null, Ability: null, Ball: null, Moves: null)).Success);
        var generated = session.GetEntity(0, 0);
        Assert.True(TrainerInfoExtensions.IsFromTrainerNoVersion(session.SaveFile, generated),
            $"gen {generation}: generated mon used OT {generated.OriginalTrainerName}/{generated.ID32}, save {session.SaveFile.OT}/{session.SaveFile.ID32}");

        session.SetTrainer(new TrainerInfo(ownerName, 333, 444, 0, 0));
        Assert.Equal(ownerName, session.GetTrainer().Name);
        var outcome = session.MakeMine(0, 0);
        Assert.True(outcome.Success, $"gen {generation}: {outcome.Message}");
        var mon = session.GetEntity(0, 0);
        Assert.Equal(ownerName, mon.OriginalTrainerName);
        Assert.Equal(333, mon.TID16);
        if (mon.Format >= 3 && !mon.VC)
            Assert.Equal(444, mon.SID16);
        Assert.True(TrainerInfoExtensions.IsFromTrainerNoVersion(session.SaveFile, mon),
            $"gen {generation}: OT identity did not match the save");
    }

    [Fact]
    public void MakeMinePreservesShinyStateWhenIdsChange()
    {
        using var session = BlankGen5();
        var legalizer = new LegalizerService(new OwnershipSettings(enabled: true));
        Assert.True(legalizer.Generate(session, 0, 0, new GenerationRequest(25, 20, Shiny: true,
            Nature: null, Ability: null, Ball: null, Moves: null)).Success);
        var before = session.GetEntity(0, 0);
        Assert.True(before.IsShiny);

        session.SetTrainer(new TrainerInfo("Bob", 333, 444, 12345, 0));
        var outcome = session.MakeMine(0, 0);

        Assert.True(outcome.Success, outcome.Message);
        var after = session.GetEntity(0, 0);
        Assert.True(after.IsShiny);
        Assert.True(new LegalityAnalysis(after).Valid);
    }

    [Fact]
    public void MakeMineUsesTheCurrentSaveTrainerAndStaysLegal()
    {
        using var session = BlankGen5();
        var legalizer = new LegalizerService(new OwnershipSettings(enabled: true));
        Assert.True(legalizer.Generate(session, 0, 0, new GenerationRequest(25, 20, Shiny: false,
            Nature: null, Ability: null, Ball: null, Moves: null)).Success);

        session.SetTrainer(new TrainerInfo("Bob", 333, 444, 12345, 0));
        var outcome = session.MakeMine(0, 0);

        Assert.True(outcome.Success, outcome.Message);
        var mon = session.GetEntity(0, 0);
        Assert.Equal("Bob", mon.OriginalTrainerName);
        Assert.Equal(333, mon.TID16);
        Assert.Equal(444, mon.SID16);
        Assert.Equal(0, mon.OriginalTrainerGender);
        Assert.True(session.SaveFile.IsFromTrainer(mon));
        Assert.True(new LegalityAnalysis(mon).Valid);
    }

    [Fact]
    public void TrainerProfileCanBeAppliedToAMon()
    {
        using var session = BlankGen5();
        var legalizer = new LegalizerService(new OwnershipSettings(enabled: true));
        Assert.True(legalizer.Generate(session, 0, 0, new GenerationRequest(25, 20, Shiny: false,
            Nature: null, Ability: null, Ball: null, Moves: null)).Success);
        var profile = new TrainerProfile("profile-1", "Champion", "Carol", 555, 666, 0);

        var outcome = session.MakeMine(0, 0, profile);

        Assert.True(outcome.Success, outcome.Message);
        var mon = session.GetEntity(0, 0);
        Assert.Equal("Carol", mon.OriginalTrainerName);
        Assert.Equal(555, mon.TID16);
        Assert.Equal(666, mon.SID16);
        Assert.Equal(0, mon.OriginalTrainerGender);
        Assert.True(new LegalityAnalysis(mon).Valid);
    }

    [Fact]
    public void FixedOtEventGiftIsRefusedInsteadOfBeingCorrupted()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(5);
        var service = new EventDatabaseService();
        var gifts = service.GetGifts(session);
        Assert.NotEmpty(gifts);
        var giftId = gifts[0].Id;

        Assert.True(service.Receive(session, giftId, 0, 0).Success);

        var outcome = session.MakeMine(0, 0);

        Assert.False(outcome.Success);
        Assert.Contains("fixed", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }
}
