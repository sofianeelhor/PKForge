using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine.Exceeded;

/// <summary>
/// One Emerald Exceeded Pokémon record, read and written in place. The record is
/// plaintext with no checksum. Only the fields proven on the real saves have accessors
/// (see Data/README.md); every other byte (0x20-0x23, 0x2A-0x2B, 0x38-0x57: the EV/IV
/// words, origins, ribbons and five extra move-like words) is carried over untouched.
/// <code>
/// 0x00 u32 PID            0x04 u32 OT id          0x08 nickname (10, Gen 3 charset)
/// 0x12 language           0x13 egg/species flags  0x14 OT name (7)     0x1B markings
/// 0x1C u16 species        0x1E u16 held item      0x24 u32 EXP
/// 0x28 PP bonuses         0x29 friendship         0x2C u16 moves ×4    0x34 PP ×4
/// party only: 0x58 u32 status, 0x5C level, 0x5D mail, 0x5E HP, 0x60 max HP,
///             0x62 Atk, 0x64 Def, 0x66 Spe, 0x68 SpA, 0x6A SpD
/// </code>
/// </summary>
internal sealed class ExceededMon(byte[] buffer, int offset, bool isParty)
{
    public bool IsParty { get; } = isParty;

    private Span<byte> Data => buffer.AsSpan(offset, IsParty ? ExceededFormat.PartyMonSize : ExceededFormat.BoxMonSize);

    public byte[] RawRecord() => Data.ToArray();

    /// <summary>The 88-byte stored part (a party record minus its battle block).</summary>
    public ReadOnlySpan<byte> BoxPart => buffer.AsSpan(offset, ExceededFormat.BoxMonSize);

    public bool IsEmpty => Species == 0;

    public uint Pid => BinaryPrimitives.ReadUInt32LittleEndian(Data);

    public uint Otid => BinaryPrimitives.ReadUInt32LittleEndian(Data[4..]);

    public int Species => BinaryPrimitives.ReadUInt16LittleEndian(Data[0x1C..]);

    public int HeldItem
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(Data[0x1E..]);
        set => BinaryPrimitives.WriteUInt16LittleEndian(Data[0x1E..], (ushort)value);
    }

    public uint Experience
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Data[0x24..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(Data[0x24..], value);
    }

    public int PpBonuses => Data[0x28];

    public int Friendship => Data[0x29];

    public bool IsEgg => (Data[0x13] & 0x04) != 0;

    public int[] Moves
    {
        get
        {
            var moves = new int[4];
            for (var i = 0; i < 4; i++)
                moves[i] = BinaryPrimitives.ReadUInt16LittleEndian(Data[(0x2C + i * 2)..]);
            return moves;
        }
    }

    public int[] MovePp => [Data[0x34], Data[0x35], Data[0x36], Data[0x37]];

    public int[] MovePpUps => [PpBonuses & 3, (PpBonuses >> 2) & 3, (PpBonuses >> 4) & 3, (PpBonuses >> 6) & 3];

    public void SetMove(int index, int move, int pp)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Data[(0x2C + index * 2)..], (ushort)move);
        Data[0x34 + index] = (byte)Math.Clamp(pp, 0, 255);
    }

    public void SetPp(int index, int pp) => Data[0x34 + index] = (byte)Math.Clamp(pp, 0, 255);

    public string Nickname
    {
        get => StringConverter3.GetString(Data.Slice(0x08, 10), jp: false);
        set
        {
            var span = Data.Slice(0x08, 10);
            span.Fill(0xFF);
            StringConverter3.SetString(span, value, 10, jp: false);
        }
    }

    public string OriginalTrainerName
    {
        get => StringConverter3.GetString(Data.Slice(0x14, 7), jp: false);
        set
        {
            var span = Data.Slice(0x14, 7);
            span.Fill(0xFF);
            StringConverter3.SetString(span, value, 7, jp: false);
        }
    }

    /// <summary>The party's stored level (box records have none: the game derives it from EXP).</summary>
    public int PartyLevel => IsParty ? Data[0x5C] : 0;

    public int StatusCondition => IsParty ? (int)BinaryPrimitives.ReadUInt32LittleEndian(Data[0x58..]) : 0;

    public int CurrentHp => IsParty ? BinaryPrimitives.ReadUInt16LittleEndian(Data[0x5E..]) : 0;

    /// <summary>The party's stored stats in app order (HP, Atk, Def, SpA, SpD, Spe), or null in a box.</summary>
    public int[]? Stats => IsParty
        ?
        [
            BinaryPrimitives.ReadUInt16LittleEndian(Data[0x60..]),
            BinaryPrimitives.ReadUInt16LittleEndian(Data[0x62..]),
            BinaryPrimitives.ReadUInt16LittleEndian(Data[0x64..]),
            BinaryPrimitives.ReadUInt16LittleEndian(Data[0x68..]),
            BinaryPrimitives.ReadUInt16LittleEndian(Data[0x6A..]),
            BinaryPrimitives.ReadUInt16LittleEndian(Data[0x66..]),
        ]
        : null;

    public bool IsShiny => Expansion.ExpansionMon.ShinyOf(Pid, Otid);

    public int Nature => (int)(Pid % 25);
}
