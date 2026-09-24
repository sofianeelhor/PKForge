using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Whole-team Showdown import and the box manager's bulk actions.</summary>
public sealed class ShowdownTeamAndBulkTests
{
    private const string Team = """
        === [gen8] Test team ===

        Pikachu @ Light Ball
        Ability: Static
        Level: 50
        EVs: 252 SpA / 4 SpD / 252 Spe
        Timid Nature
        - Thunderbolt
        - Quick Attack

        Sparky (Garchomp) (M) @ Choice Scarf
        Ability: Rough Skin
        Jolly Nature
        Nonsense line that is not a set field
        - Earthquake
        - Dragon Claw

        Bulbasaur
        Shiny: Yes
        - Vine Whip
        """;

    [Fact]
    public void SplitCutsSetsWhereShowdownDoesAndDropsTeamHeaders()
    {
        var sets = ShowdownTeamService.Split(Team.Replace("\n", "\r\n"));
        Assert.Equal(3, sets.Count);
        Assert.Equal([(ushort)Species.Pikachu, (ushort)Species.Garchomp, (ushort)Species.Bulbasaur], sets.Select(s => s.Set.Species));
        Assert.StartsWith("Pikachu @ Light Ball", sets[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(sets, s => s.Text.Contains("===", StringComparison.Ordinal));
        Assert.Equal("Sparky", sets[1].Set.Nickname);
        Assert.NotEmpty(sets[1].Set.InvalidLines);
        Assert.True(sets[2].Set.Shiny);
    }

    [Fact]
    public void PreviewLegalizesEachSetAndPlaceFillsOnlyEmptySlots()
    {
        using var session = new SaveEngine().OpenBlankSession(8);
        var legalizer = new LegalizerService();
        // Something already lives in slot 1: the import must flow around it.
        Assert.True(legalizer.GenerateFromShowdown(session, 0, 1, "Eevee").Success);

        var previews = ShowdownTeamService.Preview(session, legalizer, Team);
        Assert.Equal(3, previews.Count);
        Assert.All(previews, p => Assert.True(p.Generated && p.Legal, $"{p.Title}: {p.Verdict}"));
        Assert.Contains("Sparky", previews[1].Title, StringComparison.Ordinal);
        Assert.NotEmpty(previews[1].ParseProblems);
        Assert.True(previews[2].Shiny);
        Assert.True(session.ReadEntity(0, 0).IsEmpty, "the preview must not place anything");

        var targets = ShowdownTeamService.EmptySlots(session, 0, onlyThisBox: true);
        Assert.DoesNotContain(new SlotRef(0, 1), targets);
        var outcome = ShowdownTeamService.Place(session, legalizer, previews, targets);
        Assert.True(outcome.Success, outcome.Message);

        Assert.Equal((int)Species.Pikachu, session.ReadEntity(0, 0).Species);
        Assert.Equal((int)Species.Eevee, session.ReadEntity(0, 1).Species);
        Assert.Equal((int)Species.Garchomp, session.ReadEntity(0, 2).Species);
        Assert.Equal((int)Species.Bulbasaur, session.ReadEntity(0, 3).Species);
        Assert.True(session.ReadEntity(0, 3).IsShiny);
        var engine = (SaveEngineSession)session;
        foreach (var slot in new[] { 0, 2, 3 })
            Assert.True(new LegalityAnalysis(engine.GetEntity(0, slot)).Valid);
    }

    [Fact]
    public void PlaceStopsWhenTheBoxRunsOutOfRoom()
    {
        using var session = new SaveEngine().OpenBlankSession(8);
        var legalizer = new LegalizerService();
        var previews = ShowdownTeamService.Preview(session, legalizer, Team);
        var outcome = ShowdownTeamService.Place(session, legalizer, previews, [new SlotRef(0, 29)]);
        Assert.True(outcome.Success);
        Assert.Contains("2 did not fit", outcome.Message, StringComparison.Ordinal);
    }

    private static (ISaveEngineSession Session, LegalizerService Legalizer) Generated(params string[] sets)
    {
        var session = new SaveEngine().OpenBlankSession(8);
        var legalizer = new LegalizerService();
        for (var i = 0; i < sets.Length; i++)
            Assert.True(legalizer.GenerateFromShowdown(session, 0, i, sets[i]).Success, sets[i]);
        return (session, legalizer);
    }

    [Fact]
    public void BulkShinyKeepsLegalMonsLegalAndRoundTrips()
    {
        var (session, _) = Generated("Pikachu", "Machop", "Zigzagoon");
        using (session)
        {
            var slots = new[] { (0, 0), (0, 1), (0, 2) };
            var engine = (SaveEngineSession)session;
            var shiny = BulkBoxActions.SetShiny(session, slots, shiny: true);
            Assert.Equal(3, shiny.Changed);
            foreach (var (box, slot) in slots)
            {
                var pk = engine.GetEntity(box, slot);
                Assert.True(pk.IsShiny);
                Assert.True(new LegalityAnalysis(pk).Valid);
            }
            Assert.Equal(0, BulkBoxActions.SetShiny(session, slots, shiny: true).Changed); // already shiny
            var plain = BulkBoxActions.SetShiny(session, slots, shiny: false);
            Assert.Equal(3, plain.Changed);
            Assert.All(slots, s => Assert.False(engine.GetEntity(s.Item1, s.Item2).IsShiny));
        }
    }

    [Fact]
    public void ShinyLockedMonsAreLeftAloneNotBroken()
    {
        using var session = new SaveEngine().OpenBlankSession(8);
        var legalizer = new LegalizerService();
        // Eternatus: every origin is shiny-locked and no shiny was ever distributed.
        Assert.True(legalizer.GenerateFromShowdown(session, 0, 0, "Eternatus").Success);
        var engine = (SaveEngineSession)session;
        var before = engine.GetEntity(0, 0).Data.ToArray();
        Assert.True(new LegalityAnalysis(engine.GetEntity(0, 0)).Valid);

        var outcome = BulkBoxActions.SetShiny(session, [(0, 0)], shiny: true);
        Assert.Equal(0, outcome.Changed);
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(before, engine.GetEntity(0, 0).Data.ToArray());
    }

    [Fact]
    public void HandlerHandoverFollowsPkhexTradeRules()
    {
        var (session, _) = Generated("Pikachu");
        using (session)
        {
            var engine = (SaveEngineSession)session;
            var pk = engine.GetEntity(0, 0);
            // A mon from another trainer: the open save's trainer becomes its handler.
            pk.OriginalTrainerName = "Someone";
            pk.TID16 = (ushort)(engine.SaveFile.TID16 ^ 0x1234);
            pk.CurrentHandler = 0;
            pk.RefreshChecksum();
            engine.SaveFile.SetBoxSlotAtIndex(pk, 0, 0, EntityImportSettings.None);

            var outcome = BulkBoxActions.SetCurrentTrainerAsHandler(session, [(0, 0)]);
            Assert.Equal(1, outcome.Changed);
            var handled = engine.GetEntity(0, 0);
            Assert.Equal(1, handled.CurrentHandler);
            Assert.Equal(engine.SaveFile.OT, handled.HandlingTrainerName);

            // Running it again changes nothing: the handler is already the save's trainer.
            Assert.Equal(0, BulkBoxActions.SetCurrentTrainerAsHandler(session, [(0, 0)]).Changed);
        }
    }

    [Fact]
    public void HealRestoresPpAndFindCloneExtrasKeepsOne()
    {
        var (session, _) = Generated("Pikachu", "Machop");
        using (session)
        {
            var engine = (SaveEngineSession)session;
            var pk = engine.GetEntity(0, 0);
            pk.Move1_PP = 0;
            pk.RefreshChecksum();
            engine.SaveFile.SetBoxSlotAtIndex(pk, 0, 0, EntityImportSettings.None);
            Assert.Equal(1, BulkBoxActions.Heal(session, [(0, 0), (0, 1)]).Changed);
            Assert.Equal(pk.GetMovePP(pk.Move1, pk.Move1_PPUps), engine.GetEntity(0, 0).Move1_PP);

            Assert.True(session.DuplicateSlot(0, 0, 0, 5));
            Assert.True(session.DuplicateSlot(0, 0, 0, 7));
            var selection = new[] { (0, 0), (0, 1), (0, 5), (0, 7) };
            var extras = BulkBoxActions.FindCloneExtras(session, selection);
            Assert.Equal([(0, 5), (0, 7)], extras);
            Assert.Empty(BulkBoxActions.FindCloneExtras(session, [(0, 0), (0, 1)]));
        }
    }
}
