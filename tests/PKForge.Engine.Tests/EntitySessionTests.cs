using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The bank-editing path: a loose mon's bytes must open into a standalone editing
/// session (its own throwaway save), be editable, and export back. This reproduces
/// exactly what BankEntryEditor does, without a device.
/// </summary>
public sealed class EntitySessionTests
{
    private static string CorpusPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", file);
    }

    private static (byte[] Bytes, string Name) FirstMonBytes()
    {
        var engine = new SaveEngine();
        using var session = new SaveEngineSession(File.ReadAllBytes(CorpusPath("SM Project 802.main")));
        var slot = session.Snapshot.Slots.First(s => s.Species is not null);
        var export = session.ExportSlot(slot.Box, slot.Slot);
        return (export.Data, export.FileName);
    }

    [Fact]
    public void OpenEntitySessionEditsALooseMon()
    {
        var (bytes, _) = FirstMonBytes();
        var engine = new SaveEngine();

        using var session = engine.OpenEntitySession(bytes, "test");
        Assert.NotNull(session); // must not silently fail - this is the bank "Edit" bug

        var before = session!.ReadEntity(0, 0);
        Assert.False(before.IsEmpty);
        Assert.NotNull(before.Stats);
        Assert.Equal(6, before.Stats!.Count);

        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "EDITED", Level: 50, IsShiny: true));
        var after = session.ReadEntity(0, 0);
        Assert.Equal("EDITED", after.Nickname);
        Assert.Equal(50, after.Level);
        Assert.True(after.IsShiny);

        // Export must round-trip back into something the engine can re-describe (bank write-back).
        var export = session.ExportSlot(0, 0);
        var info = engine.TryDescribeEntity(export.Data, "test");
        Assert.NotNull(info);
        Assert.Equal(after.Species, info!.Species);
    }
}
