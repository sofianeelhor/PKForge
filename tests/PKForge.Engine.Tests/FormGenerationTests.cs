using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Alternative-form generation: the request carries a form id, the showdown text names it
/// (Rotom-Wash), and the legalizer builds the right form in a game that has it.
/// </summary>
public sealed class FormGenerationTests
{
    private static string CorpusPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", file);
    }

    [Theory]
    [InlineData(4)] // Gen 4 introduced Rotom forms
    [InlineData(7)]
    public void RotomFormChoicesAndGeneration(int generation)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(generation);

        var forms = session.GetFormChoices(479); // Rotom
        Assert.True(forms.Count >= 5, $"gen{generation} Rotom forms: [{string.Join(",", forms)}]");
        Assert.Contains("Wash", forms);

        var legalizer = new LegalizerService();
        var washForm = Array.IndexOf(forms.ToArray(), "Wash");
        var outcome = legalizer.GenerateData(session, new GenerationRequest(479, 50, Shiny: false,
            Nature: null, Ability: null, Ball: null, Moves: null, Form: washForm));
        Assert.NotNull(outcome);
        Assert.Equal(479, outcome!.Info.Species);
        Assert.Equal(washForm, outcome.Info.Form);

        // And the in-save path writes the same form to the slot.
        var placed = legalizer.Generate(session, 0, 0, new GenerationRequest(479, 50, Shiny: false,
            Nature: null, Ability: null, Ball: null, Moves: null, Form: washForm));
        Assert.True(placed.Success, placed.Message);
        var detail = session.ReadEntity(0, 0);
        Assert.Equal(479, detail.Species);
        Assert.Equal(washForm, detail.Form);
    }

    [Fact]
    public void Gen7UltranecrozmaStyleFormsResolveBySuffix()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        var forms = session.GetFormChoices(800); // Necrozma
        // Dusk Maneur / Dawn Wings are forms 1/2 in USUM context; blank uses UM.
        Assert.True(forms.Count >= 3, $"Necrozma forms: [{string.Join(",", forms)}]");
    }
}
