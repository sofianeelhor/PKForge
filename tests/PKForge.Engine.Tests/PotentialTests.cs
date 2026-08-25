using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Focused coverage for the potential block (Tera type, Hyper Training, ability slot).
/// Gen gating is exercised over the whole corpus in EntitySessionCorpusTests; these
/// tests prove the actual mutations stick on formats that support them.
/// </summary>
public sealed class PotentialTests
{
    private static string TestsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests");
    }

    private static IReadOnlyList<string> CorpusFiles(string extension) =>
        Directory.EnumerateFiles(TestsRoot(), $"*{extension}", SearchOption.AllDirectories).ToList();

    [Fact]
    public void HyperTrainingEditsOnGen7Save()
    {
        var path = Path.Combine(TestsRoot(), "PKHeX.Core.Tests", "TestData", "SM Project 802.main");
        Assert.True(File.Exists(path), $"Corpus save missing: {path}");

        using var session = new SaveEngineSession(File.ReadAllBytes(path));
        var slot = session.Snapshot.Slots.First(s => s.Species is not null);

        var before = session.GetPotential(slot.Box, slot.Slot);
        Assert.True(before.SupportsHyperTrain);
        Assert.False(before.SupportsTera); // Gen VII has no Tera data.
        Assert.Equal(6, before.HyperTrained.Count);

        var trained = before.HyperTrained.ToArray();
        trained[0] = !trained[0];
        trained[5] = !trained[5];
        session.ApplyPotentialEdit(slot.Box, slot.Slot, new PotentialEdit(HyperTrained: trained));

        var after = session.GetPotential(slot.Box, slot.Slot);
        Assert.Equal(trained, after.HyperTrained);
    }

    [Fact]
    public void TeraTypeEditsOnGen9Entities()
    {
        var engine = new SaveEngine();
        var files = CorpusFiles(".pk9");
        Assert.NotEmpty(files);

        var tested = 0;
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            using var session = engine.OpenEntitySession(bytes, "test");
            if (session is null) continue;

            var before = session.GetPotential(0, 0);
            if (!before.SupportsTera || before.TeraLocked) continue;
            tested++;

            // Choices: 18 elemental types plus Stellar (id 99).
            var choices = session.GetTeraTypeChoices();
            Assert.Equal(19, choices.Count);
            Assert.Contains(choices, c => c is { Id: 99, Name: "Stellar" });

            var target = choices.First(c => c.Id != before.TeraType && c.Id != 99);
            session.ApplyPotentialEdit(0, 0, new PotentialEdit(TeraType: target.Id));

            var after = session.GetPotential(0, 0);
            Assert.Equal(target.Id, after.TeraType);
            Assert.Equal(target.Name, after.TeraTypeName);

            // Stellar round-trips through the magic value.
            session.ApplyPotentialEdit(0, 0, new PotentialEdit(TeraType: 99));
            Assert.Equal(99, session.GetPotential(0, 0).TeraType);
            break; // one representative entity is enough
        }
        Assert.True(tested > 0, "No editable Tera entity found in the .pk9 corpus.");
    }

    [Fact]
    public void AbilitySlotEditsOnThreeSlotSpecies()
    {
        var engine = new SaveEngine();
        var files = Directory.EnumerateFiles(TestsRoot(), "*.*", SearchOption.AllDirectories)
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetExtension(f), @"^\.(pk[5-9]|pb8|pa8)$"))
            .ToList();

        var found = false;
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            using var session = engine.OpenEntitySession(bytes, "test");
            if (session is null) continue;

            var before = session.GetPotential(0, 0);
            if (before.AbilitySlots.Count < 3) continue;
            found = true;

            Assert.True(before.SupportsAbilitySlot);
            Assert.StartsWith("Hidden", before.AbilitySlots[2].Name);

            session.ApplyPotentialEdit(0, 0, new PotentialEdit(AbilitySlot: 2));
            var after = session.GetPotential(0, 0);
            Assert.Equal(2, after.AbilitySlot);

            // The entity's ability must now be the hidden one.
            var detail = session.ReadEntity(0, 0);
            var abilityChoices = session.GetAbilityChoices(detail.Species, detail.Form);
            Assert.Equal(abilityChoices.Last(), detail.Ability);
            break;
        }
        Assert.True(found, "No three-ability-slot entity found in the corpus.");
    }
}
