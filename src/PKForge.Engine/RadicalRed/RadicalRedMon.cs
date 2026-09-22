using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine.RadicalRed;

/// <summary>
/// One Pokémon in a Radical Red CFRU slot: the 100-byte party form (retail Gen 3
/// header and tail around a PLAINTEXT, fixed-order Growth/Attacks/Condition/Misc core
/// — no PID shuffle, no XOR; the vanilla mon checksum slot at 0x1C holds CFRU's
/// backupSpecies instead) or the 58-byte PC compact form (moves bit-packed 10-bit,
/// everything else verbatim; identical layout to Unbound's compact mon).
///
/// Layout verified against CFRU include/pokemon.h + include/new/pokemon_storage_system.h
/// and the owner's champion save (Terapagos: species 1370, IV word 0xBFFFFFFF,
/// champion+obedient ribbon bits in the single u32 at 0x4C, level 85, 308/308 HP at
/// 0x54/0x56). All edits patch the backing buffer in place; unknown bytes (the ribbon
/// word, contest stats, status) are never touched.
/// </summary>
internal sealed class RadicalRedMon
{
    public readonly byte[] Buffer;
    public readonly int Offset;
    public readonly bool Party;

    public RadicalRedMon(byte[] buffer, int offset, bool party)
    {
        Buffer = buffer;
        Offset = offset;
        Party = party;
    }

    public int Size => Party ? RadicalRedFormat.PartyMonSize : RadicalRedFormat.PcMonSize;
    private Span<byte> Raw => Buffer.AsSpan(Offset, Size);

