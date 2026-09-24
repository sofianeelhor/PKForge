using PKForge.Domain;
using PKForge.Engine;
using PKForge.Engine.RadicalRed;
using PKForge.Engine.Unbound;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The grid's held-item badge and the "find held item" tools read <see cref="SlotSummary.HeldItem"/>
/// from the snapshot. These prove every engine fills it from the same bytes the editor reads
/// (<see cref="ISaveEngineSession.ReadEntity"/>), and that bank facts carry it from deposit on.
/// </summary>
public sealed class HeldItemFlagTests
{
    private const int ExpShare = 216;
    private const int Leftovers = 234;

    private static string? TestData(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine([directory.FullName, ".local-testdata", .. parts]);
        return path is not null && File.Exists(path) ? path : null;
    }

    private static PKM Mon(SaveFile save, ushort species, int item)
    {
        var mon = save.BlankPKM;
        mon.Species = species;
        mon.CurrentLevel = 30;
        mon.OriginalTrainerName = "Sof";
        mon.Language = (int)LanguageID.English;
        mon.PID = 0x12345678u + species;
        mon.Move1 = 33;
        mon.HeldItem = item;
        mon.Nickname = SpeciesName.GetSpeciesNameGeneration(species, mon.Language, mon.Format);
        mon.RefreshChecksum();
        return mon;
    }

    [Fact]
    public void StockSnapshotCarriesBoxAndPartyHeldItems()
    {
        var save = BlankSaveFile.Get(GameVersion.SW, "Sof", LanguageID.English);
        save.SetBoxSlotAtIndex(Mon(save, 25, ExpShare), 0, 0, EntityImportSettings.None);
        save.SetBoxSlotAtIndex(Mon(save, 133, 0), 0, 1, EntityImportSettings.None);
        save.SetBoxSlotAtIndex(Mon(save, 1, Leftovers), 2, 7, EntityImportSettings.None);
        save.SetPartySlotAtIndex(Mon(save, 4, Leftovers), 0, EntityImportSettings.None);
        using var session = new SaveEngineSession(save, null);

        var slots = session.Snapshot.Slots;
        Assert.Equal(ExpShare, slots.Single(s => s.Box == 0 && s.Slot == 0).HeldItem);
        Assert.False(slots.Single(s => s.Box == 0 && s.Slot == 1).HasItem);
        Assert.Equal(Leftovers, slots.Single(s => s.Box == -1 && s.Slot == 0).HeldItem);

        var found = HeldItemSearch.Find(slots, Leftovers);
        Assert.Equal([(2, 7), (-1, 0)], found.Select(s => (s.Box, s.Slot)));
        Assert.Single(HeldItemSearch.Find(slots, ExpShare));
        Assert.Equal(3, HeldItemSearch.Find(slots).Count);
    }

    private static byte[] Bytes(PKM mon)
    {
        var data = new byte[mon.SIZE_PARTY];
        mon.WriteDecryptedDataParty(data);
        return data;
    }

    [Fact]
    public void BatchRemoveHeldItemsTakesOnlyTheMarkedItems()
    {
        // The finder's "mark all" hands off to the batch editor's "Remove held items" chip.
        var save = BlankSaveFile.Get(GameVersion.SW, "Sof", LanguageID.English);
        save.SetBoxSlotAtIndex(Mon(save, 25, ExpShare), 0, 0, EntityImportSettings.None);
        save.SetBoxSlotAtIndex(Mon(save, 1, ExpShare), 1, 3, EntityImportSettings.None);
        save.SetBoxSlotAtIndex(Mon(save, 4, Leftovers), 0, 2, EntityImportSettings.None);
        using var session = new SaveEngineSession(save, null);
        var marked = HeldItemSearch.Find(session.Snapshot.Slots, ExpShare).Select(s => (s.Box, s.Slot)).ToList();

        Assert.Equal(2, session.BatchApplySlots(marked, ["HeldItem=0"]));
        Assert.Equal(0, session.ReadEntity(0, 0).HeldItem);
        Assert.Equal(0, session.ReadEntity(1, 3).HeldItem);
        Assert.Equal(Leftovers, session.ReadEntity(0, 2).HeldItem);
    }

    [Fact]
    public void BankDescriptionRecordsHeldItem()
    {
        var engine = new SaveEngine();
        var save = BlankSaveFile.Get(GameVersion.SW, "Sof", LanguageID.English);
        var holder = Mon(save, 25, ExpShare);
        var bare = Mon(save, 25, 0);

        Assert.Equal(ExpShare, engine.TryDescribeEntity(Bytes(holder), "Sword", nameof(PK8))!.HeldItem);
        var none = engine.TryDescribeEntity(Bytes(bare), "Sword", nameof(PK8))!;
        Assert.Equal(0, none.HeldItem);
        Assert.False(none.HasItem);
    }

    [Fact]
    public void OldBankEntriesReadHeldItemAsUnknownUntilBackfilled()
    {
        // Index rows written before the field existed deserialize with a null held item:
        // the default facts report "nothing known" rather than guessing.
        var old = new BankEntryInfo(25, 0, false, "Pikachu", 30, 8, "Sword", nameof(PK8));
        Assert.Null(old.HeldItem);
        Assert.False(old.HasItem);
        var json = System.Text.Json.JsonSerializer.Serialize(old with { HeldItem = ExpShare });
        Assert.DoesNotContain("HasItem", json);
        Assert.Equal(ExpShare, System.Text.Json.JsonSerializer.Deserialize<BankEntryInfo>(json)!.HeldItem);
    }

    public static TheoryData<string> RomHacks => new() { "unbound", "radicalred", "gschronicles" };

    [Theory]
    [MemberData(nameof(RomHacks))]
    public void RomHackSnapshotsAgreeWithTheEditor(string hack)
    {
        ISaveEngineSession? session = hack switch
        {
            "unbound" => TestData("unbound-v2111.srm") is { } u ? new UnboundEngineSession(File.ReadAllBytes(u), "Unbound") : null,
            "radicalred" => TestData("radicalred-champ.sav") is { } r ? new RadicalRedEngineSession(File.ReadAllBytes(r), "Radical Red") : null,
            _ => TestData("romhacks", "Pokemon - GS Chronicles.sav") is { } g
                ? new PKForge.Engine.GsChronicles.GsChroniclesEngineSession(File.ReadAllBytes(g)) : null,
        };
        if (session is null) return; // gitignored ground truth: dev-only, skipped on CI
        using (session as IDisposable)
        {
            var occupied = session.Snapshot.Slots.Where(s => s.Species is not null).ToList();
            Assert.NotEmpty(occupied);
            foreach (var slot in occupied)
            {
                var detail = session.ReadEntity(slot.Box, slot.Slot);
                Assert.Equal(detail.HeldItem != 0, slot.HasItem);
                if (slot.HeldItem > 0) Assert.Equal(detail.HeldItem, slot.HeldItem);
            }
        }
    }
}
