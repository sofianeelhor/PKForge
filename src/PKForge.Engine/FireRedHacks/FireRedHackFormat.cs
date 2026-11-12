using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine.FireRedHacks;

/// <summary>The FireRed binary hacks this engine reads.</summary>
internal enum FireRedHackGame
{
    /// <summary>Pokémon Fire Red Extended 3.5.x (DjTarma, on MrDollSteak's Decap and Attack base).</summary>
    FireRedExtended,
    /// <summary>Pokémon Crystal Advance Redux (Linkandzelda / Kertra lineage, FireRed US 1.0 base).</summary>
    CrystalAdvanceRedux,
}

/// <summary>
/// The save shape shared by Fire Red Extended and Crystal Advance Redux, proven byte for byte
/// on the real saves (FireRedHacks/Data/README.md):
/// <list type="bullet">
/// <item>Retail two-slot, 14-sector envelope with FireRed's footer and signature 0x08012025.</item>
/// <item>The retail FireRed save blocks, unchanged in size (SaveBlock2 0xF24, SaveBlock1
/// 0x3D68, PC storage 0x83D0), but cut into 0xFF0-byte sector chunks (the CFRU save code) where
/// retail FireRed cuts 0xF80. Proof: the 14 box names sit at storage+0x8344 only under 0xFF0
/// reassembly, SaveBlock1's last chunk ends at 0xD98 (= 0x3D68 − 3 × 0xFF0) and PC storage at
/// 0x450 in sector 13 (= 0x83D0 − 8 × 0xFF0) on both saves. Bytes a sector holds past its block
/// (the hacks' own extra data) are never touched.</item>
/// <item>Checksums: Fire Red Extended uses the CFRU windows (0xF24, 0xFF0 × 3, 0xD98, 0xFF0 × 8,
/// 0x450); Crystal Advance Redux one flat 0xF80 window on every sector.</item>
/// <item>Pokémon: the retail 100-byte party record at SaveBlock1+0x38 (count at +0x34) and the
/// 80-byte box record. Fire Red Extended keeps retail encryption and substructure shuffling;
/// Crystal Advance Redux stores the 48-byte data area in plain G-A-E-M order whatever the PID,
/// with the checksum field left at 0.</item>
/// </list>
/// </summary>
internal static class FireRedHackFormat
{
    public const int FileSize = 0x20_000;
    public const int Chunk = 0xFF0;
    public const int SaveBlock2Size = 0xF24;
    public const int SaveBlock1Size = 0x3D68;
    public const int StorageSize = 0x83D0;

    public const int PartyCountOffset = 0x34;
    public const int PartyOffset = 0x38;
    public const int PartyRecordSize = 100;
    public const int BoxRecordSize = 80;
    public const int Boxes = 14;
    public const int SlotsPerBox = 30;
    public const int BoxDataOffset = 4;
    public const int BoxNamesOffset = 0x8344;
    public const int BoxNameLength = 9;

    private static readonly int[] CfruWindows = [0xF24, 0xFF0, 0xFF0, 0xFF0, 0xD98, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0x450];
    private static readonly int[] FlatWindows = Enumerable.Repeat(0xF80, 14).ToArray();

    /// <summary>The hack these bytes (decoded, padding trimmed) belong to, or null.</summary>
    public static FireRedHackGame? Identify(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FileSize)
            return null;
        var data = payload[..FileSize];
        if (RecognizedHacks.TwoSlot(data) is not { } live)
            return null;
        if (RecognizedHacks.FireRedExtended(data, live))
            return FireRedHackGame.FireRedExtended;
        if (RecognizedHacks.CrystalAdvanceRedux(data, live))
            return FireRedHackGame.CrystalAdvanceRedux;
        return null;
    }

    /// <summary>The bytes each logical sector's checksum covers.</summary>
    public static int[] Windows(FireRedHackGame game) => game == FireRedHackGame.FireRedExtended ? CfruWindows : FlatWindows;

    /// <summary>Live physical offset of every logical sector id (0-13).</summary>
    public static int[] LiveSectors(ReadOnlySpan<byte> data) =>
        RecognizedHacks.TwoSlot(data[..FileSize]) ?? throw new InvalidDataException("This save has no complete save slot.");

    /// <summary>A save block reassembled from its 0xFF0-byte chunks.</summary>
    public static byte[] ReadBlock(ReadOnlySpan<byte> data, int[] live, int firstId, int size)
    {
        var block = new byte[size];
        for (var offset = 0; offset < size; offset += Chunk)
            data.Slice(live[firstId + offset / Chunk], Math.Min(Chunk, size - offset)).CopyTo(block.AsSpan(offset));
        return block;
    }

    /// <summary>Writes a block back over its chunks; bytes past the block in each sector stay.</summary>
    public static void WriteBlock(Span<byte> data, int[] live, int firstId, ReadOnlySpan<byte> block)
    {
        for (var offset = 0; offset < block.Length; offset += Chunk)
            block.Slice(offset, Math.Min(Chunk, block.Length - offset)).CopyTo(data[live[firstId + offset / Chunk]..]);
    }

    /// <summary>The logical sector ids a block range touches.</summary>
    public static IEnumerable<int> SectorsOf(int firstId, int offset, int length)
    {
        for (var id = offset / Chunk; id <= (offset + length - 1) / Chunk; id++)
            yield return firstId + id;
    }

    /// <summary>Re-seals one live sector over the hack's window for it.</summary>
    public static void WriteChecksum(Span<byte> data, int sector, int window) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data[(sector + 0xFF6)..],
            Expansion.ExpansionFormat.Checksum(data.Slice(sector, window), window));

    public static string ReadText(ReadOnlySpan<byte> span) => StringConverter3.GetString(span, jp: false);

    public static void WriteText(Span<byte> span, string value)
    {
        span.Fill(0xFF);
        if (value.Length > 0)
            StringConverter3.SetString(span, value, span.Length, jp: false);
    }
}
