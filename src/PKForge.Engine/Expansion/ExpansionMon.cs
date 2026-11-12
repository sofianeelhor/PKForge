using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine.Expansion;

/// <summary>
/// One expansion <c>BoxPokemon</c> (80 bytes) or party <c>Pokemon</c> (100 bytes).
///
/// The header, the Gen 3 XOR cipher and the 48-byte substruct checksum are stock, so
/// PKHeX's <see cref="PK3"/> does the crypto and gives us the decrypted, unshuffled
/// 12-byte substructs. Expansion repacks the substruct fields (11-bit species, 21-bit
/// EXP, ball in growth, ability slot in the ribbon word, 11-bit moves, dynamax level),
/// so every field below is read and written directly in that decrypted block. Fields
/// this engine does not expose are left byte-for-byte untouched.
/// </summary>
internal sealed class ExpansionMon
{
    private const int HeaderSize = 0x20;

    private readonly byte[] _buffer;
    private readonly int _offset;
    private readonly ExpansionData _data;
    private readonly PK3 _pk;

    /// <summary>
    /// The record layout. <see cref="ExpansionMonCodec.TeamRocketEdition"/> was measured on the
    /// hack's only real save (29 records, no public source):
    /// <list type="bullet">
    /// <item>0x12 holds language (bits 0-2, = 2) AND the flags: bit 4 is set on every record, as
    /// hasSpecies must be, and no hidden-nature modifier exists (party stats match PID % 25).
    /// The original trainer's name is 7 bytes at 0x13 ("Jason", FF-padded; 0x1A stays 0 where
    /// an 8-byte copy of SaveBlock2's name would leave FF), markings at 0x1B.</item>
    /// <item>Substruct 0: species, held item and the 21-bit EXP are the expansion's (EXP equals
    /// the growth curve at each party level). Byte 0x08 is friendship (70 / 35, each species'
    /// base value); PP bonuses are not at 0x08 or 0x09 (every move's PP is its base PP while
    /// those bytes read 0x46 / 0x5A), so their place, the ball's and the 11th/12th nickname
    /// characters' are unknown.</item>
    /// <item>Substruct 3: the ability slot is bits 24-25 of the ribbon word (on the randomly
    /// generated records it follows PID bit 0, the game's rule, where the expansion's bits
    /// 29-30 are always 0); bit 26 is set on every record and its meaning is unknown.</item>
    /// <item>Moves, PP, EVs (party stats reproduce them) and IVs are the expansion's.</item>
    /// </list>
    /// Unknown fields are never written: their setters refuse (see <see cref="Unproven"/>).
    /// </summary>
    private readonly ExpansionMonCodec _codec;

    private bool TeamRocket => _codec == ExpansionMonCodec.TeamRocketEdition;

    public ExpansionMon(byte[] buffer, int offset, bool party, ExpansionData data, ExpansionMonCodec codec = ExpansionMonCodec.Standard)
    {
        _buffer = buffer;
        _offset = offset;
        _data = data;
        _codec = codec;
        IsParty = party;
        var record = new byte[100];
        buffer.AsSpan(offset, party ? 100 : 80).CopyTo(record);
        _pk = new PK3(record);
    }

    public bool IsParty { get; }

    private Span<byte> Data => _pk.Data;

    public bool IsEmpty => Pid == 0 && Otid == 0 && Species == 0;

    public bool LooksValid => Species != 0 && _pk.ChecksumValid;

