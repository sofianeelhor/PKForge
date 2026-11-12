using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine.SoulGold;
using Xunit;
using static PKForge.Engine.SoulGold.SoulGoldFormat;

namespace PKForge.Engine.Tests;

/// <summary>
/// Soul Gold write regressions from the 3.2 engine review, each on the owner's real save
/// (gitignored; skipped when absent). Every write is checked the same way: the untouched
/// round trip stays byte-identical, the edit survives a reopen, both slots still parse
/// with every windowed checksum valid, and the sectors of the other slot are untouched.
/// </summary>
public sealed class SoulGoldWriteRegressionTests
{
    private static byte[]? Sample()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", "romhacks", "soulgold.sav");
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static SoulGoldEngineSession Open(byte[] bytes) => new(bytes);

    private static string[] Party(SoulGoldEngineSession session) =>
        [.. Enumerable.Range(0, 6).Select(slot => session.ReadEntity(-1, slot)).Select(mon => mon.IsEmpty ? "-" : mon.SpeciesName)];

    /// <summary>Both slots parse (every windowed checksum valid), the written slot is still
    /// the newest, and every sector the written slot does not own is byte-identical.</summary>
    private static void AssertSoundWrite(byte[] before, byte[] after)
    {
        Assert.Equal(before.Length, after.Length);
        var slotsBefore = ParseSlots(before);
        var slotsAfter = ParseSlots(after);
        Assert.Equal(slotsBefore.Count, slotsAfter.Count);
        var live = NewestSlot(before)!;
        Assert.Equal(live.Slot, NewestSlot(after)!.Slot);
        Assert.Equal(live.Counter, NewestSlot(after)!.Counter);

        var owned = live.Sections.Append(live.OverflowSector).Append(live.AuxSector).Where(o => o >= 0).ToHashSet();
        for (var offset = 0; offset < FileSize; offset += SectorSize)
            if (!owned.Contains(offset))
                Assert.True(before.AsSpan(offset, SectorSize).SequenceEqual(after.AsSpan(offset, SectorSize)),
                    $"sector {offset / SectorSize} of the other slot changed");
    }

    // ── #1: BX21 storage is written chunk by chunk, never as one block ──

    /// <summary>The real save turned into a BX21-era one: the "BX2!" header word at storage 0
    /// (sector of id 5) and a BX21 auxiliary record in slot 0's sector 28. No 1.2.1 save
    /// exists yet; the layout is the 1.2.1 engine source's.</summary>
    private static byte[] Bx21Image(byte[] sample)
    {
        var bytes = sample.ToArray();
        var slot = NewestSlot(bytes)!;
        var s5 = slot.Sections[5];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(s5), Bx21Magic);
        WriteChecksum(bytes, s5, 3968);

