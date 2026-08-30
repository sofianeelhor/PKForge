using System.Diagnostics;
using PKForge.Domain;
using PKForge.Engine;
using Xunit;
using Xunit.Abstractions;

namespace PKForge.Engine.Tests;

public sealed class Gen89SweepTests
{
    public Gen89SweepTests(ITestOutputHelper output) => Output = output;
    private ITestOutputHelper Output { get; }

    [Theory]
    [InlineData(8, 810)]  // Grookey, starter
    [InlineData(8, 887)]  // Dragapult, ordinary
    [InlineData(8, 888)]  // Zacian, box legendary
    [InlineData(8, 890)]  // Eternatus
    [InlineData(8, 891)]  // Kubfu, DLC
    [InlineData(8, 893)]  // Zarude, gift-only mythical
    [InlineData(8, 898)]  // Calyrex
    [InlineData(9, 906)]  // Sprigatito, starter
    [InlineData(9, 987)]  // Flutter Mane, paradox
    [InlineData(9, 1007)] // Walking Wake, DLC paradox
    [InlineData(9, 1014)] // Ogerpon, DLC
    [InlineData(9, 1017)] // Archaludon? no: 1017 = Archaludon (Indigo Disk)
    [InlineData(9, 1024)] // Terapagos, DLC legendary
    [InlineData(9, 1025)] // Pecharunt, gift-only mythical
    public void Sweep(int generation, int species)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(generation);
        var legalizer = new LegalizerService();
        var watch = Stopwatch.StartNew();
        var outcome = legalizer.Generate(session, 0, 0,
            new GenerationRequest(species, 50, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null, Form: 0));
        watch.Stop();
        var ot = outcome.Success ? session.ReadEntity(0, 0).OriginalTrainer : "-";
        Output.WriteLine($"g{generation} species {species}: {watch.ElapsedMilliseconds}ms success={outcome.Success} ot={ot} msg={outcome.Message}");
        Assert.True(outcome.Success, outcome.Message);
    }

    [Theory]
    [InlineData(899)] // Wyrdeer
    [InlineData(905)] // Enamorus
    public void LegendsArceusSpeciesCannotEnterSwordShield(int species)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(8);
        var legalizer = new LegalizerService();
        var outcome = legalizer.Generate(session, 0, 0,
            new GenerationRequest(species, 50, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null, Form: 0));
        Output.WriteLine($"g8 species {species}: success={outcome.Success} msg={outcome.Message}");
        // PLA natives (899-905) never legally enter Sword/Shield: rejected honestly,
        // and the dex picker never offers them there (SwSh MaxSpeciesID = 898).
        Assert.False(outcome.Success);
        Assert.Contains("cannot store", outcome.Message, StringComparison.Ordinal);
    }
}
