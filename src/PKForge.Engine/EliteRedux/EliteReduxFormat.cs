using System.Buffers.Binary;

namespace PKForge.Engine.EliteRedux;

/// <summary>
/// Elite Redux's save envelope (eliteredux-source <c>src/save.c</c>, <c>include/save.h</c>):
/// ONE save slot of 20 sectors at the start of the flash (NUM_SAVE_SLOTS 1), each carrying
/// 0xFF4 data bytes, then the retail footer (id, checksum, signature 0x08012025, counter).
/// Every save rotates the slot by one sector (sector = (id + lastWrittenSector) % 20), so a
/// logical id is found by reading the footers. Ids 0-1 hold SaveBlock2, 2-7 SaveBlock1,
/// 8-19 the PC storage. The game zeroes each sector before copying a section in and
/// checksums the section's own size, so the bytes past it are zero and the checksum over
/// the whole 0xFF4 data area equals the game's. Sectors 20-27 are erased flash; the slot is
/// never duplicated, so an in-place write keeps every footer (rotation, counter) as found.
/// </summary>
internal static class EliteReduxFormat
{
    public const int FileSize = 0x20000;
    public const int SectorSize = 0x1000;
    public const int SectorData = 0xFF4;
    public const int SlotSectors = 20;
    public const uint Signature = 0x0801_2025;

    public const int SaveBlock2First = 0, SaveBlock2Sections = 2;
    public const int SaveBlock1First = 2, SaveBlock1Sections = 6;
    public const int StorageFirst = 8, StorageSections = 12;

    // ── SaveBlock2 (include/global.h) ──
    public const int PlayerNameOffset = 0x00;
    public const int PlayerGenderOffset = 0x08;
    public const int TrainerIdOffset = 0x0A;
    public const int PlayTimeOffset = 0x0E;

    // ── SaveBlock1 ──
    public const int PartyCountOffset = 0x234;
    public const int PartyOffset = 0x238;
    public const int MoneyOffset = 0x400;
    public const int CoinsOffset = 0x404;
    /// <summary>u16 itemFlags[256]: one bit per owned item (save version 1047+; the bag has no counts).</summary>
    public const int ItemFlagsOffset = 0x40A;
    public const int ItemFlagWords = 256;
    /// <summary>u16 vars[256] (0x4000-0x40FF); proven by the game stats that follow them.</summary>
    public const int VarsOffset = 0x1C78;
    /// <summary>VAR_SAVE_VERSION (0x406A): the build's CURRENT_GAME_VERSION when it last saved.</summary>
    public const int SaveVersionOffset = VarsOffset + 0x6A * 2;

    // ── PokemonStorage (include/pokemon_storage_system.h) ──
    public const int BoxCount = 26;
    public const int BoxSlots = 30;
    public const int BoxesOffset = 4;
    public const int BoxNamesOffset = BoxesOffset + BoxCount * BoxSlots * EliteReduxMon.BoxSize; // 0x9E74
    public const int BoxNameLength = 9;

    /// <summary>The save version these tables and offsets are pinned to (eliteredux-source
    /// <c>CURRENT_GAME_VERSION</c>). Item flags replaced the counted pockets at 1047.</summary>
    public const int PinnedVersion = 1049;
    public const int ItemFlagsSinceVersion = 1047;

    /// <summary>Physical offset of logical ids 0-19 when the bytes are a complete, intact Elite
    /// Redux slot: the first 20 sectors carry each id once, the signature, one counter and a
    /// valid checksum; sectors 20-27 carry no signature. Null otherwise.</summary>
    public static int[]? Sections(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileSize)
            return null;
        var sections = new int[SlotSectors];
        Array.Fill(sections, -1);
        uint? counter = null;
        for (var sector = 0; sector < 28; sector++)
        {
            var offset = sector * SectorSize;
            var stamped = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 0xFF8)..]) == Signature;
            if (sector >= SlotSectors)
            {
                if (stamped) return null;
                continue;
            }
            if (!stamped) return null;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 0xFF4)..]);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 0xFFC)..]);
            if (id >= SlotSectors || sections[id] >= 0 || (counter is { } c && c != value))
                return null;
            sections[id] = offset;
            counter = value;
        }
        // Footers first (cheap, and they reject every retail two-slot save), then the data.
        foreach (var offset in sections)
            if (Checksum(data.Slice(offset, SectorData)) != BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 0xFF6)..]))
                return null;
        return sections;
    }

    /// <summary>True when the bytes are an Elite Redux save this engine reads: the intact
    /// 20-sector slot, a party count of at most six, and party records that decode as the
    /// hack's Pokémon (known species, moves and item, a terminated name). Cheap: the 20
    /// footers, one pass over the sector data for the checksums, and at most six records.</summary>
    public static bool LooksLike(ReadOnlySpan<byte> data)
    {
        if (Sections(data) is not { } sections)
            return false;
        var party = sections[SaveBlock1First];
        var count = data[party + PartyCountOffset];
        if (count > 6)
            return false;
        var tables = EliteReduxData.Instance;
        for (var slot = 0; slot < count; slot++)
        {
            var mon = new EliteReduxMon(data.Slice(party + PartyOffset + slot * EliteReduxMon.PartySize, EliteReduxMon.PartySize).ToArray(), 0, true);
            if (!mon.LooksValid(tables))
                return false;
        }
        return true;
    }

    /// <summary>The retail Gen 3 section checksum: the 32-bit word sum, folded to 16 bits.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 4 <= data.Length; i += 4)
            sum += BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
        return (ushort)(sum + (sum >> 16));
    }

    /// <summary>A save block reassembled from its sections' data areas.</summary>
    public static byte[] ReadBlock(ReadOnlySpan<byte> data, int[] sections, int first, int count)
    {
        var block = new byte[count * SectorData];
        for (var i = 0; i < count; i++)
            data.Slice(sections[first + i], SectorData).CopyTo(block.AsSpan(i * SectorData));
        return block;
    }

    /// <summary>Copies a changed byte range of a block back into its sections and re-seals
    /// only the sections it touched. Footers (id, signature, counter) are kept.</summary>
    public static void WriteBlockRange(byte[] data, int[] sections, int first, byte[] block, int offset, int length)
    {
        if (length <= 0) return;
        var firstChunk = offset / SectorData;
        var lastChunk = (offset + length - 1) / SectorData;
        for (var chunk = firstChunk; chunk <= lastChunk; chunk++)
        {
            var sector = sections[first + chunk];
            block.AsSpan(chunk * SectorData, SectorData).CopyTo(data.AsSpan(sector, SectorData));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(sector + 0xFF6), Checksum(data.AsSpan(sector, SectorData)));
        }
    }
}
