using System.Buffers.Binary;

namespace PKForge.Engine.SoulGold;

/// <summary>
/// Pokémon Soul Gold (rahtak; Eemeliri/soulgold, pokeemerald-expansion 1.15.2)
/// save layout. Ground truth: the owner's save — every offset here was probed
/// against it unless noted — and the engine source at release 1.2.1 (`e738033`).
///
/// The flash envelope is a stock-expansion 14-sector rotation: two slots of 14
/// sectors of 0x1000, footer at +0xFF4 (id u16, checksum u16, signature u32,
/// counter u32), signature 0x08012025 (0x21325842 "BX2!" on a 1.2.1 BX21-era
/// storage). Checksum windows differ per logical id: SaveBlock2 0xB30,
/// SaveBlock1 3968×3 + 3540, storage 3968×9. Sectors 28/29 are the per-slot
/// auxiliary record, 30/31 the storage overflow; a footer's id field is NOT
/// trustworthy there (the sample's overflow carries its checksum in the id
/// slot), so those sectors are identified by signature + counter.
///
/// Pokémon records are 96-byte party / 76-byte (legacy V19) or 68-byte (BX21)
/// box records with the secure bitfields stored in the clear — no per-mon
/// crypto, no per-mon checksum. Two storage generations exist and are never
/// interchangeable; <see cref="StorageEra"/> is detected per save from the
/// BX magic words, never assumed.
/// </summary>
internal static class SoulGoldFormat
{
    public const int FileSize = 0x20_000;
    public const int SectorSize = 0x1000;
    public const int SectionsPerSlot = 14;
    public const int SlotCount = 2;

    public const uint SectorSignature = 0x0801_2025;
    public const uint SectorSignatureBx21 = 0x2132_5842; // "BX2!"

    /// <summary>Per-logical-id checksum windows (src/save.c sSaveSlotLayout @e738033).</summary>
    public static readonly int[] Windows =
    [
        0xB30, // 0 SaveBlock2
        3968, 3968, 3968, // 1-3 SaveBlock1 chunks 0-2
        3540, // 4 SaveBlock1 tail (sizeof(SaveBlock1) == 0x3C54)
        3968, 3968, 3968, 3968, 3968, 3968, 3968, 3968, 3968, // 5-13 Pokémon storage
    ];

    public const int SaveBlock1Size = 0x3C54;
    public const int SaveBlock2Size = 0xB30;

    // SaveBlock1 fields (probe-confirmed; global.h's comments are stale).
    public const int PartyCountOffset = 0x234;
    public const int PartyOffset = 0x238;
    public const int PartyMonSize = 96;
    public const int MoneyOffset = 0x478;
    public const int CoinsOffset = 0x47C;
    public const int BagOffset = 0x548;
    public const int DexSeenOffset = 0x31F8;
    public const int DexCaughtOffset = 0x3279;
    public const int DexBytes = 129;
    public const int DexCapacity = 1025; // NATIONAL_DEX_PECHARUNT
    public const int StorageExtensionTailOffset = 0x349A; // storage bytes 39680..41623

    // SaveBlock2 fields.
    public const int TrainerNameOffset = 0x00;
    public const int TrainerGenderOffset = 0x08;
    public const int TrainerIdOffset = 0x0A;
    public const int PlayTimeHoursOffset = 0x0E;
    public const int PlayTimeMinutesOffset = 0x10;
    public const int PlayTimeSecondsOffset = 0x11;
    public const int EncryptionKeyOffset = 0xB4;

    // Record sizes per storage generation.
    public const int BoxMonSizeV19 = 76;
    public const int BoxMonSizeBx21 = 68;

    // Storage split (ReadStorageBytes @e738033:670).
    public const int StorageInSectors = 9 * 3968; // 35712: logical ids 5..13
    public const int StorageOverflowSize = 3968; // 35712..39679: sector 30/31
    /// <summary>A legacy overflow sector's footer checksum covers only its first
    /// 1324 bytes (PKMN_STORAGE_BX16_OVERFLOW_SIZE); the rest is covered by the
    /// extension checksum at offset 1328.</summary>
    public const int LegacyOverflowFooterWindow = 1324;
    public const int StorageSb1TailSize = 1944; // 39680..41623: SaveBlock1 tail
    public const int StorageSb1TailBase = StorageInSectors + StorageOverflowSize; // 39680
    public const int StorageAuxTailBase = StorageSb1TailBase + StorageSb1TailSize; // 41624
    public const int StorageAuxDataOffset = 12;

