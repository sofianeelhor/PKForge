using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine.Quetzal;

/// <summary>
/// One of Pokémon Quetzal's three PC record layouts. The game changed the record size
/// between builds without marking the save, so the layout is identified from the data:
/// a wrong stride cannot pass every non-empty record's field-range check in exactly one
/// candidate.
/// </summary>
internal readonly record struct QuetzalBoxFormat(int RecordSize, int BoxCount, int NicknameOffset, int PpOffset)
{
    public const int SlotsPerBox = 30;
}

/// <summary>
/// Raw Quetzal save geometry. A 128 KB flash holds two slots of 16 sectors; the sector id
/// in the footer says where a sector belongs, and the newest slot is the one with the larger
/// save counter. Only the first 0xF80 bytes of each sector carry block data (the tail up to
/// 0xFF4 is empty in every save seen), so the PC is 11 such chunks concatenated. Everything
/// here is pure layout; <see cref="QuetzalEngineSession"/> owns mutation.
/// </summary>
internal static class QuetzalFormat
{
    internal const int FileSize = 0x20000;
    internal const int SectorSize = 0x1000;
    internal const int SectorDataSize = 0xFF4;
    internal const int ChunkSize = 0xF80;
    internal const int SlotsPerSave = 16;
    internal const int SlotCount = 2;

    internal const int FooterIdOffset = 0xFF4;
    internal const int FooterChecksumOffset = 0xFF6;
    internal const int FooterSignatureOffset = 0xFF8;
    internal const int FooterCounterOffset = 0xFFC;
    internal const uint Signature = 0x08012025;

    // SaveBlock2 is section 0; SaveBlock1 is sections 1-4, 0xFF4 bytes each.
    internal const int SaveBlock2Offset = 0x000;
    internal const int SaveBlock1Offset = 0x000;
    internal const int PartyCountOffset = 0x6A4;
    internal const int PartyOffset = 0x6A8;
    internal const int PartyRecordSize = 0x68;
    internal const int PartySize = 6;

    // PC: sections 5-15, first 0xF80 bytes of each, concatenated.
    internal const int PcFirstSection = 5;
    internal const int PcSectionCount = SlotsPerSave - PcFirstSection;
    internal const int PcLength = PcSectionCount * ChunkSize; // 0xAA80
    internal const int RecordsOffset = 0x461;
    internal const int BoxNameOffset = 0x001;
    internal const int BoxNameLength = 9;
    internal const int MaxBoxNames = 67;

    internal const int MaxSpeciesId = 1528;
    internal const int MaxMoveId = 848;
    internal const int MaxExpField = 105_986; // exp / 10 must fit 17 bits and the level-100 cap
    internal const int MaxExperience = 1_059_860;

    private const int NationalSpeciesMax = 1025;

    /// <summary>A save is Quetzal when it has a complete 16-id slot with valid checksums
    /// and a plausible party in the newest slot. An mGBA RTC-footer dump (0x20010) is
    /// trimmed first, exactly as the session does.</summary>
    internal static bool LooksLike(ReadOnlySpan<byte> data)
    {
        var payload = data.Length == FileSize ? data : SramPadding.Trim(data.ToArray());
        if (payload.Length != FileSize) return false;
        if (!TryGetNewestSlot(payload, out var sections)) return false;
        var count = PartyCount(payload, sections);
        if (count is < 1 or > PartySize) return false;
        for (var slot = 0; slot < count; slot++)
        {
            var offset = PartyRecordOffset(sections, slot);
            var record = payload.Slice(offset, PartyRecordSize);
            var species = BinaryPrimitives.ReadUInt16LittleEndian(record[0x28..]);
            if (species is < 1 or > MaxSpeciesId) continue;
            var experience = BinaryPrimitives.ReadUInt32LittleEndian(record[0x2C..]);
            var level = record[0x58];
            if (experience > MaxExperience || level is < 1 or > 100) continue;
            if (LevelForExperience(experience) == level) return true;
        }
        return false;
    }

