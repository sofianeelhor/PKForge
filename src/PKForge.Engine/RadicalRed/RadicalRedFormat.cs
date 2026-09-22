using System.Buffers.Binary;

namespace PKForge.Engine.RadicalRed;

/// <summary>
/// Pokémon Radical Red (CFRU engine on a FireRed base) save layout.
///
/// Ground truth: the owner's champion save (128 KiB, 151 PC mons), the CFRU engine's
/// own source (save.c / pokemon.h / pokemon_storage_system.c), and the triage scripts
/// in cases/radical-red/10-triage. The file keeps the retail GBA envelope — 32 sectors
/// of 0x1000, footer at +0xFF4 (id u16, checksum u16, signature u32, save index u32),
/// sections 0-13 duplicated across two rotating slots — and the retail signature
/// 0x08012025 (Unbound stamps 0x01121999 instead; there is no Radical Red magic).
/// The hack's delta is the CFRU chunk table: every section's checksum window shrinks
/// to the chunk it owns, and the spare tails ("parasite" bytes) plus sectors 30/31
/// (raw 0xFF0 payloads, zeroed footers, no checksums) carry extra engine state that
/// must be preserved untouched.
/// </summary>
internal static class RadicalRedFormat
{
    public const int FileSize = 0x20_000;
    public const int SectorSize = 0x1000;
    public const int SectorCount = FileSize / SectorSize;
    public const int SectionCount = 14;

    public const int PartySection = 1;
    public const int PartyCountOffset = 0x34;
    public const int PartyOffset = 0x38;
    public const int PartyMonSize = 100;

    public const int StreamSections = 9; // section ids 5..13
    public const int StreamSize = 8 * 0xFF0 + 0x450; // 0x83D0, the whole PokemonStorage block
    public const int PcMonSize = 58;
    public const int BoxSlotCount = 30;
    public const int StreamBoxArea = 4; // currentBox u32 first, then 19 boxes
    public const int StreamBoxes = 19; // boxes 0-18 live in the stream
    public const int RawBoxes = 3; // boxes 19-21 live in the raw sector-30/31 region

    /// <summary>CFRU gSaveSectionOffsets: the checksum window AND the stream chunk
    /// size of each section id (save.c). Radical Red keeps the retail checksum
    /// algorithm but sizes every window to this table.</summary>
    public static readonly ushort[] CfruWindows =
    {
        0xF24, // 0 trainer (SaveBlock2)
        0xFF0, 0xFF0, 0xFF0, // 1-3 party/dex/bag, mail, misc (SaveBlock1)
        0xD98, // 4 SaveBlock1 tail
        0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, // 5-12 PokemonStorage
        0x450, // 13 PokemonStorage tail (box names end at 0x83D0)
    };

    /// <summary>Vintage FRLG sizes (3884/3968x3/3848/3968x8/2000): a real Radical Red
    /// save validates 14/14 against the CFRU table but only 7/14 against these.</summary>
    public static readonly ushort[] VanillaWindows =
    {
        0xF2C, 0xF80, 0xF80, 0xF80, 0xF08,
        0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0x7D0,
    };