    public const int StorageSizeV19 = 43_916;
    public const int StorageSizeBx21 = 43_456;

    // Legacy V19 storage offsets (include/pokemon_legacy.h @e738033).
    public const int LegacyBoxStride = 30 * BoxMonSizeV19; // 2280
    public const int LegacyBoxesBase = 4;
    public const int LegacyBoxNamesBase = 34_204;
    public const int LegacyBox15Base = 34_744;
    public const int LegacyBox15NameOffset = 37_024;
    public const int LegacyBoxExtensionMagicOffset = 34_740; // "BX16"
    public const int LegacyBox18MagicOffset = 37_036; // "BX18"/"BX19" (0 = absent)
    public const int LegacyExtensionChecksumOffset = 37_040;
    public const int LegacyBox16Base = 37_044;
    public const int LegacyBox17Base = 39_324;
    public const int LegacyBox1617NamesBase = 41_604;
    public const int LegacyBox18Base = StorageAuxTailBase; // 41624, via the aux sector
    public const int LegacyBox18NameOffset = 43_904;

    // BX21 storage offsets (static asserts, include/pokemon_storage_system.h @e738033).
    public const int Bx21BoxesBase = 16 + 4 * PartyMonSize; // 404, after header + fusions
    public const int Bx21BoxStride = 30 * BoxMonSizeBx21; // 2040
    public const int Bx21BoxNamesBase = 43_244;
    public const uint Bx21Magic = 0x3132_5842; // "BX2!"

    public const uint Bx16Magic = 0x3631_5842;
    public const uint Bx17Magic = 0x3731_5842;
    public const uint Bx18Magic = 0x3831_5842;
    public const uint Bx19Magic = 0x3931_5842;

    /// <summary>The two on-flash storage generations. A legacy save is never
    /// rewritten with the BX21 codec or vice versa: mixing them shifts every
    /// box by 8 bytes and corrupts 21 boxes at once.</summary>
    public enum StorageEra
    {
        /// <summary>76-byte BoxPokemonV19 records, BX16..BX19 magic words,
        /// 15-19 boxes. The sample's generation.</summary>
        LegacyV19,
        /// <summary>68-byte records under a "BX2!" header, 21 boxes
        /// (offsets from the source's static asserts, not yet probed on a
        /// real 1.2.1 save).</summary>
        Bx21,
    }

    /// <summary>One parsed save slot: its 14 live sectors plus the overflow and
    /// auxiliary sectors (physical file offsets, -1 when absent/erased).</summary>
    internal sealed record SlotLayout(int Slot, uint Counter, int[] Sections, int OverflowSector, int AuxSector);

