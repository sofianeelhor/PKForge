using PKHeX.Core;
using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class Gen1ItemsAndNatureTests
{
    [Fact]
    public void Gen1BagOffersOnlyGen1ItemsIncludingRareCandy()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(1);

        var names = session.GetItemNames();
        Assert.True(names.Count > 150, $"Gen 1 item table too short: {names.Count}");
        Assert.Equal("Rare Candy", names[40]); // Gen 1 indexes Rare Candy at 0x28, not the modern id.

        var legal = session.GetPouchLegalItems("Items");
        Assert.Contains(40, legal);
        Assert.All(legal, id => Assert.True(id < names.Count && names[id].Length > 0, $"item {id} has no name"));
        Assert.All(legal, id => Assert.True(id <= 250, $"future-generation item leaked into Gen 1: {id}"));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void NatureEditSurvivesTheWritePath(int generation)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(generation);
        Assert.True(session.ImportSlot(0, 0, CreateSeedMon(generation)), $"gen {generation}: could not seed a mon");

        var before = session.ReadEntity(0, 0);
        var wanted = ((int)before.Nature + 7) % 25;
        session.ApplyEdit(0, 0, new EntityEdit(Nature: wanted));
        Assert.True(!session.ReadEntity(0, 0).IsEmpty && session.ReadEntity(0, 0).Nature == wanted,
            $"gen {generation}: nature edit did not apply in-memory");

        // Blank saves for most formats cannot be re-detected after Write (missing footers
        // and sectors PKHeX only fabricates for some games), so the full write-and-reopen
        // check runs where a blank round-trips offline: Gen 5, the user's reported game.
        if (generation != 5) return;

        var bytes = session.Serialize().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var reopened), $"gen {generation}: save must reopen");
        using var reloaded = new SaveEngineSession(reopened!, null);
        var after = reloaded.ReadEntity(0, 0);
        Assert.True(!after.IsEmpty && after.Nature == wanted,
            $"gen {generation}: nature {before.Nature}->{wanted} did not survive reload (got {after.Nature})");
    }

    [Fact]
    public void BatchNatureEditWorksOnPidDerivedFormats()
    {
        foreach (var generation in new[] { 3, 5 })
        {
            var engine = new SaveEngine();
            using var session = engine.OpenBlankSession(generation);
            Assert.True(session.ImportSlot(0, 0, CreateSeedMon(generation)));

            var touched = session.BatchApply(["Nature=12"], [0]);
            Assert.Equal(1, touched);
            var detail = session.ReadEntity(0, 0);
            Assert.True(!detail.IsEmpty && detail.Nature == 12,
                $"gen {generation}: batch nature edit reverted (got {detail.Nature})");
        }
    }

    private static byte[] CreateSeedMon(int generation)
    {
        PKM mon = generation switch
        {
            3 => new PK3(),
            5 => new PK5(),
            7 => new PK7(),
            8 => new PK8(),
            _ => new PK9(),
        };
        mon.Species = 25;
        mon.CurrentLevel = 10;
        // A real mon always carries its origin game; without it PKHeX's nature reroll
        // treats the mon as Gen6+ (PID untied to nature) and the seed is not realistic.
        mon.Version = generation switch
        {
            3 => GameVersion.FR,
            5 => GameVersion.B,
            7 => GameVersion.UM,
            8 => GameVersion.SW,
            _ => GameVersion.VL,
        };
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        return bytes;
    }
}
