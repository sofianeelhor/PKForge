using System.Buffers.Binary;

namespace PKForge.Engine.FireRedHacks;

/// <summary>
/// One Gen 3 Pokémon record (80-byte box form or 100-byte party form) of a FireRed hack, decoded
/// into its 48-byte data area in G-A-E-M order. Only <see cref="Commit"/> writes bytes back, and
/// only the fields this class exposes are ever changed: every other bit is kept as the game left it.
/// <para>Encrypted records (Fire Red Extended) use the retail scheme: the data area XORed with
/// PID ^ OTID and its substructures ordered by PID % 24, sealed by the 16-bit sum of the
/// plain data. Plain records (Crystal Advance Redux) keep G-A-E-M order and a zero checksum
/// field, exactly as that game writes them; the field is left alone.</para>
/// </summary>
internal sealed class FireRedHackMon
{
    private static readonly string[] Orders =
    [
        "GAEM", "GAME", "GEAM", "GEMA", "GMAE", "GMEA", "AGEM", "AGME", "AEGM", "AEMG", "AMGE", "AMEG",
        "EGAM", "EGMA", "EAGM", "EAMG", "EMGA", "EMAG", "MGAE", "MGEA", "MAGE", "MAEG", "MEGA", "MEAG",
    ];

    private readonly byte[] _record;
    private readonly byte[] _plain = new byte[48];

    public FireRedHackMon(ReadOnlySpan<byte> record, bool encrypted)
    {
        _record = record.ToArray();
        Encrypted = encrypted;
        Decode();
    }

    public bool Encrypted { get; }
    public bool Party => _record.Length >= FireRedHackFormat.PartyRecordSize;
    public byte[] Record => _record;

    public bool IsEmpty => _record.AsSpan(0, FireRedHackFormat.BoxRecordSize).IndexOfAnyExcept((byte)0) < 0;

    /// <summary>The record decodes to a real Pokémon: the stored checksum matches (encrypted) or
    /// is the zero the plain format writes, and the species is set.</summary>
    public bool LooksValid => !IsEmpty && Species != 0 && (Encrypted ? PlainSum() == StoredChecksum : StoredChecksum == 0);

    private ushort StoredChecksum => BinaryPrimitives.ReadUInt16LittleEndian(_record.AsSpan(0x1C));

    // ── Header ──

