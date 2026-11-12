using PKHeX.Core;

namespace PKForge.Engine.Quetzal;

/// <summary>
/// An editable PC record. Every accessor touches only the bits the game exposes; the
/// remaining bits (and the empty gaps) ride along untouched, so rewriting an untouched
/// record is byte-identical.
/// </summary>
internal sealed class QuetzalBoxRecord
{
    // Common bit layout (all three record sizes share bits 0-167).
    private const int SpeciesBit = 0;
    private const int ItemBit = 11;
    private const int ExperienceBit = 21;
    private const int BallBit = 38;
    private const int ShinyBit = 44;
    private const int MoveBit = 48;
    private const int EvBit = 88;
    private const int IvBit = 124;
    private const int FemaleBit = 160;
    private const int NatureBit = 161;
    private const int AbilityBit = 166;
    private const int CurrentHpBit = 168;

    public QuetzalBoxRecord(byte[] bytes, QuetzalBoxFormat format)
    {
        if (bytes.Length != format.RecordSize)
            throw new ArgumentException("Incorrect Quetzal record length.", nameof(bytes));
        Bytes = bytes;
        Format = format;
    }

    public byte[] Bytes { get; }

    public QuetzalBoxFormat Format { get; }

    public bool IsEmpty => Bytes.AsSpan().IndexOfAnyExcept((byte)0) < 0;

    public int Species
    {
        get => QuetzalFormat.ReadField(Bytes, SpeciesBit, 11);
        set => QuetzalFormat.WriteField(Bytes, SpeciesBit, 11, value);
    }

    public int HeldItem
    {
        get => QuetzalFormat.ReadField(Bytes, ItemBit, 10);
        set => QuetzalFormat.WriteField(Bytes, ItemBit, 10, value);
    }

    public uint Experience => (uint)QuetzalFormat.ReadField(Bytes, ExperienceBit, 17) * 10;

    public void SetExperience(uint experience) =>
        QuetzalFormat.WriteField(Bytes, ExperienceBit, 17, (int)(experience / 10));

    public int Ball
    {
        get => QuetzalFormat.ReadField(Bytes, BallBit, 6);
        set => QuetzalFormat.WriteField(Bytes, BallBit, 6, value);
    }

    public bool IsShiny
    {
        get => QuetzalFormat.ReadField(Bytes, ShinyBit, 1) != 0;
        set => QuetzalFormat.WriteField(Bytes, ShinyBit, 1, value ? 1 : 0);
    }

    public int Move(int index) => QuetzalFormat.ReadField(Bytes, MoveBit + index * 10, 10);

    public void SetMove(int index, int move) =>
        QuetzalFormat.WriteField(Bytes, MoveBit + index * 10, 10, move);

    /// <summary>EVs in app order; the record stores six 6-bit EV/4 fields in HP/Atk/Def/Spe/SpA/SpD order.</summary>
    public int Ev(int appIndex) =>
        QuetzalFormat.ReadField(Bytes, EvBit + QuetzalFormat.AppToStorage[appIndex] * 6, 6) * 4;

    public void SetEv(int appIndex, int value) =>
        QuetzalFormat.WriteField(Bytes, EvBit + QuetzalFormat.AppToStorage[appIndex] * 6, 6,
            Math.Clamp(value, 0, 255) / 4);

    public int Iv(int appIndex) =>
        QuetzalFormat.ReadField(Bytes, IvBit + QuetzalFormat.AppToStorage[appIndex] * 5, 5);

    public void SetIv(int appIndex, int value) =>
        QuetzalFormat.WriteField(Bytes, IvBit + QuetzalFormat.AppToStorage[appIndex] * 5, 5, value);

    public bool Female
    {
        get => QuetzalFormat.ReadField(Bytes, FemaleBit, 1) != 0;
        set => QuetzalFormat.WriteField(Bytes, FemaleBit, 1, value ? 1 : 0);
    }

    public int Nature
    {
        get => QuetzalFormat.ReadField(Bytes, NatureBit, 5);
        set => QuetzalFormat.WriteField(Bytes, NatureBit, 5, value);
    }

    public int AbilitySlot
    {
        get => QuetzalFormat.ReadField(Bytes, AbilityBit, 2);
        set => QuetzalFormat.WriteField(Bytes, AbilityBit, 2, value);
    }

    public int Level => QuetzalFormat.LevelForExperience(Experience);

    public bool HasNickname => Format.NicknameOffset >= 0;

    public string Nickname
    {
        get
        {
            if (!HasNickname) return string.Empty;
            var span = Bytes.AsSpan(Format.NicknameOffset, 10);
            var terminator = span.IndexOf((byte)0xFF);
            return StringConverter3.GetString(terminator < 0 ? span : span[..terminator], jp: false);
        }
        set
        {
            if (!HasNickname) return;
            var span = Bytes.AsSpan(Format.NicknameOffset, 10);
            span.Fill(0xFF);
            if (value.Length > 0) StringConverter3.SetString(span, value, 10, jp: false);
        }
    }

    public bool HasStoredPp => Format.PpOffset >= 0;

    public int Pp(int index) => HasStoredPp ? Bytes[Format.PpOffset + index] : 0;

    public void SetPp(int index, int pp)
    {
        if (HasStoredPp) Bytes[Format.PpOffset + index] = (byte)Math.Clamp(pp, 0, 255);
    }

    public int CurrentHp => Format.RecordSize == 38 ? QuetzalFormat.ReadField(Bytes, CurrentHpBit, 16) : 0;

    public int[] Ivs() => [Iv(0), Iv(1), Iv(2), Iv(3), Iv(4), Iv(5)];

    public int[] Evs() => [Ev(0), Ev(1), Ev(2), Ev(3), Ev(4), Ev(5)];
}
