using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Manual evolution: trade evolutions are offered and performed like a real link trade, the
/// result stays legal for PKHeX, and non-trade methods are gated on their condition (HaX aside).
/// </summary>
public sealed class EvolutionServiceTests
{
    private const ushort Machoke = 67, Machamp = 68, Kadabra = 64, Alakazam = 65, Onix = 95, Steelix = 208;
    private const ushort Karrablast = 588, Escavalier = 589, Shelmet = 616, Accelgor = 617, Pikachu = 25, Raichu = 26;
    private const int MetalCoat = 233;

    private static readonly EvolutionService Service = new();

    private sealed class OwnershipSettings(bool enabled) : IGenerationOwnershipSettings
    {
        public bool UseCurrentTrainerForGeneration { get; } = enabled;
    }

    private static SaveEngineSession Legal(int generation, ushort species, int level, string trainer = "Ash")
    {
        var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(generation);
        if (generation > 1) session.SetTrainer(generation <= 2 ? new TrainerInfo(trainer.ToUpperInvariant(), 12345, 0, 1000, 0) : new TrainerInfo(trainer, 12345, 54321, 1000, 0));
        var outcome = new LegalizerService(new OwnershipSettings(enabled: true)).Generate(session, 0, 0,
            new GenerationRequest(species, level, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null));
        Assert.True(outcome.Success, $"gen {generation} {(Species)species}: {outcome.Message}");
        var pk = session.GetEntity(0, 0);
        Assert.Equal(species, pk.Species);
        Assert.True(new LegalityAnalysis(pk).Valid, $"seed not legal: {new LegalityAnalysis(pk).Report()}");
        return session;
    }

    private static EvolutionOption Only(EvolutionPlan plan, ushort species) =>
        Assert.Single(plan.Options, o => o.Species == species);

