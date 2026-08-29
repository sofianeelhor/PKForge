using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Feeds every loose .pk* entity in the pinned PKHeX test corpus through the bank-edit
/// path (OpenEntitySession → edit → export). Any format that throws or returns null is
/// the "pressing Edit does nothing" bug for that generation.
/// </summary>
public sealed class EntitySessionCorpusTests
{
    private static string TestsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests");
    }

    [Fact]
    public void EveryCorpusEntityOpensForEditing()
    {
        var root = TestsRoot();
        Assert.True(Directory.Exists(root), $"Corpus root missing: {root}");

        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetExtension(f), @"^\.(pk[1-9]|pb7|pb8|pa8)$"))
            .ToList();
        Assert.NotEmpty(files);

        var engine = new SaveEngine();
        var failures = new List<string>();
        var opened = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch { continue; }

            try
            {
                var session = engine.OpenEntitySession(bytes, "test");
                if (session is null)
                {
                    // null only when TryDescribeEntity also can't read it - the same probe used at deposit.
                    if (engine.TryDescribeEntity(bytes, "x") is not null)
                        failures.Add($"NULL but describable: {name}");
                    continue;
                }
                using (session)
                {
                    var d = session.ReadEntity(0, 0);
                    if (d.IsEmpty) { failures.Add($"EMPTY after open: {name}"); continue; }
                    session.ApplyEdit(0, 0, new EntityEdit(Level: 50));

                    // Met/origin block must also read and edit without throwing, every gen.
                    _ = session.GetMetInfo(0, 0);
                    _ = session.GetLocationChoices(0, 0, egg: false);
                    _ = session.GetVersionChoices();
                    _ = session.GetLanguageChoices(0, 0);
                    session.ApplyMetEdit(0, 0, new MetEdit(MetLevel: 5, MetLocation: 1));
                    _ = session.GetMetInfo(0, 0);

                    // Potential block (Tera / Hyper Training / ability slot) is gen-gated:
                    // read always; edit only the surfaces the format reports as supported.
                    var p = session.GetPotential(0, 0);
                    if (p.SupportsTera && !p.TeraLocked)
                        session.ApplyPotentialEdit(0, 0, new PotentialEdit(TeraType: 0));
                    if (p.SupportsHyperTrain)
                        session.ApplyPotentialEdit(0, 0, new PotentialEdit(HyperTrained: [true, false, false, false, false, false]));
                    if (p.SupportsAbilitySlot)
                        session.ApplyPotentialEdit(0, 0, new PotentialEdit(AbilitySlot: 0));
                    _ = session.GetPotential(0, 0);

                    // Awards are also format-gated and must be inspectable for every
                    // loose entity accepted by the bank editor.
                    _ = session.GetPokerus(0, 0);
                    _ = session.GetRibbons(0, 0);

                    _ = session.ExportSlot(0, 0);
                    opened++;
                }
            }
            catch (Exception error)
            {
                failures.Add($"THREW {error.GetType().Name} ({error.Message}) on {name}");
            }
        }

        Assert.True(failures.Count == 0,
            $"Opened {opened}/{files.Count}. Failures:\n  " + string.Join("\n  ", failures.Take(25)));
    }
}
