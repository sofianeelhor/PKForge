using System.Buffers.Binary;
using System.Diagnostics;
using PKHeX.Core;
using static PKForge.Engine.SoulGold.SoulGoldFormat;

namespace PKForge.Engine.SoulGold;

/// <summary>
/// One Soul Gold Pokémon record, read and written in place. Party records are
/// 96 bytes; box records are 76 (legacy V19) or 68 (BX21). The 32-byte header
/// and the secure bitfields are stored in the clear (there is no per-mon
/// crypto and no per-mon checksum), so edits are plain bit surgery that must
/// leave every unknown field untouched.
///
/// Field packing: include/pokemon_legacy.h @e738033 for the 44-byte V19 secure
/// layout, include/pokemon_storage_system.h @e738033 for the 36-byte BX21 one;
/// both verified against the sample's party and box records.
/// IV/EV storage order is the GBA one (HP, Atk, Def, Spe, SpA, SpD); the app's
/// order is HP, Atk, Def, SpA, SpD, Spe.
/// </summary>
internal sealed class SoulGoldMon
{
    private readonly byte[] _buffer;
    private readonly bool _party;
    private readonly StorageEra _era;
    private readonly SoulGoldData _data;

    public SoulGoldMon(byte[] buffer, int offset, bool party, StorageEra era, SoulGoldData data)
    {
        _buffer = buffer;
        Offset = offset;
        _party = party;
        _era = era;
        _data = data;
    }

    public int Offset { get; }

    public int Size => _party ? PartyMonSize : BoxMonSize;

    private int BoxMonSize => _era == StorageEra.Bx21 ? BoxMonSizeBx21 : BoxMonSizeV19;

    private int Secure => Offset + 0x20;

    public byte[] Buffer => _buffer;

    public bool Party => _party;

    // ── Header (shared by both generations) ──

