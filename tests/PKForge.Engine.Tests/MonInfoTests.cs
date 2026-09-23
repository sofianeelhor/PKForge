using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The info-rich pickers' data: the embedded PokeAPI dex facts, per-game move types and
/// categories, learn-source labelling from PKHeX's own learnability walk, EXP math,
/// Hidden Power, species cards and the batch editor's dry run (which must write nothing).
/// </summary>
public sealed class MonInfoTests
{
    private const int Pikachu = 25, Garchomp = 445, Magnemite = 81;
    private const int ThunderShock = 84, Thunderbolt = 85, Flamethrower = 53, Tackle = 33, SwordsDance = 14, Bite = 44;
    private static readonly MonInfoService Info = new();

    // ---------- Dataset ----------

    [Fact]
    public void DatasetCarriesKnownMoveNumbersAndProse()
    {
        var table = DexFacts.Default;
        Assert.True(table.MoveCount >= 900, $"moves: {table.MoveCount}");
        Assert.True(table.AbilityCount >= 300, $"abilities: {table.AbilityCount}");
        Assert.True(table.ItemCount >= 800, $"items: {table.ItemCount}");

        var bolt = DexFacts.Move(Thunderbolt)!;
        Assert.Equal((12, MoveCategory.Special, 90, 100), (bolt.Type, bolt.Category, bolt.Power, bolt.Accuracy));
        Assert.Contains("10% chance to paralyze", bolt.Effect);
        Assert.Equal(MoveCategory.Physical, DexFacts.Move(Tackle)!.Category);
        var dance = DexFacts.Move(SwordsDance)!;
        Assert.Equal((MoveCategory.Status, 0, 0), (dance.Category, dance.Power, dance.Accuracy));
        Assert.Contains("Grass", DexFacts.Ability(65)); // Overgrow
        Assert.Contains("1/16", DexFacts.Item("Leftovers"));
        Assert.NotNull(DexFacts.Item("Poké Ball"));   // accent folding: "pokeball"
        Assert.NotNull(DexFacts.Item("POKé BALL"));   // Gen 3 capitalisation
        Assert.Null(DexFacts.Item(""));
        Assert.Equal("pokeball", DexFacts.NormalizeName("Poké Ball"));
        Assert.Equal("kingsrock", DexFacts.NormalizeName("King’s Rock"));
    }

    [Fact]
    public void DatasetMoveTypesAgreeWithPkhexLatestTable()
    {
        // The dataset's ids are PokeAPI's: they must line up with the games' move ids.
        var mismatches = Enumerable.Range(1, 919)
            .Where(m => DexFacts.Move(m) is { } fact && fact.Type != MoveInfo.GetType((ushort)m, EntityContext.Gen9))
            .ToList();
        Assert.True(mismatches.Count <= 2, $"type mismatches: {string.Join(",", mismatches)}");
    }

    [Theory]
    [InlineData(3, 0.98)]
    [InlineData(4, 1.0)]
    [InlineData(7, 1.0)]
    [InlineData(8, 1.0)]
    [InlineData(9, 0.85)] // PokeAPI has no prose yet for some Scarlet/Violet-only items
    public void HeldItemsHaveDescriptionsByTheirOwnGamesNames(int generation, double coverage)
    {
        using var session = Blank(generation);
        var names = session.GetItemNames(); // Gen 1-4 ids differ: lookup is by the game's own name
        var held = Info.GetHeldItems(session);
        Assert.DoesNotContain(0, held);
        var machines = held.Count(id => names[id].StartsWith("TM", StringComparison.Ordinal) || names[id].StartsWith("TR", StringComparison.Ordinal));
        var described = held.Count(id => DexFacts.Item(names[id]) is not null);
        Assert.True(described >= (held.Count - machines) * coverage, $"gen {generation}: {described}/{held.Count - machines} described");
        Assert.NotNull(DexFacts.Item(names[held.First(id => names[id].Equals("Leftovers", StringComparison.OrdinalIgnoreCase))]));
    }

