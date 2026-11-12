using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine.Expansion;

/// <summary>
/// One save variant of the pokeemerald-expansion family. All official expansion builds
/// (and the 14-box downstream hacks that kept the envelope) write 32 sectors of 0x1000
/// bytes: 3968 bytes of block data, 116 bytes of SaveBlock3, then the 12-byte footer at
/// 0xFF4. The checksum window of a logical sector is
/// <c>min(blockSize - chunk * sectorDataSize, sectorDataSize)</c>, never a flat 0xF80.
/// </summary>
internal enum ExpansionGame
{
    HeartAndSoul20,
    StandardExpansion,
    PerfectEmerald,
    TeamRocketEdition,
    BlackPearlLegacy,
    FireEmerald,
    Lazarus,
    Lucid,
    EmeraldEnhanced,
}

/// <summary>How a variant's 80/100-byte records are laid out.</summary>
internal enum ExpansionMonCodec
{
    /// <summary>pokeemerald-expansion 1.8+ (see <see cref="ExpansionMon"/>).</summary>
    Standard,

    /// <summary>
    /// Team Rocket Edition's own header and substructs, measured on the real save (no source is
    /// public): language and the egg/species flags share 0x12, the original trainer's name is
    /// 7 bytes at 0x13, friendship is the first byte of substruct 0's 0x08 word, and the ability
    /// slot is bits 24-25 of the ribbon word. See <see cref="ExpansionMon"/>.
    /// </summary>
    TeamRocketEdition,
}

/// <summary>Which name and id tables a variant reads through (see <see cref="ExpansionData"/>).</summary>
internal enum ExpansionTableSet
{
    /// <summary>The full 1.17 pack.</summary>
    Pack,

    /// <summary>Only the ids every expansion release since 1.8.0 shares.</summary>
    SharedSince18,

    /// <summary>The shared ids plus Black Pearl Emerald's own item table.</summary>
    BlackPearlLegacy,
}

/// <summary>The per-variant offsets and block sizes an expansion save is read through.</summary>
internal sealed record ExpansionProfile
{
    public required ExpansionGame Game { get; init; }
    public required string GameName { get; init; }
    public required string SnapshotTag { get; init; }

    /// <summary>Bytes of a sector the game checksums and loads as block data (3968 or 4084).</summary>
    public required int SectorDataSize { get; init; }

    /// <summary>Real size of SaveBlock2; the id-0 window is the smaller of this and the sector data.</summary>
    public required int SaveBlock2Size { get; init; }

    /// <summary>Real size of SaveBlock1; ids 1-4 split it into sector-sized chunks.</summary>
    public required int SaveBlock1Size { get; init; }

    /// <summary>Real size of PokemonStorage; ids 5-13 split it into sector-sized chunks.</summary>
    public required int StorageSize { get; init; }

    public required int PartyCountOffset { get; init; }
    public required int PartyOffset { get; init; }
    public required int PartyMonSize { get; init; }
    public required int BoxMonSize { get; init; }
    public required int BoxSlotCount { get; init; }
    public required int BoxCount { get; init; }
    public required int BoxNamesOffset { get; init; }

    public required int MoneyOffset { get; init; }
    public required int CoinsOffset { get; init; }
    public required int EncryptionKeyOffset { get; init; }

    /// <summary>The storage block starts after a 4-byte-aligned currentBox byte.</summary>
    public int StorageBoxOffset => 0x4;

    /// <summary>How the records are encoded.</summary>
    public ExpansionMonCodec Codec { get; init; } = ExpansionMonCodec.Standard;

    /// <summary>Which id and name tables the variant reads through.</summary>
    public ExpansionTableSet Tables { get; init; } = ExpansionTableSet.Pack;

    public ExpansionData TableData => Tables switch
    {
        ExpansionTableSet.SharedSince18 => ExpansionData.SharedSince18,
        ExpansionTableSet.BlackPearlLegacy => ExpansionData.BlackPearlLegacy,
        _ => ExpansionData.Instance,
    };

    /// <summary>Why the engine reads this variant but must not write it, or null when it
    /// is editable. Such a save opens read-only (see <see cref="WriteSafety"/>).</summary>
    public string? ReadOnlyReason { get; init; }

    public static readonly ExpansionProfile HeartAndSoul20 = new()
    {
        Game = ExpansionGame.HeartAndSoul20,
        GameName = "Heart and Soul 2.0",
        SnapshotTag = "EXPANSION",
        SectorDataSize = 3968,
        SaveBlock2Size = 0xF34,
        SaveBlock1Size = 0x3D90,
        StorageSize = 0x83D0,
        PartyCountOffset = 0x238,
        PartyOffset = 0x23C,
        PartyMonSize = 100,
        BoxMonSize = 80,
        BoxSlotCount = 30,
        BoxCount = 14,
        BoxNamesOffset = 0x8344,
        MoneyOffset = 0x494,
        CoinsOffset = 0x498,
        EncryptionKeyOffset = 0xAC,
    };

