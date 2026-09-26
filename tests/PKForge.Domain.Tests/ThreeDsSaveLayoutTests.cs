using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>Synthetic Citra-family trees (Azahar, Lime3DS, Citra MMJ) walked through the shared layout.</summary>
public sealed class ThreeDsSaveLayoutTests : IDisposable
{
    private const string Id0 = "0123456789abcdef0123456789abcdef";
    private const string Id1 = "fedcba9876543210fedcba9876543210";
    private const string SunTitle = "00164800";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "pkforge-3ds-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    private static IEnumerable<TreeEntry<string>> List(string dir) =>
        Directory.EnumerateFileSystemEntries(dir).Select(path =>
            new TreeEntry<string>(Path.GetFileName(path), Directory.Exists(path), path));

    /// <summary>Creates &lt;base&gt;/&lt;prefix&gt;/Nintendo 3DS/... with a main save and returns the grant root.</summary>
    private string Build(string prefix, string title = SunTitle)
    {
        var slot = Path.Combine(_temp, prefix, "Nintendo 3DS", Id0, Id1, "title", "00040000", title, "data", "00000001");
        Directory.CreateDirectory(slot);
        File.WriteAllBytes(Path.Combine(slot, "main"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(slot, "..", "00000001.metadata"), [0]);
        return _temp;
    }

    private static List<ThreeDsSaveHit<string>> Scan(string root)
    {
        var n3ds = ThreeDsSaveLayout.FindNintendoThreeDs(root, Path.GetFileName(root), List);
        return n3ds is null ? [] : ThreeDsSaveLayout.EnumerateMainSaves(n3ds, List).ToList();
    }

    private static List<ThreeDsSaveHit<string>> Find(string root) =>
        ThreeDsSaveLayout.FindMainSaves(root, Path.GetFileName(root), List).ToList();

    [Theory]
    // A parent of the Azahar folder (found by the bounded search).
    [InlineData("Emulation/azahar/sdmc", "Emulation")]
    // Below "Nintendo 3DS": ID0, ID1, title, 00040000, one game, its data folder.
    [InlineData("user/sdmc", "user/sdmc/Nintendo 3DS/" + Id0)]
    [InlineData("user/sdmc", "user/sdmc/Nintendo 3DS/" + Id0 + "/" + Id1)]
    [InlineData("user/sdmc", "user/sdmc/Nintendo 3DS/" + Id0 + "/" + Id1 + "/title")]
    [InlineData("user/sdmc", "user/sdmc/Nintendo 3DS/" + Id0 + "/" + Id1 + "/title/00040000")]
    [InlineData("user/sdmc", "user/sdmc/Nintendo 3DS/" + Id0 + "/" + Id1 + "/title/00040000/" + SunTitle)]
    public void FindsMainSaveFromParentAndDeeperGrants(string layout, string grant)
    {
        Build(layout);
        var hit = Assert.Single(Find(Path.Combine(_temp, grant)));
        Assert.Equal("main", Path.GetFileName(hit.File));
        Assert.Equal(SunTitle, hit.TitleId);
    }

    [Fact]
    public void FolderNamesMatchInAnyCase()
    {
        var slot = Path.Combine(_temp, "user", "SDMC", "nintendo 3ds", Id0, Id1, "Title", "00040000", SunTitle, "Data", "00000001");
        Directory.CreateDirectory(slot);
        File.WriteAllBytes(Path.Combine(slot, "main"), [1]);
        Assert.Equal(SunTitle, Assert.Single(Find(Path.Combine(_temp, "user"))).TitleId);
    }

    [Fact]
    public void AnUnrelatedFolderFindsNothing()
    {
        Directory.CreateDirectory(Path.Combine(_temp, "music", "albums"));
        Assert.Empty(Find(Path.Combine(_temp, "music")));
    }

    [Theory]
    // Azahar / Lime3DS: user-picked folder containing sdmc/.
    [InlineData("user/sdmc", "user")]
    // Citra MMJ scoped: grant of Android/data/org.citra.emu/files.
    [InlineData("files/citra-emu/sdmc", "files")]
    // Citra MMJ: grant of the citra-emu user folder (legacy /sdcard/citra-emu or scoped).
    [InlineData("files/citra-emu/sdmc", "files/citra-emu")]
    // Citra MMJ: grant of the package folder Android/data/org.citra.emu.
    [InlineData("org.citra.emu/files/citra-emu/sdmc", "org.citra.emu")]
    // Any fork: grant of sdmc, or of Nintendo 3DS itself.
    [InlineData("citra-emu/sdmc", "citra-emu/sdmc")]
    [InlineData("user/sdmc", "user/sdmc/Nintendo 3DS")]
    public void FindsMainSaveFromEveryGrantLevel(string layout, string grant)
    {
        Build(layout);
        var hits = Scan(Path.Combine(_temp, grant));

        var hit = Assert.Single(hits);
        Assert.Equal(SunTitle, hit.TitleId);
        Assert.Equal("main", Path.GetFileName(hit.File));
    }

    [Fact]
    public void FindsEveryTitleAndSkipsNonRetailAndMissingSaves()
    {
        Build("user/sdmc", "00164800");
        Build("user/sdmc", "00175E00");
        // DLC/update high IDs and a title without a main save are ignored.
        Directory.CreateDirectory(Path.Combine(_temp, "user/sdmc/Nintendo 3DS", Id0, Id1, "title", "0004000e", "00164800", "content"));
        Directory.CreateDirectory(Path.Combine(_temp, "user/sdmc/Nintendo 3DS", Id0, Id1, "title", "00040000", "0011C400", "data", "00000001"));
        // Extdata lives beside title/ and is never treated as a save.
        Directory.CreateDirectory(Path.Combine(_temp, "user/sdmc/Nintendo 3DS", Id0, Id1, "extdata", "00000000", "00000a16"));

        var titles = Scan(Path.Combine(_temp, "user")).Select(x => x.TitleId).Order().ToArray();

        Assert.Equal(["00164800", "00175E00"], titles);
    }

    [Fact]
    public void UnrelatedFolderYieldsNothing()
    {
        Directory.CreateDirectory(Path.Combine(_temp, "RetroArch", "saves"));
        Assert.Empty(Scan(_temp));
    }

    [Theory]
    [InlineData("primary:Android/data/org.citra.emu/files", "files")]
    [InlineData("root/citra-emu", "citra-emu")]
    [InlineData("primary:citra-emu/sdmc/Nintendo 3DS", "Nintendo 3DS")]
    [InlineData("primary:", "")]
    public void LastSegmentOfSafDocumentIds(string docId, string expected) =>
        Assert.Equal(expected, ThreeDsSaveLayout.LastSegment(docId));

    [Fact]
    public void CitraMmjIsNandStructuredLikeAzahar() =>
        Assert.True(EmulatorSaveHeuristics.RequiresExtraCare(EmulatorKind.CitraMmj));

    [Fact]
    public void PersistedEnumValuesAreStable()
    {
        Assert.Equal(2, (int)EmulatorKind.Azahar);
        Assert.Equal(9, (int)EmulatorKind.CitraMmj);
    }
}
