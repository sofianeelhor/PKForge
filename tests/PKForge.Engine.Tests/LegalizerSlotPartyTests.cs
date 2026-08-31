using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Legalize must treat the party as first-class storage: the box accessors go out of
/// range for box -1, which used to abort the mutation behind the loading overlay.
/// </summary>
public sealed class LegalizerSlotPartyTests
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
    public void LegalizeSlotRepairsAPartyMon()
    {
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));
        var before = session.ReadEntity(-1, 0);
        Assert.False(before.IsEmpty);

        // An impossible ball makes the mon illegal without touching its identity.
        session.ApplyEdit(-1, 0, new EntityEdit(Ball: 999));
        var outcome = new LegalizerService().LegalizeSlot(session, -1, 0);

        Assert.True(outcome.Success);
        var after = session.ReadEntity(-1, 0);
        Assert.False(after.IsEmpty);
        Assert.Equal(before.Species, after.Species);
        Assert.NotEqual(999 & 0xFF, after.Ball);
    }

    [Fact]
    public void LegalizeSlotOnAnEmptyPartySlotReportsEmptyInsteadOfThrowing()
    {
        using var session = new SaveEngine().OpenBlankSession(7);

        var outcome = new LegalizerService().LegalizeSlot(session, -1, 0);

        Assert.False(outcome.Success);
        Assert.Equal("Empty slot.", outcome.Message);
    }
}