    public static readonly ExpansionProfile StandardExpansion = new()
    {
        Game = ExpansionGame.StandardExpansion,
        GameName = "pokeemerald-expansion",
        SnapshotTag = "EXPANSION",
        SectorDataSize = 3968,
        SaveBlock2Size = 0xF34,
        SaveBlock1Size = 0x3D90,
        StorageSize = 0x83D0,
        PartyCountOffset = 0x234,
        PartyOffset = 0x238,
        PartyMonSize = 100,
        BoxMonSize = 80,
        BoxSlotCount = 30,
        BoxCount = 14,
        BoxNamesOffset = 0x8344,
        MoneyOffset = 0x490,
        CoinsOffset = 0x494,
        EncryptionKeyOffset = 0xAC,
    };

    /// <summary>
    /// Newer expansion builds grew SaveBlock2 past 0xF34 (a real Lazarus save checksums
    /// its id-0 sector over 0xF5C or more), so the 0xF34 window no longer holds. The game
    /// zero-fills the sector buffer before copying a block in, so the full 3968-byte data
    /// window gives the same checksum as the block's real size on every save it wrote.
    /// </summary>
    public static readonly ExpansionProfile StandardExpansionGrownSaveBlock2 = StandardExpansion with
    {
        SaveBlock2Size = 3968,
    };

    public static readonly ExpansionProfile PerfectEmerald = new()
    {
        Game = ExpansionGame.PerfectEmerald,
        GameName = "Perfect Emerald",
        SnapshotTag = "EXPANSION",
        SectorDataSize = 0xFF4,
        SaveBlock2Size = 0xFF4,
        SaveBlock1Size = 4 * 0xFF4,
        StorageSize = 8 * 0xFF4 + 0xD9C,
        PartyCountOffset = 0x234,
        PartyOffset = 0x238,
        PartyMonSize = 100,
        BoxMonSize = 80,
        BoxSlotCount = 30,
        BoxCount = 15,
        BoxNamesOffset = 0x8CA4,
        MoneyOffset = 0x490,
        CoinsOffset = 0x494,
        EncryptionKeyOffset = 0xAC,
    };

    /// <summary>
    /// DragonsdenTV's Team Rocket Edition (an Emerald hack ported to pokeemerald-expansion):
    /// the standard 14-box storage and SaveBlock1 offsets, but written in 4084-byte sector
    /// chunks like Perfect Emerald (ids 1 and 3 only validate over 0xFF4; id 13 holds the
    /// last 0x430 bytes of the 0x83D0 storage). Like Perfect Emerald, SaveBlock2 and
    /// SaveBlock1 are checksummed over whole chunks: the game zero-fills the sector buffer,
    /// so the full window equals the real block size on every save it wrote.
    /// Its records use a header of their own (<see cref="ExpansionMonCodec.TeamRocketEdition"/>,
    /// measured on the real save: no source is public). Its 2.x release (April 2025) predates
    /// expansion 1.12, so only the ids every release since 1.8 shares are offered.
    /// </summary>
    public static readonly ExpansionProfile TeamRocketEdition = StandardExpansion with
    {
        Game = ExpansionGame.TeamRocketEdition,
        GameName = "Team Rocket Edition",
        SectorDataSize = 0xFF4,
        SaveBlock2Size = 0xFF4,
        SaveBlock1Size = 4 * 0xFF4,
        Codec = ExpansionMonCodec.TeamRocketEdition,
        Tables = ExpansionTableSet.SharedSince18,
    };

    /// <summary>
    /// Black Pearl Emerald before 2.1 (tags bpe/v1.0.1 on expansion 1.8.6, bpe/v2.0.x on
    /// 1.16.3; 2.1 moved to a save engine of its own and refuses these saves). Its global.h is
    /// the expansion's, but its config grows SaveBlock1 (PC_ITEMS_COUNT 30, BAG_ITEMS_COUNT 150,
    /// BAG_TMHM_COUNT 250: the last SaveBlock1 sector validates only past the standard window)
    /// and frees Mystery Gift and the record-mixing hall records from SaveBlock2. Party, money
    /// and coins keep the expansion's offsets and the storage is the standard 14 boxes. Every
    /// block is checksummed over whole 3968-byte chunks (the game zero-fills them, so this
    /// equals its real block sizes). Items come from the hack's own enum.
    /// </summary>
    public static readonly ExpansionProfile BlackPearlLegacy = StandardExpansion with
    {
        Game = ExpansionGame.BlackPearlLegacy,
        GameName = "Black Pearl Emerald",
        SaveBlock2Size = 3968,
        SaveBlock1Size = 4 * 3968,
        Tables = ExpansionTableSet.BlackPearlLegacy,
    };

