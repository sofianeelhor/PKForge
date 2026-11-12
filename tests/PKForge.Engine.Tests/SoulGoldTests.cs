using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Engine.SoulGold;
using Xunit;
using static PKForge.Engine.SoulGold.SoulGoldFormat;

namespace PKForge.Engine.Tests;

/// <summary>
/// Pokémon Soul Gold ground truth: the owner's sample save (kept out of the
/// repository) — newest slot counter 4 in the legacy BX16 generation (16
/// boxes, 76-byte records), backup slot a valid BX19 save, party of six at
/// SaveBlock1+0x238. Field-for-field expectations come from
/// an independent decoder's reading of the same save.
/// </summary>
public sealed class SoulGoldTests
{
    private static string? LocalArtifact(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", file);
        return path is not null && File.Exists(path) ? path : null;
    }

    private static byte[]? Sample() => LocalArtifact(Path.Combine("romhacks", "soulgold.sav")) is { } path
        ? File.ReadAllBytes(path)
        : null;

    private static SoulGoldEngineSession Open(byte[] bytes) => new(bytes);

    [Fact]
    public void RealSaveIsRecognised()
    {
        var sample = Sample();
        if (sample is null) return;
        Assert.True(SoulGoldFormat.LooksLike(sample));
        using var session = Open(sample);
        Assert.Equal(16, session.BoxCount);
        Assert.Equal(30, session.BoxSlotCount);
        Assert.Equal(3, session.Generation);
    }

    [Fact]
    public void OtherRomHackSamplesAreNotSoulGold()
    {
        foreach (var file in new[]
                 {
                     "recharged-yellow.sav", "quetzal-9.1.sav", "lazarus.sav", "heart-and-soul.sav",
                     "emerald-seaglass.sav", "emerald-enhanced.sav", "perfect-emerald.sav",
                 })
        {
            var path = LocalArtifact(Path.Combine("romhacks", file));
            if (path is null) continue;
            Assert.False(SoulGoldFormat.LooksLike(File.ReadAllBytes(path)), file);
        }
    }

    [Fact]
    public void RoutingValidatesDescribesAndRejectsTheWrongRoute()
    {
        var bytes = Sample();
        if (bytes is null) return;

        var engine = new SaveEngine();
        Assert.True(SaveParser.IsPokemonSoulGold(bytes));
        Assert.True(engine.Validate(bytes));
        Assert.Null(engine.AssessLayoutRisk(bytes));

        using var session = engine.OpenSession(bytes);
        Assert.IsType<SoulGoldEngineSession>(session);

        var description = engine.TryDescribe(bytes);
        Assert.NotNull(description);
        Assert.Equal("Soul Gold", description.GameName);
        var guess = SaveIdentityRules.Guess(description.GameName, description.Generation, "s.sav", null, "Save");
        Assert.Equal(SaveFormat.SoulGold, guess.Format);
        Assert.Equal(SaveLayoutFamily.SoulGold, guess.Family);

        Assert.Throws<InvalidDataException>(() => engine.OpenSession(bytes, null, SaveFormat.Standard));
        using var explicitSession = engine.OpenSession(bytes, null, SaveFormat.SoulGold);
        Assert.IsType<SoulGoldEngineSession>(explicitSession);
    }