    // ---------- Labels and ordering ----------

    [Fact]
    public void CategoryFollowsTypeBeforeThePhysicalSpecialSplit()
    {
        Assert.Equal(MoveCategory.Special, TypeFacts.CategoryIn(3, MoveCategory.Physical, 16)); // Crunch (Dark) in Gen 3
        Assert.Equal(MoveCategory.Physical, TypeFacts.CategoryIn(4, MoveCategory.Physical, 16));
        Assert.Equal(MoveCategory.Physical, TypeFacts.CategoryIn(2, MoveCategory.Special, 0));   // Normal was physical
        Assert.Equal(MoveCategory.Status, TypeFacts.CategoryIn(1, MoveCategory.Status, 9));

        using var gen3 = Blank(3);
        Assert.Equal(MoveCategory.Special, Info.GetMove(gen3, Bite)!.Category);  // Bite: Dark, special in RSE
        using var gen9 = Blank(9);
        Assert.Equal(MoveCategory.Physical, Info.GetMove(gen9, Bite)!.Category);
        Assert.Equal(0, Info.GetMove(Blank(1), Bite)!.Type);                      // Bite was Normal in Gen 1
    }

    [Fact]
    public void LearnLabelsReadLikeTheGames()
    {
        Assert.Equal("Lv 32", new MoveLearn(LearnKind.LevelUp, 32).Label);
        Assert.Equal("Lv", new MoveLearn(LearnKind.LevelUp).Label);
        Assert.Equal("TM", new MoveLearn(LearnKind.Machine).Label);
        Assert.Equal("Egg", new MoveLearn(LearnKind.Egg).Label);
        Assert.Equal("Tutor", new MoveLearn(LearnKind.Tutor).Label);
        Assert.Equal("Evo", new MoveLearn(LearnKind.Evolution).Label);
        Assert.Equal("", default(MoveLearn).Label);
        Assert.False(default(MoveLearn).IsLegal);
        Assert.Contains("Lv 32", new MoveLearn(LearnKind.LevelUp, 32).Sentence);
    }

    [Fact]
    public void MoveOrderPutsLegalFirstLevelUpByLevel()
    {
        MoveChoice M(int id, MoveLearn learn) => new(id, 0, MoveCategory.Physical, 40, 100, 35, learn, "");
        var names = new Dictionary<int, string> { [1] = "Zap", [2] = "Bash", [3] = "Aqua", [4] = "Claw", [5] = "Dive" };
        var sorted = MoveChoiceOrder.Sort(
        [
            M(3, default), M(1, new MoveLearn(LearnKind.LevelUp, 20)), M(4, new MoveLearn(LearnKind.Machine)),
            M(2, new MoveLearn(LearnKind.LevelUp, 5)), M(5, default),
        ], id => names[id]);
        Assert.Equal([2, 1, 4, 3, 5], sorted.Select(m => m.Id));
    }

    [Fact]
    public void BudgetAndGenderLabels()
    {
        var caps6 = new TrainingCaps(31, 252);
        Assert.Equal(510, TrainingBudget.TotalCap(9, caps6));
        Assert.Equal(510, TrainingBudget.TotalCap(3, new TrainingCaps(31, 255)));
        Assert.Null(TrainingBudget.TotalCap(2, new TrainingCaps(15, 65535)));
        Assert.Equal("Total 508 / 510", TrainingBudget.Label([4, 252, 0, 252, 0, 0], 510));
        Assert.True(TrainingBudget.IsOver([252, 252, 8, 0, 0, 0], 510));
        Assert.False(TrainingBudget.IsOver([252, 252, 6, 0, 0, 0], 510));

        Assert.Equal("♂ 50% · ♀ 50%", new GenderRatio(127).Label);
        Assert.Equal("♂ 87.5% · ♀ 12.5%", new GenderRatio(31).Label);
        Assert.Equal("♂ 12.5% · ♀ 87.5%", new GenderRatio(225).Label);
        Assert.Equal("Genderless", new GenderRatio(255).Label);
        Assert.Equal("Always ♂", new GenderRatio(0).Label);
        Assert.Equal("Always ♀", new GenderRatio(254).Label);
    }