    /// <summary>
    /// ManicPacer's Fire Emerald (pokeemerald-expansion, 0.5 released July 2026, no public
    /// source): the expansion's blocks, offsets and records, but its sectors carry 3948 bytes
    /// of block data (0xF6C) and a 136-byte SaveBlock3 chunk in place of 3968 + 116. Read in
    /// 3968-byte chunks its box names seemed to sit 0xA0 late: that is 8 chunks × 20 bytes, and
    /// in 0xF6C chunks they are exactly at the standard 0x8344. The real save shows it: SaveBlock2
    /// bytes at 0xF6E (SaveBlock3 data) lie outside the id-0 checksum, and every sector
    /// validates over 0xF6C. Its storage carries custom data after the wallpapers (a rival
    /// name, a table), so blocks are checksummed over whole chunks (zero-filled by the game).
    /// </summary>
    public static readonly ExpansionProfile FireEmerald = StandardExpansion with
    {
        Game = ExpansionGame.FireEmerald,
        GameName = "Fire Emerald",
        SectorDataSize = FireEmeraldSectorDataSize,
        SaveBlock2Size = FireEmeraldSectorDataSize,
        SaveBlock1Size = 4 * FireEmeraldSectorDataSize,
        StorageSize = 9 * FireEmeraldSectorDataSize,
        Tables = ExpansionTableSet.SharedSince18,
    };

    private const int FireEmeraldSectorDataSize = 0xF6C;

    /// <summary>
    /// Lazarus: a standard expansion save (blocks, offsets and records) whose SaveBlock2 holds
    /// 4 more bytes before the encryption key, so the key sits at 0xB0, its copy at 0x1F8 and
    /// the Battle Frontier names 4 bytes later than upstream. Both real saves (a 0:30 start and
    /// a 177-hour Pokédex run) prove it: their money and coins only decode with the key at 0xB0.
    /// </summary>
    public static readonly ExpansionProfile Lazarus = StandardExpansion with
    {
        Game = ExpansionGame.Lazarus,
        GameName = "Lazarus",
        EncryptionKeyOffset = 0xB0,
    };

    /// <summary>
    /// Lucid: a standard expansion save whose SaveBlock2 is 8 bytes shorter before the
    /// encryption key: the key sits at 0xA4 and its copy at 0x1EC (the Frontier names 8 bytes
    /// earlier than upstream). The real save's money and coins only decode with the key at 0xA4.
    /// </summary>
    public static readonly ExpansionProfile Lucid = StandardExpansion with
    {
        Game = ExpansionGame.Lucid,
        GameName = "Lucid",
        EncryptionKeyOffset = 0xA4,
    };

    /// <summary>
    /// Emerald Enhanced: the expansion's SaveBlock1 and storage, but a SaveBlock2 of its own
    /// that dropped the Pokédex flags and the GameCube link word, so the encryption key sits at
    /// 0x34 (local time at 0x24) and has no copy. Both real saves' money and coins only decode
    /// with the key there.
    /// </summary>
    public static readonly ExpansionProfile EmeraldEnhanced = StandardExpansion with
    {
        Game = ExpansionGame.EmeraldEnhanced,
        GameName = "Emerald Enhanced",
        EncryptionKeyOffset = 0x34,
    };
}

/// <summary>
/// The expansion save envelope and the markers that identify its variants. Detection
/// never trusts the retail signature alone (every Emerald save has it): Heart and Soul
/// carries a SaveBlock1 magic, standard expansion saves must show at least one mon whose
/// decoded field carries an expansion-only bit, and Perfect Emerald and Team Rocket
/// Edition are proven by their 4084-byte windows plus their box-name runs.
/// </summary>
internal static class ExpansionFormat
{
    public const int FileSize = 0x20_000;
    public const int SectorSize = 0x1000;
    public const int SectorCount = 32;
    public const int SectionCount = 14;
    public const int SectorDataSize = 3968;
    public const int SaveBlock3Size = 116;
    public const int FooterSize = 12;
    public const uint Signature = 0x0801_2025;

    public const int SaveBlock1FirstSection = 1;
    public const int SaveBlock1Sections = 4;
    public const int StorageFirstSection = 5;
    public const int StorageSections = 9;

    private const ushort MaxHeartAndSoulSaveVersion = 0xFF;
    private const int SaveVersionMagicOffset = 0x10E0;
    private const uint SaveVersionMagic = 0xE8F8_28BC;

    /// <summary>
    /// The profile these bytes are, or null when they are not a save this engine can
    /// prove. The caller has already trimmed emulator padding.
    /// </summary>
    public static ExpansionProfile? LooksLike(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileSize)
            return null;
        var bytes = data[..FileSize];
        var sections = SectionOffsets(bytes);
        if (sections.Any(offset => offset < 0))
            return null;