    private static void AssertLegal(PKM pk)
    {
        var la = new LegalityAnalysis(pk);
        Assert.True(la.Valid, la.Report());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void MachokeTradeEvolvesIntoALegalMachamp(int generation)
    {
        using var session = Legal(generation, Machoke, 40);
        var plan = Service.Plan(session, 0, 0, hax: false);
        var option = Only(plan, Machamp);
        Assert.Equal(EvolutionTrigger.Trade, option.Trigger);
        Assert.Equal("Trade", option.Requirement);
        Assert.True(option.ConditionMet);
        Assert.True(option.Available);
        var expBefore = session.GetEntity(0, 0).EXP;
        var wasUntraded = session.GetEntity(0, 0).IsUntraded;
        var handlerBefore = session.GetEntity(0, 0).CurrentHandler;

        var outcome = Service.Evolve(session, 0, 0, new EvolutionRequest(option.Id));

        Assert.True(outcome.Success, outcome.Message);
        var pk = session.GetEntity(0, 0);
        Assert.Equal(Machamp, pk.Species);
        Assert.Equal(expBefore, pk.EXP);
        Assert.Equal(40, pk.CurrentLevel);
        Assert.Equal(SpeciesName.GetSpeciesNameGeneration(Machamp, pk.Language, pk.Format), pk.Nickname); // in its own language
        Assert.True(session.GetDexEntry(Machamp).Caught);
        if (generation >= 6)
        {
            // A link trade leaves its trace: an untraded mon comes home with the partner recorded.
            Assert.False(pk.IsUntraded);
            Assert.Equal(wasUntraded ? 0 : handlerBefore, pk.CurrentHandler);
            if (wasUntraded) Assert.Equal(EvolutionService.DefaultTradePartner, pk.HandlingTrainerName);
        }
        AssertLegal(pk);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void OnixNeedsAndConsumesTheMetalCoat(int generation)
    {
        using var session = Legal(generation, Onix, 30);
        var option = Only(Service.Plan(session, 0, 0, hax: false), Steelix);
        Assert.Equal(EvolutionTrigger.TradeHoldingItem, option.Trigger);
        Assert.Equal("Trade holding Metal Coat", option.Requirement);
        Assert.False(option.Available);
        Assert.Contains("Metal Coat", option.BlockedReason);

        var pk = session.GetEntity(0, 0);
        pk.HeldItem = generation == 3 ? ItemConverter.GetItemOld3(MetalCoat) : MetalCoat; // Gen 3 stores its own item ids
        session.SaveFile.SetBoxSlotAtIndex(pk, 0, 0, EntityImportSettings.None);
        option = Only(Service.Plan(session, 0, 0, hax: false), Steelix);
        Assert.True(option.Available);
        Assert.Equal("Metal Coat", option.ConsumedItem);

        Assert.True(Service.Evolve(session, 0, 0, new EvolutionRequest(option.Id)).Success);
        var evolved = session.GetEntity(0, 0);
        Assert.Equal(Steelix, evolved.Species);
        Assert.Equal(0, evolved.HeldItem);
        AssertLegal(evolved);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void KarrablastAndShelmetEvolveForEachOther(int generation)
    {
        using var karra = Legal(generation, Karrablast, 30);
        var escavalier = Only(Service.Plan(karra, 0, 0, hax: false), Escavalier);
        Assert.Equal(EvolutionTrigger.TradeForPartner, escavalier.Trigger);
        Assert.Equal("Trade with Shelmet", escavalier.Requirement);
        Assert.True(Service.Evolve(karra, 0, 0, new EvolutionRequest(escavalier.Id)).Success);
        Assert.Equal(Escavalier, karra.GetEntity(0, 0).Species);
        AssertLegal(karra.GetEntity(0, 0));

        using var shel = Legal(generation, Shelmet, 30);
        var accelgor = Only(Service.Plan(shel, 0, 0, hax: false), Accelgor);
        Assert.Equal("Trade with Karrablast", accelgor.Requirement);
        Assert.True(Service.Evolve(shel, 0, 0, new EvolutionRequest(accelgor.Id)).Success);
        Assert.Equal(Accelgor, shel.GetEntity(0, 0).Species);
        AssertLegal(shel.GetEntity(0, 0));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    public void KadabraKeepsACustomNicknameAndDefaultNamesFollowTheSpecies(int generation)
    {
        using var plain = Legal(generation, Kadabra, 30);
        var option = Only(Service.Plan(plain, 0, 0, hax: false), Alakazam);
        Assert.Equal("Alakazam", option.NewNickname);
        Assert.True(Service.Evolve(plain, 0, 0, new EvolutionRequest(option.Id)).Success);
        var evolved = plain.GetEntity(0, 0);
        Assert.False(evolved.IsNicknamed);
        Assert.Equal("ALAKAZAM", evolved.Nickname.ToUpperInvariant());
        AssertLegal(evolved);

        using var named = Legal(generation, Kadabra, 30);
        var pk = named.GetEntity(0, 0);
        pk.SetNickname("Spoony");
        named.SaveFile.SetBoxSlotAtIndex(pk, 0, 0, EntityImportSettings.None);
        option = Only(Service.Plan(named, 0, 0, hax: false), Alakazam);
        Assert.Null(option.NewNickname);
        Assert.True(Service.Evolve(named, 0, 0, new EvolutionRequest(option.Id)).Success);
        evolved = named.GetEntity(0, 0);
        Assert.True(evolved.IsNicknamed);
        Assert.Equal("Spoony", evolved.Nickname);
        AssertLegal(evolved);
    }

    [Fact]
    public void EverstoneBlocksTradeEvolutionUntilRemoved()
    {
        using var session = Legal(8, Machoke, 40);
        var pk = session.GetEntity(0, 0);
        pk.HeldItem = 229; // Everstone
        session.SaveFile.SetBoxSlotAtIndex(pk, 0, 0, EntityImportSettings.None);
        var option = Only(Service.Plan(session, 0, 0, hax: false), Machamp);
        Assert.False(option.Available);
        Assert.Contains("Everstone", option.BlockedReason);
        Assert.False(Service.Evolve(session, 0, 0, new EvolutionRequest(option.Id)).Success);
        Assert.Equal(Machoke, session.GetEntity(0, 0).Species);
    }

    [Fact]
    public void StonesNeedTheItemInTheBagUnlessHaX()
    {
        using var session = Legal(8, Pikachu, 20);
        var option = Only(Service.Plan(session, 0, 0, hax: false), Raichu);
        Assert.Equal(EvolutionTrigger.UseItem, option.Trigger);
        Assert.Equal("Use Thunder Stone", option.Requirement);
        Assert.False(option.Available);
        Assert.False(Service.Evolve(session, 0, 0, new EvolutionRequest(option.Id)).Success);

        var haxOption = Only(Service.Plan(session, 0, 0, hax: true), Raichu);
        Assert.True(haxOption.Available);
        Assert.False(haxOption.ConditionMet);
        Assert.True(Service.Evolve(session, 0, 0, new EvolutionRequest(haxOption.Id, Hax: true)).Success);
        Assert.Equal(Raichu, session.GetEntity(0, 0).Species);
    }

    [Fact]
    public void StatsAreRecomputedAtTheSameLevel()
    {
        using var session = Legal(8, Machoke, 40);
        var option = Only(Service.Plan(session, 0, 0, hax: false), Machamp);
        Assert.True(option.StatsAfter[1] > option.StatsBefore[1]); // Machamp hits harder
        Assert.True(Service.Evolve(session, 0, 0, new EvolutionRequest(option.Id)).Success);
        Assert.Equal(option.StatsAfter, session.ReadEntity(0, 0).Stats);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void AnUntradedHaunterComesHomeWithTheLinkPartnerRecorded(int generation)
    {
        const ushort haunter = 93, gengar = 94;
        using var session = Legal(generation, haunter, 30);
        var pk = session.GetEntity(0, 0);
        // Caught by the save's own trainer and never traded: no handling trainer yet.
        pk.CurrentHandler = 0;
        pk.HandlingTrainerName = string.Empty;
        pk.HandlingTrainerGender = 0;
        pk.HandlingTrainerFriendship = 0;
        if (pk is IMemoryHT ht) ht.ClearMemoriesHT();
        if (pk is IHandlerLanguage lang) lang.HandlingTrainerLanguage = 0;
        // The generator's ribbons assumed the handler we just removed; start from none.
        if (pk is IRibbonSetCommon6 ribbons) { ribbons.RibbonBestFriends = false; ribbons.RibbonChampionKalos = false; }
        pk.RefreshChecksum();
        Assert.True(pk.IsUntraded);
        AssertLegal(pk);
        session.SaveFile.SetBoxSlotAtIndex(pk, 0, 0, EntityImportSettings.None);

        var option = Only(Service.Plan(session, 0, 0, hax: false), gengar);
        Assert.NotNull(option.HandlerNote);
        Assert.True(Service.Evolve(session, 0, 0, new EvolutionRequest(option.Id)).Success);

        var evolved = session.GetEntity(0, 0);
        Assert.Equal(gengar, evolved.Species);
        Assert.False(evolved.IsUntraded);
        Assert.Equal(0, evolved.CurrentHandler);
        Assert.Equal(EvolutionService.DefaultTradePartner, evolved.HandlingTrainerName);
        AssertLegal(evolved);
    }
}
