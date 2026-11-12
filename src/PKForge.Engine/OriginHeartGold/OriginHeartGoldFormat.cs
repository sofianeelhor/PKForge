using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine.OriginHeartGold;

/// <summary>
/// Pokémon Origin HeartGold (口袋妖怪 起源心金, a Chinese binary hack of HeartGold by
/// 雁南飞TB, later "Alex") save layout. No source or table dump of the hack is public;
/// every offset here was read on the real sample
/// (romhacks/batch-2026-11-12/loose/Pokemon - Origin HeartGold Version.sav) and is
/// pinned by OriginHeartGoldTests.
///
/// The envelope is HeartGold's: a 512 KB flash holding two 0x40000 partitions, each with
/// a general block at +0 and a storage block at +0xF800, both closed by the retail
/// 16-byte footer {u32 save count, u32 block size, u32 magic 0x20060623, u16 block id,
/// u16 CRC-16/CCITT over everything before the footer}. The hack grew both blocks:
/// general 0xF7CC (retail 0xF628) and storage 0x18408 (retail 0x12310) = 24 boxes of
/// 0x1000 (30 × 136-byte PK4 + 16 bytes of padding) followed by the current box, the
/// box-changed flags, 24 × 40-byte box names and 24 wallpaper bytes. Each block's
/// current copy is the one with the higher save count, chosen independently, as in
/// the retail game.
///
/// Pokémon records are retail PK4 (136 bytes boxed, 236 in the party, PRNG encryption
/// and checksum unchanged), but the hack stores modern national ids in them: species
/// past 493 (Cottonee 546, Vullaby 629, Steenee 762, each holding its default English
/// name as nickname), moves past 467 (Baby-Doll Eyes 608 on an Umbreon, Fairy Wind 584
/// on a Cottonee) and items past 536 (Mega Bracelet 744 in the bag). Byte 0x15, the
/// retail ability id, holds 0/1 (the hack's ability slot), and the ability itself is
/// the u16 at 0x42 (retail padding), a modern ability id: Competitive 172 on a
/// Jigglypuff, Infiltrator 151 on a Zubat, Telepathy 140 on a Kirlia.
/// </summary>
internal static class OriginHeartGoldFormat
{
    public const int FileSize = 0x80_000;
    public const int PartitionSize = 0x40_000;
    public const int GeneralSize = 0xF7CC;
    public const int StorageOffset = 0xF800;
    public const int StorageSize = 0x18408;
    public const int FooterSize = 0x10;
    public const uint Magic = 0x2006_0623;
    public const uint MagicKorean = 0x2007_0903;
    public const ushort GeneralBlockId = 0;
    public const ushort StorageBlockId = 1;

    // ── Storage (offsets inside the storage block) ──
    public const int BoxCount = 24;
    public const int SlotsPerBox = 30;
    public const int BoxStride = 0x1000;
    public const int BoxEnd = BoxCount * BoxStride; // 0x18000
    public const int CurrentBoxOffset = BoxEnd; // u32, as retail
    public const int BoxChangedFlagsOffset = BoxEnd + 4; // u32 bit per box, as retail
    public const int BoxNamesOffset = BoxEnd + 8; // 0x18008
    public const int BoxNameSize = 40; // 20 Gen 4 characters
    public const int WallpapersOffset = BoxNamesOffset + BoxCount * BoxNameSize; // 0x183C8
    /// <summary>Every box marked changed (retail HGSS: 0x3FFFF for 18 boxes).</summary>
    public const uint AllBoxesChanged = (1u << BoxCount) - 1;

    // ── General block (retail HGSS offsets, verified on the sample) ──
    public const int TrainerNameOffset = 0x64; // 8 Gen 4 characters
    public const int TrainerIdOffset = 0x74;
    public const int SecretIdOffset = 0x76;
    public const int MoneyOffset = 0x78;
    public const int GenderOffset = 0x7C;
    public const int LanguageOffset = 0x7D;
    public const int BadgesOffset = 0x7E;
    public const int PlayTimeHoursOffset = 0x86;
    public const int PlayTimeMinutesOffset = 0x88;
    public const int PlayTimeSecondsOffset = 0x89;
    public const int PartyCountOffset = 0x94;
    public const int PartyOffset = 0x98;
    public const int BagOffset = 0x644;
    public const int MaxMoney = 999_999;

    public const int StoredSize = 136; // PK4 stored
    public const int PartySize = 236; // PK4 party

    /// <summary>The live copy of each block: its partition (0 or <see cref="PartitionSize"/>)
    /// and save count.</summary>
    internal sealed record Layout(int General, uint GeneralCount, int Storage, uint StorageCount);

    /// <summary>A block copy is valid when its footer names the hack's block (size, magic,
    /// id) and its CRC matches.</summary>
    internal static bool BlockValid(ReadOnlySpan<byte> data, int start, int size, ushort id)
    {
        if (start + size > data.Length)
            return false;
        var block = data.Slice(start, size);
        var footer = block[^FooterSize..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) != (uint)size)
            return false;
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]);
        if (magic is not (Magic or MagicKorean) || BinaryPrimitives.ReadUInt16LittleEndian(footer[12..]) != id)
            return false;
        return Checksums.CRC16_CCITT(block[..^FooterSize]) == BinaryPrimitives.ReadUInt16LittleEndian(footer[14..]);
    }

    private static uint SaveCount(ReadOnlySpan<byte> data, int start, int size) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[(start + size - FooterSize)..]);

    /// <summary>
    /// The current copy of each block, or null when either block has no valid copy. Like
    /// the retail game, the higher save count wins and each block is chosen on its own.
    /// </summary>
    internal static Layout? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileSize)
            return null;
        if (Pick(data, 0, GeneralSize, GeneralBlockId) is not { } general)
            return null;
        if (Pick(data, StorageOffset, StorageSize, StorageBlockId) is not { } storage)
            return null;
        return new Layout(general, SaveCount(data, general, GeneralSize), storage, SaveCount(data, storage, StorageSize));

        static int? Pick(ReadOnlySpan<byte> data, int offset, int size, ushort id)
        {
            var first = BlockValid(data, offset, size, id);
            var second = BlockValid(data, PartitionSize + offset, size, id);
            if (!first && !second) return null;
            if (first != second) return first ? offset : PartitionSize + offset;
            return SaveCount(data, PartitionSize + offset, size) > SaveCount(data, offset, size)
                ? PartitionSize + offset
                : offset;
        }
    }

    /// <summary>
    /// Cheap structural test first (a footer in either partition carrying the hack's
    /// general block size, which no retail Gen 4 save writes: HGSS 0xF628, Platinum 0xCF2C,
    /// Diamond/Pearl 0xC100), then the full parse with CRCs.
    /// </summary>
    public static bool LooksLike(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileSize)
            return false;
        if (!HasSizeFooter(data, 0) && !HasSizeFooter(data, PartitionSize))
            return false;
        return Parse(data) is not null;

        static bool HasSizeFooter(ReadOnlySpan<byte> data, int partition) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data[(partition + GeneralSize - 12)..]) == GeneralSize
            && BinaryPrimitives.ReadUInt32LittleEndian(data[(partition + StorageOffset + StorageSize - 12)..]) == StorageSize;
    }

    /// <summary>Recomputes a block's footer CRC in place.</summary>
    internal static void WriteCrc(Span<byte> data, int start, int size) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data[(start + size - 2)..],
            Checksums.CRC16_CCITT(data.Slice(start, size - FooterSize)));
}
