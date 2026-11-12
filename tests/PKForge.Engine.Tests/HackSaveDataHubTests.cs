using PKForge.Domain;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// QA 3.2 #1: Save data did nothing on every custom-engine ROM hack. The hub probes each
/// service with the open session, and seven of them hard-cast it to the PKHeX-backed
/// <see cref="SaveEngineSession"/>; a hack session threw, and the fire-and-forget task hid it.
/// Every hub service must now answer a hack session (unsupported, empty, or a clean
/// <see cref="NotSupportedException"/> for a write), and the hub must list what the hack's
/// own engine can back. Real saves live in .local-testdata (gitignored); a missing one skips.
/// </summary>
public sealed class HackSaveDataHubTests
{
    public static TheoryData<string, string> Hacks => new()
    {
        { "romhacks/batch-2026-11-12/loose/Pokemon - Origin HeartGold Version.sav", "OriginHeartGoldEngineSession" },
        { "romhacks/recharged-yellow.sav", "RechargedYellowEngineSession" },
        { "romhacks/quetzal-9.1.sav", "QuetzalEngineSession" },
        { "romhacks/soulgold.sav", "SoulGoldEngineSession" },
        { "romhacks/lazarus.sav", "ExpansionEngineSession" },
        { "romhacks/batch-2026-11-12/loose/Pokemon Fire Red Extended 3.5.3.sav", "FireRedHackEngineSession" },
        { "romhacks/batch-2026-11-12/saves/saves/Pokemon - Elite Redux.sav", "EliteReduxEngineSession" },
        { "romhacks/batch-2026-11-12/saves/saves/Pokemon - Emerald Exceeded.sav", "ExceededEngineSession" },
        { "romhacks/batch-2026-11-12/romhack/romhack saves/Pokemon - GS Chronicles.sav", "GsChroniclesEngineSession" },
        { "thor/retroarch/mGBA/Radical-red-champ.sav", "RadicalRedEngineSession" },
        { "thor/retroarch/mGBA/Pokémon Unbound (v2.1.1.1).srm", "UnboundEngineSession" },
    };

    internal static ISaveEngineSession? Open(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", relative);
        return path is not null && File.Exists(path)
            ? new SaveEngine().OpenSession(File.ReadAllBytes(path), Path.GetFileName(path))
            : null;
    }

    [Theory]
    [MemberData(nameof(Hacks))]
    public void Every_hub_probe_answers_a_hack_session_without_throwing(string sample, string engine)
    {
        using var session = Open(sample);
        if (session is null) return;
        Assert.Equal(engine, session.GetType().Name);
        Assert.IsNotType<SaveEngineSession>(session);

        // The probes BoxBrowserPage.SaveDataCapabilitiesOf runs, in its order.
        Assert.False(EventFlagService.IsSupported(session));
        Assert.False(KeyItemEventService.IsSupported(session));
        Assert.False(SaveBlockEditorService.IsSupported(session));
        Assert.False(HoneyTreeService.IsSupported(session));
        Assert.False(HallOfFameService.IsSupported(session));
        Assert.False(RaidService.IsSupported(session));
        Assert.Equal(TreatCaseKind.None, TreatCaseService.GetKind(session));
        Assert.False(SinnohUndergroundService.IsSupported(session));
        Assert.False(ZygardeCellService.IsSupported(session));
        Assert.False(FestivalPlazaService.IsSupported(session));
        Assert.False(SuperTrainingService.IsSupported(session));
        Assert.False(PokewalkerService.IsSupported(session));
        Assert.False(EReaderService.IsSupported(session));
        Assert.False(PalParkService.IsSupported(session));
        // World & encounters (WorldEventsMenu.HasAny).
        Assert.False(ClockRepairService.IsSupported(session));
        Assert.False(RoamerService.IsSupported(session));
        Assert.False(EventResetService.IsSupported(session));
        Assert.False(EntralinkService.IsSupported(session));
        Assert.False(ApricornService.IsSupported(session));
        Assert.False(MysteryGiftSlotService.IsSupported(session));
    }