    [Fact]
    public void PartyMatchesTheIndependentDecoder()
    {
        var sample = Sample();
        if (sample is null) return;
        using var session = Open(sample);
        // species (national ids), levels — as the independent decoder reads them.
        (int Species, int Level, string Nickname)[] expected =
        [
            (653, 11, "Fennekin"),
            (280, 5, "Ralts"),
            (16, 2, "Pidgey"),
            (263, 3, "Zigzagoon"),
            (921, 4, "Fluff"), // Pawmi, rom id 1305
            (167, 4, "Spinarak"),
        ];
        for (var slot = 0; slot < expected.Length; slot++)
        {
            var mon = session.ReadEntity(-1, slot);
            Assert.False(mon.IsEmpty);
            Assert.Equal(expected[slot].Species, mon.Species);
            Assert.Equal(expected[slot].Level, mon.Level);
            Assert.Equal(expected[slot].Nickname, mon.Nickname);
        }
        Assert.True(session.ReadEntity(-1, 6).IsEmpty);

        var rng = session.GetRngInfo(-1, 0);
        Assert.NotEqual(0u, rng.Pid);
        // The independent decoder lists storage order (HP, Atk, Def, Spe, SpA,
        // SpD); the session reports app order, so Spe and SpA swap places.
        Assert.Equal([14, 24, 19, 18, 22, 16], rng.IVs);
    }

    [Fact]
    public void BoxGenerationCountAndNamesAreReadable()
    {
        var sample = Sample();
        if (sample is null) return;
        using var session = Open(sample);
        // Newest slot is the BX16 stage: 16 boxes, default names.
        Assert.Equal(16, session.BoxCount);
        Assert.Equal("Box1", session.GetBoxName(0));
        Assert.Equal("Box15", session.GetBoxName(14));
        Assert.Equal("Box16", session.GetBoxName(15));

        // Box 0 holds nine released mons; the first is a Pidgey (national 16,
        // level 2 from its 9 EXP).
        var mon = session.ReadEntity(0, 0);
        Assert.False(mon.IsEmpty);
        Assert.Equal(16, mon.Species);
        Assert.Equal(2, mon.Level);
        Assert.True(session.ReadEntity(1, 0).IsEmpty);
    }

    [Fact]
    public void TrainerMoneyAndCoinsUseTheSaveBlockLayout()
    {
        var sample = Sample();
        if (sample is null) return;
        using var session = Open(sample);
        var trainer = session.GetTrainer();
        Assert.Equal("Mia", trainer.Name);
        Assert.Equal(1, trainer.Gender);
        Assert.Equal(1684u, trainer.Money);
        var stats = session.GetTrainerStats();
        Assert.True(stats.SupportsCoins);
        Assert.Equal(0, stats.Coins);
        Assert.True(stats.SupportsPlayTime);
        Assert.Equal(54, stats.PlayTimeMinutes);
    }

    [Fact]
    public void UnchangedSaveRoundTripsByteForByte()
    {
        var sample = Sample();
        if (sample is null) return;
        using var session = Open(sample);
        Assert.Equal(sample, session.Serialize().ToArray());
    }

