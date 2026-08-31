using System.Buffers.Binary;

namespace PKForge.Engine.Unbound;

/// <summary>
/// Pokémon Unbound v2.1.x save layout (CFRU engine on a FireRed base).
///
/// Ground truth: the owner's device save plus the PUSE project's empirical maps.
/// The file is a GBA 0x20000 envelope: 32 sectors of 0x1000, each with a footer
/// (id u16 @0xFF4, checksum u16 @0xFF6, signature u32 @0xFF8, save index u32 @0xFFC).
/// Unbound stamps 0x01121999 as the signature where retail saves carry 0x080120xx.
/// Section ids 0-13 each appear in both rotating halves; the copy with the highest
/// save index is live. Mons are CFRU formats, NOT retail Gen 3: the party keeps the
/// retail 100-byte layout but stores the 48-byte core as PLAINTEXT in a fixed
/// B,A,D,C substruct order, and the PC uses a lossy 58-byte compact encoding.
/// </summary>
internal static class UnboundFormat
{
    public const int FileSize = 0x20_000;
    public const int SectorSize = 0x1000;
    public const int SectorCount = FileSize / SectorSize;
    public const int SectionCount = 14;

    public const uint SectorSignature = 0x0112_1999;

    public const int PartySection = 1;
    public const int PartyCountOffset = 0x34;
    public const int PartyOffset = 0x38;
    public const int PartyMonSize = 100;

    public const int StreamSections = 8; // section ids 5..12
    public const int StreamPayloadOffset = 4; // 4-byte header, then 0xFF0 payload
    public const int StreamPayloadSize = 0xFF0;
    public const int PcMonSize = 58;
    public const int BoxSlotCount = 30;

    public const int PresetSection = 0;
    public const int PresetOffset = 0xB0;
    public const int PresetChecksumLength = 0xADC; // the game only covers this window
    public const int ChecksumLength = 0xFF4; // standard sector window (u32 aligned)

    /// <summary>Live (highest save index) physical offset of every section id.</summary>
    public static int[] SectionOffsets(ReadOnlySpan<byte> data)
    {
        var offsets = new int[SectionCount];
        var indices = new uint[SectionCount];
        for (var sector = 0; sector < SectorCount; sector++)
        {
            var off = sector * SectorSize;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 0xFF4)..]);
            if (id >= SectionCount)
                continue; // erased (0xFFFF) or padding sectors
            var index = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 0xFFC)..]);
            if (index >= indices[id])
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
            if (BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 0xFF4)..]) == sectionId)
                result.Add(off);
        }
        return result;
    }

    /// <summary>The PC stream: payloads of sections 5..12 (live copies) concatenated.</summary>
    public static byte[] ReadStream(ReadOnlySpan<byte> data, int[] sections)
    {
        var stream = new byte[StreamSections * StreamPayloadSize];
        var cursor = 0;
        for (var id = 5; id <= 12; id++)
        {
            data[(sections[id] + StreamPayloadOffset)..(sections[id] + StreamPayloadOffset + StreamPayloadSize)]
                .CopyTo(stream.AsSpan(cursor));
            cursor += StreamPayloadSize;
        }
        return stream;
    }

    /// <summary>Writes the stream back into the live section copies and fixes their checksums.</summary>
    public static void WriteStream(byte[] data, int[] sections, ReadOnlySpan<byte> stream)
    {
        var cursor = 0;
        for (var id = 5; id <= 12; id++)
        {
            var off = sections[id];
            stream.Slice(cursor, StreamPayloadSize).CopyTo(data.AsSpan(off + StreamPayloadOffset));
            WriteChecksum(data, off, ChecksumLength);
            cursor += StreamPayloadSize;
        }
    }

    /// <summary>Retail Gen 3 checksum: u32 sum folded to 16 bits, over the given length.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> sector, int length)
    {
        uint total = 0;
        length &= ~3; // the window is a whole number of u32 words
        for (var offset = 0; offset < length; offset += sizeof(uint))
            total += BinaryPrimitives.ReadUInt32LittleEndian(sector[offset..]);
        return (ushort)(total + (total >> 16));
    }

    public static void WriteChecksum(byte[] data, int sectorOffset, int length)
    {
        var checksum = Checksum(data.AsSpan(sectorOffset, length), length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(sectorOffset + 0xFF6), checksum);
    }

    /// <summary>Valid mon filter shared by every scanner: species in range, exp sane.</summary>
    public static bool LooksLikeMon(ReadOnlySpan<byte> raw, int speciesOffset, int expOffset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(raw[speciesOffset..]) is > 0 and <= 2500
        && BinaryPrimitives.ReadUInt32LittleEndian(raw[expOffset..]) is > 0 and <= 2_000_000;
}
