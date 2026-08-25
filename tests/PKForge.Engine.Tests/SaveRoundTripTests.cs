using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The single most important safety guard (§12.1): opening then serializing an
/// untouched save must produce byte-identical output. Corpus comes from the
/// pristine pinned PKHeX submodule's own test data.
/// </summary>
public sealed class SaveRoundTripTests
{
    private static string CorpusPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", file);
        Assert.True(File.Exists(path), $"Corpus file missing: {path}");
        return path;
    }

    private static byte[] Gen7Save() => File.ReadAllBytes(CorpusPath("SM Project 802.main"));

    [Fact]
    public void UnchangedSaveRoundTripsByteIdentical()
    {
        var bytes = Gen7Save();
        using var session = new SaveEngineSession(bytes);

        var written = session.Serialize();

        Assert.True(written.Span.SequenceEqual(bytes),
            "Open→serialize with no edits changed bytes; save writes are not safe.");
    }

    [Fact]
    public void SessionParsesExpectedGen7Shape()
    {
        using var session = new SaveEngineSession(Gen7Save());
        Assert.Equal(7, session.Snapshot.Generation);
        Assert.NotEmpty(session.Snapshot.Slots);
    }

    [Fact]
    public void EditThenSerializeRevalidatesAndPersistsField()
    {
        var bytes = Gen7Save();
        int box, slot;
        using (var session = new SaveEngineSession(bytes))
        {
            var occupied = session.Snapshot.Slots.First(s => s.Species is not null);
            box = occupied.Box;
            slot = occupied.Slot;

            session.ApplyEdit(box, slot, new EntityEdit(Nickname: "PKFORGE"));
            var candidate = session.Serialize();

            // Candidate must re-validate through the same engine before any write is allowed.
            Assert.True(new SaveEngine().Validate(candidate));

            using var reopened = new SaveEngineSession(candidate);
            Assert.Equal("PKFORGE", reopened.ReadEntity(box, slot).Nickname);
        }
    }

    [Fact]
    public void LegalityAnalysisProducesReportForOccupiedSlot()
    {
        using var session = new SaveEngineSession(Gen7Save());
        var occupied = session.Snapshot.Slots.First(s => s.Species is not null);

        var report = new LegalityService().Analyze(session, occupied.Box, occupied.Slot);

        Assert.NotNull(report);
        Assert.NotEmpty(report.Lines);
    }
}
