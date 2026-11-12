using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine.OriginHeartGold;

/// <summary>
/// One Origin HeartGold PK4 record, decrypted into a private copy. The retail PK4 codec
/// (PRNG keyed by the checksum, block shuffle by PID, party tail keyed by the PID) is
/// unchanged; only the meaning of a few fields differs (see <see cref="OriginHeartGoldFormat"/>).
/// Nothing is written back until <see cref="Encrypted"/> is called by the session, and only
/// the setters below exist: every other byte keeps its stored value.
/// </summary>
internal sealed class OriginHeartGoldMon
{
    private readonly byte[] _plain;

    public OriginHeartGoldMon(ReadOnlySpan<byte> encrypted)
    {
        if (encrypted.Length is not (OriginHeartGoldFormat.StoredSize or OriginHeartGoldFormat.PartySize))
            throw new ArgumentException("A PK4 record is 136 or 236 bytes.", nameof(encrypted));
        _plain = encrypted.ToArray();
        PokeCrypto.Decrypt45(_plain);
    }

    public bool Party => _plain.Length == OriginHeartGoldFormat.PartySize;

    /// <summary>The empty slot the game writes: PID 0 and an all-zero data area.</summary>
    public bool IsEmpty => Pid == 0 && _plain.AsSpan(8, OriginHeartGoldFormat.StoredSize - 8).IndexOfAnyExcept((byte)0) < 0;

    public bool ChecksumValid => Checksums.Add16(_plain.AsSpan(8, OriginHeartGoldFormat.StoredSize - 8)) == StoredChecksum;
    public bool LooksValid => ChecksumValid && Species > 0 && Species <= OriginHeartGoldData.MaxStoredSpecies;

    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_plain.AsSpan(offset));
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_plain.AsSpan(offset));

    public uint Pid => U32(0x00);
    private ushort StoredChecksum => U16(0x06);
    public int Species => U16(0x08);
    public int HeldItem => U16(0x0A);
    public int TrainerId => U16(0x0C);
    public int SecretId => U16(0x0E);
    public uint Experience => U32(0x10);

    public int Friendship
    {
        get => _plain[0x14];
        set => _plain[0x14] = (byte)Math.Clamp(value, 0, 255);
    }

    /// <summary>The hack's ability slot (0/1), where the retail game keeps the ability id.</summary>
    public int AbilitySlot => _plain[0x15];
    public int Language => _plain[0x17];

    /// <summary>EVs in the app's order HP, Atk, Def, SpA, SpD, Spe (stored HP, Atk, Def, Spe, SpA, SpD).</summary>
    public int[] Evs
    {
        get => [_plain[0x18], _plain[0x19], _plain[0x1A], _plain[0x1C], _plain[0x1D], _plain[0x1B]];
        set
        {
            _plain[0x18] = (byte)value[0];
            _plain[0x19] = (byte)value[1];
            _plain[0x1A] = (byte)value[2];
            _plain[0x1C] = (byte)value[3];
            _plain[0x1D] = (byte)value[4];
            _plain[0x1B] = (byte)value[5];
        }
    }

    public int[] Moves => [U16(0x28), U16(0x2A), U16(0x2C), U16(0x2E)];
    public int[] Pp => [_plain[0x30], _plain[0x31], _plain[0x32], _plain[0x33]];
    public int[] PpUps => [_plain[0x34], _plain[0x35], _plain[0x36], _plain[0x37]];

    private uint Iv32
    {
        get => U32(0x38);
        set => BinaryPrimitives.WriteUInt32LittleEndian(_plain.AsSpan(0x38), value);
    }

    /// <summary>IVs in the app's order HP, Atk, Def, SpA, SpD, Spe (stored HP, Atk, Def, Spe, SpA, SpD).</summary>
    public int[] Ivs
    {
        get
        {
            var iv = Iv32;
            int Get(int index) => (int)(iv >> (index * 5)) & 31;
            return [Get(0), Get(1), Get(2), Get(4), Get(5), Get(3)];
        }
        set
        {
            var stored = new[] { value[0], value[1], value[2], value[5], value[3], value[4] };
            var iv = Iv32 & 0xC000_0000; // egg and nickname flags stay
            for (var i = 0; i < 6; i++)
                iv |= (uint)(stored[i] & 31) << (i * 5);
            Iv32 = iv;
        }
    }

    public bool IsEgg => (Iv32 >> 30 & 1) != 0;

    public bool IsNicknamed
    {
        get => (Iv32 >> 31) != 0;
        set => Iv32 = value ? Iv32 | 0x8000_0000 : Iv32 & 0x7FFF_FFFF;
    }

    private byte GenderFormByte => _plain[0x40];
    public bool FatefulEncounter => (GenderFormByte & 1) != 0;
    /// <summary>0 male, 1 female, 2 genderless (the stored flags, never re-derived).</summary>
    public int Gender => (GenderFormByte & 4) != 0 ? 2 : (GenderFormByte & 2) != 0 ? 1 : 0;
    public int Form => GenderFormByte >> 3;

    /// <summary>The hack's ability id (modern numbering), in the retail record's padding at 0x42.</summary>
    public int AbilityId => U16(0x42);
    public int EggLocation => U16(0x44);
    public int MetLocation => U16(0x46);

    public string Nickname => StringConverter4.GetString(_plain.AsSpan(0x48, 22));

    public void SetNickname(string value)
    {
        StringConverter4.SetString(_plain.AsSpan(0x48, 22), value, 10, Language, StringConverterOption.ClearZero);
    }

    public int Version => _plain[0x5F];
    public string OriginalTrainerName => StringConverter4.GetString(_plain.AsSpan(0x68, 16));
    public int Ball => _plain[0x86] != 0 ? _plain[0x86] : _plain[0x83];
    public int MetLevel => _plain[0x84] & 0x7F;
    public int OriginalTrainerGender => _plain[0x84] >> 7;

    public bool IsShiny => (TrainerId ^ SecretId ^ (int)(Pid >> 16) ^ (int)(Pid & 0xFFFF)) < 8;
    public int Nature => (int)(Pid % 25);

    // ── Party tail (computed by the game; never recomputed here) ──
    public int StatusCondition => Party ? (int)U32(0x88) : 0;
    public int StoredLevel => Party ? _plain[0x8C] : 0;
    public int CurrentHp => Party ? U16(0x8E) : 0;

    /// <summary>Stored party stats in the app's order HP, Atk, Def, SpA, SpD, Spe.</summary>
    public int[] PartyStats => Party ? [U16(0x90), U16(0x92), U16(0x94), U16(0x98), U16(0x9A), U16(0x96)] : [];

    /// <summary>The decrypted record (a copy).</summary>
    public byte[] Plain() => _plain.ToArray();

    /// <summary>The record as stored: checksum refreshed over the data area, re-encrypted.</summary>
    public byte[] Encrypted()
    {
        var bytes = _plain.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), Checksums.Add16(bytes.AsSpan(8, OriginHeartGoldFormat.StoredSize - 8)));
        PokeCrypto.Encrypt45(bytes);
        return bytes;
    }

    /// <summary>The bytes the game writes into an empty box slot (an encrypted all-zero record).</summary>
    public static byte[] EmptyBoxRecord()
    {
        var bytes = new byte[OriginHeartGoldFormat.StoredSize];
        PokeCrypto.Encrypt45(bytes);
        return bytes;
    }
}