    public uint Pid
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Raw);
        set => BinaryPrimitives.WriteUInt32LittleEndian(Raw, value);
    }

    public uint Otid
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Raw[4..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(Raw[4..], value);
    }

    public string Nickname
    {
        get => StringConverter3.GetString(Raw[8..18], jp: false);
        set => StringConverter3.SetString(Raw[8..18], value, 10, jp: false);
    }

    public string OriginalTrainerName => StringConverter3.GetString(Raw[0x14..0x1B], jp: false);

    public int Species
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Raw[Party ? 0x20.. : 0x1C..]);
        set => BinaryPrimitives.WriteUInt16LittleEndian(Raw[Party ? 0x20.. : 0x1C..], (ushort)value);
    }

    public int HeldItem
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Raw[Party ? 0x22.. : 0x1E..]);
        set => BinaryPrimitives.WriteUInt16LittleEndian(Raw[Party ? 0x22.. : 0x1E..], (ushort)value);
    }

    public uint Experience
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Raw[Party ? 0x24.. : 0x20..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(Raw[Party ? 0x24.. : 0x20..], value);
    }

    public int PpBonuses
    {
        get => Raw[Party ? 0x28 : 0x24];
        set => Raw[Party ? 0x28 : 0x24] = (byte)value;
    }

    /// <summary>Both forms store real friendship (0x29 party / 0x25 compact).</summary>
    public int Friendship
    {
        get => Raw[Party ? 0x29 : 0x25];
        set => Raw[Party ? 0x29 : 0x25] = (byte)value;
    }

    public int Ball
    {
        get => Raw[Party ? 0x2A : 0x26];
        set => Raw[Party ? 0x2A : 0x26] = (byte)value;
    }

    /// <summary>Moves: u16 each in the party form, 4x10 bits packed LE at 0x27 compact.</summary>
    public int[] Moves
    {
        get
        {
            if (Party)
            {
                return new[]
                {
                    (int)BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x2C..]),
                    (int)BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x2E..]),
                    (int)BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x30..]),
                    (int)BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x32..]),
                };
            }
            ulong packed = 0;
            for (var i = 0; i < 5; i++)
                packed |= (ulong)Raw[0x27 + i] << (8 * i);
            return [(int)(packed & 0x3FF), (int)((packed >> 10) & 0x3FF), (int)((packed >> 20) & 0x3FF), (int)((packed >> 30) & 0x3FF)];
        }
        set
        {
            if (Party)
            {
                for (var i = 0; i < 4; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(Raw[(0x2C + i * 2)..], (ushort)(i < value.Length ? value[i] : 0));
                return;
            }
            ulong packed = 0;
            for (var i = 0; i < value.Length; i++)
                packed |= (ulong)(value[i] & 0x3FF) << (10 * i);
            for (var i = 0; i < 5; i++)
                Raw[0x27 + i] = (byte)((packed >> (8 * i)) & 0xFF);
        }
    }

    /// <summary>Party-only current PP bytes; the compact form stores none (max is derived).</summary>
    public int[] MovePp => Party ? [Raw[0x34], Raw[0x35], Raw[0x36], Raw[0x37]] : [0, 0, 0, 0];

    /// <summary>App stat order: HP, Atk, Def, SpA, SpD, Spe. The bytes store the CFRU
    /// order HP, Atk, Def, Spe, SpA, SpD (Spe at +3, SpA at +4, SpD at +5).</summary>
    public int[] EVs
    {
        get
        {
            var b = Party ? 0x38 : 0x2C;
            return [Raw[b], Raw[b + 1], Raw[b + 2], Raw[b + 4], Raw[b + 5], Raw[b + 3]];
        }
        set
        {
            var b = Party ? 0x38 : 0x2C;
            Raw[b] = (byte)value[0];
            Raw[b + 1] = (byte)value[1];
            Raw[b + 2] = (byte)value[2];
            Raw[b + 3] = (byte)value[5];
            Raw[b + 4] = (byte)value[3];
            Raw[b + 5] = (byte)value[4];
        }
    }

    private uint IvWord
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Raw[Party ? 0x48.. : 0x36..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(Raw[Party ? 0x48.. : 0x36..], value);
    }

    /// <summary>App stat order: HP, Atk, Def, SpA, SpD, Spe. The packed word stores
    /// HP/Atk/Def at bits 0/5/10, Spe at 15, SpA at 20, SpD at 25.</summary>
    public int[] IVs
    {
        get
        {
            var word = IvWord;
            int Get(int shift) => (int)((word >> shift) & 0x1F);
            return new[] { Get(0), Get(5), Get(10), Get(20), Get(25), Get(15) };
        }
        set
        {
            var word = IvWord & 0xC000_0000u; // keep egg + hidden-ability flags
            word |= (uint)(value[0] & 0x1F);
            word |= (uint)(value[1] & 0x1F) << 5;
            word |= (uint)(value[2] & 0x1F) << 10;
            word |= (uint)(value[5] & 0x1F) << 15;
            word |= (uint)(value[3] & 0x1F) << 20;
            word |= (uint)(value[4] & 0x1F) << 25;
            IvWord = word;
        }
    }

    public bool HiddenAbility
    {
        get => (IvWord & 0x8000_0000u) != 0;
        set => IvWord = value ? IvWord | 0x8000_0000u : IvWord & ~0x8000_0000u;
    }

    public bool IsEgg
    {
        get => (IvWord & 0x4000_0000u) != 0;
        set => IvWord = value ? IvWord | 0x4000_0000u : IvWord & ~0x4000_0000u;
    }

    public int Nature => (int)(Pid % 25);

    /// <summary>CFRU shiny rule: halves xor (TID^SID^PIDhi^PIDlo) below 16 — Radical
    /// Red doubles vanilla's 8/16 odds like every CFRU game.</summary>
    public bool IsShiny
    {
        get
        {
            var xor = Otid ^ Pid;
            return ((xor & 0xFFFF) ^ (xor >> 16)) < 16;
        }
    }

    /// <summary>Header fields shared verbatim by both forms: language, the sanity
    /// flags byte (bit 1 hasSpecies), markings, and the met block (pokerus, met
    /// location, metInfo u16: level 7 / game 4 / blank 3 / OT gender 1).</summary>
    public int Language
    {
        get => Raw[0x12];
        set => Raw[0x12] = (byte)value;
    }

    public int SanityFlags
    {
        get => Raw[0x13];
        set => Raw[0x13] = (byte)value;
    }

    public int Markings
    {
        get => Raw[0x1B];
        set => Raw[0x1B] = (byte)value;
    }

    public int Pokerus
    {
        get => Raw[Party ? 0x44 : 0x32];
        set => Raw[Party ? 0x44 : 0x32] = (byte)value;
    }

    public int MetLocation
    {
        get => Raw[Party ? 0x45 : 0x33];
        set => Raw[Party ? 0x45 : 0x33] = (byte)value;
    }

    public int MetInfo
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Raw[Party ? 0x46.. : 0x34..]);
        set => BinaryPrimitives.WriteUInt16LittleEndian(Raw[Party ? 0x46.. : 0x34..], (ushort)value);
    }

    public bool IsEmpty => Species == 0;

    public bool LooksValid => Species is > 0 and <= 2500 && Experience is > 0 and <= 2_000_000;

    /// <summary>Visual level: stored in the party tail; derived from exp + growth for PC mons.</summary>
    public int Level => Party ? Raw[0x54] : RadicalRedData.LevelForExperience(Species, Experience);

    public int CurrentHp => Party ? BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x56..]) : 0;

    /// <summary>Battle stats from the party tail in app order (HP, Atk, Def, SpA, SpD, Spe).</summary>
    public int[]? PartyStats
    {
        get
        {
            if (!Party) return null;
            return new[]
            {
                (int)BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x58..]),
                (int)BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x5A..]),
                (int)BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x5C..]),
                (int)BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x60..]),
                (int)BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x62..]),
                (int)BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x5E..]),
            };
        }
    }

    // CFRU ball ids (the engine's shared item-table order, no None) -> PKHeX Ball ids
    // for display and sprites. Radical Red observed using ids up to 26 on ground truth.
    private static readonly int[] BallToPkHeX =
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, // Master..Cherish (+1)
        0,                                                      // Park has no PKHeX ball
        17, 18, 19, 20, 21, 22, 23, 24,                        // Fast..Sport (same)
        26, 25,                                                 // CFRU Beast/Dream are swapped
    };

    private static readonly int[] BallFromPkHeX = BuildReverseBallMap();

    private static int[] BuildReverseBallMap()
    {
        var reverse = new int[40];
        Array.Fill(reverse, -1);
        for (var cfru = 0; cfru < BallToPkHeX.Length; cfru++)
        {
            var pkhex = BallToPkHeX[cfru];
            if (pkhex > 0)
                reverse[pkhex] = cfru;
        }
        return reverse;
    }

    public int DisplayBall => (uint)Ball < BallToPkHeX.Length ? BallToPkHeX[Ball] : 0;

    /// <summary>Translates a PKHeX ball id back to CFRU; false when the ball cannot be stored.</summary>
    public static bool TryStoreBall(int pkhexBall, out int cfruBall)
    {
        cfruBall = 3; // Poke Ball fallback
        if ((uint)pkhexBall >= BallFromPkHeX.Length || BallFromPkHeX[pkhexBall] < 0)
            return false;
        cfruBall = BallFromPkHeX[pkhexBall];
        return true;
    }
}
