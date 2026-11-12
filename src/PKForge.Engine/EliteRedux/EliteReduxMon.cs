using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine.EliteRedux;

/// <summary>
/// One Elite Redux Pokémon, read and written in place in its save block. The record is the
/// hack's own unencrypted, checksum-less <c>struct BoxPokemon</c> (eliteredux-source
/// <c>include/pokemon.h</c>, 52 bytes), little-endian bit fields:
/// <code>
/// 0x00 personality            0x04 otId
/// 0x08 move1:11 experience:21
/// 0x0C move2:11 move3:11 friendship:8 isEventMon:1 isAlpha:1
/// 0x10 species:16 move4:11 hpType:5
/// 0x14 heldItem:10 nature:5 isEgg:1 language:3 metLevel:7 isShiny:2 maxShiny:2 abilityNum:2
/// 0x18 EVs hp atk def spe spa spd   0x1E metLocation   0x1F otName[7]   0x26 nickname[12]
/// 0x32 pokeball:5 speedDown:1 otGender:1 isDisabled:1   0x33 markings:4 (high nibble unused)
/// </code>
/// A party member appends <c>struct Pokemon</c>'s tail (76 bytes in all): pp[4], status u32,
/// level, mail, hp, maxHP, atk, def, spe, spa, spd. The game keeps no IVs (every IV is 31,
/// Speed 0 when speedDown is set), no PP Ups (always three) and no Pokérus. Every bit this
/// type does not name is left exactly as found.
/// </summary>
internal sealed class EliteReduxMon
{
    public const int BoxSize = 52;
    public const int PartySize = 76;

    private readonly byte[] _buffer;
    private readonly int _offset;

    public EliteReduxMon(byte[] buffer, int offset, bool isParty)
    {
        _buffer = buffer;
        _offset = offset;
        IsParty = isParty;
    }

    public bool IsParty { get; }
    public int Size => IsParty ? PartySize : BoxSize;
    private Span<byte> Data => _buffer.AsSpan(_offset, Size);

    public byte[] Raw() => Data.ToArray();
    public void Load(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Data);

    public bool IsEmpty => Data[..BoxSize].IndexOfAnyExcept((byte)0) < 0;

    // ── Bit plumbing ──

    private uint Word(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Data[at..]);
    private void SetWord(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Data[at..], value);
    private int Bits(int at, int shift, int width) => (int)((Word(at) >> shift) & ((1u << width) - 1));

    private void SetBits(int at, int shift, int width, int value)
    {
        var mask = ((1u << width) - 1) << shift;
        SetWord(at, (Word(at) & ~mask) | (((uint)value << shift) & mask));
    }

    // ── Header ──

    public uint Pid => Word(0x00);
    public uint OtId
    {
        get => Word(0x04);
        set => SetWord(0x04, value);
    }

    public int Species
    {
        get => Bits(0x10, 0, 16);
        set => SetBits(0x10, 0, 16, value);
    }

    public uint Experience
    {
        get => (uint)Bits(0x08, 11, 21);
        set => SetBits(0x08, 11, 21, (int)Math.Min(value, (1u << 21) - 1));
    }

    public int Move(int index) => index switch
    {
        0 => Bits(0x08, 0, 11),
        1 => Bits(0x0C, 0, 11),
        2 => Bits(0x0C, 11, 11),
        _ => Bits(0x10, 16, 11),
    };

    public void SetMove(int index, int move)
    {
        switch (index)
        {
            case 0: SetBits(0x08, 0, 11, move); break;
            case 1: SetBits(0x0C, 0, 11, move); break;
            case 2: SetBits(0x0C, 11, 11, move); break;
            default: SetBits(0x10, 16, 11, move); break;
        }
    }

    public int[] Moves => [Move(0), Move(1), Move(2), Move(3)];

    public int Friendship
    {
        get => Bits(0x0C, 22, 8);
        set => SetBits(0x0C, 22, 8, value);
    }

    public int HeldItem
    {
        get => Bits(0x14, 0, 10);
        set => SetBits(0x14, 0, 10, value);
    }

    public int Nature
    {
        get => Bits(0x14, 10, 5);
        set => SetBits(0x14, 10, 5, value);
    }

    public bool IsEgg => Bits(0x14, 15, 1) != 0;
    public int MetLevel => Bits(0x14, 19, 7);

    /// <summary>0 not shiny, 1 shiny, 2 rare shiny, 3 legendary shiny (the game's own field; the PID plays no part).</summary>
    public int ShinyKind
    {
        get => Bits(0x14, 26, 2);
        set => SetBits(0x14, 26, 2, value);
    }

    public bool IsShiny => ShinyKind != 0;

    public int AbilitySlot
    {
        get => Bits(0x14, 30, 2);
        set => SetBits(0x14, 30, 2, value);
    }

