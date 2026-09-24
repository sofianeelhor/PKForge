using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The read-only summary model behind the Bank inspector and the full-screen summary:
/// built from a live slot and from a bank entry's bytes, across generations, with each
/// format's gaps (Gen 1/2 natures and abilities, pre-Gen 4 characteristics, Tera only in
/// Gen 9) left empty instead of invented.
/// </summary>
public sealed class MonSummaryTests
{
    private static readonly MonSummaryService Summaries = new();

    [Theory]
    [InlineData(1, 25)]
    [InlineData(2, 25)]
    [InlineData(3, 25)]
    [InlineData(4, 445)]
    [InlineData(5, 445)]
    [InlineData(7, 445)]
    [InlineData(8, 25)]
    [InlineData(9, 445)]
    public void BuildsAFullSummaryForEachGeneration(int generation, int species)
    {
        using var session = Legal(generation, species);
        var summary = Summaries.Build(session, 0, 0, analyzeLegality: true);
        Assert.NotNull(summary);
        var s = summary!;
        var entity = session.GetEntity(0, 0);

        Assert.Equal(species, s.Species);
        Assert.Equal(generation, s.Generation);
        Assert.Equal(entity.GetType().Name, s.Format);
        Assert.Equal(entity.CurrentLevel, s.Level);
        Assert.False(string.IsNullOrEmpty(s.SpeciesName));
        Assert.NotEmpty(s.Types);
        Assert.Equal(6, s.Stats.Count);
        Assert.Equal(6, s.BaseStats.Count);
        Assert.Equal(6, s.IVs.Count);
        Assert.Equal(6, s.EVs.Count);
        Assert.NotEmpty(s.Moves);
        Assert.All(s.Moves, m => Assert.True(m.MaxPP > 0 && m.Name.Length > 0, $"{m.Id} {m.Name} pp {m.MaxPP}"));
        Assert.True(s.Legal, string.Join('\n', s.LegalityLines));
        Assert.NotEmpty(s.LegalityLines);
        Assert.NotNull(s.Met);

        // Per-generation gaps are empty, never invented.
        Assert.Equal(generation >= 3, s.Nature is not null);
        Assert.Equal(generation >= 3, s.AbilityName is not null);
        Assert.Equal(generation <= 2, s.ClassicTraining);
        Assert.Equal(generation >= 4, s.Characteristic is not null);
        Assert.Equal(generation == 9, s.TeraTypeName is not null);
        Assert.Equal(generation is >= 2 and <= 7, s.HiddenPowerType is not null);
        if (generation >= 3)
        {
            Assert.Equal(entity.Nature, (Nature)s.Nature!.Value);
            Assert.Equal(entity.Ability, s.Ability);
            Assert.False(string.IsNullOrEmpty(s.AbilityEffect));
        }
        if (s.Characteristic is not null)
            Assert.Contains(s.Characteristic.ToLowerInvariant(), GameInfo.Strings.characteristics[entity.Characteristic].ToLowerInvariant()); // same phrase, the games word it "It's …!"
    }

    [Fact]
    public void NatureArrowsAndStatTablesAgreeWithTheEntity()
    {
        using var session = Legal(9, 445);
        session.ApplyEdit(0, 0, new EntityEdit(Nature: (int)Nature.Jolly)); // +Spe −SpA
        var s = Summaries.Build(session, 0, 0)!;
        Assert.Equal(5, s.NatureUp);   // Spe in display order
        Assert.Equal(3, s.NatureDown); // SpA in display order
        Assert.Equal("Jolly", s.NatureName);
        Assert.Equal(s.IVs.Sum(), s.IvTotal);
        Assert.Equal(new[] { 108, 130, 95, 80, 85, 102 }, s.BaseStats); // Garchomp
        Assert.Equal(600, s.BaseTotal);
        Assert.Null(s.Legal); // not analyzed unless asked
    }

    [Fact]
    public void MovesCarryInGameTypeCategoryAndPp()
    {
        using var session = Legal(4, 445);
        session.ApplyEdit(0, 0, new EntityEdit(Move1: 89 /* Earthquake */, Move2: 0, Move3: 0, Move4: 0));
        var s = Summaries.Build(session, 0, 0)!;
        var quake = Assert.Single(s.Moves);
        Assert.Equal("Earthquake", quake.Name);
        Assert.Equal(4, quake.Type); // Ground
        Assert.Equal(MoveCategory.Physical, quake.Category);
        Assert.Equal(100, quake.Power);
        Assert.Equal(10, quake.MaxPP);
    }

    [Fact]
    public void BankBytesDecodeThroughTheirOwnEntitySession()
    {
        byte[] bytes;
        using (var source = Legal(7, 445))
            bytes = source.ExportSlot(0, 0).Data;

        using var entitySession = new SaveEngine().OpenEntitySession(bytes, "bank");
        Assert.NotNull(entitySession);
        var s = Summaries.Build(entitySession!, 0, 0, analyzeLegality: true)!;
        Assert.Equal(445, s.Species);
        Assert.Equal("PK7", s.Format);
        Assert.True(s.Legal, string.Join('\n', s.LegalityLines));
        Assert.False(string.IsNullOrEmpty(s.Met!.VersionName));
    }

    [Fact]
    public void EmptySlotHasNoSummary()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(9);
        Assert.Null(Summaries.Build(session, 0, 5));
    }

    [Fact]
    public void RibbonsAndMarksAreCountedSeparately()
    {
        using var session = Legal(9, 445);
        var ribbons = session.GetRibbons(0, 0);
        var ribbon = ribbons.First(r => !r.IsMark && r.MaxValue == 1);
        var mark = ribbons.First(r => r.IsMark && r.MaxValue == 1);
        session.SetRibbon(0, 0, ribbon.Id, 1);
        session.SetRibbon(0, 0, mark.Id, 1);
        var s = Summaries.Build(session, 0, 0)!;
        Assert.True(s.RibbonCount >= 1);
        Assert.True(s.MarkCount >= 1);
        Assert.Contains(ribbon.Name, s.RibbonNames);
        Assert.Contains(mark.Name, s.RibbonNames);
    }

    private static SaveEngineSession Legal(int generation, int species)
    {
        var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(generation);
        var outcome = new LegalizerService().Generate(session, 0, 0,
            new GenerationRequest(species, 50, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null, Form: 0));
        Assert.True(outcome.Success, outcome.Message);
        return session;
    }
}