        // Heart & Soul first: its magic is proof, and a Heart & Soul save in a layout the
        // engine does not read (the upstream 15-box / 4084-byte build) has Perfect
        // Emerald's geometry and must never be read through another profile.
        if (MatchesHeartAndSoul(bytes, sections))
            return ExpansionProfile.HeartAndSoul20;
        if (CarriesHeartAndSoulMagic(bytes, sections))
            return null;
        if (MatchesStandard(bytes, sections, ExpansionProfile.StandardExpansion))
            return NameStandard(bytes, sections, ExpansionProfile.StandardExpansion);
        if (MatchesStandard(bytes, sections, ExpansionProfile.StandardExpansionGrownSaveBlock2))
            return NameStandard(bytes, sections, ExpansionProfile.StandardExpansionGrownSaveBlock2);
        if (MatchesTeamRocketEdition(bytes, sections))
            return ExpansionProfile.TeamRocketEdition;
        if (MatchesPerfectEmerald(bytes, sections))
            return ExpansionProfile.PerfectEmerald;
        if (MatchesBlackPearlLegacy(bytes, sections))
            return ExpansionProfile.BlackPearlLegacy;
        if (MatchesFireEmerald(bytes, sections))
            return ExpansionProfile.FireEmerald;
        return null;
    }

    /// <summary>
    /// The standard save's own hack, when its SaveBlock2 layout proves one: the hacks that moved
    /// the encryption key are told apart by where the key really is. A key is proven when it is
    /// not zero, decodes SaveBlock1's money to at most 999,999 and its coins to at most 9,999
    /// (both XORed with the key, a random word: a wrong word passes about once in 30,000), and,
    /// where the hack keeps it, sits again 0x148 bytes later (the copy every save of these
    /// layouts writes). The name is given only when exactly one hack's key is proven and the
    /// upstream key at 0xAC does not decode the money: any doubt keeps the generic name, whose
    /// profile is otherwise identical (only the key and the name differ).
    /// </summary>
    private static ExpansionProfile NameStandard(ReadOnlySpan<byte> data, int[] sections, ExpansionProfile standard)
    {
        if (DecodesCurrency(data, sections, standard, standard.EncryptionKeyOffset))
            return standard;
        ExpansionProfile? named = null;
        foreach (var (hack, keyCopied) in NamedStandardHacks)
        {
            if (!ProvesKey(data, sections, hack, keyCopied))
                continue;
            if (named is not null)
                return standard;
            named = hack;
        }
        return named is null ? standard : named with { SaveBlock2Size = standard.SaveBlock2Size };
    }

    private static readonly (ExpansionProfile Hack, bool KeyCopied)[] NamedStandardHacks =
    [
        (ExpansionProfile.Lazarus, true),
        (ExpansionProfile.Lucid, true),
        (ExpansionProfile.EmeraldEnhanced, false),
    ];

    /// <summary>Distance from SaveBlock2's encryption key to the copy the game keeps of it.</summary>
    private const int EncryptionKeyCopyDistance = 0x148;

    private static bool ProvesKey(ReadOnlySpan<byte> data, int[] sections, ExpansionProfile profile, bool keyCopied)
    {
        var key = BinaryPrimitives.ReadUInt32LittleEndian(data[(sections[0] + profile.EncryptionKeyOffset)..]);
        if (key == 0 || !DecodesCurrency(data, sections, profile, profile.EncryptionKeyOffset))
            return false;
        return !keyCopied ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[(sections[0] + profile.EncryptionKeyOffset + EncryptionKeyCopyDistance)..]) == key;
    }

    /// <summary>True when the word at <paramref name="keyOffset"/> of SaveBlock2 turns the money
    /// and coins into amounts the game can hold (money and coins sit in SaveBlock1's first chunk).</summary>
    private static bool DecodesCurrency(ReadOnlySpan<byte> data, int[] sections, ExpansionProfile profile, int keyOffset)
    {
        var key = BinaryPrimitives.ReadUInt32LittleEndian(data[(sections[0] + keyOffset)..]);
        var saveBlock1 = sections[SaveBlock1FirstSection];
        var money = BinaryPrimitives.ReadUInt32LittleEndian(data[(saveBlock1 + profile.MoneyOffset)..]) ^ key;
        var coins = BinaryPrimitives.ReadUInt16LittleEndian(data[(saveBlock1 + profile.CoinsOffset)..]) ^ (ushort)key;
        return money <= MaxMoney && coins <= MaxCoins;
    }

    private const uint MaxMoney = 999_999;
    private const int MaxCoins = 9_999;

    /// <summary>
    /// Black Pearl Emerald before 2.1: a standard 14-box expansion save with expansion-coded
    /// Pokémon whose last SaveBlock1 sector needs more than the standard window (its bag config
    /// grew the block) and whose SaveBlock2 ends by 0xB00 (Mystery Gift and the record-mixing
    /// hall records freed): every byte of the live id-0 sector past it is zero.
    /// </summary>
    private static bool MatchesBlackPearlLegacy(ReadOnlySpan<byte> data, int[] sections)
    {
        var profile = ExpansionProfile.BlackPearlLegacy;
        if (!MatchesStandard(data, sections, profile))
            return false;
        if (ValidWindows(data, sections, ExpansionProfile.StandardExpansion, onlySection: SaveBlock1FirstSection + SaveBlock1Sections - 1))
            return false;
        if (data.Slice(sections[0] + BlackPearlSaveBlock2End, SectorDataSize - BlackPearlSaveBlock2End).IndexOfAnyExcept((byte)0) >= 0)
            return false;
        var storage = ReadBlock(data, sections, profile, StorageFirstSection, StorageSections, profile.StorageSize);
        return HasBoxNames(storage, profile);
    }

    /// <summary>Black Pearl's SaveBlock2 with Mystery Gift and the hall records freed
    /// (sizeof 0xB00 at bpe/v1.0.1 and bpe/v2.0.7-beta; the real save ends its data at 0xAE1).</summary>
    private const int BlackPearlSaveBlock2End = 0xB00;

    /// <summary>
    /// Fire Emerald: every live sector validates over 0xF6C, the 14 box names sit at the
    /// standard offset when the storage is reassembled in 0xF6C chunks and NOT when it is
    /// reassembled in 3968-byte ones (so the shorter chunk is real, not a zero tail), the
    /// Emerald encryption key, and every party record a valid PK3 (an empty party is allowed:
    /// the chunk size proven by the names is this hack's own).
    /// </summary>
    private static bool MatchesFireEmerald(ReadOnlySpan<byte> data, int[] sections)
    {
        var profile = ExpansionProfile.FireEmerald;
        if (!ValidWindows(data, sections, profile))
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[(sections[0] + profile.EncryptionKeyOffset)..]) == 1)
            return false;
        var storage = ReadBlock(data, sections, profile, StorageFirstSection, StorageSections, profile.StorageSize);
        if (!HasBoxNames(storage, profile))
            return false;
        var standard = ExpansionProfile.StandardExpansion;
        if (HasBoxNames(ReadBlock(data, sections, standard, StorageFirstSection, StorageSections, standard.StorageSize), standard))
            return false;
        var saveBlock1 = ReadBlock(data, sections, profile, SaveBlock1FirstSection, SaveBlock1Sections, profile.SaveBlock1Size);
        var count = saveBlock1[profile.PartyCountOffset];
        if (count > 6)
            return false;
        var maxSpecies = ExpansionData.Instance.MaxSpecies;
        for (var i = 0; i < count; i++)
        {
            RecordHasExpansionEvidence(saveBlock1.AsSpan(profile.PartyOffset + i * profile.PartyMonSize, profile.PartyMonSize), maxSpecies, out var valid);
            if (!valid)
                return false;
        }
        return true;
    }

    /// <summary>
    /// A pokeemerald-expansion save whose SaveBlock1 is larger than the standard 0x3D90
    /// (its last SaveBlock1 sector only validates over a longer window) but which is
    /// otherwise a standard 14-box save with expansion-coded Pokémon: the pre-2.1 Black
    /// Pearl Emerald layout. The engine does not know where the grown block moved its
    /// fields, so these saves are recognized, never read.
    /// </summary>
    public static bool LooksLikeGrownSaveBlock1(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileSize)
            return false;
        var bytes = data[..FileSize];
        var sections = SectionOffsets(bytes);
        if (sections.Any(offset => offset < 0) || LooksLike(bytes) is not null)
            return false;
        foreach (var standard in new[] { ExpansionProfile.StandardExpansion, ExpansionProfile.StandardExpansionGrownSaveBlock2 })
        {
            var grown = standard with { SaveBlock1Size = SaveBlock1Sections * SectorDataSize };
            if (MatchesStandard(bytes, sections, grown) && !ValidWindows(bytes, sections, standard, onlySection: 4))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Live (highest save index) physical offset of every logical sector id, or -1 when
    /// absent. Only the retail expansion signature participates; the Hall of Fame sector
    /// keeps its checksum in the id slot and would otherwise alias a real id.
    /// </summary>
    public static int[] SectionOffsets(ReadOnlySpan<byte> data)
    {
        var offsets = new int[SectionCount];
        Array.Fill(offsets, -1);
        var counters = new uint[SectionCount];
        for (var sector = 0; sector < SectorCount; sector++)
        {
            var offset = sector * SectorSize;
            if (offset + SectorSize > data.Length)
                break;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 0xFF8)..]) != Signature)
                continue;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 0xFF4)..]);
            if (id >= SectionCount)
                continue;
            var counter = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 0xFFC)..]);
            if (offsets[id] < 0 || counter >= counters[id])
            {
                offsets[id] = offset;
                counters[id] = counter;
            }
        }
        return offsets;
    }

    /// <summary>Every stamped physical sector, for backup-preservation checks.</summary>
    public static IEnumerable<(int Section, int Offset, uint Counter)> PhysicalSections(byte[] data)
    {
        for (var sector = 0; sector < SectorCount; sector++)
        {
            var offset = sector * SectorSize;
            if (offset + SectorSize > data.Length)
                yield break;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 0xFF8)) != Signature)
                continue;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 0xFF4));
            if (id >= SectionCount)
                continue;
            yield return (id, offset, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 0xFFC)));
        }
    }

    /// <summary>The checksum window of one logical sector, derived from its block's real size.</summary>
    public static int Window(ExpansionProfile profile, int sectionId) => sectionId switch
    {
        0 => Math.Min(profile.SaveBlock2Size, profile.SectorDataSize),
        >= 1 and <= 4 => Math.Min(
            profile.SaveBlock1Size - (sectionId - 1) * profile.SectorDataSize,
            profile.SectorDataSize),
        >= 5 and <= 13 => Math.Min(
            profile.StorageSize - (sectionId - 5) * profile.SectorDataSize,
            profile.SectorDataSize),
        _ => 0,
    };

    /// <summary>Retail Gen 3 checksum: u32 word sum folded to 16 bits over the window.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> data, int length)
    {
        uint total = 0;
        length &= ~3;
        for (var offset = 0; offset < length; offset += sizeof(uint))
            total += BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        return (ushort)(total + (total >> 16));
    }

    public static void WriteChecksum(byte[] data, int sectorOffset, int length) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(sectorOffset + 0xFF6),
            Checksum(data.AsSpan(sectorOffset, length), length));

    /// <summary>Reassembles a save block from its sector chunks using the profile's real size.</summary>
    public static byte[] ReadBlock(
        ReadOnlySpan<byte> data, int[] sections, ExpansionProfile profile, int firstSection, int sectionCount, int size)
    {
        var block = new byte[size];
        var cursor = 0;
        for (var id = firstSection; id < firstSection + sectionCount && cursor < size; id++)
        {
            var length = Math.Min(size - cursor, profile.SectorDataSize);
            if (length <= 0)
                break;
            data.Slice(sections[id], length).CopyTo(block.AsSpan(cursor));
            cursor += length;
        }
        return block;
    }

    /// <summary>Copies a span of a working block back into its live sector chunks.</summary>
    public static void WriteBlockRange(
        byte[] data, int[] sections, ExpansionProfile profile, int firstSection, int sectionCount, int blockOffset, ReadOnlySpan<byte> value)
    {
        var blockSize = firstSection == SaveBlock1FirstSection ? profile.SaveBlock1Size : profile.StorageSize;
        var blockEnd = blockOffset + value.Length;
        for (var id = firstSection; id < firstSection + sectionCount; id++)
        {
            var chunkStart = (id - firstSection) * profile.SectorDataSize;
            var chunkLength = Math.Min(profile.SectorDataSize, blockSize - chunkStart);
            if (chunkLength <= 0)
                break;
            var chunkEnd = chunkStart + chunkLength;
            var copyStart = Math.Max(blockOffset, chunkStart);
            var copyEnd = Math.Min(blockEnd, chunkEnd);
            if (copyEnd <= copyStart)
                continue;
            var sourceOffset = copyStart - blockOffset;
            var targetOffset = copyStart - chunkStart;
            var length = copyEnd - copyStart;
            value.Slice(sourceOffset, length).CopyTo(data.AsSpan(sections[id] + targetOffset, length));
            WriteChecksum(data, sections[id], Window(profile, id));
        }
    }

    /// <summary>Writes one live sector's data prefix and recomputes only its own checksum.</summary>
    public static void CommitSection(byte[] data, int[] sections, ExpansionProfile profile, int sectionId)
    {
        var length = Window(profile, sectionId);
        if (length <= 0)
            return;
        WriteChecksum(data, sections[sectionId], length);
    }

    /// <summary>
    /// A record is an expansion mon when its Gen 3 header/checksum validate and its
    /// bitfields use something vanilla Gen 3 cannot store: species above the internal
    /// Gen 3 range, a non-zero Tera type, an evolution tracker, or an 11-bit move.
    /// </summary>
    internal static bool RecordHasExpansionEvidence(ReadOnlySpan<byte> record, int maxSpecies, out bool valid)
    {
        valid = false;
        var buffer = new byte[100];
        record.CopyTo(buffer);
        var pk = new PK3(buffer);
        if (!pk.ChecksumValid)
            return false;
        var data = pk.Data;
        var species = (int)(BinaryPrimitives.ReadUInt16LittleEndian(data[0x20..]) & 0x7FF);
        if (species == 0 || species > maxSpecies)
            return false;
        valid = true;
        var tera = (BinaryPrimitives.ReadUInt16LittleEndian(data[0x20..]) >> 11) & 0x1F;
        if (species > 511 || tera != 0)
            return true;
        for (var i = 0; i < 4; i++)
        {
            var word = BinaryPrimitives.ReadUInt16LittleEndian(data[(0x2C + i * 2)..]);
            if ((word & 0x7FF) > 511)
                return true;
            if ((word >> 11) != 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the bytes carry Heart &amp; Soul's SaveBlock1 magic, in any layout: the one
    /// the engine reads (14 boxes, 3968-byte chunks) or another build's (the upstream
    /// repository has written 15 boxes in 4084-byte chunks since its first commit). A
    /// save that carries it but is not read by the engine is recognized and never
    /// written as vanilla Emerald or read through another expansion profile.
    /// </summary>
    public static bool CarriesHeartAndSoulMagic(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileSize)
            return false;
        var bytes = data[..FileSize];
        var sections = SectionOffsets(bytes);
        return !sections.Any(offset => offset < 0) && CarriesHeartAndSoulMagic(bytes, sections);
    }

    /// <summary>
    /// Heart &amp; Soul stamps <c>0xE8F828BC</c> into SaveBlock1, followed by a u16 save
    /// version (0 in every real save seen; upstream counted 2 to 7). The u16 at
    /// SaveBlock1+0 is the player's X coordinate, NOT a version, so it is never read.
    /// SaveBlock1 is reassembled under both chunk sizes, and the magic is searched at
    /// every aligned offset so a build that moved it still matches; the small version
    /// that must follow keeps a random hit on a vanilla save out of reach.
    /// </summary>
    private static bool CarriesHeartAndSoulMagic(ReadOnlySpan<byte> data, int[] sections)
    {
        foreach (var chunk in new[] { SectorDataSize, SectorSize - FooterSize })
        {
            var profile = ExpansionProfile.HeartAndSoul20 with { SectorDataSize = chunk, SaveBlock1Size = SaveBlock1Sections * chunk };
            var saveBlock1 = ReadBlock(data, sections, profile, SaveBlock1FirstSection, SaveBlock1Sections, profile.SaveBlock1Size);
            for (var offset = 0; offset + 6 <= saveBlock1.Length; offset += 4)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(saveBlock1.AsSpan(offset)) == SaveVersionMagic
                    && BinaryPrimitives.ReadUInt16LittleEndian(saveBlock1.AsSpan(offset + 4)) <= MaxHeartAndSoulSaveVersion)
                    return true;
            }
        }
        return false;
    }

    /// <summary>The layout the engine reads: Heart and Soul's 3968-byte windows and its
    /// magic exactly where those saves keep it (SaveBlock1+0x10E0).</summary>
    private static bool MatchesHeartAndSoul(ReadOnlySpan<byte> data, int[] sections)
    {
        var profile = ExpansionProfile.HeartAndSoul20;
        if (!ValidWindows(data, sections, profile))
            return false;
        var saveBlock1 = ReadBlock(data, sections, profile, SaveBlock1FirstSection, SaveBlock1Sections, profile.SaveBlock1Size);
        return BinaryPrimitives.ReadUInt32LittleEndian(saveBlock1.AsSpan(SaveVersionMagicOffset)) == SaveVersionMagic;
    }

    /// <summary>
    /// Team Rocket Edition: its 4084-byte windows hold on every live sector, at least one
    /// SaveBlock1 sector really uses the longer window (a 3968-byte save validates there
    /// too when its tail is zero), all 14 box names sit where the standard storage keeps
    /// them under 4084-byte reassembly, the encryption key is Emerald's (FireRed keeps
    /// its game code there), and every party record is a valid PK3.
    /// </summary>
    private static bool MatchesTeamRocketEdition(ReadOnlySpan<byte> data, int[] sections)
    {
        var profile = ExpansionProfile.TeamRocketEdition;
        if (!ValidWindows(data, sections, profile))
            return false;
        var longer = false;
        for (var id = SaveBlock1FirstSection; id < SaveBlock1FirstSection + SaveBlock1Sections; id++)
            longer |= !ValidWindows(data, sections, ExpansionProfile.StandardExpansion with { SaveBlock1Size = SaveBlock1Sections * SectorDataSize }, onlySection: id);
        if (!longer)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[(sections[0] + profile.EncryptionKeyOffset)..]) == 1)
            return false;
        var storage = ReadBlock(data, sections, profile, StorageFirstSection, StorageSections, profile.StorageSize);
        if (!HasBoxNames(storage, profile))
            return false;
        var saveBlock1 = ReadBlock(data, sections, profile, SaveBlock1FirstSection, SaveBlock1Sections, profile.SaveBlock1Size);
        var count = saveBlock1[profile.PartyCountOffset];
        if (count > 6)
            return false;
        var maxSpecies = ExpansionData.Instance.MaxSpecies;
        for (var i = 0; i < count; i++)
        {
            RecordHasExpansionEvidence(saveBlock1.AsSpan(profile.PartyOffset + i * profile.PartyMonSize, profile.PartyMonSize), maxSpecies, out var valid);
            if (!valid)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Every box of the profile has a name the game could have written: a terminated
    /// string of at most 8 characters that starts with a glyph and holds no NUL before
    /// its terminator, followed by one wallpaper byte per box below 32.
    /// </summary>
    internal static bool HasBoxNames(ReadOnlySpan<byte> storage, ExpansionProfile profile) =>
        HasBoxNames(storage, profile.BoxNamesOffset, profile.BoxCount);

    internal static bool HasBoxNames(ReadOnlySpan<byte> storage, int offset, int boxes)
    {
        if (offset + boxes * 10 > storage.Length)
            return false;
        for (var box = 0; box < boxes; box++)
        {
            var name = storage.Slice(offset + box * 9, 9);
            var end = name.IndexOf((byte)0xFF);
            if (end <= 0 || name[..end].IndexOf((byte)0) >= 0)
                return false;
        }
        for (var box = 0; box < boxes; box++)
            if (storage[offset + boxes * 9 + box] >= 32)
                return false;
        return true;
    }

    private static bool MatchesStandard(ReadOnlySpan<byte> data, int[] sections, ExpansionProfile profile)
    {
        if (!ValidWindows(data, sections, profile))
            return false;

        var saveBlock1 = ReadBlock(data, sections, profile, SaveBlock1FirstSection, SaveBlock1Sections, profile.SaveBlock1Size);
        var count = saveBlock1[profile.PartyCountOffset];
        if (count > 6)
            return false;
        var maxSpecies = ExpansionData.Instance.MaxSpecies;

        // One foreign-species record is not proof of the expansion LAYOUT: a retail save
        // with a single injected mon must stay a suspected hack (read-only until the
        // player accepts). An expansion record in the party is strong (real early saves
        // carry the starter there); otherwise two independent records anywhere prove it.
        for (var i = 0; i < count; i++)
        {
            var record = saveBlock1.AsSpan(profile.PartyOffset + i * profile.PartyMonSize, profile.PartyMonSize);
            if (RecordHasExpansionEvidence(record, maxSpecies, out _))
                return true;
        }

        var evidence = 0;
        var storage = ReadBlock(data, sections, profile, StorageFirstSection, StorageSections, profile.StorageSize);
        for (var slot = 0; slot < profile.BoxCount * profile.BoxSlotCount; slot++)
        {
            var offset = profile.StorageBoxOffset + slot * profile.BoxMonSize;
            var record = storage.AsSpan(offset, profile.BoxMonSize);
            if (record.IndexOfAnyExcept((byte)0) < 0)
                continue;
            if (!RecordHasExpansionEvidence(record, maxSpecies, out _))
                continue;
            if (++evidence >= 2)
                return true;
        }
        return false;
    }

    private static bool MatchesPerfectEmerald(ReadOnlySpan<byte> data, int[] sections)
    {
        var profile = ExpansionProfile.PerfectEmerald;
        if (!ValidWindows(data, sections, profile))
            return false;
        // A standard-expansion save validates at 3968 too (its unused tail is zero);
        // Perfect Emerald writes 4084-byte chunks, so at least one sector must depend on
        // the longer window.
        var shorterWindowsAllMatch = true;
        for (var id = 0; id < SectionCount; id++)
        {
            var offset = sections[id];
            var stored = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 0xFF6)..]);
            var length = Math.Min(SectorDataSize, SectorSize - FooterSize);
            if (Checksum(data.Slice(offset, length), length) != stored)
            {
                shorterWindowsAllMatch = false;
                break;
            }
        }
        if (shorterWindowsAllMatch)
            return false;

        var saveBlock1 = ReadBlock(data, sections, profile, SaveBlock1FirstSection, SaveBlock1Sections, profile.SaveBlock1Size);
        if (saveBlock1[profile.PartyCountOffset] > 6)
            return false;
        var storage = ReadBlock(data, sections, profile, StorageFirstSection, StorageSections, profile.StorageSize);
        var names = 0;
        for (var box = 0; box < profile.BoxCount; box++)
        {
            var span = storage.AsSpan(profile.BoxNamesOffset + box * 9, 9);
            if (span.IndexOfAnyExcept((byte)0) >= 0 && span[0] != 0xFF)
                names++;
        }
        return names >= 10;
    }

    private static bool ValidWindows(ReadOnlySpan<byte> data, int[] sections, ExpansionProfile profile, int? onlySection = null)
    {
        for (var id = 0; id < SectionCount; id++)
        {
            if (onlySection is { } only && id != only)
                continue;
            var offset = sections[id];
            var length = Window(profile, id);
            if (length <= 0)
                return false;
            var stored = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 0xFF6)..]);
            if (Checksum(data.Slice(offset, length), length) != stored)
                return false;
        }
        return true;
    }
}
