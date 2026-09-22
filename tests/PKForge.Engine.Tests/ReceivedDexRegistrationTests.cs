using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// A Pokémon that arrives from outside the save (bank transfer, .pk import, a catch)
/// must register in that save's Pokédex, exactly as it would if the game had handed it
/// over. Reported case: a Pokémon moved from the Bank into Pokémon Yellow never showed
/// as owned. Edits to Pokémon already in the save stay surgical and must not touch it.
/// </summary>
public sealed class ReceivedDexRegistrationTests
{
    private static SaveEngineSession Seed(GameVersion version, PKM mon)
    {
        var save = BlankSaveFile.Get(version, "PKForge", LanguageID.English);
        var session = new SaveEngineSession(save, null);
        var bytes = new byte[mon.SIZE_STORED];
        mon.RefreshChecksum();
        mon.WriteDecryptedDataStored(bytes);
        Assert.True(session.ImportSlot(0, 0, bytes), "seeding the save must work");
        return session;
    }

    [Fact]
    public void ImportingIntoYellowRegistersTheSpeciesAsOwned()
    {
        var save = BlankSaveFile.Get(GameVersion.YW, "PKForge", LanguageID.English);
        using var session = new SaveEngineSession(save, null);
        Assert.False(session.GetDexEntry(25).Caught, "a blank save starts without the species");

        var mon = new PK1 { Species = 25, CurrentLevel = 20 };
        var bytes = new byte[mon.SIZE_STORED];
        mon.RefreshChecksum();
        mon.WriteDecryptedDataStored(bytes);

        Assert.True(session.ImportSlot(0, 0, bytes));

        var entry = session.GetDexEntry(25);
        Assert.True(entry.Caught, "the received Pokémon must read as owned");
        Assert.True(entry.Seen);
    }

    [Fact]
    public void ImportingRegistersEverySupportedGeneration()
    {
        // Same rule for the modern games: a received mon counts in their dex too.
        foreach (var (version, mon) in new (GameVersion, PKM)[]
        {
            (GameVersion.Pt, new PK4 { Species = 25, CurrentLevel = 20 }),
            (GameVersion.B2, new PK5 { Species = 25, CurrentLevel = 20 }),
            (GameVersion.UM, new PK7 { Species = 25, CurrentLevel = 20 }),
            (GameVersion.SW, new PK8 { Species = 25, CurrentLevel = 20 }),
        })
        {
            using var session = Seed(version, mon);
            Assert.True(session.GetDexEntry(25).Caught, $"{version}: a received Pokémon must register");
        }
    }

    [Fact]
    public void ReceivedImportKeepsTheMonOwnIdentity()
    {
        // Only the dex is updated: the handler/record pipeline stays off, so the mon
        // keeps its own trainer instead of being re-stamped as a fresh trade.
        var save = BlankSaveFile.Get(GameVersion.YW, "PKForge", LanguageID.English);
        using var session = new SaveEngineSession(save, null);
        var mon = new PK1 { Species = 25, CurrentLevel = 20, OriginalTrainerName = "RED", TID16 = 1234 };
        var bytes = new byte[mon.SIZE_STORED];
        mon.RefreshChecksum();
        mon.WriteDecryptedDataStored(bytes);

        Assert.True(session.ImportSlot(0, 0, bytes));

        var placed = session.ReadEntity(0, 0);
        Assert.Equal(25, placed.Species);
        Assert.Equal("RED", placed.OriginalTrainer);
        Assert.Equal(20, placed.Level);
    }

    [Fact]
    public void EditingAnExistingMonDoesNotTouchTheDex()
    {
        // The surgical contract for edits: changing a Pokémon already in the save must
        // never silently mark its species as obtained.
        using var session = Seed(GameVersion.Pt, new PK4 { Species = 25, CurrentLevel = 20 });
        session.SetDexEntry(25, seen: false, caught: false);
        Assert.False(session.GetDexEntry(25).Caught);

        session.ApplyEdit(0, 0, new PKForge.Domain.EntityEdit(Nickname: "SPARKY"));

        Assert.False(session.GetDexEntry(25).Caught, "an edit is not an acquisition");
    }
}