    [Theory]
    [MemberData(nameof(Hacks))]
    public void The_services_that_hard_cast_the_session_now_degrade_cleanly(string sample, string engine)
    {
        using var session = Open(sample);
        if (session is null) return;
        _ = engine;

        // Trainer card: the core fields come from the session, every extra field is unsupported.
        Assert.False(TrainerCardService.IsSupported(session));
        var card = TrainerCardService.Get(session);
        Assert.Equal(session.Generation, card.Generation);
        Assert.False(card.SupportsBadges);
        Assert.False(card.SupportsRivalName);
        Assert.False(card.Geo.Supported);
        Assert.False(card.StartDate.Supported);
        Assert.False(card.SupportsFlyDestinations);
        Assert.Throws<NotSupportedException>(() => TrainerCardService.Apply(session, new TrainerCardEdit(RivalName: "GARY")));

        // Hall of Fame, roamers, ticket events, the Gen 3 clock, Mystery Gift cards, Pal Park.
        Assert.Throws<NotSupportedException>(() => HallOfFameService.Get(session));
        Assert.Throws<NotSupportedException>(() => HallOfFameService.RecordParty(session));
        Assert.Empty(RoamerService.GetRoamers(session));
        Assert.Throws<NotSupportedException>(() => RoamerService.Reroll(session, 0, shiny: false));
        Assert.Empty(KeyItemEventService.GetEvents(session));
        Assert.False(KeyItemEventService.Enable(session, "eon-ticket").Success);
        Assert.False(KeyItemEventService.Disable(session, "eon-ticket").Success);
        Assert.Throws<NotSupportedException>(() => ClockRepairService.ArmInGameReset(session));
        Assert.Throws<NotSupportedException>(() => EventFlagService.Load(session));
        Assert.Null(MysteryGiftSlotService.Load(session));
        Assert.Throws<NotSupportedException>(() => PalParkService.GetState(session));
        Assert.Throws<NotSupportedException>(() => PalParkService.Preview(session, PalParkSource.FromSave(0, 0), allowNonGba: true));

        // The legalizer refuses instead of casting (Unbound and Radical Red have their own generator).
        if (session.GetType().Name is not ("UnboundEngineSession" or "RadicalRedEngineSession" or "GsChroniclesEngineSession"))
        {
            var legalizer = new LegalizerService();
            var request = new GenerationRequest(25, 5, false, null, null, null, null);
            Assert.Null(legalizer.GenerateData(session, request));
            Assert.False(legalizer.Generate(session, 0, 0, request).Success);
        }
    }

    [Theory]
    [InlineData("romhacks/batch-2026-11-12/loose/Pokemon - Origin HeartGold Version.sav", false, true)]
    [InlineData("romhacks/recharged-yellow.sav", true, true)]
    [InlineData("romhacks/quetzal-9.1.sav", false, false)]
    [InlineData("romhacks/soulgold.sav", true, true)]
    [InlineData("romhacks/lazarus.sav", false, false)]
    [InlineData("romhacks/batch-2026-11-12/loose/Pokemon Fire Red Extended 3.5.3.sav", false, false)]
    [InlineData("romhacks/batch-2026-11-12/saves/saves/Pokemon - Elite Redux.sav", false, true)]
    [InlineData("romhacks/batch-2026-11-12/saves/saves/Pokemon - Emerald Exceeded.sav", false, false)]
    [InlineData("thor/retroarch/mGBA/Radical-red-champ.sav", true, true)]
    [InlineData("thor/retroarch/mGBA/Pokémon Unbound (v2.1.1.1).srm", false, true)]
    public void The_hub_lists_what_the_hacks_own_engine_backs(string sample, bool pokedex, bool bag)
    {
        using var session = Open(sample);
        if (session is null) return;

        var core = SaveDataSupport.Of(session);
        Assert.True(core.TrainerCard);
        Assert.Equal(pokedex, core.Pokedex);
        Assert.Equal(bag, core.Bag);
        Assert.False(core.GiftInbox);

        var entries = SaveDataMenu.Build(new SaveDataCapabilities(
                TrainerCard: core.TrainerCard, Pokedex: core.Pokedex, TrainerRecords: core.TrainerRecords,
                Bag: core.Bag, GiftInbox: core.GiftInbox))
            .SelectMany(group => group.Rows).Select(row => row.Entry).ToList();
        Assert.Contains(SaveDataEntry.TrainerCard, entries);
        Assert.Equal(pokedex, entries.Contains(SaveDataEntry.Pokedex));
        Assert.Equal(bag, entries.Contains(SaveDataEntry.Bag));
        Assert.DoesNotContain(SaveDataEntry.GiftInbox, entries);
        Assert.DoesNotContain(SaveDataEntry.HallOfFame, entries);
        Assert.Contains(SaveDataEntry.RestorePoints, entries);
        Assert.Contains(SaveDataEntry.Export, entries);
    }

    [Fact]
    public void A_retail_save_keeps_card_dex_and_bag()
    {
        using var session = Open("thor/retroarch/mGBA/Pokemon Fire Red.sav");
        if (session is null) return;
        Assert.IsType<SaveEngineSession>(session);
        // Card, Pokédex and bag always stay on a retail save; records and stored gifts follow
        // what the game has (FireRed keeps no trainer records).
        var support = SaveDataSupport.Of(session);
        Assert.True(support.TrainerCard && support.Pokedex && support.Bag);
        Assert.Equal(session.GetTrainerRecords().Supported, support.TrainerRecords);
        Assert.True(TrainerCardService.IsSupported(session));
        Assert.True(HallOfFameService.IsSupported(session));
    }
}