    public uint Pid
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(_record);
        set => BinaryPrimitives.WriteUInt32LittleEndian(_record, value);
    }

    public uint OtId => BinaryPrimitives.ReadUInt32LittleEndian(_record.AsSpan(4));
    public int Tid => (int)(OtId & 0xFFFF);
    public int Sid => (int)(OtId >> 16);

    public string Nickname
    {
        get => FireRedHackFormat.ReadText(_record.AsSpan(0x08, 10));
        set => FireRedHackFormat.WriteText(_record.AsSpan(0x08, 10), value);
    }

    public string OriginalTrainerName
    {
        get => FireRedHackFormat.ReadText(_record.AsSpan(0x14, 7));
        set => FireRedHackFormat.WriteText(_record.AsSpan(0x14, 7), value);
    }

    // ── Growth ──

    public int Species
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(_plain);
        set => BinaryPrimitives.WriteUInt16LittleEndian(_plain, (ushort)value);
    }

    public int HeldItem
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(_plain.AsSpan(2));
        set => BinaryPrimitives.WriteUInt16LittleEndian(_plain.AsSpan(2), (ushort)value);
    }

    public uint Experience
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(_plain.AsSpan(4));
        set => BinaryPrimitives.WriteUInt32LittleEndian(_plain.AsSpan(4), value);
    }

    public int PpBonuses => _plain[8];

    public int Friendship
    {
        get => _plain[9];
        set => _plain[9] = (byte)value;
    }

    // ── Attacks ──

    public int Move(int index) => BinaryPrimitives.ReadUInt16LittleEndian(_plain.AsSpan(12 + index * 2));
    public void SetMove(int index, int move) => BinaryPrimitives.WriteUInt16LittleEndian(_plain.AsSpan(12 + index * 2), (ushort)move);
    public int Pp(int index) => _plain[20 + index];
    public void SetPp(int index, int pp) => _plain[20 + index] = (byte)Math.Clamp(pp, 0, 255);
    public int PpUps(int index) => (PpBonuses >> (index * 2)) & 3;

    // ── EVs (stored HP, Atk, Def, Spe, SpA, SpD) ──

    /// <summary>App order (HP, Atk, Def, SpA, SpD, Spe) to the storage order of EVs, IVs and party stats.</summary>
    public static readonly int[] AppToStorage = [0, 1, 2, 4, 5, 3];

    public int Ev(int appIndex) => _plain[24 + AppToStorage[appIndex]];
    public void SetEv(int appIndex, int value) => _plain[24 + AppToStorage[appIndex]] = (byte)Math.Clamp(value, 0, 255);

    // ── Misc ──

    private ushort Origins
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(_plain.AsSpan(38));
        set => BinaryPrimitives.WriteUInt16LittleEndian(_plain.AsSpan(38), value);
    }

    public int Ball
    {
        get => (Origins >> 11) & 0xF;
        set => Origins = (ushort)((Origins & ~(0xF << 11)) | ((value & 0xF) << 11));
    }

    private uint IvWord
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(_plain.AsSpan(40));
        set => BinaryPrimitives.WriteUInt32LittleEndian(_plain.AsSpan(40), value);
    }

    public int Iv(int appIndex) => (int)(IvWord >> (AppToStorage[appIndex] * 5)) & 0x1F;

    public void SetIv(int appIndex, int value)
    {
        var shift = AppToStorage[appIndex] * 5;
        IvWord = (IvWord & ~(0x1Fu << shift)) | ((uint)Math.Clamp(value, 0, 31) << shift);
    }

    public bool IsEgg => (IvWord >> 30 & 1) != 0;
    public int AbilityBit => (int)(IvWord >> 31);

    // ── Derived ──

    public int Nature => (int)(Pid % 25);

    public bool IsShiny => ShinyValue(Pid, OtId) < 8;

    public static uint ShinyValue(uint pid, uint otId) => (otId & 0xFFFF) ^ (otId >> 16) ^ (pid & 0xFFFF) ^ (pid >> 16);

    // ── Party tail ──

    public int Level
    {
        get => _record[0x54];
        set => _record[0x54] = (byte)value;
    }

    public int CurrentHp
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(_record.AsSpan(0x56));
        set => BinaryPrimitives.WriteUInt16LittleEndian(_record.AsSpan(0x56), (ushort)value);
    }

    /// <summary>Stored party stats in app order (HP, Atk, Def, SpA, SpD, Spe).</summary>
    public int[] PartyStats
    {
        get
        {
            var stats = new int[6];
            stats[0] = BinaryPrimitives.ReadUInt16LittleEndian(_record.AsSpan(0x58));
            for (var i = 1; i < 6; i++)
                stats[i] = BinaryPrimitives.ReadUInt16LittleEndian(_record.AsSpan(0x58 + AppToStorage[i] * 2));
            return stats;
        }
        set
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_record.AsSpan(0x58), (ushort)value[0]);
            for (var i = 1; i < 6; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(_record.AsSpan(0x58 + AppToStorage[i] * 2), (ushort)value[i]);
        }
    }

    // ── Codec ──

    private void Decode()
    {
        var area = _record.AsSpan(0x20, 48);
        if (!Encrypted)
        {
            area.CopyTo(_plain);
            return;
        }
        var key = Pid ^ OtId;
        Span<byte> clear = stackalloc byte[48];
        for (var i = 0; i < 48; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(clear[i..], BinaryPrimitives.ReadUInt32LittleEndian(area[i..]) ^ key);
        var order = Orders[Pid % 24];
        for (var position = 0; position < 4; position++)
            clear.Slice(position * 12, 12).CopyTo(_plain.AsSpan("GAEM".IndexOf(order[position]) * 12));
    }

    private ushort PlainSum()
    {
        ushort sum = 0;
        for (var i = 0; i < 48; i += 2)
            sum += BinaryPrimitives.ReadUInt16LittleEndian(_plain.AsSpan(i));
        return sum;
    }

    /// <summary>Encodes the data area back into the record (after a PID change too) and returns it.</summary>
    public byte[] Commit()
    {
        var area = _record.AsSpan(0x20, 48);
        if (!Encrypted)
        {
            _plain.CopyTo(area);
            return _record;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(_record.AsSpan(0x1C), PlainSum());
        var order = Orders[Pid % 24];
        var key = Pid ^ OtId;
        for (var position = 0; position < 4; position++)
            _plain.AsSpan("GAEM".IndexOf(order[position]) * 12, 12).CopyTo(area[(position * 12)..]);
        for (var i = 0; i < 48; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(area[i..], BinaryPrimitives.ReadUInt32LittleEndian(area[i..]) ^ key);
        return _record;
    }
}