    /// <summary>EVs in the app's order (HP, Atk, Def, SpA, SpD, Spe); stored HP, Atk, Def, Spe, SpA, SpD.</summary>
    public int[] Evs => [Data[0x18], Data[0x19], Data[0x1A], Data[0x1C], Data[0x1D], Data[0x1B]];

    public void SetEvs(IReadOnlyList<int> evs)
    {
        Data[0x18] = (byte)evs[0];
        Data[0x19] = (byte)evs[1];
        Data[0x1A] = (byte)evs[2];
        Data[0x1C] = (byte)evs[3];
        Data[0x1D] = (byte)evs[4];
        Data[0x1B] = (byte)evs[5];
    }

    public int MetLocation => Data[0x1E];

    public bool SpeedDown => (Data[0x32] & 0x20) != 0;

    /// <summary>The game's IVs: all 31, Speed 0 under speedDown (app order HP, Atk, Def, SpA, SpD, Spe).</summary>
    public int[] Ivs => [31, 31, 31, 31, 31, SpeedDown ? 0 : 31];

    public void SetSpeedDown(bool value) => Data[0x32] = (byte)((Data[0x32] & ~0x20) | (value ? 0x20 : 0));

    /// <summary>The ball's item id (5 bits).</summary>
    public int Ball
    {
        get => Data[0x32] & 0x1F;
        set => Data[0x32] = (byte)((Data[0x32] & ~0x1F) | (value & 0x1F));
    }

    public int OtGender => (Data[0x32] >> 6) & 1;

    public string OriginalTrainerName
    {
        get => ReadText(Data.Slice(0x1F, 7));
        set => WriteText(Data.Slice(0x1F, 7), value);
    }

    /// <summary>The 12-character nickname (the game copies all 12 bytes; a 0xFF ends it).</summary>
    public string Nickname
    {
        get => ReadText(Data.Slice(0x26, 12));
        set => WriteText(Data.Slice(0x26, 12), value);
    }

    // ── Party tail ──

    public int Pp(int index) => IsParty ? Data[0x34 + index] : 0;
    public void SetPp(int index, int value) { if (IsParty) Data[0x34 + index] = (byte)value; }
    public uint Status => IsParty ? Word(0x38) : 0;
    public int StoredLevel
    {
        get => IsParty ? Data[0x3C] : 0;
        set { if (IsParty) Data[0x3C] = (byte)value; }
    }

    public int CurrentHp
    {
        get => IsParty ? BinaryPrimitives.ReadUInt16LittleEndian(Data[0x3E..]) : 0;
        set { if (IsParty) BinaryPrimitives.WriteUInt16LittleEndian(Data[0x3E..], (ushort)value); }
    }

    /// <summary>Stored party stats in the app's order (HP max, Atk, Def, SpA, SpD, Spe), or null in a box.</summary>
    public int[]? StoredStats => IsParty
        ? [U16(0x40), U16(0x42), U16(0x44), U16(0x48), U16(0x4A), U16(0x46)]
        : null;

    public void SetStoredStats(IReadOnlyList<int> stats)
    {
        if (!IsParty) return;
        W16(0x40, stats[0]);
        W16(0x42, stats[1]);
        W16(0x44, stats[2]);
        W16(0x48, stats[3]);
        W16(0x4A, stats[4]);
        W16(0x46, stats[5]);
    }

    private int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(Data[at..]);
    private void W16(int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(Data[at..], (ushort)value);

    // ── Derived ──

    /// <summary>The game's experience table: every species grows on the "slow" curve
    /// (src/data/pokemon/experience_tables.h redefines all six as EXP_SLOW, 5n³/4).</summary>
    public static uint ExperienceAt(int level) => level switch
    {
        <= 0 => 0,
        1 => 1,
        _ => (uint)(5L * level * level * level / 4),
    };

    public int Level
    {
        get
        {
            var level = 1;
            while (level < 100 && ExperienceAt(level + 1) <= Experience)
                level++;
            return level;
        }
    }

    /// <summary>A record the hack itself could have written: a known species, moves and item,
    /// a nature in range and a terminated (or full) name. Empty records are not valid.</summary>
    public bool LooksValid(EliteReduxData tables)
    {
        if (IsEmpty || !tables.IsKnownSpecies(Species) || Species == 0)
            return false;
        for (var i = 0; i < 4; i++)
            if (!tables.IsKnownMove(Move(i)))
                return false;
        if (!tables.IsKnownItem(HeldItem) || Nature > 24 || Experience > ExperienceAt(100))
            return false;
        return IsParty ? StoredLevel is >= 1 and <= 100 : true;
    }

    private static string ReadText(ReadOnlySpan<byte> field) => StringConverter3.GetString(field, jp: false);

    /// <summary>Writes the text and its 0xFF terminator; the bytes after it are cleared to 0xFF.</summary>
    private static void WriteText(Span<byte> field, string value)
    {
        field.Fill(0xFF);
        StringConverter3.SetString(field, value, field.Length, jp: false);
    }
}