        var aux = (28 + slot.Slot) * SectorSize;
        bytes.AsSpan(aux, SectorSize).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(aux), Bx21Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(aux + 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(aux + 6), 3420);
        var record = RollingChecksum(0, bytes.AsSpan(aux + 12, 3420));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(aux + 8), record);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(aux + 10), (ushort)~record);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(aux + 0xFF4), (ushort)(28 + slot.Slot));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(aux + 0xFF8), SectorSignatureBx21);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(aux + 0xFFC), slot.Counter);
        WriteChecksum(bytes, aux, 3968);
        return bytes;
    }

    private static (int Box, int Slot) FirstEmpty(SoulGoldEngineSession session, int box, int fromSlot = 0)
    {
        for (var slot = fromSlot; slot < session.BoxSlotCount; slot++)
            if (session.ReadEntity(box, slot).IsEmpty)
                return (box, slot);
        throw new InvalidOperationException($"box {box} has no empty slot");
    }

    [Fact]
    public void Bx21WriteKeepsEverySectorFooterAndTheBackupSlot()
    {
        if (Sample() is not { } sample) return;
        var image = Bx21Image(sample);
        var live = NewestSlot(image)!;
        Assert.True(LooksLike(image));

        using (var untouched = Open(image))
            Assert.Equal(image, untouched.Serialize().ToArray());

        // Before the fix, a play-time edit alone copied the 43,456-byte image from
        // sector 5 onwards: the footers of sectors 9-18 were zeroed, and no slot parsed.
        byte[] timeOnly;
        using (var session = Open(image))
        {
            Assert.Equal(21, session.BoxCount);
            session.SetTrainerStats(new TrainerStatsEdit(PlayTimeMinutes: 7));
            timeOnly = session.Serialize().ToArray();
        }
        AssertSoundWrite(image, timeOnly);
        Assert.True(image.AsSpan(live.OverflowSector, SectorSize).SequenceEqual(timeOnly.AsSpan(live.OverflowSector, SectorSize)));
        Assert.True(image.AsSpan(live.AuxSector, SectorSize).SequenceEqual(timeOnly.AsSpan(live.AuxSector, SectorSize)));
        using (var reopened = Open(timeOnly))
            Assert.Equal(7, reopened.GetTrainerStats().PlayTimeMinutes);

        // Box edits in every home of the storage: sectors 5-13 (box 0), the overflow
        // sector (box 18) and the auxiliary tail (box 20's last slot).
        (int Box, int Slot)[] targets;
        byte[] written;
        using (var session = Open(image))
        {
            targets = [FirstEmpty(session, 0), FirstEmpty(session, 18), FirstEmpty(session, 20, 29)];
            foreach (var (box, slot) in targets)
                session.ApplyEdit(box, slot, new EntityEdit(Species: 16, Level: 5));
            written = session.Serialize().ToArray();
        }
        AssertSoundWrite(image, written);
        using (var reopened = Open(written))
            foreach (var (box, slot) in targets)
                Assert.Equal((16, 5), (reopened.ReadEntity(box, slot).Species, reopened.ReadEntity(box, slot).Level));

        // The checksums inside the image hold: the header payload sum, the aux record sum,
        // and the overflow and aux footers.
        var slotAfter = NewestSlot(written)!;
        var storage = ReadStorage(written, slotAfter, StorageEra.Bx21);
        var payload = RollingChecksum(0, storage.AsSpan(16));
        Assert.Equal(payload, BinaryPrimitives.ReadUInt16LittleEndian(storage.AsSpan(8)));
        Assert.Equal((ushort)~payload, BinaryPrimitives.ReadUInt16LittleEndian(storage.AsSpan(10)));
        var aux = slotAfter.AuxSector;
        Assert.Equal(RollingChecksum(0, written.AsSpan(aux + 12, 3420)), BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(aux + 8)));
        Assert.Equal(Checksum(written.AsSpan(aux, 3968), 3968), BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(aux + 0xFF6)));
        var overflow = slotAfter.OverflowSector;
        Assert.Equal(Checksum(written.AsSpan(overflow, 3968), 3968), BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(overflow + 0xFF6)));
    }

    // ── #2, #3, #4, #10: party moves, swaps, imports and releases ──

    [Fact]
    public void PartyPokemonMovedOntoAnEmptyPartySlotGoesToTheEnd()
    {
        if (Sample() is not { } sample) return;
        byte[] written;
        using (var session = Open(sample))
        {
            session.ReleaseSlot(-1, 5); // Spinarak: a party of five
            Assert.Equal(["Fennekin", "Ralts", "Pidgey", "Zigzagoon", "Pawmi", "-"], Party(session));
            // Before the fix Fennekin was appended past the lowered count and lost.
            session.MoveSlot(-1, 0, -1, 5);
            Assert.Equal(["Ralts", "Pidgey", "Zigzagoon", "Pawmi", "Fennekin", "-"], Party(session));
            written = session.Serialize().ToArray();
        }
        AssertSoundWrite(sample, written);
        using var reopened = Open(written);
        Assert.Equal(["Ralts", "Pidgey", "Zigzagoon", "Pawmi", "Fennekin", "-"], Party(reopened));
        Assert.Equal(11, reopened.ReadEntity(-1, 4).Level);
    }

    [Fact]
    public void PartyPokemonDroppedOnAnOccupiedBoxSlotSwapsWithIt()
    {
        if (Sample() is not { } sample) return;
        byte[] written;
        using (var session = Open(sample))
        {
            Assert.Equal(16, session.ReadEntity(0, 0).Species); // a boxed Pidgey, level 2
            // Before the fix this threw after Fennekin had already overwritten the Pidgey.
            session.MoveSlot(-1, 0, 0, 0);
            written = session.Serialize().ToArray();
        }
        AssertSoundWrite(sample, written);
        using var reopened = Open(written);
        Assert.Equal(653, reopened.ReadEntity(0, 0).Species);
        Assert.Equal(11, reopened.ReadEntity(0, 0).Level);
        var pidgey = reopened.ReadEntity(-1, 0);
        Assert.Equal((16, 2), (pidgey.Species, pidgey.Level));
        Assert.True(pidgey.Stats![0] > 0);
        Assert.Equal(pidgey.Stats[0], pidgey.CurrentHp);
        Assert.Equal(6, Party(reopened).Count(name => name != "-"));
    }

    [Fact]
    public void ImportIntoThePartyLandsThePokemonItself()
    {
        if (Sample() is not { } sample) return;
        byte[] written;
        using (var session = Open(sample))
        {
            var export = session.ExportSlot(0, 0); // the boxed Pidgey's 76-byte record
            session.ReleaseSlot(-1, 5);
            // Before the fix the record was cleared by its own copy: a counted species-0 member.
            Assert.True(session.ImportSlot(-1, 0, export.Data, "pk3sg"));
            written = session.Serialize().ToArray();
        }
        AssertSoundWrite(sample, written);
        using var reopened = Open(written);
        var landed = reopened.ReadEntity(-1, 5);
        Assert.Equal((16, 2), (landed.Species, landed.Level));
        Assert.True(landed.Stats![0] > 0);
        Assert.Equal(["Fennekin", "Ralts", "Pidgey", "Zigzagoon", "Pawmi", "Pidgey"], Party(reopened));
    }

    [Fact]
    public void ReleasingAnEmptyPartySlotChangesNothing()
    {
        if (Sample() is not { } sample) return;
        byte[] written;
        using (var session = Open(sample))
        {
            session.ReleaseSlot(-1, 5);
            // Before the fix this removed Pawmi too.
            session.ReleaseSlot(-1, 5);
            Assert.Equal(["Fennekin", "Ralts", "Pidgey", "Zigzagoon", "Pawmi", "-"], Party(session));
            written = session.Serialize().ToArray();
        }
        AssertSoundWrite(sample, written);
        using (var reopened = Open(written))
            Assert.Equal(["Fennekin", "Ralts", "Pidgey", "Zigzagoon", "Pawmi", "-"], Party(reopened));

        // An empty party: before the fix the count byte became 255 and the save no longer opened.
        byte[] empty;
        using (var session = Open(sample))
        {
            for (var i = 0; i < 6; i++)
                session.ReleaseSlot(-1, 0);
            session.ReleaseSlot(-1, 0);
            empty = session.Serialize().ToArray();
        }
        AssertSoundWrite(sample, empty);
        Assert.True(LooksLike(empty));
        using (var reopened = Open(empty))
            Assert.All(Party(reopened), name => Assert.Equal("-", name));
    }

    // ── #7: the legacy overflow footer covers the first 1324 bytes ──

    /// <summary>The sample's backup slot is a genuine BX19 save (19 boxes, overflow
    /// sector 31, aux sector 29); raising its counter makes it the slot the game loads.</summary>
    private static byte[] Bx19Image(byte[] sample)
    {
        var bytes = sample.ToArray();
        var backup = ParseSlots(bytes).Single(slot => slot.Slot == 1);
        foreach (var offset in backup.Sections.Append(backup.OverflowSector).Append(backup.AuxSector))
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 0xFFC), 100);
        return bytes;
    }

    private static ushort ExtensionChecksum(byte[] storage)
    {
        uint total = Bx19Magic;
        foreach (var (start, length) in new[] { (StorageInSectors + 1332, StorageOverflowSize - 1332), (StorageSb1TailBase, StorageSb1TailSize) })
            for (var offset = start; offset < start + length; offset += 4)
                total += BinaryPrimitives.ReadUInt32LittleEndian(storage.AsSpan(offset));
        return (ushort)(total + (total >> 16));
    }

    [Fact]
    public void LegacyOverflowFooterIsTheGamesFirst1324ByteChecksum()
    {
        if (Sample() is not { } sample) return;
        var image = Bx19Image(sample);
        var live = NewestSlot(image)!;
        Assert.Equal(1, live.Slot);
        var overflow = live.OverflowSector;

        // The ground truth: the game stored the sum over 1324 bytes, not over 3968.
        var stored = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(overflow + 0xFF6));
        Assert.Equal(Checksum(image.AsSpan(overflow, LegacyOverflowFooterWindow), LegacyOverflowFooterWindow), stored);
        Assert.NotEqual(Checksum(image.AsSpan(overflow, 3968), 3968), stored);

        using (var untouched = Open(image))
            Assert.Equal(image, untouched.Serialize().ToArray());

        // Before the fix a play-time edit rewrote that footer with the 3968-byte sum.
        byte[] timeOnly;
        using (var session = Open(image))
        {
            Assert.Equal(19, session.BoxCount);
            session.SetTrainerStats(new TrainerStatsEdit(PlayTimeMinutes: 7));
            timeOnly = session.Serialize().ToArray();
        }
        AssertSoundWrite(image, timeOnly);
        Assert.True(image.AsSpan(overflow, SectorSize).SequenceEqual(timeOnly.AsSpan(overflow, SectorSize)));

        // Box edits inside the footer window (box 15's overflow part), past it (box 16,
        // covered by the extension checksum) and in the aux sector (box 18).
        (int Box, int Slot)[] targets;
        byte[] written;
        using (var session = Open(image))
        {
            targets = [FirstEmpty(session, 15, 13), FirstEmpty(session, 16), FirstEmpty(session, 18)];
            foreach (var (box, slot) in targets)
                session.ApplyEdit(box, slot, new EntityEdit(Species: 16, Level: 5));
            written = session.Serialize().ToArray();
        }
        AssertSoundWrite(image, written);
        using (var reopened = Open(written))
            foreach (var (box, slot) in targets)
                Assert.Equal((16, 5), (reopened.ReadEntity(box, slot).Species, reopened.ReadEntity(box, slot).Level));

        Assert.False(image.AsSpan(overflow, LegacyOverflowFooterWindow).SequenceEqual(written.AsSpan(overflow, LegacyOverflowFooterWindow)));
        Assert.Equal(Checksum(written.AsSpan(overflow, LegacyOverflowFooterWindow), LegacyOverflowFooterWindow),
            BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(overflow + 0xFF6)));
        var storage = ReadStorage(written, NewestSlot(written)!, StorageEra.LegacyV19);
        var extension = ExtensionChecksum(storage);
        Assert.Equal(extension, BinaryPrimitives.ReadUInt16LittleEndian(storage.AsSpan(LegacyExtensionChecksumOffset)));
        Assert.Equal((ushort)~extension, BinaryPrimitives.ReadUInt16LittleEndian(storage.AsSpan(LegacyExtensionChecksumOffset + 2)));
        var aux = live.AuxSector;
        Assert.Equal(Checksum(written.AsSpan(aux + 12, 3880), 3880), BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(aux + 8)));
        Assert.Equal(Checksum(written.AsSpan(aux, 3968), 3968), BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(aux + 0xFF6)));
    }

    // ── #14: an unreadable bag entry is never erased by an edit ──

    [Fact]
    public void BagEditRefusesWhenAnUnreadableEntryWouldBeErased()
    {
        if (Sample() is not { } sample) return;
        var image = sample.ToArray();
        var live = NewestSlot(image)!;
        var key = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(live.Sections[0] + EncryptionKeyOffset));
        int count;
        using (var session = Open(image))
            count = session.GetBag().Single(pouch => pouch.Name == "Items").Items.Count;
        // After the readable list: an id no table knows, then a real Potion behind it.
        var items = live.Sections[1] + BagOffset; // SaveBlock1 chunk 0 holds the Items pocket
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(items + count * 4), 0x7FFF);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(items + count * 4 + 2), (ushort)(1 ^ key));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(items + count * 4 + 4), 13);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(items + count * 4 + 6), (ushort)(5 ^ key));
        WriteChecksum(image, live.Sections[1], Windows[1]);

        byte[] written;
        using (var session = Open(image))
        {
            Assert.Equal(image, session.Serialize().ToArray());
            Assert.Equal(count, session.GetBag().Single(pouch => pouch.Name == "Items").Items.Count);
            // Before the fix this rewrote the pocket from the short list: the new entry took
            // the unreadable one's place and the Potion behind it was cleared.
            Assert.Throws<InvalidOperationException>(() => session.SetItemCount("Items", 14, 2));
            // Another pocket still edits, and leaves Items as it was.
            session.SetItemCount("Balls", 4, 3);
            written = session.Serialize().ToArray();
        }
        AssertSoundWrite(image, written);
        var block = ReadSaveBlock1(written, NewestSlot(written)!);
        Assert.Equal(13, BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(BagOffset + count * 4 + 4)));
        Assert.True(image.AsSpan(items, 150 * 4).SequenceEqual(written.AsSpan(items, 150 * 4)));
        using var reopened = Open(written);
        Assert.Contains(reopened.GetBag().Single(pouch => pouch.Name == "Balls").Items, item => item is { Id: 4, Count: 3 });
    }
}