    /// <summary>Live (highest save index) physical offset of every section id, -1 when
    /// absent. Only sectors with a retail 0x080120xx footer signature participate: the
    /// Hall of Fame sector carries its checksum in the id slot, and CFRU zeroes the
    /// footers of the raw sectors 30/31 — either would alias a real section id.</summary>
    public static int[] SectionOffsets(ReadOnlySpan<byte> data)
    {
        var offsets = new int[SectionCount];
        Array.Fill(offsets, -1);
        var indices = new uint[SectionCount];
        for (var sector = 0; sector < SectorCount; sector++)
        {
            var off = sector * SectorSize;
            if (!IsRetailFooter(data, off))
                continue;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 0xFF4)..]);
            if (id >= SectionCount)
                continue;
            var index = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 0xFFC)..]);
            if (offsets[id] < 0 || index >= indices[id])
            {
                indices[id] = index;
                offsets[id] = off;
            }
        }
        return offsets;
    }

    /// <summary>Every physical offset holding a copy of the given section id.</summary>
    public static List<int> AllSectionOffsets(ReadOnlySpan<byte> data, int sectionId)
    {
        var result = new List<int>(2);
        for (var sector = 0; sector < SectorCount; sector++)
        {
            var off = sector * SectorSize;
            if (IsRetailFooter(data, off) && BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 0xFF4)..]) == sectionId)
                result.Add(off);
        }
        return result;
    }

    private static bool IsRetailFooter(ReadOnlySpan<byte> data, int off) =>
        (BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 0xFF8)..]) & 0xFFFF_FF00u) == 0x0801_2000u;

    /// <summary>The PokemonStorage stream: the data prefix of sections 5..13, each as
    /// long as its CFRU window, concatenated. Mons straddle sector boundaries, so the
    /// stream is always reassembled before parsing (4080 is not a multiple of 58).</summary>
    public static byte[] ReadStream(ReadOnlySpan<byte> data, int[] sections)
    {
        var stream = new byte[StreamSize];
        var cursor = 0;
        for (var id = 5; id <= 13; id++)
        {
            var size = CfruWindows[id];
            data[sections[id]..(sections[id] + size)].CopyTo(stream.AsSpan(cursor));
            cursor += size;
        }
        return stream;
    }

    /// <summary>Splits the stream back into the live section prefixes and recomputes
    /// each checksum over its CFRU window. Bytes past a window (the CFRU parasite,
    /// e.g. sector 13's 0xBA0 spare tail) are never touched.</summary>
    public static void WriteStream(byte[] data, int[] sections, ReadOnlySpan<byte> stream)
    {
        var cursor = 0;
        for (var id = 5; id <= 13; id++)
        {
            var size = CfruWindows[id];
            stream.Slice(cursor, size).CopyTo(data.AsSpan(sections[id]));
            WriteChecksum(data, sections[id], size);
            cursor += size;
        }
    }

    /// <summary>Retail Gen 3 checksum: u32 word sum folded to 16 bits, over the window.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> sector, int length)
    {
        uint total = 0;
        length &= ~3;
        for (var offset = 0; offset < length; offset += sizeof(uint))
            total += BinaryPrimitives.ReadUInt32LittleEndian(sector[offset..]);
        return (ushort)(total + (total >> 16));
    }

    public static void WriteChecksum(byte[] data, int sectorOffset, int length)
    {
        var checksum = Checksum(data.AsSpan(sectorOffset, length), length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(sectorOffset + 0xFF6), checksum);
    }

    // ── Raw sector-30/31 region (CFRU LoadSector30And31) ──
    // Sector 30's first 0xFF0 bytes load to RAM 0x203C038 and sector 31's to
    // 0x203D028 — 0xFF0 apart — so the RAM region is NOT contiguous in file space:
    // file offsets 0x1EFF0..0x1F000 (sector 30's footer) map to nothing. Boxes 19-21
    // (1-based 20-22) sit at RAM 0x203CB44 + 1740*i (pokemon_storage_system.c), i.e.
    // region offset 0xB0C; box 19 straddles the sector boundary at file 0x1F000.
    public const int RawRegionFile = 0x1E000;
    public const int RawRegionSize = 0x1FE0;
    public const int RawBoxesRegionOffset = 0xB0C;

    /// <summary>File offset of a raw-region offset, accounting for the footer gap.</summary>
    public static int RawFileOffset(int regionOffset) =>
        regionOffset < 0xFF0 ? RawRegionFile + regionOffset : RawRegionFile + 0x1000 + (regionOffset - 0xFF0);

    // ── Bag (the CFRU bag expansion, engine src/item.c) ──
    // Five pockets of 4-byte ItemSlots (item u16, quantity u16 XOR the security key),
    // zero-id terminated, parked at RAM 0x203BB20 in game order Items/Key/Balls/TM/
    // Berries with capacities 450/75/50/128/75. The run begins inside section 13's
    // parasite tail and only 0x518 bytes fit before the sector data ends; everything
    // past RAM 0x203C038 continues into the raw sector-30/31 region, which is why
    // key items, balls, TMs and berries live at fixed file offsets in the 0x1E000
    // area. The section 13 checksum window (0x450) stops right where the bag begins,
    // so the in-sector bag bytes ride the parasite, checksum-free like the rest of it.
    public const int BagSection = 13;
    public const int BagImageOffset = 0xAD8; // bag image start inside section 13's data
    public const int BagImageInSector = 0x518; // (0xFF0 - 0xAD8); slots past this spill into the raw region

    /// <summary>
    /// Structural Radical Red test (there is no magic signature): every live section
    /// validates against the CFRU window table while at least one vintage FRLG window
    /// fails, and the party decodes as plaintext. Callers that also test Unbound must
    /// test it FIRST — Unbound stamps 0x01121999 and would pass the window test too.
    /// Validated against real saves: Radical Red CFRU 14/14 + vanilla 7/14, vanilla
    /// FireRed CFRU 12/14, Unbound excluded by signature.
    /// </summary>
    public static bool IsRadicalRed(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileSize)
            return false;
        if (SaveParser.IsPokemonUnbound(data))
            return false;

        var sections = SectionOffsets(data);
        for (var id = 0; id < SectionCount; id++)
            if (sections[id] < 0)
                return false;

        var vanillaFailures = 0;
        for (var id = 0; id < SectionCount; id++)
        {
            var off = sections[id];
            var stored = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 0xFF6)..]);
            if (Checksum(data.Slice(off, CfruWindows[id]), CfruWindows[id]) != stored)
                return false;
            if (Checksum(data.Slice(off, VanillaWindows[id]), VanillaWindows[id]) != stored)
                vanillaFailures++;
        }
        if (vanillaFailures == 0)
            return false; // a stock FRLG save, not the hack

        // Party sanity: u32 count <= 6; with at least one mon the first species must
        // be plaintext-sane. A fresh save has count 0 and a zeroed party (species 0)
        // and must still detect — falling through to vanilla FRLG parsing would
        // corrupt a CFRU save on the first edit.
        var party = sections[PartySection];
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[(party + PartyCountOffset)..]);
        if (count > 6)
            return false;
        if (count == 0)
            return true;
        var species = BinaryPrimitives.ReadUInt16LittleEndian(data[(party + PartyOffset + 0x20)..]);
        return RadicalRedData.IsKnownSpecies(species);
    }
}
