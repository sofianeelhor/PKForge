using PKForge.Domain;
using PKForge.Engine;
using Xunit;
using Xunit.Abstractions;

namespace PKForge.Engine.Tests;

/// <summary>
/// HaX mode lets a species beyond the save's table be forced in: no encounter, no
/// legality, an explicit no-guarantees message, and nothing may crash on read-back.
/// </summary>
public sealed class HaXUnsupportedSpeciesTests
{
    public HaXUnsupportedSpeciesTests(ITestOutputHelper output) => Output = output;
    private ITestOutputHelper Output { get; }

    [Fact]
    public void Gen8SpeciesForcedIntoUltraMoon()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        var legalizer = new LegalizerService();

        var outcome = legalizer.Generate(session, 0, 0, new GenerationRequest(810, 50, Shiny: false,
            Nature: null, Ability: null, Ball: null, Moves: null, Form: 0, AllowUnsupportedSpecies: true));

        Output.WriteLine("gen8-on-um: " + outcome.Message);
        Assert.True(outcome.Success, outcome.Message);
        Assert.Contains("HaX", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("no guarantee", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(810, session.ReadEntity(0, 0).Species);
    }

    [Fact]
    public void LegendsArceusSpeciesForcedIntoSword()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(8);
        var legalizer = new LegalizerService();

        var outcome = legalizer.Generate(session, 0, 0, new GenerationRequest(899, 50, Shiny: false,
            Nature: null, Ability: null, Ball: null, Moves: null, Form: 0, AllowUnsupportedSpecies: true));

        Output.WriteLine("pla-on-sword: " + outcome.Message);
        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(899, session.ReadEntity(0, 0).Species);
    }

    [Fact]
    public void ForcedSpeciesSurvivesAppReadSurfacesAndSerialization()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        // Control: an untouched blank UM session must revalidate; if it does not,
        // serialization validation is not a meaningful bar for blanks and the
        // forced-species case is judged on the read surfaces alone.
        var blankValid = engine.Validate(session.Serialize());
        var legalizer = new LegalizerService();
        Assert.True(legalizer.Generate(session, 0, 0, new GenerationRequest(906, 50, Shiny: false,
            Nature: null, Ability: null, Ball: null, Moves: null, Form: 0, AllowUnsupportedSpecies: true)).Success);

        // Everything the editor calls after a placement must tolerate the out-of-table species.
        Assert.NotNull(session.ReadEntity(0, 0));
        Assert.NotNull(session.GetSpeciesTypes(906));
        Assert.NotNull(session.GetFormChoices(906));
        Assert.NotNull(session.GetAbilityChoices(906, 0));
        Assert.NotNull(session.GetBaseStats(906));
        Assert.NotEmpty(session.GetShowdownText(0, 0));

        Output.WriteLine($"blank revalidates: {blankValid}");
        if (blankValid)
        {
            var bytes = session.Serialize();
            Output.WriteLine($"forced revalidates: {engine.Validate(bytes)}");
            Assert.True(engine.Validate(bytes));
        }
    }

    [Fact]
    public void ForcedSpeciesIntoPartyAppends()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        var legalizer = new LegalizerService();

        var outcome = legalizer.Generate(session, -1, 0, new GenerationRequest(810, 50, Shiny: false,
            Nature: null, Ability: null, Ball: null, Moves: null, Form: 0, AllowUnsupportedSpecies: true));

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(810, session.ReadEntity(-1, 0).Species);
    }

    [Fact]
    public void BankDepositOfForcedSpeciesProducesBytes()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        var legalizer = new LegalizerService();

        Assert.Null(legalizer.GenerateDataFromShowdown(session, "Grookey\nLevel: 50"));
        var generated = legalizer.GenerateDataFromShowdown(session, "Grookey\nLevel: 50", allowUnsupportedSpecies: true);

        Assert.NotNull(generated);
        Assert.Equal(810, generated!.Info.Species);
        Assert.NotEmpty(generated.Data);
    }

    [Fact]
    public void RealSaveWithForcedSpeciesStillValidates()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", "SM Project 802.main");

        var engine = new SaveEngine();
        var bytes = File.ReadAllBytes(path);
        Assert.True(engine.Validate(bytes), "corpus save must validate before we touch it");
        using var session = engine.OpenSession(bytes);
        var legalizer = new LegalizerService();

        var outcome = legalizer.Generate(session, 0, 0, new GenerationRequest(810, 50, Shiny: false,
            Nature: null, Ability: null, Ball: null, Moves: null, Form: 0, AllowUnsupportedSpecies: true));

        Assert.True(outcome.Success, outcome.Message);
        var rewritten = session.Serialize();
        var valid = engine.Validate(rewritten);
        Output.WriteLine($"real save revalidates with forced species: {valid}");
        Assert.True(valid);
        using var reopened = engine.OpenSession(rewritten);
        Assert.Equal(810, reopened.ReadEntity(0, 0).Species);
    }
}
