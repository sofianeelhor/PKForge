using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The transfer primitive behind bank-to-game and game-to-game: a loose entity from an
/// older generation must convert and land in the open save via ImportSlot. This is the
/// exact path TransferService drives; any format that fails to convert breaks transfers.
/// </summary>
public sealed class CrossFormatImportTests
{
    private static string CorpusPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", file);
    }

    [Fact]
    public void OlderGenerationEntitiesConvertIntoTheOpenSave()
    {
        var root = Path.Combine(Path.GetDirectoryName(CorpusPath("SM Project 802.main"))!, "..");
        var entities = Directory.EnumerateFiles(Path.GetFullPath(root), "*.*", SearchOption.AllDirectories)
            // Mainline formats only (pk1-6). LGPE (.pb7) is a separate transfer lineage:
            // the engine intentionally cannot convert it to mainline, and transfers from
            // it correctly report "cannot enter this game's format" instead of corrupting.
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetExtension(f), @"^\.pk[1-6]$"))
            .ToList();
        Assert.NotEmpty(entities);

        var engine = new SaveEngine();
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));
        var emptySlots = new Queue<(int Box, int Slot)>(
            session.Snapshot.Slots.Where(s => s.Species is null).Select(s => (s.Box, s.Slot)));
        Assert.NotEmpty(emptySlots);

        var failures = new List<string>();
        var imported = 0;
        foreach (var file in entities)
        {
            if (emptySlots.Count == 0) break;
            var name = Path.GetFileName(file);
            var bytes = File.ReadAllBytes(file);
            var (box, slot) = emptySlots.Dequeue();
            try
            {
                if (!session.ImportSlot(box, slot, bytes))
                {
                    failures.Add($"convert returned false: {name}");
                    continue;
                }
                var detail = session.ReadEntity(box, slot);
                if (detail.IsEmpty)
                {
                    failures.Add($"empty after import: {name}");
                    continue;
                }
                imported++;
            }
            catch (Exception error)
            {
                failures.Add($"THREW {error.GetType().Name} on {name}");
            }
        }

        Assert.True(failures.Count == 0 && imported > 0,
            $"Imported {imported}/{entities.Count}. Failures:\n  " + string.Join("\n  ", failures.Take(20)));
    }
}