    public uint Pid
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Data);
        set => BinaryPrimitives.WriteUInt32LittleEndian(Data, value);
    }

    public uint Otid
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Data[4..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(Data[4..], value);
    }

    public int Species => (int)(BinaryPrimitives.ReadUInt16LittleEndian(Data[0x20..]) & 0x7FF);

    public int TeraType => (BinaryPrimitives.ReadUInt16LittleEndian(Data[0x20..]) >> 11) & 0x1F;

    public int HeldItem => (int)ReadBits(0, 16, 10);

    public uint Experience => (uint)ReadBits(0, 32, 21);

    /// <summary>The ball, or 0 where the codec's ball field is not known.</summary>
    public int Ball => TeamRocket ? 0 : (int)ReadBits(0, 80, 6);

    public int Friendship => Data[TeamRocket ? 0x28 : 0x29];

    /// <summary>PP Up counts; 0 where the codec's field is not known (every move then shows its base PP, as the game does).</summary>
    public int PpBonuses => TeamRocket ? 0 : Data[0x28];

    /// <summary>The fields this record's codec leaves unproven, so they are shown but never written.</summary>
    public bool KnowsBall => !TeamRocket;
    public bool KnowsPpBonuses => !TeamRocket;
    public bool KnowsLongNickname => !TeamRocket;
    public bool KnowsHeader => !TeamRocket;
    public bool KnowsIvOrder => !TeamRocket;
    public bool KnowsAbilitySlotNames => !TeamRocket;

    public int Level => IsParty ? Data[0x54] : _data.LevelForExperience(Species, Experience);

    public int Nature => (int)(Pid % 25);

    public int Gender
    {
        get
        {
            var ratio = _data.GenderRatio(Species);
            if (ratio == 255) return 2;
            if (ratio == 0) return 0;
            if (ratio == 254) return 1;
            return (Pid & 0xFF) < ratio ? 1 : 0;
        }
    }

    public bool IsShiny => ShinyOf(Pid, Otid);

    /// <summary>Bit of substruct 3 where the 2-bit ability slot starts (the ribbon word's bit 29, or 24 in Team Rocket Edition).</summary>
    private int AbilityBit => TeamRocket ? 88 : 93;

    public int AbilitySlot => (int)ReadBits(3, AbilityBit, 2) & 0x3;

    public int ActiveAbility
    {
        get
        {
            var (first, second, hidden) = _data.Abilities(Species);
            return AbilitySlot switch
            {
                2 when hidden != 0 => hidden,
                1 when second != 0 => second,
                _ => first,
            };
        }
    }

    public string Nickname
    {
        get
        {
            var bytes = new byte[12];
            var count = 0;
            var terminated = false;
            var first = Data.Slice(0x08, 10);
            for (var i = 0; i < first.Length && count < bytes.Length; i++)
            {
                var value = first[i];
                if (value is 0xFF or 0x00)
                {
                    terminated = true;
                    break;
                }
                bytes[count++] = value;
            }
            // The 11th/12th characters only exist when the 10-byte field is full; a
            // terminator inside it ends the string and the spare bits may hold stale data.
            if (!terminated && KnowsLongNickname)
            {
                var nickname11 = (byte)ReadBits(0, 53, 8);
                var nickname12 = (byte)ReadBits(0, 86, 8);
                if (nickname11 is not (0xFF or 0x00) && count < bytes.Length)
                    bytes[count++] = nickname11;
                if (nickname12 is not (0xFF or 0x00) && count < bytes.Length)
                    bytes[count++] = nickname12;
            }
            return StringConverter3.GetString(bytes.AsSpan(0, count), jp: false);
        }
        set
        {
            var temp = new byte[12];
            temp.AsSpan().Fill(0xFF);
            if (!KnowsLongNickname)
            {
                // Only the 10-byte header field: where (and whether) this codec keeps an 11th
                // and 12th character is unknown, so those bits are left as they are.
                if (value.Length > 10)
                    throw Unproven("Nicknames longer than 10 characters (this game's 11th and 12th character fields are not known)");
                StringConverter3.SetString(temp.AsSpan(0, 10), value, 10, jp: false);
                temp.AsSpan(0, 10).CopyTo(Data[0x08..]);
                return;
            }
            StringConverter3.SetString(temp, value, temp.Length, jp: false);
            temp.AsSpan(0, 10).CopyTo(Data[0x08..]);
            WriteBits(0, 53, 8, temp[10]);
            WriteBits(0, 86, 8, temp[11]);
        }
    }

    /// <summary>Where the 7-byte original trainer's name starts.</summary>
    private int TrainerNameOffset => TeamRocket ? 0x13 : 0x14;

    public string OriginalTrainerName
    {
        get => StringConverter3.GetString(Data.Slice(TrainerNameOffset, 7), jp: false);
        set
        {
            var span = Data.Slice(TrainerNameOffset, 7);
            span.Fill(0xFF);
            StringConverter3.SetString(span, value, 7, jp: false);
        }
    }

    public bool IsEgg
    {
        get => (KnowsHeader && (Data[0x13] & 0x04) != 0) || ((BinaryPrimitives.ReadUInt32LittleEndian(Data[0x48..]) >> 30) & 1) != 0;
        set
        {
            ThrowIfHeaderUnknown();
            if (value) Data[0x13] |= 0x04;
            else Data[0x13] &= 0xFB;
            var ivs = BinaryPrimitives.ReadUInt32LittleEndian(Data[0x48..]);
            ivs = value ? ivs | 0x4000_0000u : ivs & ~0x4000_0000u;
            BinaryPrimitives.WriteUInt32LittleEndian(Data[0x48..], ivs);
        }
    }

    public IReadOnlyList<int> Moves =>
        [.. Enumerable.Range(0, 4).Select(index => (int)ReadBits(1, index * 16, 11))];

    public IReadOnlyList<int> MovePp => [.. Enumerable.Range(0, 4).Select(index => Data[0x34 + index] & 0x7F)];

    public IReadOnlyList<int> MovePpUps =>
        [.. Enumerable.Range(0, 4).Select(index => (PpBonuses >> (index * 2)) & 0x3)];

    /// <summary>The IV word is substruct 3's second u32 (after pokérus, met location and the
    /// met level/game/dynamax/OT-gender word); stored order HP, Atk, Def, Spe, SpA, SpD.</summary>
    private const int IvBit = 32;

    /// <summary>App stat order: HP, Atk, Def, SpA, SpD, Spe.</summary>
    public IReadOnlyList<int> Ivs =>
    [
        (int)ReadBits(3, IvBit + 0, 5),
        (int)ReadBits(3, IvBit + 5, 5),
        (int)ReadBits(3, IvBit + 10, 5),
        (int)ReadBits(3, IvBit + 20, 5),
        (int)ReadBits(3, IvBit + 25, 5),
        (int)ReadBits(3, IvBit + 15, 5),
    ];

    /// <summary>App stat order: HP, Atk, Def, SpA, SpD, Spe (stored Spe sits at EV byte 3).</summary>
    public IReadOnlyList<int> Evs =>
    [
        Data[0x38],
        Data[0x39],
        Data[0x3A],
        Data[0x3C],
        Data[0x3D],
        Data[0x3B],
    ];

    public int CurrentHp => IsParty ? BinaryPrimitives.ReadUInt16LittleEndian(Data[0x56..]) : 0;

    public uint StatusCondition => IsParty ? BinaryPrimitives.ReadUInt32LittleEndian(Data[0x50..]) : 0;

    /// <summary>App stat order; the party's stored stats, or a computed preview for a box mon.</summary>
    public IReadOnlyList<int> Stats => IsParty
        ?
        [
            BinaryPrimitives.ReadUInt16LittleEndian(Data[0x58..]),
            BinaryPrimitives.ReadUInt16LittleEndian(Data[0x5A..]),
            BinaryPrimitives.ReadUInt16LittleEndian(Data[0x5C..]),
            BinaryPrimitives.ReadUInt16LittleEndian(Data[0x60..]),
            BinaryPrimitives.ReadUInt16LittleEndian(Data[0x62..]),
            BinaryPrimitives.ReadUInt16LittleEndian(Data[0x5E..]),
        ]
        : ComputeStats();

    public void SetSpecies(int species) => WriteBits(0, 0, 11, (ulong)Math.Clamp(species, 0, 0x7FF));

    public void SetHeldItem(int item) => WriteBits(0, 16, 10, (ulong)Math.Clamp(item, 0, 0x3FF));

    public void SetBall(int ball)
    {
        if (!KnowsBall)
            throw Unproven("The Poké Ball (this game's ball field is not known)");
        WriteBits(0, 80, 6, (ulong)Math.Clamp(ball, 0, 0x3F));
    }

    public void SetFriendship(int friendship) => Data[TeamRocket ? 0x28 : 0x29] = (byte)Math.Clamp(friendship, 0, 255);

    public void SetExperience(uint experience) => WriteBits(0, 32, 21, Math.Min(experience, 0x1F_FFFF));

    public void SetLevel(int level)
    {
        level = Math.Clamp(level, 1, 100);
        SetExperience(_data.ExperienceAtLevel(Species, level));
        if (!IsParty)
            return;
        Data[0x54] = (byte)level;
        RecomputePartyStats();
    }

    public void SetMove(int index, int move, int pp)
    {
        if ((uint)index >= 4) return;
        WriteBits(1, index * 16, 11, (ulong)Math.Clamp(move, 0, 0x7FF));
        Data[0x34 + index] = (byte)((Data[0x34 + index] & 0x80) | (Math.Clamp(pp, 0, 0x7F) & 0x7F));
    }

    public void SetPp(int index, int pp)
    {
        if ((uint)index >= 4) return;
        Data[0x34 + index] = (byte)((Data[0x34 + index] & 0x80) | (Math.Clamp(pp, 0, 0x7F) & 0x7F));
    }

    public void SetPpUps(IReadOnlyList<int> ups)
    {
        if (!KnowsPpBonuses)
            throw Unproven("PP Ups (this game's PP-bonus field is not known)");
        var packed = 0;
        for (var i = 0; i < 4; i++)
            packed |= Math.Clamp(i < ups.Count ? ups[i] : 0, 0, 3) << (i * 2);
        Data[0x28] = (byte)packed;
    }

    public void SetIvs(IReadOnlyList<int> ivs)
    {
        if (!KnowsIvOrder)
            throw Unproven("IVs (every proven record of this game holds 31 in all six, so their order is not proven)");
        WriteBits(3, IvBit + 0, 5, (ulong)Math.Clamp(ivs[0], 0, 31));
        WriteBits(3, IvBit + 5, 5, (ulong)Math.Clamp(ivs[1], 0, 31));
        WriteBits(3, IvBit + 10, 5, (ulong)Math.Clamp(ivs[2], 0, 31));
        WriteBits(3, IvBit + 20, 5, (ulong)Math.Clamp(ivs[3], 0, 31));
        WriteBits(3, IvBit + 25, 5, (ulong)Math.Clamp(ivs[4], 0, 31));
        WriteBits(3, IvBit + 15, 5, (ulong)Math.Clamp(ivs[5], 0, 31));
        if (IsParty) RecomputePartyStats();
    }

    public void SetEvs(IReadOnlyList<int> evs)
    {
        Data[0x38] = (byte)Math.Clamp(evs[0], 0, 255);
        Data[0x39] = (byte)Math.Clamp(evs[1], 0, 255);
        Data[0x3A] = (byte)Math.Clamp(evs[2], 0, 255);
        Data[0x3C] = (byte)Math.Clamp(evs[3], 0, 255);
        Data[0x3D] = (byte)Math.Clamp(evs[4], 0, 255);
        Data[0x3B] = (byte)Math.Clamp(evs[5], 0, 255);
        if (IsParty) RecomputePartyStats();
    }

    public bool SetAbilitySlot(int slot)
    {
        if (slot is < 0 or > 2)
            return false;
        if (!KnowsAbilitySlotNames)
            throw Unproven("The ability (this game changed some species' data, so which ability a slot holds is not proven)");
        WriteBits(3, AbilityBit, 2, (ulong)slot);
        return true;
    }

    public bool SetAbilityId(int ability)
    {
        var (first, second, hidden) = _data.Abilities(Species);
        if (ability == hidden && hidden != 0)
            return SetAbilitySlot(2);
        if (ability == second && second != 0 && second != first)
            return SetAbilitySlot(1);
        if (ability == first && first != 0)
            return SetAbilitySlot(0);
        return false;
    }

    /// <summary>Initializes the header of an empty slot as a caught mon of the current species.</summary>
    public void InitializeHeader(uint otid, string trainerName, int language = 2)
    {
        ThrowIfHeaderUnknown();
        var pid = (uint)((Species * 2654435761u) ^ 0x9E37_79B9u);
        Pid = pid;
        Otid = otid;
        OriginalTrainerName = trainerName;
        Data[0x12] = (byte)(language & 0x7);
        Data[0x13] = 0x02; // hasSpecies
        Data[0x1B] = 0;    // markings / compressed status
        SetBall(1);
        SetFriendship(70);
        SetAbilitySlot(0);
        WriteBits(3, 16, 7, 5); // met level 5
        Nickname = _data.SpeciesName(Species);
        if (IsParty)
            InitializePartyTail();
    }

    /// <summary>A box record moved into a party slot gets the game's computed party tail.</summary>
    public void InitializePartyTail()
    {
        Data.Slice(0x50, 0x14).Clear();
        var level = Math.Clamp(_data.LevelForExperience(Species, Experience), 1, 100);
        Data[0x54] = (byte)level;
        RecomputePartyStats();
        BinaryPrimitives.WriteUInt16LittleEndian(Data[0x56..], (ushort)Stats[0]);
    }

    private void ThrowIfHeaderUnknown()
    {
        if (!KnowsHeader)
            throw Unproven("Creating a Pokémon or changing its egg state (this game packs its language and egg/species flags into one byte whose egg bit is not proven)");
    }

    /// <summary>True when the stored party stats are exactly what the tables compute, so a
    /// recomputation writes what the game would (always true for a boxed record).</summary>
    public bool StoredStatsMatchTables => !IsParty || ComputeStats().SequenceEqual(Stats);

    /// <summary>The refusal for a field the record's codec does not prove.</summary>
    public static NotSupportedException Unproven(string what) =>
        new($"{what} cannot be edited in this game yet, so it was left as it is.");

    public void Clear()
    {
        Data.Clear();
    }

    /// <summary>Copies another record's decrypted state, rebuilding the party tail when
    /// a boxed mon is withdrawn.</summary>
    public void CopyFrom(ExpansionMon source)
    {
        Data.Clear();
        source._pk.Data[..100].CopyTo(Data);
        if (!IsParty)
        {
            Data[80..].Clear();
            return;
        }
        if (!source.IsParty)
            InitializePartyTail();
    }

    /// <summary>Writes the edited mon back into its buffer, encrypted and re-checksummed.</summary>
    public void Commit()
    {
        _pk.RefreshChecksum();
        if (IsParty)
            _pk.WriteEncryptedDataParty(_buffer.AsSpan(_offset, 100));
        else
            _pk.WriteEncryptedDataStored(_buffer.AsSpan(_offset, 80));
    }

    public byte[] RawRecord()
    {
        var length = IsParty ? 100 : 80;
        return _buffer.AsSpan(_offset, length).ToArray();
    }

    /// <summary>Changes the PID while preserving the requested nature, shiny and gender.</summary>
    public bool RerollPid(int? nature = null, bool? shiny = null, int? gender = null)
    {
        var targetNature = nature ?? Nature;
        var wantShiny = shiny ?? IsShiny;
        var targetGender = gender ?? Gender;
        var ratio = _data.GenderRatio(Species);
        var otid = Otid;
        for (var attempt = 0; attempt < 200_000; attempt++)
        {
            var pid = (uint)Random.Shared.NextInt64(0, 0x1_0000_0000L);
            if (pid % 25 != (uint)targetNature)
                continue;
            if (ShinyOf(pid, otid) != wantShiny)
                continue;
            if (GenderOf(pid, ratio) != targetGender)
                continue;
            Pid = pid;
            return true;
        }
        return false;
    }

    public static bool ShinyOf(uint pid, uint otid) =>
        (((pid & 0xFFFF) ^ (pid >> 16) ^ (otid & 0xFFFF) ^ (otid >> 16)) & 0xFFFF) < 8;

    public static int GenderOf(uint pid, int ratio) => ratio switch
    {
        255 => 2,
        0 => 0,
        254 => 1,
        _ => (pid & 0xFF) < ratio ? 1 : 0,
    };

    /// <summary>Gen 3 stat formula in app order (HP, Atk, Def, SpA, SpD, Spe).</summary>
    public IReadOnlyList<int> ComputeStats()
    {
        var baseStats = _data.BaseStats(Species);
        var ivs = Ivs;
        var evs = Evs;
        var level = Math.Clamp(Level, 1, 100);
        var hp = ((2 * baseStats.Hp + ivs[0] + evs[0] / 4) * level) / 100 + level + 10;
        Span<int> stats = [hp, 0, 0, 0, 0, 0];
        ReadOnlySpan<int> bases = [baseStats.Atk, baseStats.Def, baseStats.SpA, baseStats.SpD, baseStats.Spe];
        // Natures index Gen 3's stat order (Atk, Def, Spe, SpA, SpD); a neutral nature
        // (raised == lowered) changes nothing.
        var nature = Nature;
        var increased = NatureStatToAppIndex[nature / 5];
        var decreased = NatureStatToAppIndex[nature % 5];
        for (var i = 0; i < 5; i++)
        {
            var stat = ((2 * bases[i] + ivs[i + 1] + evs[i + 1] / 4) * level) / 100 + 5;
            if (increased != decreased)
            {
                if (i + 1 == increased) stat = stat * 110 / 100;
                else if (i + 1 == decreased) stat = stat * 90 / 100;
            }
            stats[i + 1] = stat;
        }
        return [.. stats];
    }

    /// <summary>Gen 3 nature stat (Atk, Def, Spe, SpA, SpD) → app stat index (HP 0, Atk 1, Def 2, SpA 3, SpD 4, Spe 5).</summary>
    private static readonly int[] NatureStatToAppIndex = [1, 2, 5, 3, 4];

    private void RecomputePartyStats()
    {
        var stats = ComputeStats();
        BinaryPrimitives.WriteUInt16LittleEndian(Data[0x58..], (ushort)stats[0]);
        BinaryPrimitives.WriteUInt16LittleEndian(Data[0x5A..], (ushort)stats[1]);
        BinaryPrimitives.WriteUInt16LittleEndian(Data[0x5C..], (ushort)stats[2]);
        BinaryPrimitives.WriteUInt16LittleEndian(Data[0x60..], (ushort)stats[3]);
        BinaryPrimitives.WriteUInt16LittleEndian(Data[0x62..], (ushort)stats[4]);
        BinaryPrimitives.WriteUInt16LittleEndian(Data[0x5E..], (ushort)stats[5]);
        var current = BinaryPrimitives.ReadUInt16LittleEndian(Data[0x56..]);
        if (current == 0 || current > stats[0])
            BinaryPrimitives.WriteUInt16LittleEndian(Data[0x56..], (ushort)stats[0]);
    }

    private ulong ReadBits(int block, int bit, int width)
    {
        var span = Data.Slice(HeaderSize + block * 12, 12);
        ulong value = 0;
        for (var i = 0; i < width; i++)
        {
            var position = bit + i;
            value |= (ulong)((span[position >> 3] >> (position & 7)) & 1) << i;
        }
        return value;
    }

    private void WriteBits(int block, int bit, int width, ulong value)
    {
        var span = Data.Slice(HeaderSize + block * 12, 12);
        for (var i = 0; i < width; i++)
        {
            var position = bit + i;
            var mask = (byte)(1 << (position & 7));
            if (((value >> i) & 1) != 0)
                span[position >> 3] |= mask;
            else
                span[position >> 3] &= (byte)~mask;
        }
    }
}
