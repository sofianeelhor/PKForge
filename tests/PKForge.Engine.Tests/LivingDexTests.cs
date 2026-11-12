using PKForge.Engine;
using PKHeX.Core;
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

    // GitHub #79: Legends: Arceus and BDSP are generation 8 too, and used to be handed the
    // Sword/Shield file. Each game family now has its own bundle in its own format.
    [Theory]
    [InlineData(GameVersion.PLA, typeof(PA8), 242)]
    [InlineData(GameVersion.BD, typeof(PB8), 493)]
    [InlineData(GameVersion.SP, typeof(PB8), 493)]
    [InlineData(GameVersion.GP, typeof(PB7), 153)]
    [InlineData(GameVersion.ZA, typeof(PA9), 364)]
    public void EachGameFamilyGetsAFullLegalDexInItsOwnFormat(GameVersion version, Type format, int expected)
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(version);
        var save = session.SaveFile;
        Assert.Equal(format, save.PKMType);
        var name = session.LivingDexBundle;
        Assert.NotNull(name);
        var placed = session.PlaceLivingDex(File.ReadAllBytes(RepoPath(name!)));
        Assert.Equal(expected, placed);

        var species = new HashSet<ushort>();
        for (var i = 0; i < placed; i++)
        {
            var mon = save.GetBoxSlotAtIndex(i / save.BoxSlotCount, i % save.BoxSlotCount);
            Assert.IsType(format, mon);
            Assert.True(species.Add(mon.Species), $"duplicate species {mon.Species}");
            var legality = new LegalityAnalysis(mon);
            Assert.True(legality.Valid, $"{(Species)mon.Species}: {legality.Report()}");
        }
    }

    [Fact]
    public void Gen8FamiliesPickTheirOwnFile()
    {
        var engine = new SaveEngine();
        using var swsh = engine.OpenBlankSession(GameVersion.SW);
        using var arceus = engine.OpenBlankSession(GameVersion.PLA);
        using var bdsp = engine.OpenBlankSession(GameVersion.BD);
        Assert.Equal("dex-g8.bin.gz", swsh.LivingDexBundle);
        Assert.Equal("dex-gen8a.bin.gz", arceus.LivingDexBundle);
        Assert.Equal("dex-gen8b.bin.gz", bdsp.LivingDexBundle);
        foreach (var family in LivingDexBundles.Families)
            Assert.True(File.Exists(RepoPath(family.File)), family.File);
    }

    [Theory]
    [InlineData(GameVersion.PLA, "dex-g8.bin.gz")]   // the #79 bug: PK8 into a PA8 save
    [InlineData(GameVersion.BD, "dex-g8.bin.gz")]    // PK8 and PB8 are the same size
    [InlineData(GameVersion.SW, "dex-gen8a.bin.gz")]
    [InlineData(GameVersion.GP, "dex-g7.bin.gz")]    // PK7 and PB7 are the same size
    [InlineData(GameVersion.UM, "dex-gen7b.bin.gz")]
    [InlineData(GameVersion.ZA, "dex-g9.bin.gz")]
    [InlineData(GameVersion.BD, "dex-gen8a.bin.gz")]
    public void MismatchedBundleIsRefusedAndWritesNothing(GameVersion version, string file)
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(version);
        var before = session.SaveFile.Data.ToArray();
        var error = Assert.Throws<InvalidOperationException>(() => session.PlaceLivingDex(File.ReadAllBytes(RepoPath(file))));
        Assert.Contains("Nothing was written", error.Message);
        Assert.Equal(before, session.SaveFile.Data.ToArray());

        var outcome = new LegalizerService().FillLivingDex(session, File.ReadAllBytes(RepoPath(file)));
        Assert.False(outcome.Success);
        Assert.Contains("Nothing was written", outcome.Message);
        Assert.Equal(before, session.SaveFile.Data.ToArray());
    }

    [Fact]
    public void ForeignFormatInsideAMatchingHeaderIsRefusedWhole()
    {
        // A bundle that claims Legends: Arceus but carries a Sword/Shield Pokémon after a
        // valid one: refused before the first write, nothing half-applied.
        using var swsh = (SaveEngineSession)new SaveEngine().OpenBlankSession(GameVersion.SW);
        swsh.PlaceLivingDex(File.ReadAllBytes(RepoPath("dex-g8.bin.gz")));
        using var arceus = (SaveEngineSession)new SaveEngine().OpenBlankSession(GameVersion.PLA);
        arceus.PlaceLivingDex(File.ReadAllBytes(RepoPath("dex-gen8a.bin.gz")));
        var forged = LivingDexBundles.Write(EntityContext.Gen8a,
            [arceus.SaveFile.GetBoxSlotAtIndex(0, 0), swsh.SaveFile.GetBoxSlotAtIndex(0, 0)]);

        using var target = (SaveEngineSession)new SaveEngine().OpenBlankSession(GameVersion.PLA);
        var before = target.SaveFile.Data.ToArray();
        Assert.Throws<InvalidOperationException>(() => target.PlaceLivingDex(forged));
        Assert.Equal(before, target.SaveFile.Data.ToArray());
    }

    [Theory]
    [InlineData(GameVersion.SW, 400)]
    [InlineData(GameVersion.VL, 400)]
    public void Gen8And9MainBundlesStillFill(GameVersion version, int atLeast)
    {
        using var session = new SaveEngine().OpenBlankSession(version);
        var placed = session.PlaceLivingDex(File.ReadAllBytes(RepoPath(session.LivingDexBundle!)));
        Assert.True(placed >= atLeast, $"placed {placed}");
    }
}
