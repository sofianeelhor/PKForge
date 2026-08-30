using PKHeX.Core;
using PKForge.Domain;
using PKForge.Engine;
using Xunit;
using Xunit.Abstractions;

namespace PKForge.Engine.Tests;

public sealed class Gen6MythicalTests
{
    public Gen6MythicalTests(ITestOutputHelper output) => Output = output;
    private ITestOutputHelper Output { get; }

    [Theory]
    [InlineData(GameVersion.X, 719)]
    [InlineData(GameVersion.Y, 719)] // Diancie
    [InlineData(GameVersion.X, 720)]
    [InlineData(GameVersion.Y, 720)] // Hoopa
    [InlineData(GameVersion.X, 721)]
    [InlineData(GameVersion.Y, 721)] // Volcanion
    [InlineData(GameVersion.AS, 719)] // ORAS control
    public void GiftOnlyMythicalsOnXYORAS(GameVersion version, int species)
    {
        var blank = BlankSaveFile.Get(version, "PKForge", LanguageID.English);
        using var session = new SaveEngineSession(blank, version.ToString());
        var legalizer = new LegalizerService();
        var outcome = legalizer.Generate(session, 0, 0,
            new GenerationRequest(species, 50, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null, Form: 0));
        Output.WriteLine($"{version} species {species}: success={outcome.Success} msg={outcome.Message}");
        if (outcome.Success)
            Output.WriteLine("  OT=" + session.ReadEntity(0, 0).OriginalTrainer);
        Assert.True(outcome.Success, outcome.Message);
    }
}