    /// <summary>The 16 file offsets of the newest complete slot's sectors, indexed by
    /// logical section id. Ties and unreadable newest slots fall back like bills-pc:
    /// the second slot wins a tie, and only slots whose checksums all hold compete.</summary>
    internal static bool TryGetNewestSlot(ReadOnlySpan<byte> data, out int[] sections)
    {
        sections = [];
        if (data.Length != FileSize) return false;
        int[]? first = null, second = null;
        if (TryReadSlot(data, 0, out var a, out var firstCount)) first = a;
        if (TryReadSlot(data, SlotsPerSave, out var b, out var secondCount)) second = b;
        if (first is null && second is null) return false;
        sections = first is null ? second! : second is null ? first : secondCount >= firstCount ? second : first;
        return true;
    }

    private static bool TryReadSlot(ReadOnlySpan<byte> data, int firstSector, out int[] sections, out uint counterSum)
    {
        sections = new int[SlotsPerSave];
        Array.Fill(sections, -1);
        counterSum = 0;
        for (var index = firstSector; index < firstSector + SlotsPerSave; index++)
        {
            var offset = index * SectorSize;
            var footer = data.Slice(offset + FooterIdOffset, 12);
            if (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) != Signature) return false;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(footer);
            if (id >= SlotsPerSave || sections[id] != -1) return false;
            if (Checksum(data, offset) != BinaryPrimitives.ReadUInt16LittleEndian(footer[2..])) return false;
            sections[id] = offset;
            counterSum = unchecked(counterSum + BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]));
        }
        return Array.IndexOf(sections, -1) < 0;
    }

    /// <summary>Gen 3 section checksum over the full 0xFF4 data window: the folded sum of
    /// its little-endian 32-bit words.</summary>
    internal static ushort Checksum(ReadOnlySpan<byte> data, int sectorOffset)
    {
        uint sum = 0;
        for (var offset = 0; offset < SectorDataSize; offset += 4)
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32LittleEndian(data[(sectorOffset + offset)..]));
        return (ushort)((sum & 0xFFFF) + (sum >> 16));
    }

    internal static void WriteChecksum(Span<byte> data, int sectorOffset) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data[(sectorOffset + FooterChecksumOffset)..], Checksum(data, sectorOffset));

    internal static int PartyCount(ReadOnlySpan<byte> data, int[] sections) =>
        (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[(sections[1] + PartyCountOffset)..]), (uint)PartySize);

    internal static int PartyRecordOffset(int[] sections, int slot) =>
        sections[1] + PartyOffset + slot * PartyRecordSize;

    /// <summary>File offset of one byte of the logical PC block. A record can straddle two
    /// section chunks, which are not physically adjacent in the file.</summary>
    private static int PcFileOffset(int[] sections, int offset) =>
        sections[PcFirstSection + offset / ChunkSize] + offset % ChunkSize;

    internal static void ReadPc(ReadOnlySpan<byte> data, int[] sections, int offset, Span<byte> destination)
    {
        for (var i = 0; i < destination.Length; i++)
            destination[i] = data[PcFileOffset(sections, offset + i)];
    }

    internal static void WritePc(Span<byte> data, int[] sections, int offset, ReadOnlySpan<byte> source)
    {
        for (var i = 0; i < source.Length; i++)
            data[PcFileOffset(sections, offset + i)] = source[i];
    }

    internal static int RecordOffset(QuetzalBoxFormat format, int box, int slot) =>
        RecordsOffset + (box * QuetzalBoxFormat.SlotsPerBox + slot) * format.RecordSize;

    /// <summary>Identifies the PC record layout: exactly one candidate must validate every
    /// non-empty record and hold at least one occupied one.</summary>
    internal static QuetzalBoxFormat? DetectBoxFormat(ReadOnlySpan<byte> data, int[] sections)
    {
        QuetzalBoxFormat? found = null;
        Span<byte> record = stackalloc byte[38];
        foreach (var candidate in Candidates())
        {
            if (RecordsOffset + candidate.BoxCount * QuetzalBoxFormat.SlotsPerBox * candidate.RecordSize > PcLength)
                continue;
            var occupied = 0;
            var valid = true;
            for (var slot = 0; slot < candidate.BoxCount * QuetzalBoxFormat.SlotsPerBox; slot++)
            {
                var buffer = record[..candidate.RecordSize];
                ReadPc(data, sections, RecordOffset(candidate, 0, 0) + slot * candidate.RecordSize, buffer);
                if (buffer.IndexOfAnyExcept((byte)0) < 0) continue;
                if (!IsValidRecord(buffer)) { valid = false; break; }
                occupied++;
            }
            if (!valid || occupied == 0) continue;
            if (found is not null) return null; // ambiguous: never guess
            found = candidate;
        }
        return found;

        static IEnumerable<QuetzalBoxFormat> Candidates()
        {
            yield return new QuetzalBoxFormat(21, 67, -1, -1);
            yield return new QuetzalBoxFormat(31, 45, 21, -1);
            yield return new QuetzalBoxFormat(38, 37, 28, 24);
        }
    }

    private static bool IsValidRecord(ReadOnlySpan<byte> record)
    {
        var species = ReadField(record, 0, 11);
        if (species is < 1 or > MaxSpeciesId) return false;
        if (ReadField(record, 21, 17) > MaxExpField) return false;
        if (ReadField(record, 161, 5) > 24) return false;
        if (ReadField(record, 166, 2) > 2) return false;
        for (var move = 0; move < 4; move++)
            if (ReadField(record, 48 + move * 10, 10) > MaxMoveId) return false;
        return true;
    }

    /// <summary>One LSB-first bit field, as the hack's PC records pack them.</summary>
    internal static int ReadField(ReadOnlySpan<byte> record, int startBit, int width)
    {
        var value = 0;
        for (var bit = 0; bit < width; bit++)
        {
            var position = startBit + bit;
            value |= ((record[position >> 3] >> (position & 7)) & 1) << bit;
        }
        return value;
    }

    internal static void WriteField(Span<byte> record, int startBit, int width, int value)
    {
        for (var bit = 0; bit < width; bit++)
        {
            var position = startBit + bit;
            var mask = (byte)(1 << (position & 7));
            var index = position >> 3;
            if (((value >> bit) & 1) != 0) record[index] |= mask;
            else record[index] &= (byte)~mask;
        }
    }

    // ── Experience (Quetzal uses Medium Slow for every species) ──

    internal static int ExperienceForLevel(int level)
    {
        if (level <= 1) return 0;
        long n = Math.Clamp(level, 1, 100);
        var value = 6 * n * n * n / 5 - 15 * n * n + 100 * n - 140;
        return value <= 0 ? 0 : (int)value;
    }

    internal static int LevelForExperience(uint experience)
    {
        var level = 1;
        for (var candidate = 2; candidate <= 100; candidate++)
        {
            if (ExperienceForLevel(candidate) > experience) break;
            level = candidate;
        }
        return level;
    }

    /// <summary>The box stores EXP/10 in 17 bits, so a level's threshold rounds up to the
    /// next multiple of ten: down would decode one level lower.</summary>
    internal static uint BoxExperienceForLevel(int level) =>
        (uint)Math.Min(MaxExperience, (ExperienceForLevel(level) + 9) / 10 * 10);

    // ── Names (no vendored Quetzal tables: PKHeX's national names, raw id otherwise) ──

    private static GameStrings Strings => GameInfo.GetStrings("en");

    internal static string SpeciesName(int species)
    {
        if (species <= 0) return string.Empty;
        var names = Strings.specieslist;
        return (uint)species < (uint)names.Length && names[species].Length > 0
            ? names[species]
            : $"Species {species}";
    }

    internal static string MoveName(int move)
    {
        if (move <= 0) return string.Empty;
        var names = Strings.movelist;
        return (uint)move < (uint)names.Length && names[move].Length > 0
            ? names[move]
            : $"Move {move}";
    }

    internal static string ItemName(int item)
    {
        if (item <= 0) return string.Empty;
        var names = Strings.itemlist;
        return (uint)item < (uint)names.Length && names[item].Length > 0
            ? names[item]
            : $"Item {item}";
    }

    internal static IReadOnlyList<string> ItemNames() => Strings.itemlist;

    // ── Personal data (Gen 3 table for its own species, modern table above it) ──

    /// <summary>
    /// The ability a stored ability slot (0-2) gives a species, as a PKHeX id: the species'
    /// first or second ability (the second falls back to the first when the species has one,
    /// as Gen 3 does), or 0 for a slot the personal data does not name (a Gen 3 species' third).
    /// </summary>
    internal static int AbilityAt(int species, int slot)
    {
        if (PersonalOf(species) is not { AbilityCount: > 0 } personal || slot is < 0 or > 2) return 0;
        var ability = slot < personal.AbilityCount ? personal.GetAbilityAtIndex(slot) : 0;
        return ability == 0 && slot == 1 ? personal.GetAbilityAtIndex(0) : ability;
    }

    /// <summary>The slot that gives <paramref name="ability"/>, or null when none of the three does.</summary>
    internal static int? AbilitySlotOf(int species, int ability)
    {
        if (ability <= 0) return null;
        for (var slot = 0; slot < 3; slot++)
            if (AbilityAt(species, slot) == ability) return slot;
        return null;
    }

    internal static PersonalInfo? PersonalOf(int species)
    {
        if (species is < 1 or > NationalSpeciesMax) return null;
        if (species <= 386) return PersonalTable.E[species];
        return PersonalTable.SV.GetFormEntry((ushort)species, 0);
    }

    /// <summary>Base stats in app order: HP, Atk, Def, SpA, SpD, Spe.</summary>
    internal static int[] BaseStats(int species)
    {
        var info = PersonalOf(species);
        return info is null
            ? [50, 50, 50, 50, 50, 50]
            : [info.HP, info.ATK, info.DEF, info.SPA, info.SPD, info.SPE];
    }

    internal static int[] TypesOf(int species)
    {
        var info = PersonalOf(species);
        if (info is null) return [0];
        return info.Type1 == info.Type2 ? [info.Type1] : [info.Type1, info.Type2];
    }

    internal static int GenderOf(uint pid, int species)
    {
        var info = PersonalOf(species);
        if (info is null) return 2;
        return info.Gender switch
        {
            PersonalInfo.RatioMagicGenderless => 2,
            PersonalInfo.RatioMagicFemale => 1,
            PersonalInfo.RatioMagicMale => 0,
            var threshold => (pid & 0xFF) < threshold ? 1 : 0,
        };
    }

    internal static int GenderThreshold(int species) => PersonalOf(species)?.Gender ?? PersonalInfo.RatioMagicGenderless;

    /// <summary>Gen 3 stat formula with the Quetzal record's IV/EV/nature values; app order.</summary>
    internal static int[]? ComputeStats(int species, int level, IReadOnlyList<int> ivs, IReadOnlyList<int> evs, int nature)
    {
        var info = PersonalOf(species);
        if (info is null) return null;
        int[] baseStats = [info.HP, info.ATK, info.DEF, info.SPA, info.SPD, info.SPE];
        var result = new int[6];
        for (var stat = 0; stat < 6; stat++)
        {
            var core = (2 * baseStats[stat] + ivs[stat] + evs[stat] / 4) * Math.Clamp(level, 1, 100) / 100;
            if (stat == 0)
            {
                result[stat] = core + Math.Clamp(level, 1, 100) + 10;
                continue;
            }
            var (plus, minus) = NatureModifiers(nature);
            var modifier = plus == stat ? 1.1 : minus == stat ? 0.9 : 1.0;
            result[stat] = (int)((core + 5) * modifier);
        }
        return result;
    }

    /// <summary>App stat index a nature raises and lowers (HP excluded).</summary>
    internal static (int Plus, int Minus) NatureModifiers(int nature) => nature switch
    {
        1 => (1, 2), 2 => (1, 5), 3 => (1, 3), 4 => (1, 4),
        5 => (2, 1), 7 => (2, 5), 8 => (2, 3), 9 => (2, 4),
        10 => (5, 1), 11 => (5, 2), 13 => (5, 3), 14 => (5, 4),
        15 => (3, 1), 16 => (3, 2), 17 => (3, 5), 19 => (3, 4),
        20 => (4, 1), 21 => (4, 2), 22 => (4, 5), 23 => (4, 3),
        _ => (0, 0),
    };

    /// <summary>App order (HP, Atk, Def, SpA, SpD, Spe) to the record's storage order
    /// (HP, Atk, Def, Spe, SpA, SpD).</summary>
    internal static readonly int[] AppToStorage = [0, 1, 2, 4, 5, 3];
}
