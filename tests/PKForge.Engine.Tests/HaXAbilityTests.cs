using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// HaX mode's contract: an off-table ability (Drought on a mon that cannot have it) is
/// written into the entity's stored ability field and survives every round trip PKForge
/// performs. Known game behavior, same as PKHeX: Gen 4/5 derive the ability from the
/// ability SLOT on evolution, so an off-table ability reverts then; the stored ID itself
/// is what we hold. Gen 6+ store the raw id and it sticks forever.
/// </summary>
public sealed class HaXAbilityTests
{
    private const int Drought = 70;

    private static string FirstPk4()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var root = Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests");
        return Directory.EnumerateFiles(root, "*.pk4", SearchOption.AllDirectories).First();
    }

    [Fact]
    public void OffTableAbilitySurvivesEntityRoundTrip()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenEntitySession(File.ReadAllBytes(FirstPk4()), "hax");
        Assert.NotNull(session);

        var before = session!.ReadEntity(0, 0);
        Assert.NotEqual(Drought, before.Ability);

        // The HaX edit: an ability this species cannot have.
        session.ApplyEdit(0, 0, new EntityEdit(Ability: Drought));
        Assert.Equal(Drought, session.ReadEntity(0, 0).Ability);

        // Export (what the bank, transfers and .pk files carry) and re-import.
        var exported = session.ExportSlot(0, 0);
        using var reopened = engine.OpenEntitySession(exported.Data, "hax");
        Assert.NotNull(reopened);
        Assert.Equal(Drought, reopened!.ReadEntity(0, 0).Ability);
    }

    [Fact]
    public void OffTableAbilitySurvivesSaveReloadOnGen7()
    {
        var engine = new SaveEngine();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var savePath = Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", "SM Project 802.main");

        using (var session = new SaveEngineSession(File.ReadAllBytes(savePath)))
        {
            var slot = session.Snapshot.Slots.First(s => s.Box >= 0 && s.Species is not null);
            session.ApplyEdit(slot.Box, slot.Slot, new EntityEdit(Ability: Drought));
            Assert.Equal(Drought, session.ReadEntity(slot.Box, slot.Slot).Ability);
            File.WriteAllBytes("/tmp/hax-save.main", session.Serialize().ToArray());
        }

        // Reload from disk bytes: the save file itself must carry the edit.
        using (var reloaded = new SaveEngineSession(File.ReadAllBytes("/tmp/hax-save.main")))
        {
            var slot = reloaded.Snapshot.Slots.First(s => s.Box >= 0 && s.Species is not null);
            Assert.Equal(Drought, reloaded.ReadEntity(slot.Box, slot.Slot).Ability);
        }
    }
}