    // ---------- Engine answers ----------

    [Fact]
    public void MoveChoicesTagHowALegalPikachuLearns()
    {
        using var session = Legal(9, Pikachu);
        var choices = Info.GetMoveChoices(session, 0, 0).ToDictionary(c => c.Id);

        var shock = choices[ThunderShock];
        Assert.Equal(LearnKind.LevelUp, shock.Learn.Kind);
        Assert.True(shock.Learn.Level >= 1);
        Assert.True(choices[Thunderbolt].Learn.IsLegal);
        Assert.Equal(15, choices[Thunderbolt].PP);                 // SV PP table
        Assert.Equal(MoveCategory.Special, choices[Thunderbolt].Category);
        Assert.False(choices[Flamethrower].Learn.IsLegal);         // no Fire move for Pikachu
        Assert.Contains(choices.Values, c => c.Learn.Kind == LearnKind.Machine);
        // Max/Z moves never show up in a move picker.
        Assert.DoesNotContain(choices.Keys, m => !MoveInfo.IsMoveKnowable((ushort)m));
    }

    [Fact]
    public void PendingSpeciesEditLearnsForTheNewSpecies()
    {
        using var session = Legal(9, Pikachu);
        var asGarchomp = Info.GetMoveChoices(session, 0, 0, species: Garchomp).ToDictionary(c => c.Id);
        Assert.False(asGarchomp[ThunderShock].Learn.IsLegal);
        Assert.Contains(asGarchomp.Values, c => c.Learn.IsLegal);  // its own (Dragon/Ground) moves
    }

    [Fact]
    public void MaxPpByUpsFollowsTheFormat()
    {
        using var session = Legal(9, Pikachu);
        Assert.Equal([15, 18, 21, 24], Info.GetMaxPPByUps(session, 0, 0, Thunderbolt));
    }

    [Fact]
    public void AbilitySlotsAndDescriptionsComeFromPersonalInfo()
    {
        using var session = Blank(9);
        var abilities = Info.GetAbilityChoices(session, Garchomp, 0);
        Assert.Equal([(8, "1"), (24, "Hidden")], abilities.Select(a => (a.Id, a.Slot)));    // Sand Veil, Rough Skin
        Assert.All(abilities, a => Assert.False(string.IsNullOrWhiteSpace(a.Effect)));

        using var gen4 = Blank(4);
        Assert.DoesNotContain(Info.GetAbilityChoices(gen4, Garchomp, 0), a => a.Slot == "Hidden");
    }

    [Fact]
    public void SpeciesCardShowsTypingStatsAndGender()
    {
        using var session = Blank(9);
        var card = Info.GetSpeciesCard(session, Garchomp, 0)!;
        Assert.Equal([15, 4], card.Types);   // Dragon / Ground
        Assert.Equal(600, card.Total);
        Assert.Equal(new BaseStats(108, 130, 95, 80, 85, 102), card.BaseStats);
        Assert.Equal("♂ 50% · ♀ 50%", card.Gender!.Value.Label);
        Assert.True(Info.GetSpeciesCard(session, Magnemite, 0)!.Gender!.Value.Genderless);
        Assert.Null(Info.GetSpeciesCard(session, 0, 0));
        Assert.Null(Info.GetSpeciesCard(Blank(1), Pikachu, 0)!.Gender); // Gen 1 has no genders
    }

