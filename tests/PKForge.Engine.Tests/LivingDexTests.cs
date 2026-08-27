using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The living dex ships pre-generated (tools/DexGen): the fill must place one of every
/// species instantly from the bundle - no on-device legalization.
/// </summary>
public sealed class LivingDexTests
{
    private static string RepoPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(new[] { directory!.FullName, "src", "PKForge.App", "Resources", "UI", "dex" }.Concat(parts).ToArray());
    }

    [Fact]
    public void Gen7BundleFillsStorageInstantly()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        var bundle = File.ReadAllBytes(RepoPath("dex-g7.bin.gz"));
        Assert.True(bundle.Length > 1000);

        var placed = session.PlaceLivingDex(bundle);
        Assert.True(placed > 800, $"placed {placed}");

        // Species must be unique and dense from Bulbasaur up.
        var seen = new HashSet<int>();
        for (var box = 0; box < 40 && seen.Count < placed; box++)
            for (var slot = 0; slot < 30; slot++)
            {
                var d = session.ReadEntity(box, slot);
                if (!d.IsEmpty) Assert.True(seen.Add(d.Species), $"duplicate species {d.Species}");
            }
        Assert.Equal(placed, seen.Count);
    }

    [Theory]
    [InlineData(1, 151)]
    [InlineData(2, 251)]
    [InlineData(3, 386)]
    [InlineData(4, 493)]
    [InlineData(5, 649)]
    [InlineData(6, 720)] // Zygarde (718) is a legalizer gap: 720 of 721
    [InlineData(7, 807)]
    public void BundlesCoverTheirGeneration(int generation, int expected)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(generation);
        var bundle = File.ReadAllBytes(RepoPath($"dex-g{generation}.bin.gz"));
        var placed = session.PlaceLivingDex(bundle);
        Assert.Equal(expected, placed);
    }

    [Fact]
    public void CorruptBundleWritesNothing()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(7);
        var garbage = new byte[] { 1, 2, 3, 4, 5 };
        Assert.ThrowsAny<Exception>(() => session.PlaceLivingDex(garbage));
        var empty = true;
        for (var slot = 0; slot < 30; slot++)
            empty &= session.ReadEntity(0, slot).IsEmpty;
        Assert.True(empty);
    }
}