    /// <summary>
    /// Parses both slots. A slot qualifies when all 14 logical ids are present
    /// exactly once, every windowed checksum matches, the signature is one of
    /// the two valid words, and a single counter covers all 14 sectors. The
    /// overflow (30/31) and auxiliary (28/29) sectors are matched by signature
    /// and counter, because 1.2.1's strict footer-id test rejects real saves
    /// (the sample's overflow carries its checksum in the id slot).
    /// </summary>
    public static List<SlotLayout> ParseSlots(ReadOnlySpan<byte> data)
    {
        var result = new List<SlotLayout>(SlotCount);
        for (var slot = 0; slot < SlotCount; slot++)
        {
            var sections = new int[SectionsPerSlot];
            Array.Fill(sections, -1);
            var counters = new uint[SectionsPerSlot];
            var complete = true;
            for (var index = 0; index < SectionsPerSlot && complete; index++)
            {
                var offset = (slot * SectionsPerSlot + index) * SectorSize;
                var id = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 0xFF4)..]);
                if (id >= SectionsPerSlot || sections[id] >= 0)
                { complete = false; continue; }
                if (!IsLiveSignature(data, offset) ||
                    Checksum(data.Slice(offset, Windows[id]), Windows[id]) !=
                    BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 0xFF6)..]))
                { complete = false; continue; }
                sections[id] = offset;
                counters[id] = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 0xFFC)..]);
            }
            if (!complete || sections.Any(o => o < 0) || counters.Distinct().Count() != 1)
                continue;

            var counter = counters[0];
            var overflow = FindTailSector(data, 30 + slot, counter);
            var aux = FindTailSector(data, 28 + slot, counter);
            result.Add(new SlotLayout(slot, counter, sections, overflow, aux));
        }
        return result;
    }

    internal static bool IsLiveSignature(ReadOnlySpan<byte> data, int offset)
    {
        var signature = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 0xFF8)..]);
        return signature == SectorSignature || signature == SectorSignatureBx21;
    }

    /// <summary>A tail sector counts as live when it carries a valid signature
    /// and this slot's counter; an erased (0xFF) tail is absent, not invalid.</summary>
    private static int FindTailSector(ReadOnlySpan<byte> data, int sector, uint counter)
    {
        var offset = sector * SectorSize;
        if (!IsLiveSignature(data, offset))
            return -1;
        return BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 0xFFC)..]) == counter
            ? offset
            : -1;
    }

    /// <summary>The game's slot choice: the higher counter wins, signed compare.</summary>
    public static SlotLayout? NewestSlot(ReadOnlySpan<byte> data)
    {
        var slots = ParseSlots(data);
        if (slots.Count == 0)
            return null;
        if (slots.Count == 1)
            return slots[0];
        var difference = (int)(slots[1].Counter - slots[0].Counter);
        return difference > 0 ? slots[1] : slots[0];
    }

    /// <summary>Box count of a legacy storage image: 15 without the BX16 word,
    /// then 16/17/18/19 per the extension magics.</summary>
    public static int LegacyBoxCount(ReadOnlySpan<byte> storage)
    {
        var extension = BinaryPrimitives.ReadUInt32LittleEndian(storage[LegacyBoxExtensionMagicOffset..]);
        if (extension is not (Bx16Magic or Bx17Magic or Bx18Magic or Bx19Magic))
            return 15;
        return BinaryPrimitives.ReadUInt32LittleEndian(storage[LegacyBox18MagicOffset..]) switch
        {
            Bx18Magic => 18,
            Bx19Magic => 19,
            _ => 16,
        };
    }

    /// <summary>Storage offset of legacy box <paramref name="box"/> (0-based).</summary>
    public static int LegacyBoxOffset(int box) => box switch
    {
        <= 14 => LegacyBoxesBase + box * LegacyBoxStride,
        15 => LegacyBox15Base,
        16 => LegacyBox16Base,
        17 => LegacyBox17Base,
        _ => LegacyBox18Base,
    };

    internal static int LegacyBoxNameOffset(int box) => box switch
    {
        <= 14 => LegacyBoxNamesBase + 9 * box,
        15 => LegacyBox15NameOffset,
        <= 17 => LegacyBox1617NamesBase + 9 * (box - 16),
        _ => LegacyBox18NameOffset,
    };

    /// <summary>Reassembles the slot's whole storage image from its four homes:
    /// sectors 5-13, the overflow sector, the SaveBlock1 extension tail and the
    /// auxiliary sector's storage tail (data offset 12). An absent (erased)
    /// tail contributes erased bytes verbatim — a BX16-stage save simply never
    /// wrote them, and writing them back is a no-op.</summary>
    public static byte[] ReadStorage(ReadOnlySpan<byte> data, SlotLayout slot, StorageEra era)
    {
        var size = era == StorageEra.Bx21 ? StorageSizeBx21 : StorageSizeV19;
        var storage = new byte[size];
        var cursor = 0;
        for (var id = 5; id <= 13; id++)
        {
            data.Slice(slot.Sections[id], 3968).CopyTo(storage.AsSpan(cursor));
            cursor += 3968;
        }

        var overflow = storage.AsSpan(StorageInSectors, StorageOverflowSize);
        if (slot.OverflowSector >= 0)
            data.Slice(slot.OverflowSector, StorageOverflowSize).CopyTo(overflow);
        else
            overflow.Fill(0xFF);

        var tailSectorOffset = slot.Sections[4] + StorageExtensionTailOffset - 3 * 3968;
        data.Slice(tailSectorOffset, StorageSb1TailSize)
            .CopyTo(storage.AsSpan(StorageSb1TailBase, StorageSb1TailSize));

        var auxTail = (era == StorageEra.Bx21 ? StorageSizeBx21 : StorageSizeV19) - StorageAuxTailBase;
        var aux = storage.AsSpan(StorageAuxTailBase, auxTail);
        if (slot.AuxSector >= 0)
            data.Slice(slot.AuxSector + StorageAuxDataOffset, auxTail).CopyTo(aux);
        else
            aux.Fill(0xFF);
        return storage;
    }

    /// <summary>Reassembles SaveBlock1 (0x3C54 bytes) from logical ids 1-4.</summary>
    public static byte[] ReadSaveBlock1(ReadOnlySpan<byte> data, SlotLayout slot)
    {
        var block = new byte[SaveBlock1Size];
        var cursor = 0;
        for (var id = 1; id <= 4; id++)
        {
            data.Slice(slot.Sections[id], Windows[id]).CopyTo(block.AsSpan(cursor));
            cursor += Windows[id];
        }
        return block;
    }

    /// <summary>Retail Gen 3 checksum: u32 word sum over the window, folded to 16 bits.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> sector, int length)
    {
        uint total = 0;
        length &= ~3;
        for (var offset = 0; offset < length; offset += sizeof(uint))
            total += BinaryPrimitives.ReadUInt32LittleEndian(sector[offset..]);
        return (ushort)(total + (total >> 16));
    }

    public static void WriteChecksum(byte[] data, int sectorOffset, int length) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(sectorOffset + 0xFF6),
            Checksum(data.AsSpan(sectorOffset, length), length));

    /// <summary>Folded rolling u32-word sum, the storage extension checksum's
    /// accumulator (AddChecksumWords + FoldChecksum, src/save.c @e738033).
    /// Verified against the sample's backup slot: BX19 seed + overflow tail +
    /// SaveBlock1 tail = the stored 0x7C5A, and the aux record's own checksum
    /// reproduces its stored 0x9853.</summary>
    public static ushort RollingChecksum(uint seed, ReadOnlySpan<byte> data)
    {
        uint total = seed;
        for (var offset = 0; offset < data.Length; offset += sizeof(uint))
            total += BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        return (ushort)(total + (total >> 16));
    }

    /// <summary>
    /// Structural detection: a complete, checksum-valid, newest slot that
    /// carries Soul Gold's Pokémon layer — the party at SaveBlock1+0x238 with
    /// 96-byte plaintext records and sane species/levels, plus a BX storage
    /// marker. The sector envelope alone is Emerald's (PKHeX parses this file
    /// as SAV3E and shows a broken party/PC), so Soul Gold must be routed
    /// before the stock Emerald path.
    /// </summary>
    public static bool LooksLike(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileSize)
            return false;
        data = data[..FileSize];
        if (NewestSlot(data) is not { } slot)
            return false;

        var sb1 = ReadSaveBlock1(data, slot);
        var era = BinaryPrimitives.ReadUInt32LittleEndian(data[slot.Sections[5]..]) == Bx21Magic
            ? StorageEra.Bx21
            : StorageEra.LegacyV19;
        var storage = ReadStorage(data, slot, era);

        var hasMarker = era == StorageEra.Bx21 ||
            BinaryPrimitives.ReadUInt32LittleEndian(storage[LegacyBoxExtensionMagicOffset..])
                is Bx16Magic or Bx17Magic or Bx18Magic or Bx19Magic ||
            (slot.AuxSector >= 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(data[slot.AuxSector..]) is Bx19Magic or Bx21Magic);

        var count = sb1[PartyCountOffset];
        if (count > 6)
            return false;
        for (var index = 0; index < count; index++)
        {
            var record = sb1.AsSpan(PartyOffset + index * PartyMonSize, PartyMonSize);
            var pid = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var species = BinaryPrimitives.ReadUInt32LittleEndian(record[0x20..]) & 0x7FF;
            var level = record[0x50];
            if ((pid == 0 && species == 0) || species is < 1 or > 2047 || level is < 1 or > 100)
                return false;
        }

        // caught ⊆ seen over the two 129-byte bitmaps.
        for (var index = 0; index < DexBytes; index++)
            if ((sb1[DexCaughtOffset + index] & ~sb1[DexSeenOffset + index]) != 0)
                return false;

        return hasMarker || count > 0;
    }
}
