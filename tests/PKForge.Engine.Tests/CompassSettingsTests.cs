using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Compass keeps the vanilla S/V save format, so detection rides on the romhack's
/// marker block: vanilla and non-Gen9 saves must never report Compass settings, and
/// the surface must stay empty instead of throwing when the blocks are absent.
/// </summary>
public sealed class CompassSettingsTests
{
    private static string? LocalArtifact(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", file);
        return path is not null && File.Exists(path) ? path : null;
    }

    [Fact]
    public void VanillaScarletVioletDoesNotReportCompassSettings()
    {
        using var session = new SaveEngine().OpenBlankSession(9);

        Assert.False(session.SupportsCompassSettings);
        Assert.Empty(session.GetCompassSettings());
        Assert.False(session.SetCompassSetting("levelcap", 50));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void NonGen9SavesNeverReportCompassSettings(int generation)
    {
        using var session = new SaveEngine().OpenBlankSession(generation);

        Assert.False(session.SupportsCompassSettings);
        Assert.Empty(session.GetCompassSettings());
    }

    [Fact]
    public void PreTwoOneCompassSavesAreLabeledButOfferNoSettings()
    {
        // Ground truth from the owner's device: a pre-2.1 Compass save carries the
        // all-versions TrainerSeed marker but none of the v2.1 setting blocks.
        var path = LocalArtifact("compass-pre21-main");
        if (path is null) return;

        var engine = new SaveEngine();
        var description = engine.TryDescribe(File.ReadAllBytes(path));

        Assert.NotNull(description);
        Assert.Equal("Compass", description!.GameName);
        using var session = engine.OpenSession(File.ReadAllBytes(path));
        Assert.False(session.SupportsCompassSettings);
        Assert.Empty(session.GetCompassSettings());
    }
}
