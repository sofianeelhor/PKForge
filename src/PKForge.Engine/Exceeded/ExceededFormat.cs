using System.Buffers.Binary;

namespace PKForge.Engine.Exceeded;

/// <summary>
/// The Emerald Exceeded save (a closed pokeemerald-expansion hack; both the "Emerald
/// Exceeded" and the older "Exceeded" samples share it). The envelope is retail Emerald's
/// two 14-sector slots with the 0x08012025 footer, but every sector carries 4084 bytes
/// (0xFF4) of block data, and every live sector checksums over that whole window: the game
/// zero-fills the sector buffer, so the full window equals each block's real size.
///
/// The PC holds 13 boxes of 30 plaintext 88-byte records (no encryption, no shuffle, no
/// checksum), then 13 nine-byte box names at storage+0x8614 and 13 wallpaper bytes. The
/// party (count at SaveBlock1+0x234, records at +0x238) is the 88-byte record plus the
/// retail 20-byte battle block: 108 bytes. Evidence: Data/README.md beside this file.
/// </summary>
internal static class ExceededFormat
{
    public const string GameName = "Emerald Exceeded";

    public const int FileSize = 0x20_000;
    public const int SectorSize = 0x1000;
    public const int SectionCount = 14;
    public const int ChunkSize = 0xFF4;
    public const uint Signature = 0x0801_2025;

    public const int SaveBlock1Section = 1;
    public const int StorageFirstSection = 5;
    public const int StorageSections = 9;

    public const int PartyCountOffset = 0x234;
    public const int PartyOffset = 0x238;
    public const int PartyMonSize = 108;
    public const int BoxMonSize = 88;
    public const int BoxCount = 13;
    public const int BoxSlotCount = 30;
    public const int StorageBoxOffset = 4;
    public const int BoxNamesOffset = StorageBoxOffset + BoxCount * BoxSlotCount * BoxMonSize; // 0x8614
    public const int StorageSize = BoxNamesOffset + BoxCount * 9 + BoxCount;

    private const int RetailChunk = 0xF80;

    /// <summary>
    /// Whether these (trimmed) bytes are an Emerald Exceeded save this engine can write: a
    /// complete two-slot envelope, every live sector valid over 0xFF4 (the window every
    /// write re-seals), at least one SaveBlock1 sector that is NOT valid over retail's 0xF80
    /// (so the 4084-byte chunks are proven, not assumed), the 13-name box run at
    /// storage+0x8614, and a party whose records are not retail PK3. Cheap: no record is
    /// decoded beyond the party header.
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileSize)
            return false;
        data = data[..FileSize];
        if (SectionOffsets(data) is not { } live)
            return false;
        for (var id = 0; id < SectionCount; id++)
            if (!Valid(data, live[id], ChunkSize))
                return false;
        var longer = false;
        for (var id = 1; id <= 3; id++)
            longer |= !Valid(data, live[id], RetailChunk);
        if (!longer)
            return false;

        var storage = ReadStorage(data, live);
        if (!Expansion.ExpansionFormat.HasBoxNames(storage, BoxNamesOffset, BoxCount))
            return false;

        var saveBlock1 = data.Slice(live[SaveBlock1Section], ChunkSize);
        var count = saveBlock1[PartyCountOffset];
        if (count > 6)
            return false;
        for (var i = 0; i < count; i++)
        {
            var record = saveBlock1.Slice(PartyOffset + i * PartyMonSize, PartyMonSize);
            if (RecognizedHacks.Pk3Valid(record[..80], encrypted: true))
                return false;
            var species = BinaryPrimitives.ReadUInt16LittleEndian(record[0x1C..]);
            var level = record[0x5C];
            if (species == 0 || species > Expansion.ExpansionData.Instance.MaxSpecies || level is 0 or > 100)
                return false;
        }
        return true;
    }

    /// <summary>Live physical offset of logical ids 0-13 (highest save counter wins), or null
    /// when an id is missing or a slot sector carries an id past 13 (one-slot hacks).</summary>
    public static int[]? SectionOffsets(ReadOnlySpan<byte> data)
    {
        var live = new int[SectionCount];
        var counters = new uint[SectionCount];
        Array.Fill(live, -1);
        for (var sector = 0; sector < 28; sector++)
        {
            var footer = data[(sector * SectorSize + 0xFF4)..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) != Signature)
                continue;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(footer);
            if (id >= SectionCount)
            {
                if (id < 0x100) return null;
                continue;
            }
            var counter = BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]);
            if (live[id] < 0 || counter >= counters[id])
            {
                live[id] = sector * SectorSize;
                counters[id] = counter;
            }
        }
        return live.Any(offset => offset < 0) ? null : live;
    }

    /// <summary>The PC storage block reassembled from ids 5-13 (4084-byte chunks).</summary>
    public static byte[] ReadStorage(ReadOnlySpan<byte> data, int[] live)
    {
        var storage = new byte[StorageSize];
        for (var i = 0; i < StorageSections; i++)
        {
            var start = i * ChunkSize;
            var length = Math.Min(ChunkSize, StorageSize - start);
            if (length <= 0) break;
            data.Slice(live[StorageFirstSection + i], length).CopyTo(storage.AsSpan(start));
        }
        return storage;
    }

    /// <summary>Copies a storage range back into its live sectors and re-seals each one touched.</summary>
    public static void WriteStorageRange(byte[] data, int[] live, byte[] storage, int offset, int length)
    {
        var end = offset + length;
        for (var i = offset / ChunkSize; i < StorageSections && i * ChunkSize < end; i++)
        {
            var chunkStart = i * ChunkSize;
            var from = Math.Max(offset, chunkStart);
            var to = Math.Min(end, Math.Min(chunkStart + ChunkSize, StorageSize));
            if (to <= from) continue;
            var sector = live[StorageFirstSection + i];
            storage.AsSpan(from, to - from).CopyTo(data.AsSpan(sector + (from - chunkStart)));
            Seal(data, sector);
        }
    }

    /// <summary>Writes the sector's checksum over the full 4084-byte window.</summary>
    public static void Seal(byte[] data, int sector) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(sector + 0xFF6),
            Expansion.ExpansionFormat.Checksum(data.AsSpan(sector, ChunkSize), ChunkSize));

    public static bool Valid(ReadOnlySpan<byte> data, int sector, int length) =>
        Expansion.ExpansionFormat.Checksum(data.Slice(sector, length), length)
            == BinaryPrimitives.ReadUInt16LittleEndian(data[(sector + 0xFF6)..]);
}
