using PKForge.Domain;
using PKForge.Engine;
using Xunit.Abstractions;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Gift-only mythicals: their only legal origin is the event distribution, which the
/// ownership stamp must never rewrite (fixed OT). Generation must keep the authentic
/// event OT instead of failing the request.
/// </summary>
public sealed class MythicalGenerationTests
{
    private sealed class NoOwnership : IGenerationOwnershipSettings
    {
        public bool UseCurrentTrainerForGeneration => false;
    }

    public MythicalGenerationTests(ITestOutputHelper output) => Output = output;
    private ITestOutputHelper Output { get; }

    [Theory]
    [InlineData(719)] // Diancie
    [InlineData(720)] // Hoopa
    [InlineData(721)] // Volcanion
    [InlineData(801)] // Magearna
    [InlineData(802)] // Marshadow
    [InlineData(807)] // Zeraora
    public void GiftOnlyMythicalsGenerateWithAuthenticEventOT(int species)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        var legalizer = new LegalizerService(); // ownership stamp on, the default

        var outcome = legalizer.Generate(session, 0, 0,
            new GenerationRequest(species, 50, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null, Form: 0));

        Output.WriteLine($"species {species}: success={outcome.Success} message={outcome.Message}");
        Assert.True(outcome.Success, outcome.Message);
        Assert.Contains("event OT", outcome.Message, StringComparison.Ordinal);
        var detail = session.ReadEntity(0, 0);
        Assert.Equal(species, detail.Species);
        Assert.NotEqual("PKForge", detail.OriginalTrainer); // distribution identity, not the save trainer
    }

    [Theory]
    [InlineData(25)]  // ordinary
    [InlineData(791)] // catchable legendary
    public void OrdinarySpeciesStillCarryTheOwnerIdentity(int species)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        var legalizer = new LegalizerService();

        var outcome = legalizer.Generate(session, 0, 0,
            new GenerationRequest(species, 50, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null, Form: 0));

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal("PKForge", session.ReadEntity(0, 0).OriginalTrainer);
    }

    [Fact]
    public void MarshadowAlsoGeneratesWhenTheOwnershipStampIsOff()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        var legalizer = new LegalizerService(new NoOwnership());

        var outcome = legalizer.Generate(session, 0, 0,
            new GenerationRequest(802, 50, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null, Form: 0));

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(802, session.ReadEntity(0, 0).Species);
    }

    [Theory]
    [InlineData(808)] // Meltan: GO/LGPE only
    [InlineData(810)] // Grookey: Gen 8
    [InlineData(906)] // Sprigatito: Gen 9
    public void NewerGenerationSpeciesFailWithTheRealReason(int species)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        var legalizer = new LegalizerService();

        var outcome = legalizer.Generate(session, 0, 0,
            new GenerationRequest(species, 50, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null, Form: 0));

        Output.WriteLine($"species {species}: success={outcome.Success} message={outcome.Message}");
        Assert.False(outcome.Success);
        Assert.Contains("cannot store", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("No legal combination", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UltraMoonMaxSpeciesMatchesItsTable()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        Assert.Equal(807, session.MaxSpeciesId); // Zeraora; Meltan/Melmetal are GO/LGPE
    }
}