    public uint Pid
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(Offset));
        set => BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(Offset), value);
    }

    public uint Otid
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(Offset + 4));
        set => BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(Offset + 4), value);
    }

    public string Nickname
    {
        get => StringConverter3.GetString(_buffer.AsSpan(Offset + 8, 12), jp: false);
        set => WriteName(Offset + 8, 12, value);
    }

    public string OriginalTrainerName
    {
        get => StringConverter3.GetString(_buffer.AsSpan(Offset + 0x16, 7), jp: false);
        set => WriteName(Offset + 0x16, 7, value);
    }

    private void WriteName(int at, int length, string value)
    {
        var span = _buffer.AsSpan(at, length);
        span.Fill(0xFF);
        StringConverter3.SetString(span, value, length - 1, jp: false);
    }

    /// <summary>language:3 + hiddenNatureModifier:5; the effective nature is
    /// the modifier (the PID no longer decides it).</summary>
    public int Nature
    {
        get => _buffer[Offset + 0x14] >> 3;
        set => _buffer[Offset + 0x14] = (byte)((_buffer[Offset + 0x14] & 0x07) | ((value % 25) << 3));
    }

    public int Language
    {
        get => _buffer[Offset + 0x14] & 0x07;
        set => _buffer[Offset + 0x14] = (byte)((_buffer[Offset + 0x14] & 0xF8) | (value & 0x07));
    }

    private byte Sanity
    {
        get => _buffer[Offset + 0x15];
        set => _buffer[Offset + 0x15] = value;
    }

    public bool IsBadEgg
    {
        get => (Sanity & 1) != 0;
        set => Sanity = (byte)((Sanity & ~1) | (value ? 1 : 0));
    }

    public bool HasSpeciesFlag
    {
        get => (Sanity & 2) != 0;
        set => Sanity = (byte)((Sanity & ~2) | (value ? 2 : 0));
    }

    public bool IsEggFlag
    {
        get => (Sanity & 4) != 0;
        set => Sanity = (byte)((Sanity & ~4) | (value ? 4 : 0));
    }

    /// <summary>hpLost:14 + shinyModifier:1 + modernFatefulEncounter:1.</summary>
    public bool IsShiny
    {
        get => (_buffer[Offset + 0x1E] & 0x40) != 0;
        set
        {
            if (value) _buffer[Offset + 0x1E] |= 0x40;
            else _buffer[Offset + 0x1E] &= 0xBF;
        }
    }

    // ── Secure data, V19 and BX21 layouts ──
    // The first two words pack identically: species:11, teraType:5, heldItem:10,
    // pokeball:6 then experience:24.

    public int Species
    {
        get => (int)(Read(0) & 0x7FF);
        set => Write(0, Read(0) & ~0x7FFu, (uint)(value & 0x7FF), 0x7FF);
    }

    public int TeraType
    {
        get => (int)((Read(0) >> 11) & 0x1F);
        set => Write(0, Read(0) & ~(0x1Fu << 11), (uint)(value & 0x1F) << 11, 0x1F << 11);
    }

    public int HeldItem
    {
        get => (int)((Read(0) >> 16) & 0x3FF);
        set => Write(0, Read(0) & ~(0x3FFu << 16), (uint)(value & 0x3FF) << 16, 0x3FF << 16);
    }

    public int Ball
    {
        get => (int)((Read(0) >> 26) & 0x3F);
        set => Write(0, Read(0) & ~(0x3Fu << 26), (uint)(value & 0x3F) << 26, 0x3Fu << 26);
    }

    public uint Experience
    {
        get => Read(1) & 0xFF_FFFF;
        set => Write(1, Read(1) & ~0xFF_FFFFu, value & 0xFF_FFFF, 0xFF_FFFF);
    }

    /// <summary>move1/2 live in word 3, move3/4 in word 4, both layouts, with
    /// evolution trackers in the gaps (left untouched by these accessors).</summary>
    public int[] Moves
    {
        get => [(int)(Read(3) & 0x7FF), (int)((Read(3) >> 16) & 0x7FF), (int)(Read(4) & 0x7FF), (int)((Read(4) >> 16) & 0x7FF)];
        set
        {
            for (var i = 0; i < 4; i++)
                this[i] = value[i];
        }
    }

    private int this[int slot]
    {
        set
        {
            var word = 3 + slot / 2;
            var shift = (slot % 2) * 16;
            Write(word, Read(word) & ~(0x7FFu << shift), (uint)(value & 0x7FF) << shift, 0x7FFu << shift);
        }
    }

    public int AbilitySlot
    {
        get => _era == StorageEra.Bx21 ? (int)((Read(5) >> 27) & 3) : (int)((Read(4) >> 27) & 3);
        set
        {
            if (_era == StorageEra.Bx21)
                Write(5, Read(5) & ~(3u << 27), (uint)(value & 3) << 27, 3u << 27);
            else
                Write(4, Read(4) & ~(3u << 27), (uint)(value & 3) << 27, 3u << 27);
        }
    }

    public int Friendship
    {
        get => _era == StorageEra.Bx21 ? (int)(Read(2) & 0xFF) : _buffer[Secure + 0x0B];
        set
        {
            if (_era == StorageEra.Bx21)
                Write(2, Read(2) & ~0xFFu, (uint)value, 0xFF);
            else
                _buffer[Secure + 0x0B] = (byte)value;
        }
    }

    public int Pokerus
    {
        get => _era == StorageEra.Bx21 ? (int)((Read(2) >> 8) & 0xFF) : _buffer[Secure + 0x24];
        set
        {
            if (_era == StorageEra.Bx21)
                Write(2, Read(2) & ~(0xFFu << 8), (uint)value << 8, 0xFF << 8);
            else
                _buffer[Secure + 0x24] = (byte)value;
        }
    }

    public int MetLocation
    {
        get => _era == StorageEra.Bx21 ? (int)((Read(2) >> 16) & 0xFF) : _buffer[Secure + 0x25];
        set
        {
            if (_era == StorageEra.Bx21)
                Write(2, Read(2) & ~(0xFFu << 16), (uint)value << 16, 0xFF << 16);
            else
                _buffer[Secure + 0x25] = (byte)value; // the met-location high bits stay as found
        }
    }

    /// <summary>metLevel:7 + metGame:4 + otGender:1, packed in one u16 (V19) or
    /// the third word's top byte pair (BX21; metGame moves to word 5 there).</summary>
    public int MetLevel => (MetWord >> 7) & 0x7F;

    public int MetGame => _era == StorageEra.Bx21 ? (int)((Read(5) >> 28) & 0xF) : (MetWord >> 11) & 0xF;

    public int OtGender => _era == StorageEra.Bx21 ? (int)((Read(2) >> 31) & 1) : (MetWord >> 15) & 1;

    private ushort MetWord
    {
        get => _era == StorageEra.Bx21
            ? (ushort)((Read(2) >> 24) & 0x7F)
            : BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(Secure + 0x26));
    }

    public void SetMet(int metLevel, int? metGame = null, int? otGender = null)
    {
        if (_era == StorageEra.Bx21)
        {
            var word = Read(2) & ~(0x7Fu << 24);
            word |= (uint)(metLevel & 0x7F) << 24;
            if (otGender is { } gender)
            {
                word &= ~(1u << 31);
                word |= (uint)(gender & 1) << 31;
            }
            Write(2, word, 0, 0);
            if (metGame is { } game)
                Write(5, Read(5) & ~(0xFu << 28), (uint)(game & 0xF) << 28, 0xFu << 28);
            return;
        }
        var current = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(Secure + 0x26));
        current = (ushort)((current & ~0x7F) | (metLevel & 0x7F));
        if (metGame is { } game2)
            current = (ushort)((current & ~0x780) | ((game2 & 0xF) << 11));
        if (otGender is { } gender2)
            current = (ushort)((current & 0x7FFF) | ((gender2 & 1) << 15));
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(Secure + 0x26), current);
    }

    /// <summary>IVs in app order (HP, Atk, Def, SpA, SpD, Spe); storage order is
    /// HP, Atk, Def, Spe, SpA, SpD with 5 bits each, then isEgg and the
    /// gigantamax factor (left untouched).</summary>
    public int[] Ivs
    {
        get
        {
            var word = (int)IvWord;
            return [(word >> 0) & 31, (word >> 5) & 31, (word >> 10) & 31, (word >> 20) & 31, (word >> 25) & 31, (word >> 15) & 31];
        }
        set
        {
            var order = new[] { value[0], value[1], value[2], value[5], value[3], value[4] };
            uint word = 0;
            for (var i = 0; i < 6; i++)
                word |= (uint)(order[i] & 31) << (5 * i);
            Write(IvWordIndex, Read(IvWordIndex) & ~0x3FFF_FFFFu, word, 0x3FFF_FFFF);
        }
    }

    private uint IvWord => Read(IvWordIndex) & 0x3FFF_FFFF;

    private int IvWordIndex => _era == StorageEra.Bx21 ? 8 : 10;

    /// <summary>EVs in app order, 8 bits each; V19 packs six bytes from
    /// +0x18, BX21 splits them over words 6 and 7.</summary>
    public int[] Evs
    {
        get
        {
            var raw = new int[6];
            if (_era == StorageEra.Bx21)
            {
                for (var i = 0; i < 4; i++) raw[i] = (int)((Read(6) >> (8 * i)) & 0xFF);
                raw[4] = (int)(Read(7) & 0xFF);
                raw[5] = (int)((Read(7) >> 8) & 0xFF);
            }
            else
            {
                for (var i = 0; i < 6; i++) raw[i] = _buffer[Secure + 0x18 + i];
            }
            // Both stores keep the GBA order HP, Atk, Def, Spe, SpA, SpD.
            return [raw[0], raw[1], raw[2], raw[5], raw[3], raw[4]];
        }
        set
        {
            // app order -> storage order: HP, Atk, Def, Spe(=idx5), SpA(=3), SpD(=4)
            var order = new[] { (uint)value[0], (uint)value[1], (uint)value[2], (uint)value[5], (uint)value[3], (uint)value[4] };
            if (_era == StorageEra.Bx21)
            {
                var word6 = Read(6) & 0xFF00_0000u;
                word6 |= order[0] | (order[1] << 8) | (order[2] << 16) | (order[3] << 24);
                Write(6, word6, 0, 0);
                Write(7, Read(7) & ~0xFFFFu, order[4] | (order[5] << 8), 0xFFFF);
                return;
            }
            for (var i = 0; i < 6; i++)
                _buffer[Secure + 0x18 + i] = (byte)order[i];
        }
    }

    public int PpBonuses
    {
        get => _era == StorageEra.Bx21 ? (int)(Read(1) >> 24) : _buffer[Secure + 0x0A];
        set
        {
            if (_era == StorageEra.Bx21)
                Write(1, Read(1) & ~0xFF00_0000u, (uint)value << 24, 0xFF00_0000);
            else
                _buffer[Secure + 0x0A] = (byte)value;
        }
    }

    /// <summary>Current PP per move slot; V19 keeps four bytes at +0x14 (7 bits
    /// each + a hyper-training bit), BX21 packs 4×7 bits into word 5.</summary>
    public int[] Pp
    {
        get
        {
            if (_era != StorageEra.Bx21)
                return [_buffer[Secure + 0x14] & 0x7F, _buffer[Secure + 0x15] & 0x7F, _buffer[Secure + 0x16] & 0x7F, _buffer[Secure + 0x17] & 0x7F];
            var word = Read(5);
            return [(int)(word & 0x7F), (int)((word >> 7) & 0x7F), (int)((word >> 14) & 0x7F), (int)((word >> 21) & 0x7F)];
        }
        set
        {
            if (_era != StorageEra.Bx21)
            {
                for (var i = 0; i < 4; i++)
                    _buffer[Secure + 0x14 + i] = (byte)((_buffer[Secure + 0x14 + i] & 0x80) | (value[i] & 0x7F));
                return;
            }
            uint word = 0;
            for (var i = 0; i < 4; i++)
                word |= (uint)(value[i] & 0x7F) << (7 * i);
            Write(5, Read(5) & ~0x0FFF_FFFFu, word, 0x0FFF_FFFF);
        }
    }

    // ── Party tail (same absolute offsets in both generations) ──

    public uint StatusCondition
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(Offset + 0x4C));
        set => BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(Offset + 0x4C), value);
    }

    public int Level
    {
        get => _buffer[Offset + 0x50];
        set => _buffer[Offset + 0x50] = (byte)Math.Clamp(value, 1, 100);
    }

    /// <summary>Level as derived from EXP and the species' growth rate; party
    /// records also carry a stored level byte, which edits keep in step.</summary>
    int LevelFromExperience => _data.LevelForExperience(_data.GrowthRate(Species), Experience);

    public int StoredOrDerivedLevel => _party ? Level : LevelFromExperience;

    public int CurrentHp
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(Offset + 0x52));
        set => BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(Offset + 0x52), (ushort)value);
    }

    /// <summary>Party stats in storage order: HP, maxHP, Atk, Def, Spe, SpA, SpD
    /// at +0x52..+0x60.</summary>
    public int[] PartyStats =>
    [
        CurrentHp,
        BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(Offset + 0x54)),
        BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(Offset + 0x56)),
        BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(Offset + 0x58)),
        BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(Offset + 0x5A)),
        BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(Offset + 0x5C)),
        BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(Offset + 0x5E)),
    ];

    public void WritePartyStats(IReadOnlyList<int> stats)
    {
        var offsets = new[] { 0x52, 0x54, 0x56, 0x58, 0x5A, 0x5C, 0x5E };
        for (var i = 0; i < 7; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(Offset + offsets[i]), (ushort)stats[i]);
    }

    // ── Validity ──

    public bool IsEmpty => Species == 0 && Pid == 0;

    /// <summary>Mirror of the game's structural check: known species, not a bad
    /// egg, and the hasSpecies flag consistent with a non-zero species.</summary>
    public bool LooksValid =>
        _data.IsKnownSpecies(Species) && !IsBadEgg &&
        (Species == 0 ? !HasSpeciesFlag : HasSpeciesFlag || _party);

    /// <summary>Keeps the header flags the loader expects on an occupied slot.</summary>
    public void MarkOccupied()
    {
        HasSpeciesFlag = Species > 0;
        IsBadEgg = false;
    }

    public byte[] RawRecord() => _buffer.AsSpan(Offset, Size).ToArray();

    public void Clear() => _buffer.AsSpan(Offset, Size).Clear();

    // ── Bit plumbing ──

    private uint Read(int word) => BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(Secure + 4 * word));

    /// <summary>Replaces the masked bits of one secure word; <paramref name="valid"/>
    /// documents the mask and is asserted against it.</summary>
    private void Write(int word, uint cleared, uint payload, uint valid)
    {
        Debug.Assert(valid == 0 || payload == (payload & valid));
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(Secure + 4 * word), cleared | payload);
    }
}