    [Fact]
    public void HiddenPowerTypesMatchTheKnownSpreads()
    {
        using var session = Blank(7);
        Assert.Equal(16, Info.GetHiddenPowerType(session, [31, 31, 31, 31, 31, 31]));    // Dark
        Assert.Equal(9, Info.GetHiddenPowerType(session, [31, 30, 31, 30, 31, 30]));     // Fire (HP/Atk/Def/SpA/SpD/Spe)
        Assert.Equal(14, Info.GetHiddenPowerType(session, [31, 30, 30, 31, 31, 31]));    // Ice
        using var gen9 = Blank(9);
        Assert.Null(Info.GetHiddenPowerType(gen9, [31, 31, 31, 31, 31, 31]));             // removed in Gen 8

        for (var type = 1; type <= 16; type++)
        {
            var ivs = Info.GetIVsForHiddenPower(session, [31, 31, 31, 31, 31, 31], type);
            Assert.NotNull(ivs);
            Assert.Equal(type, Info.GetHiddenPowerType(session, ivs!));
            Assert.All(ivs!, iv => Assert.InRange(iv, 30, 31));
        }
        Assert.Null(Info.GetIVsForHiddenPower(session, [31, 31, 31, 31, 31, 31], 0)); // no Normal Hidden Power
    }

    [Fact]
    public void LevelInfoGivesExpAndTheMetLevelFloor()
    {
        using var session = Legal(9, Pikachu); // medium-fast growth: EXP = level^3
        var met = session.GetMetInfo(0, 0).MetLevel;
        var info = Info.GetLevelInfo(session, 0, 0, 50)!;
        Assert.Equal(125_000, info.ExpAtLevel);
        Assert.Equal(7_651, info.ExpToNext);
        Assert.False(info.BelowMetLevel);
        Assert.Equal(info.StatsNow, session.ReadEntity(0, 0).Stats);   // "now" is the stored level
        Assert.Equal(info.StatsAtLevel, new StatPreviewService().PreviewSlot(session, 0, 0, new StatPreviewOverrides(Level: 50))!.Current);

        var low = Info.GetLevelInfo(session, 0, 0, 1)!;
        Assert.Equal(0, low.ExpAtLevel);
        Assert.Equal(met > 1, low.BelowMetLevel);
        Assert.True(low.StatsAtLevel[0] < info.StatsNow[0]);

        var max = Info.GetLevelInfo(session, 0, 0, 100)!;
        Assert.Equal(1_000_000, max.ExpAtLevel);
        Assert.Equal(0, max.ExpToNext);
    }

    [Fact]
    public void BatchDryRunCountsWithoutWriting()
    {
        using var session = Legal(9, Pikachu);
        var before = session.Serialize().ToArray();
        var level = session.ReadEntity(0, 0).Level;
        var noop = Info.DryRunBatch(session, [$"Level={level}"], boxes: [0]);
        Assert.Equal(new BatchDryRun(1, 0, 0, 0), noop);

        var friend = Info.DryRunBatch(session, ["Friendship=255", "IV_HP=31", "IV_ATK=31"], boxes: [0]);
        Assert.True(friend.Affected <= 1);
        Assert.Equal(0, friend.BecomeIllegal);

        var down = Info.DryRunBatch(session, ["Level=1"], slots: [(0, 0), (0, 1)]);
        Assert.Equal(1, down.Targeted);                   // the empty slot is not a target
        Assert.Equal(1, down.Affected);
        Assert.Equal(1, down.BecomeIllegal);              // no Lv 1 encounter backs a legalizer-made Pikachu

        Assert.Equal(before, session.Serialize().ToArray()); // nothing was written
        Assert.Equal(level, session.ReadEntity(0, 0).Level);
    }

    // ---------- Helpers ----------

    private static SaveEngineSession Blank(int generation) => (SaveEngineSession)new SaveEngine().OpenBlankSession(generation);

    /// <summary>A blank save with one legalizer-made (so legal, encounter-matched) mon in box 0 slot 0.</summary>
    private static SaveEngineSession Legal(int generation, int species)
    {
        var session = Blank(generation);
        var outcome = new LegalizerService().Generate(session, 0, 0,
            new GenerationRequest(species, 50, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null, Form: 0));
        Assert.True(outcome.Success, outcome.Message);
        Assert.True(new LegalityAnalysis(session.GetEntity(0, 0)).Valid);
        return session;
    }
}