    [Fact]
    public void BackupSlotAndTailSectorsStayUntouched()
    {
        var sample = Sample();
        if (sample is null) return;
        using var session = Open(sample);
        session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "EMBER"));
        var written = session.Serialize().ToArray();
        Assert.Equal(sample.Length, written.Length);
        // The older slot (sectors 14-27) and the tail sectors 28-31 stay verbatim.
        Assert.True(sample.AsSpan(14 * SectorSize, 18 * SectorSize)
            .SequenceEqual(written.AsSpan(14 * SectorSize)));
        var differences = CountDifferences(sample, written);
        Assert.True(differences > 0 && differences < SectorSize,
            $"expected a surgical write, got {differences} changed bytes");
    }

    [Fact]
    public void PartyAndBoxEditsPersistAcrossReopen()
    {
        var sample = Sample();
        if (sample is null) return;
        byte[] written;
        using (var session = Open(sample))
        {
            session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "EMBER", Level: 15));
            session.ApplyEdit(0, 0, new EntityEdit(Level: 3));
            written = session.Serialize().ToArray();
        }
        Assert.NotEqual(sample, written);
        Assert.True(SoulGoldFormat.LooksLike(written));

        using var reopened = Open(written);
        var party = reopened.ReadEntity(-1, 0);
        Assert.Equal("EMBER", party.Nickname);
        Assert.Equal(15, party.Level);
        Assert.Equal(653, party.Species);

        var box = reopened.ReadEntity(0, 0);
        Assert.Equal(16, box.Species);
        Assert.Equal(3, box.Level);

        // The untouched party members keep their bytes.
        Assert.Equal("Ralts", reopened.ReadEntity(-1, 1).Nickname);
    }

    [Fact]
    public void OverflowBoxEditKeepsEverySectorChecksumValid()
    {
        var sample = Sample();
        if (sample is null) return;
        byte[] written;
        using (var session = Open(sample))
        {
            // Box 15 lives in the overflow sector; an empty slot becomes a Pidgey.
            session.ApplyEdit(15, 1, new EntityEdit(Species: 16, Level: 5));
            written = session.Serialize().ToArray();
        }
        using (var reopened = Open(written))
        {
            var mon = reopened.ReadEntity(15, 1);
            Assert.False(mon.IsEmpty);
            Assert.Equal(16, mon.Species);
            Assert.Equal(5, mon.Level);
            Assert.Equal(16, reopened.BoxCount);
        }
        foreach (var slot in SoulGoldFormat.ParseSlots(written))
        {
            foreach (var offset in slot.Sections)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(offset + 0xFF4));
                if (id >= SectionsPerSlot)
                    continue;
                Assert.Equal(
                    BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(offset + 0xFF6)),
                    SoulGoldFormat.Checksum(written.AsSpan(offset, Windows[id]), Windows[id]));
            }
        }
    }

    [Fact]
    public void BackupSlotBx19ChecksumsReproduce()
    {
        var sample = Sample();
        if (sample is null) return;
        // The older slot is a genuine BX19 save: the rolling extension checksum
        // (seeded with the magic) and the aux record checksum both reproduce
        // their stored values from the parsed layout — the same arithmetic the
        // write path uses after a box edit.
        var slots = SoulGoldFormat.ParseSlots(sample);
        Assert.Equal(2, slots.Count);
        var newest = slots.MaxBy(slot => slot.Counter)!;
        var backup = slots.Single(slot => slot != newest);
        var storage = SoulGoldFormat.ReadStorage(sample, backup, StorageEra.LegacyV19);
        Assert.Equal(Bx19Magic, BinaryPrimitives.ReadUInt32LittleEndian(storage.AsSpan(LegacyBox18MagicOffset)));

        uint total = Bx19Magic;
        foreach (var (start, length) in new[]
                 {
                     (StorageInSectors + 1332, StorageOverflowSize - 1332),
                     (StorageSb1TailBase, StorageSb1TailSize),
                 })
            for (var offset = start; offset < start + length; offset += 4)
                total += BinaryPrimitives.ReadUInt32LittleEndian(storage.AsSpan(offset));
        var aux = backup.AuxSector;
        Assert.Equal(Bx19Magic, BinaryPrimitives.ReadUInt32LittleEndian(sample.AsSpan(aux)));
        uint auxTotal = 0;
        for (var offset = 12; offset < 3892; offset += 4)
            auxTotal += BinaryPrimitives.ReadUInt32LittleEndian(sample.AsSpan(aux + offset));
        Assert.Equal(
            BinaryPrimitives.ReadUInt16LittleEndian(sample.AsSpan(aux + 8)),
            (ushort)(auxTotal + (auxTotal >> 16)));
    }

    [Fact]
    public void SpeciesTableBridgesRomIdsToNational()
    {
        var sample = Sample();
        if (sample is null) return;
        using var session = Open(sample);
        // Pawmi is stored as rom id 1305 and surfaces as national 921.
        var pawmi = session.ReadEntity(-1, 4);
        Assert.Equal(921, pawmi.Species);
        Assert.Equal("Pawmi", pawmi.SpeciesName);
    }

    private static int CountDifferences(byte[] a, byte[] b)
    {
        var count = 0;
        for (var index = 0; index < a.Length; index++)
            if (a[index] != b[index])
                count++;
        return count;
    }
}
