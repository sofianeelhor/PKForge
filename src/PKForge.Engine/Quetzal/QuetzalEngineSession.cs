using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine.Quetzal;

/// <summary>
/// Pokémon Quetzal as a PKForge session. The hack keeps Emerald's sector envelope but
/// widens it to 16 sections per slot; the party and PC are plaintext records of its own
/// shape. The PC record size changed between builds without a marker, so it is identified
/// from the data instead of assumed. Only species/exposed bit fields are interpreted:
/// every other bit (and the gaps between records) is preserved.
///
/// Names are best-effort: no Quetzal id tables are vendored, so ids that fall inside the
/// national ranges are named through PKHeX and anything above them keeps its raw id and
/// is labelled <c>Species N</c>/<c>Move N</c>/<c>Item N</c>.
/// </summary>
internal sealed class QuetzalEngineSession : ISaveEngineSession
{
    private readonly byte[] _data;
    private readonly byte[] _originalBytes;
    private readonly int[] _sections;
    private readonly string? _displayName;
    private readonly QuetzalBoxFormat? _boxFormat;
    private readonly int _boxCount;
    private bool _dirty;
    private bool _disposed;

    private const string SaveFormat = "Pokémon Quetzal";

    // ── Party record offsets (plaintext, 104 bytes) ──
    private const int PartyPid = 0x00;
    private const int PartyOtId = 0x04;
    private const int PartyNickname = 0x08;
    private const int PartyNicknameLength = 10;
    private const int PartyLanguage = 0x12;
    private const int PartyFlags = 0x13;
    private const int PartyOtName = 0x14;
    private const int PartyOtNameLength = 7;
    private const int PartyCurrentHp = 0x23;
    private const int PartySpecies = 0x28;
    private const int PartyItem = 0x2A;
    private const int PartyExperience = 0x2C;
    private const int PartyFriendship = 0x31;
    private const int PartyBall = 0x32;
    private const int PartyMoves = 0x34;
    private const int PartyPp = 0x3C;
    private const int PartyEvs = 0x40;
    private const int PartyIvs = 0x50;
    private const int PartyAbilityWord = 0x54;
    private const int PartyLevel = 0x58;
    private const int PartyStats = 0x5A;

    private const int ShinyFlag = 0x08;

    internal QuetzalEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _displayName = displayName;
        _originalBytes = bytes.ToArray();
        // Emulator padding and mGBA's RTC footer sit past the flash chip; Serialize re-appends them.
        _data = SramPadding.Trim(RetroArchSaveContainer.Decode(bytes.Span));
        if (_data.Length != QuetzalFormat.FileSize || !QuetzalFormat.TryGetNewestSlot(_data, out _sections))
            throw new InvalidDataException("These bytes are not a Pokémon Quetzal save.");
        _boxFormat = QuetzalFormat.DetectBoxFormat(_data, _sections);
        _boxCount = _boxFormat?.BoxCount ?? 0;
    }

    public SaveSnapshot Snapshot => BuildSnapshot(_displayName);

    public int Generation => 3;
    public IReadOnlyList<string> GameNames => ["Quetzal"];
    public int MaxSpeciesId => QuetzalFormat.MaxSpeciesId;
    public int BoxCount => _boxCount;
    public int BoxSlotCount => QuetzalBoxFormat.SlotsPerBox;
    public bool SupportsBoxTools => false;
    public bool SupportsLegalityAnalysis => false;
    /// <summary>Abilities are stored as a slot, never as a free id: HaX cannot pick an arbitrary one.</summary>
    public bool StoresAnyAbility => false;

    public bool StoresMove(int move) => move is >= 0 and <= QuetzalFormat.MaxMoveId;
    public bool SupportsRTCRepair => false;
    public bool SupportsLegalFashionUnlock => false;
    public bool SupportsStylePoints => false;
    public bool SupportsCompassSettings => false;

    /// <summary>Whether the PC record layout was identified. A session still opens for
    /// party editing when it was not; the box surfaces stay empty rather than guessed.</summary>
    internal bool BoxFormatDetected => _boxFormat is not null;

    /// <summary>The detected record size (21, 31 or 38), or 0 when none was identified.</summary>
    internal int BoxRecordSize => _boxFormat?.RecordSize ?? 0;

    private int PartyCount => QuetzalFormat.PartyCount(_data, _sections);

    // ── Slot plumbing ──

    private QuetzalBoxRecord? ReadBoxRecord(int box, int slot)
    {
        if (_boxFormat is not { } format || (uint)box >= (uint)_boxCount || (uint)slot >= BoxSlotCount)
            return null;
        var bytes = new byte[format.RecordSize];
        QuetzalFormat.ReadPc(_data, _sections, QuetzalFormat.RecordOffset(format, box, slot), bytes);
        return new QuetzalBoxRecord(bytes, format);
    }

    private QuetzalBoxRecord BoxRecordForEdit(int box, int slot)
    {
        var record = ReadBoxRecord(box, slot)
            ?? throw new InvalidOperationException("That PC slot does not exist.");
        if (record.Species == 0)
            throw new InvalidOperationException("Cannot edit an empty slot.");
        return record;
    }

    private void WriteBoxRecord(int box, int slot, QuetzalBoxRecord record)
    {
        if (_boxFormat is not { } format) return;
        QuetzalFormat.WritePc(_data, _sections, QuetzalFormat.RecordOffset(format, box, slot), record.Bytes);
    }

    private int PartyRecordOffset(int slot) => QuetzalFormat.PartyRecordOffset(_sections, slot);

    // ── Reading ──

    /// <summary>Raw bytes of every slot (party first, then every detected box record) for
    /// the write-safety structural diff (see <see cref="WriteSafety"/>).</summary>
    internal IEnumerable<SlotImage> SlotImages()
    {
        var count = PartyCount;
        for (var slot = 0; slot < QuetzalFormat.PartySize; slot++)
        {
            var occupied = slot < count;
            var bytes = occupied
                ? _data.AsSpan(PartyRecordOffset(slot), QuetzalFormat.PartyRecordSize).ToArray()
                : new byte[QuetzalFormat.PartyRecordSize];
            yield return new SlotImage(new SlotRef(-1, slot), bytes, Empty: !occupied, Valid: true);
        }
        for (var box = 0; box < _boxCount; box++)
        for (var slot = 0; slot < BoxSlotCount; slot++)
        {
            var record = ReadBoxRecord(box, slot);
            yield return record is null
                ? new SlotImage(new SlotRef(box, slot), [], Empty: true, Valid: true)
                : new SlotImage(new SlotRef(box, slot), record.Bytes, record.Species == 0, Valid: true);
        }
    }

    public EntityDetail ReadEntity(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (box == -1)
        {
            if ((uint)slot >= (uint)PartyCount) return EmptyDetail(box, slot);
            return ReadParty(slot);
        }
        var record = ReadBoxRecord(box, slot);
        return record is null || record.Species == 0 ? EmptyDetail(box, slot) : ReadBox(box, slot, record);
    }

    private EntityDetail ReadParty(int slot)
    {
        var start = PartyRecordOffset(slot);
        var record = _data.AsSpan(start, QuetzalFormat.PartyRecordSize);
        var pid = BinaryPrimitives.ReadUInt32LittleEndian(record[PartyPid..]);
        var species = BinaryPrimitives.ReadUInt16LittleEndian(record[PartySpecies..]);
        var nickname = ReadText(record.Slice(PartyNickname, PartyNicknameLength));
        var ivWord = BinaryPrimitives.ReadUInt32LittleEndian(record[PartyIvs..]);
        int[] ivs = new int[6];
        int[] evs = new int[6];
        int[] stats = new int[6];
        for (var i = 0; i < 6; i++)
        {
            var storage = QuetzalFormat.AppToStorage[i];
            ivs[i] = (int)(ivWord >> (storage * 5)) & 0x1F;
            evs[i] = record[PartyEvs + storage];
            stats[i] = BinaryPrimitives.ReadUInt16LittleEndian(record[(PartyStats + storage * 2)..]);
        }
        return new EntityDetail(
            -1, slot, false, species, QuetzalFormat.SpeciesName(species), 0,
            nickname.Length > 0 ? nickname : QuetzalFormat.SpeciesName(species),
            record[PartyLevel], (int)(pid & 0xFF) % 25, QuetzalFormat.AbilityAt(species, PartyAbilitySlot(record)),
            BinaryPrimitives.ReadUInt16LittleEndian(record[PartyItem..]),
            BinaryPrimitives.ReadUInt16LittleEndian(record[(PartyMoves + 0)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(record[(PartyMoves + 2)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(record[(PartyMoves + 4)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(record[(PartyMoves + 6)..]),
            ivs, evs,
            (record[PartyFlags] & ShinyFlag) != 0,
            record[PartyBall],
            ReadText(record.Slice(PartyOtName, PartyOtNameLength)),
            QuetzalFormat.TypesOf(species),
            QuetzalFormat.GenderOf(pid, species),
            record[PartyFriendship],
            stats,
            BinaryPrimitives.ReadUInt16LittleEndian(record[PartyCurrentHp..]),
            0,
            AbilityName: AbilityLabel(species, PartyAbilitySlot(record)));
    }

    private static int PartyAbilitySlot(ReadOnlySpan<byte> record) =>
        (int)((BinaryPrimitives.ReadUInt32LittleEndian(record[PartyAbilityWord..]) >> 28) & 3);

    /// <summary>Names a slot the personal data cannot (a Gen 3 species' third, one of the
    /// hack's own species), so it never reads "(None)".</summary>
    private static string? AbilityLabel(int species, int slot) =>
        QuetzalFormat.AbilityAt(species, slot) == 0 ? $"Ability slot {slot + 1}" : null;

    /// <summary>The stored ability slot (0-2) of a filled slot.</summary>
    private int AbilitySlotAt(int box, int slot)
    {
        if (box != -1)
            return ReadBoxRecord(box, slot)?.AbilitySlot ?? 0;
        return PartyAbilitySlot(_data.AsSpan(PartyRecordOffset(slot), QuetzalFormat.PartyRecordSize));
    }

    private EntityDetail ReadBox(int box, int slot, QuetzalBoxRecord record)
    {
        var species = record.Species;
        var nickname = record.Nickname;
        return new EntityDetail(
            box, slot, false, species, QuetzalFormat.SpeciesName(species), 0,
            nickname.Length > 0 ? nickname : QuetzalFormat.SpeciesName(species),
            record.Level, record.Nature, QuetzalFormat.AbilityAt(species, record.AbilitySlot), record.HeldItem,
            record.Move(0), record.Move(1), record.Move(2), record.Move(3),
            record.Ivs(), record.Evs(), record.IsShiny, record.Ball, string.Empty,
            QuetzalFormat.TypesOf(species),
            record.Female ? 1 : 0,
            0, // the packed PC record has no friendship byte
            [0, 0, 0, 0, 0, 0],
            record.CurrentHp,
            0,
            AbilityName: AbilityLabel(species, record.AbilitySlot));
    }

    private static EntityDetail EmptyDetail(int box, int slot) =>
        new(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0,
            [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);

    // ── Editing ──

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        edit = NameCharset.FitGen3Edit(edit); // charset first: a name the game cannot hold is refused, not emptied
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (box == -1)
        {
            if ((uint)slot >= (uint)PartyCount) throw new InvalidOperationException("Cannot edit an empty slot.");
            ApplyPartyEdit(slot, edit with { Ability = AbilitySlotFor(box, slot, edit) });
            return;
        }
        var record = BoxRecordForEdit(box, slot);
        ApplyBoxEdit(box, slot, record, edit with { Ability = AbilitySlotFor(box, slot, edit) });
    }

    /// <summary>
    /// The editor speaks ability ids; the record keeps a slot. The id shown is a no-op, any
    /// other id must be one of the (new) species' three slots.
    /// </summary>
    private int? AbilitySlotFor(int box, int slot, EntityEdit edit)
    {
        if (edit.Ability is not { } ability) return null;
        var shown = ReadEntity(box, slot);
        var species = edit.Species ?? shown.Species;
        if (ability == shown.Ability && species == shown.Species) return null;
        return QuetzalFormat.AbilitySlotOf(species, ability)
            ?? throw new ArgumentOutOfRangeException(nameof(edit), $"Ability {ability} is not one of this Pokémon's abilities.");
    }

    private void ApplyPartyEdit(int slot, EntityEdit edit)
    {
        var start = PartyRecordOffset(slot);
        var before = _data.AsSpan(start, QuetzalFormat.PartyRecordSize).ToArray();
        var record = _data.AsSpan(start, QuetzalFormat.PartyRecordSize);

        // Validate every supplied field before touching a byte, so a refused edit is atomic.
        if (edit.Species is { } species) RequireRange(species, 1, QuetzalFormat.MaxSpeciesId, "Species");
        if (edit.Nickname is { } nickname) RequireText(nickname, PartyNicknameLength, "Nickname");
        if (edit.Level is { } level) RequireRange(level, 1, 100, "Level");
        if (edit.Nature is { } nature) RequireRange(nature, 0, 24, "Nature");
        if (edit.Ability is { } ability) RequireRange(ability, 0, 2, "Ability slot");
        if (edit.HeldItem is { } item) RequireRange(item, 0, ushort.MaxValue, "Held item");
        if (edit.Ball is { } ball) RequireRange(ball, 0, byte.MaxValue, "Ball");
        if (edit.OriginalTrainer is { } ot) RequireText(ot, PartyOtNameLength, "Original trainer");
        if (edit.Friendship is { } friendship) RequireRange(friendship, 0, byte.MaxValue, "Friendship");
        if (edit.Gender is { } gender) RequireRange(gender, 0, 2, "Gender");
        RequireMoves(edit);
        RequireIvsEvs(edit);

        var statsAffected = false;

        if (edit.Species is { } wantedSpecies &&
            BinaryPrimitives.ReadUInt16LittleEndian(record[PartySpecies..]) != wantedSpecies)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(record[PartySpecies..], (ushort)wantedSpecies);
            statsAffected = true;
        }
        if (edit.Nickname is { Length: > 0 } wantedNickname &&
            wantedNickname != ReadText(record.Slice(PartyNickname, PartyNicknameLength)))
        {
            WriteText(record.Slice(PartyNickname, PartyNicknameLength), wantedNickname);
        }
        if (edit.Level is { } wantedLevel && record[PartyLevel] != wantedLevel)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(record[PartyExperience..],
                (uint)QuetzalFormat.ExperienceForLevel(wantedLevel));
            record[PartyLevel] = (byte)wantedLevel;
            statsAffected = true;
        }
        if (edit.Nature is { } wantedNature)
        {
            var pid = BinaryPrimitives.ReadUInt32LittleEndian(record[PartyPid..]);
            if ((int)(pid & 0xFF) % 25 != wantedNature)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(record[PartyPid..], RerollLowByte(pid, wantedNature));
                statsAffected = true;
            }
        }
        if (edit.Ability is { } wantedAbility)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(record[PartyAbilityWord..]);
            if ((int)((word >> 28) & 3) != wantedAbility)
            {
                word = (word & ~(3u << 28)) | ((uint)wantedAbility << 28);
                BinaryPrimitives.WriteUInt32LittleEndian(record[PartyAbilityWord..], word);
            }
        }
        if (edit.HeldItem is { } wantedItem &&
            BinaryPrimitives.ReadUInt16LittleEndian(record[PartyItem..]) != wantedItem)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(record[PartyItem..], (ushort)wantedItem);
        }
        WriteMoves(record, edit);
        if (edit.IVs is { Count: 6 } wantedIvs && !IvsMatch(record, wantedIvs))
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(record[PartyIvs..]);
            for (var i = 0; i < 6; i++)
            {
                var storage = QuetzalFormat.AppToStorage[i];
                word = (word & ~(0x1Fu << (storage * 5))) | ((uint)wantedIvs[i] << (storage * 5));
            }
            BinaryPrimitives.WriteUInt32LittleEndian(record[PartyIvs..], word);
            statsAffected = true;
        }
        if (edit.EVs is { Count: 6 } wantedEvs && !EvsMatch(record, wantedEvs))
        {
            for (var i = 0; i < 6; i++)
                record[PartyEvs + QuetzalFormat.AppToStorage[i]] = (byte)wantedEvs[i];
            statsAffected = true;
        }
        if (edit.IsShiny is { } shiny && ((record[PartyFlags] & ShinyFlag) != 0) != shiny)
        {
            record[PartyFlags] = shiny
                ? (byte)(record[PartyFlags] | ShinyFlag)
                : (byte)(record[PartyFlags] & ~ShinyFlag);
        }
        if (edit.Ball is { } wantedBall && record[PartyBall] != wantedBall)
            record[PartyBall] = (byte)wantedBall;
        if (edit.OriginalTrainer is { Length: > 0 } wantedOt &&
            wantedOt != ReadText(record.Slice(PartyOtName, PartyOtNameLength)))
        {
            WriteText(record.Slice(PartyOtName, PartyOtNameLength), wantedOt);
        }
        if (edit.Gender is 0 or 1 && edit.Gender != QuetzalFormat.GenderOf(
                BinaryPrimitives.ReadUInt32LittleEndian(record[PartyPid..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[PartySpecies..])))
        {
            var pid = BinaryPrimitives.ReadUInt32LittleEndian(record[PartyPid..]);
            var genderSpecies = BinaryPrimitives.ReadUInt16LittleEndian(record[PartySpecies..]);
            if (TryRerollGender(pid, genderSpecies, edit.Gender.Value, out var rerolled))
                BinaryPrimitives.WriteUInt32LittleEndian(record[PartyPid..], rerolled);
        }
        // The editor echoes the stored byte it was shown, so an untouched value writes nothing.
        if (edit.Friendship is { } wantedFriendship &&
            record[PartyFriendship] != wantedFriendship)
            record[PartyFriendship] = (byte)wantedFriendship;

        if (statsAffected) RecomputePartyStats(start);

        if (!before.AsSpan().SequenceEqual(_data.AsSpan(start, QuetzalFormat.PartyRecordSize)))
            _dirty = true;
    }

    private void ApplyBoxEdit(int box, int slot, QuetzalBoxRecord record, EntityEdit edit)
    {
        if (edit.Species is { } species) RequireRange(species, 1, QuetzalFormat.MaxSpeciesId, "Species");
        if (edit.Nickname is { } nickname) RequireText(nickname, 10, "Nickname");
        if (edit.Level is { } level) RequireRange(level, 1, 100, "Level");
        if (edit.Nature is { } nature) RequireRange(nature, 0, 24, "Nature");
        if (edit.Ability is { } ability) RequireRange(ability, 0, 2, "Ability slot");
        if (edit.HeldItem is { } item) RequireRange(item, 0, 1023, "Held item");
        if (edit.Ball is { } ball) RequireRange(ball, 0, 63, "Ball");
        if (edit.Gender is { } gender) RequireRange(gender, 0, 2, "Gender");
        RequireMoves(edit);
        RequireIvsEvs(edit);

        var before = record.Bytes.ToArray();

        if (edit.Species is { } wantedSpecies && record.Species != wantedSpecies)
            record.Species = wantedSpecies;
        if (edit.Nickname is { Length: > 0 } wantedNickname && record.HasNickname &&
            wantedNickname != record.Nickname)
        {
            record.Nickname = wantedNickname;
        }
        if (edit.Level is { } wantedLevel && record.Level != wantedLevel)
            record.SetExperience(QuetzalFormat.BoxExperienceForLevel(wantedLevel));
        if (edit.Nature is { } wantedNature && record.Nature != wantedNature)
            record.Nature = wantedNature;
        if (edit.Ability is { } wantedAbility && record.AbilitySlot != wantedAbility)
            record.AbilitySlot = wantedAbility;
        if (edit.HeldItem is { } wantedItem && record.HeldItem != wantedItem)
            record.HeldItem = wantedItem;
        for (var i = 0; i < 4; i++)
        {
            int? wanted = i switch { 0 => edit.Move1, 1 => edit.Move2, 2 => edit.Move3, _ => edit.Move4 };
            if (wanted is { } move && record.Move(i) != move) record.SetMove(i, move);
        }
        if (edit.IVs is { Count: 6 } wantedIvs)
            for (var i = 0; i < 6; i++)
                if (record.Iv(i) != wantedIvs[i]) record.SetIv(i, wantedIvs[i]);
        if (edit.EVs is { Count: 6 } wantedEvs)
            for (var i = 0; i < 6; i++)
                if (record.Ev(i) != wantedEvs[i]) record.SetEv(i, wantedEvs[i]);
        if (edit.IsShiny is { } shiny && record.IsShiny != shiny)
            record.IsShiny = shiny;
        if (edit.Ball is { } wantedBall && record.Ball != wantedBall)
            record.Ball = wantedBall;
        if (edit.Gender is 0 or 1 && record.Female != (edit.Gender == 1))
            record.Female = edit.Gender == 1;

        if (!before.AsSpan().SequenceEqual(record.Bytes))
        {
            WriteBoxRecord(box, slot, record);
            _dirty = true;
        }
    }

    private void WriteMoves(Span<byte> record, EntityEdit edit)
    {
        for (var i = 0; i < 4; i++)
        {
            int? wanted = i switch { 0 => edit.Move1, 1 => edit.Move2, 2 => edit.Move3, _ => edit.Move4 };
            if (wanted is not { } move) continue;
            var offset = PartyMoves + i * 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[offset..]) != move)
                BinaryPrimitives.WriteUInt16LittleEndian(record[offset..], (ushort)move);
        }
    }

    private static void RequireMoves(EntityEdit edit)
    {
        int?[] moves = [edit.Move1, edit.Move2, edit.Move3, edit.Move4];
        foreach (var move in moves)
            if (move is { } value) RequireRange(value, 0, QuetzalFormat.MaxMoveId, "Move");
    }

    private static void RequireIvsEvs(EntityEdit edit)
    {
        if (edit.IVs is { } ivs)
        {
            if (ivs.Count != 6) throw new InvalidOperationException("IVs must have six values.");
            foreach (var iv in ivs) RequireRange(iv, 0, 31, "IV");
        }
        if (edit.EVs is { } evs)
        {
            if (evs.Count != 6) throw new InvalidOperationException("EVs must have six values.");
            foreach (var ev in evs) RequireRange(ev, 0, 255, "EV");
        }
    }

    private static int RequireRange(int value, int minimum, int maximum, string what)
    {
        if (value < minimum || value > maximum)
            throw new InvalidOperationException($"{what} {value} is outside Pokémon Quetzal's {minimum}-{maximum} range.");
        return value;
    }

    private static void RequireText(string value, int maximum, string what)
    {
        if (value.Length > maximum)
            throw new InvalidOperationException($"{what} is longer than the {maximum} characters this record stores.");
    }

    /// <summary>Keeps the nature remainder and rewrites only the PID's low byte.</summary>
    private static uint RerollLowByte(uint pid, int nature)
    {
        var quotient = (pid & 0xFF) / 25;
        var low = quotient * 25 + nature;
        if (low > 0xFF) low -= 25;
        return (pid & 0xFFFF_FF00) | (uint)low;
    }

    /// <summary>Finds a low byte with the requested gender that keeps the PID's nature.</summary>
    private static bool TryRerollGender(uint pid, int species, int gender, out uint rerolled)
    {
        rerolled = pid;
        var threshold = QuetzalFormat.GenderThreshold(species);
        if (threshold is PersonalInfo.RatioMagicGenderless or PersonalInfo.RatioMagicFemale or PersonalInfo.RatioMagicMale)
            return false;
        var nature = (int)(pid & 0xFF) % 25;
        var current = (int)(pid & 0xFF);
        var best = -1;
        for (var quotient = 0; quotient <= 10; quotient++)
        {
            var low = quotient * 25 + nature;
            if (low > 0xFF) break;
            if ((low < threshold ? 1 : 0) != gender) continue;
            if (best < 0 || Math.Abs(low - current) < Math.Abs(best - current)) best = low;
        }
        if (best < 0) return false;
        rerolled = (pid & 0xFFFF_FF00) | (uint)best;
        return true;
    }

    /// <summary>Gen 3 stats from the record's own level/IV/EV/nature after an edit, plus HP
    /// clamped to the new maximum.</summary>
    private void RecomputePartyStats(int start)
    {
        var record = _data.AsSpan(start, QuetzalFormat.PartyRecordSize);
        var species = BinaryPrimitives.ReadUInt16LittleEndian(record[PartySpecies..]);
        var level = record[PartyLevel];
        var pid = BinaryPrimitives.ReadUInt32LittleEndian(record[PartyPid..]);
        var ivWord = BinaryPrimitives.ReadUInt32LittleEndian(record[PartyIvs..]);
        int[] ivs = new int[6];
        int[] evs = new int[6];
        for (var i = 0; i < 6; i++)
        {
            var storage = QuetzalFormat.AppToStorage[i];
            ivs[i] = (int)(ivWord >> (storage * 5)) & 0x1F;
            evs[i] = record[PartyEvs + storage];
        }
        var stats = QuetzalFormat.ComputeStats(species, level, ivs, evs, (int)(pid & 0xFF) % 25);
        if (stats is null) return;
        var current = BinaryPrimitives.ReadUInt16LittleEndian(record[PartyCurrentHp..]);
        for (var i = 0; i < 6; i++)
        {
            var storage = QuetzalFormat.AppToStorage[i];
            BinaryPrimitives.WriteUInt16LittleEndian(record[(PartyStats + storage * 2)..], (ushort)stats[i]);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(record[PartyCurrentHp..], (ushort)Math.Min(current, stats[0]));
    }

    private static bool IvsMatch(ReadOnlySpan<byte> record, IReadOnlyList<int> wanted)
    {
        var word = BinaryPrimitives.ReadUInt32LittleEndian(record[PartyIvs..]);
        for (var i = 0; i < 6; i++)
            if (((int)(word >> (QuetzalFormat.AppToStorage[i] * 5)) & 0x1F) != wanted[i]) return false;
        return true;
    }

    private static bool EvsMatch(ReadOnlySpan<byte> record, IReadOnlyList<int> wanted)
    {
        for (var i = 0; i < 6; i++)
            if (record[PartyEvs + QuetzalFormat.AppToStorage[i]] != wanted[i]) return false;
        return true;
    }

    // ── Text ──

    private static string ReadText(ReadOnlySpan<byte> span)
    {
        var terminator = span.IndexOf((byte)0xFF);
        return StringConverter3.GetString(terminator < 0 ? span : span[..terminator], jp: false);
    }

    private static void WriteText(Span<byte> span, string value)
    {
        span.Fill(0xFF);
        if (value.Length > 0) StringConverter3.SetString(span, value, span.Length, jp: false);
    }

    // ── Snapshot / write-out ──

    private SaveSnapshot BuildSnapshot(string? displayName)
    {
        var slots = new List<SlotSummary>(_boxCount * BoxSlotCount + QuetzalFormat.PartySize);
        for (var box = 0; box < _boxCount; box++)
        for (var slot = 0; slot < BoxSlotCount; slot++)
        {
            var record = ReadBoxRecord(box, slot);
            if (record is null || record.Species == 0)
            {
                slots.Add(new SlotSummary(box, slot, null, null, false, true));
                continue;
            }
            var nickname = record.Nickname;
            slots.Add(new SlotSummary(box, slot, record.Species,
                nickname.Length > 0 ? nickname : QuetzalFormat.SpeciesName(record.Species),
                record.IsShiny, true, HeldItem: record.HeldItem));
        }
        var count = PartyCount;
        for (var slot = 0; slot < QuetzalFormat.PartySize; slot++)
        {
            if (slot >= count)
            {
                slots.Add(new SlotSummary(-1, slot, null, null, false, true));
                continue;
            }
            var start = PartyRecordOffset(slot);
            var record = _data.AsSpan(start, QuetzalFormat.PartyRecordSize);
            var species = BinaryPrimitives.ReadUInt16LittleEndian(record[PartySpecies..]);
            var nickname = ReadText(record.Slice(PartyNickname, PartyNicknameLength));
            slots.Add(new SlotSummary(-1, slot, species,
                nickname.Length > 0 ? nickname : QuetzalFormat.SpeciesName(species),
                (record[PartyFlags] & ShinyFlag) != 0, true,
                HeldItem: BinaryPrimitives.ReadUInt16LittleEndian(record[PartyItem..])));
        }
        return new SaveSnapshot(SaveFormat, 3, _originalBytes.ToArray(), slots, displayName);
    }

    public ReadOnlyMemory<byte> Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_dirty) return _originalBytes;
        // Only the live slot's sectors are rewritten; the backup slot, every footer id,
        // signature and save counter stay exactly as the game left them.
        for (var id = 0; id < QuetzalFormat.SlotsPerSave; id++)
            QuetzalFormat.WriteChecksum(_data, _sections[id]);
        return SramPadding.Restore(_data, _originalBytes);
    }

    // ── Trainer card ──

    public TrainerInfo GetTrainer()
    {
        var sec = _sections[0];
        var name = StringConverter3.GetString(_data.AsSpan(sec, 7), jp: false);
        return new TrainerInfo(
            name.Length > 0 ? name : "PKForge",
            BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(sec + 0xA)),
            BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(sec + 0xC)),
            0,
            _data[sec + 0x8]);
    }

    public void SetTrainer(TrainerInfo trainer)
    {
        if (trainer.Name is { Length: > 0 } typedName)
            trainer = trainer with { Name = NameCharset.FitGen3(typedName, 0, NameField.TrainerName) };
        var sec = _sections[0];
        if (trainer.Name.Length > 0 && trainer.Name != StringConverter3.GetString(_data.AsSpan(sec, 7), jp: false))
            WriteText(_data.AsSpan(sec, 7), trainer.Name);
        if (trainer.TID is >= 0 and <= ushort.MaxValue &&
            BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(sec + 0xA)) != trainer.TID)
            BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(sec + 0xA), (ushort)trainer.TID);
        if (trainer.SID is >= 0 and <= ushort.MaxValue &&
            BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(sec + 0xC)) != trainer.SID)
            BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(sec + 0xC), (ushort)trainer.SID);
        var gender = (byte)Math.Clamp(trainer.Gender, 0, byte.MaxValue);
        if (_data[sec + 0x8] != gender) _data[sec + 0x8] = gender;
        _dirty = true;
    }

    /// <summary>Play time as "h:mm:ss", the form every other save shows on the shelf.</summary>
    internal string PlayTimeText()
    {
        var sec = _sections[0];
        return $"{BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(sec + 0x10))}:{_data[sec + 0x14]:00}:{_data[sec + 0x15]:00}";
    }

    public TrainerStats GetTrainerStats()
    {
        var sec = _sections[0];
        return new TrainerStats(
            true,
            BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(sec + 0x10)),
            _data[sec + 0x14],
            ushort.MaxValue,
            false, 0, 0,
            false, 0, 0);
    }

    public void SetTrainerStats(TrainerStatsEdit edit)
    {
        var sec = _sections[0];
        if (edit.PlayTimeHours is { } hours)
        {
            var value = (ushort)Math.Clamp(hours, 0, ushort.MaxValue);
            if (BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(sec + 0x10)) != value)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(sec + 0x10), value);
                _dirty = true;
            }
        }
        if (edit.PlayTimeMinutes is { } minutes)
        {
            var value = (byte)Math.Clamp(minutes, 0, 59);
            if (_data[sec + 0x14] != value)
            {
                _data[sec + 0x14] = value;
                _dirty = true;
            }
        }
    }

    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile = null)
    {
        if (box != -1)
            return new GenerationOutcome(false, "Boxed Pokémon in Pokémon Quetzal do not store an original trainer.");
        if ((uint)slot >= (uint)PartyCount) return new GenerationOutcome(false, "That slot is empty.");
        var trainer = GetTrainer();
        var name = profile?.OriginalTrainer ?? trainer.Name;
        var tid = profile?.TID ?? trainer.TID;
        var sid = profile?.SID ?? trainer.SID;
        var start = PartyRecordOffset(slot);
        var record = _data.AsSpan(start, QuetzalFormat.PartyRecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(record[PartyOtId..], (uint)((sid << 16) | (tid & 0xFFFF)));
        WriteText(record.Slice(PartyOtName, PartyOtNameLength), name);
        _dirty = true;
        return new GenerationOutcome(true,
            $"{ReadText(record.Slice(PartyNickname, PartyNicknameLength))} now belongs to {name}.");
    }

    // ── Box metadata ──

    public string GetBoxName(int box)
    {
        if (_boxFormat is null || (uint)box >= (uint)_boxCount) return string.Empty;
        Span<byte> name = stackalloc byte[QuetzalFormat.BoxNameLength];
        QuetzalFormat.ReadPc(_data, _sections,
            QuetzalFormat.BoxNameOffset + box * QuetzalFormat.BoxNameLength, name);
        var text = ReadText(name);
        return string.IsNullOrWhiteSpace(text) ? $"BOX {box + 1}" : text;
    }

    // ── Species facts ──

    public IReadOnlyList<int> GetAbilityChoices(int species, int form) =>
        [.. Enumerable.Range(0, 3).Select(slot => QuetzalFormat.AbilityAt(species, slot)).Where(id => id > 0).Distinct()];
    public IReadOnlyList<string> GetItemNames() => QuetzalFormat.ItemNames();
    public IReadOnlyList<string> GetFormChoices(int species) => [""];
    public IReadOnlyList<int> GetSpeciesTypes(int species) => QuetzalFormat.TypesOf(species);

    public BaseStats GetBaseStats(int species)
    {
        var stats = QuetzalFormat.BaseStats(species);
        return new BaseStats(stats[0], stats[1], stats[2], stats[3], stats[4], stats[5]);
    }

    public TrainingCaps GetTrainingCaps() => new(31, 255);

    public RngInfo GetRngInfo(int box, int slot)
    {
        if (box != -1)
        {
            var record = ReadBoxRecord(box, slot);
            if (record is null || record.Species == 0)
                return new RngInfo(0, null, 0, false, false, [0, 0, 0, 0, 0, 0], 0, 2);
            return new RngInfo(0, null, record.Nature, record.IsShiny, false,
                record.Ivs(), record.AbilitySlot, record.Female ? 1 : 0);
        }
        var detail = ReadEntity(-1, slot);
        if (detail.IsEmpty)
            return new RngInfo(0, null, 0, false, false, [0, 0, 0, 0, 0, 0], 0, 2);
        var start = PartyRecordOffset(slot);
        return new RngInfo(
            BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(start + PartyPid)),
            null, detail.Nature, detail.IsShiny, true,
            detail.IVs, AbilitySlotAt(-1, slot), detail.Gender);
    }

    public bool RerollNatureKeepShiny(int box, int slot, int nature)
    {
        if (nature is < 0 or > 24) return false;
        if (box == -1 ? (uint)slot >= (uint)PartyCount : ReadBoxRecord(box, slot) is not { Species: > 0 })
            return false;
        ApplyEdit(box, slot, new EntityEdit(Nature: nature));
        return true;
    }

    public IReadOnlyList<int> GetMissingSpecies() => [];

    // ── Pokédex (layout not established) ──
    public DexEntryState GetDexEntry(int species) => new(false, false);
    public void SetDexEntry(int species, bool seen, bool caught) { }
    public bool IsDexSpecies(int species) => false;
    public DexProgress GetDexProgress() => new(0, 0, 0);
    public void CompleteDex() => throw NotYet("The complete-dex action (Quetzal's regional dex blocks are not mapped)");
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];

    // ── Showdown / export ──

    public string GetShowdownText(int box, int slot)
    {
        var mon = ReadEntity(box, slot);
        if (mon.IsEmpty) return string.Empty;
        var text = $"{mon.SpeciesName}\nLevel: {mon.Level}\n{NatureName(mon.Nature)} Nature";
        if (mon.HeldItem > 0) text += $"\nItem: {QuetzalFormat.ItemName(mon.HeldItem)}";
        int[] moves = [mon.Move1, mon.Move2, mon.Move3, mon.Move4];
        foreach (var move in moves)
            if (move > 0) text += $"\n- {QuetzalFormat.MoveName(move)}";
        return text;
    }

    public string ExportBoxShowdown(int box)
    {
        var parts = new List<string>();
        for (var slot = 0; slot < BoxSlotCount; slot++)
        {
            var text = GetShowdownText(box, slot);
            if (text.Length > 0) parts.Add(text);
        }
        return string.Join("\n\n", parts);
    }

    private static string NatureName(int nature) => nature switch
    {
        0 => "Hardy", 1 => "Lonely", 2 => "Brave", 3 => "Adamant", 4 => "Naughty",
        5 => "Bold", 6 => "Docile", 7 => "Relaxed", 8 => "Impish", 9 => "Lax",
        10 => "Timid", 11 => "Hasty", 12 => "Serious", 13 => "Jolly", 14 => "Naive",
        15 => "Modest", 16 => "Mild", 17 => "Quiet", 18 => "Bashful", 19 => "Rash",
        20 => "Calm", 21 => "Gentle", 22 => "Sassy", 23 => "Careful", _ => "Quirky",
    };

    // ── Potential: ability slot is the only stored choice ──

    public PotentialInfo GetPotential(int box, int slot)
    {
        var mon = ReadEntity(box, slot);
        var slots = new List<NamedChoice>();
        if (!mon.IsEmpty)
        {
            var personal = QuetzalFormat.PersonalOf(mon.Species);
            if (personal is not null)
            {
                var names = GameInfo.GetStrings("en").abilitylist;
                for (var i = 0; i < Math.Min(3, personal.AbilityCount); i++)
                {
                    var ability = personal.GetAbilityAtIndex(i);
                    var label = (uint)ability < (uint)names.Length && names[ability].Length > 0
                        ? names[ability]
                        : $"Ability {ability}";
                    slots.Add(new NamedChoice(i, label));
                }
            }
        }
        return new PotentialInfo(
            SupportsTera: false, TeraType: 0, TeraTypeName: "", TeraTypeOriginalName: "", TeraLocked: false,
            SupportsHyperTrain: false, HyperTrained: [false, false, false, false, false, false],
            SupportsAbilitySlot: slots.Count > 0,
            AbilitySlot: mon.IsEmpty ? 0 : AbilitySlotAt(box, slot),
            AbilitySlots: slots,
            SupportsAwakening: false, Awakening: [], SupportsGanbaru: false, Ganbaru: [], GanbaruMaximums: []);
    }

    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit)
    {
        if (edit.AbilitySlot is not { } ability) return;
        // A slot, not an id: written as is (a Gen 3 species' third slot has no PKHeX id to pass).
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (box == -1)
        {
            if ((uint)slot >= (uint)PartyCount) throw new InvalidOperationException("Cannot edit an empty slot.");
            ApplyPartyEdit(slot, new EntityEdit(Ability: ability));
        }
        else ApplyBoxEdit(box, slot, BoxRecordForEdit(box, slot), new EntityEdit(Ability: ability));
    }

    public IReadOnlyList<NamedChoice> GetTeraTypeChoices() => [];

    // ── Move details ──

    public MoveDetails GetMoveDetails(int box, int slot)
    {
        var mon = ReadEntity(box, slot);
        if (mon.IsEmpty) return new MoveDetails([], false, []);
        int[] moves = [mon.Move1, mon.Move2, mon.Move3, mon.Move4];
        var pp = new int[4];
        if (box == -1)
        {
            var start = PartyRecordOffset(slot);
            for (var i = 0; i < 4; i++) pp[i] = _data[start + PartyPp + i];
        }
        else if (ReadBoxRecord(box, slot) is { } record)
        {
            for (var i = 0; i < 4; i++) pp[i] = record.Pp(i);
        }
        var slots = new List<MoveSlotDetail>(4);
        for (var i = 0; i < 4; i++)
            slots.Add(new MoveSlotDetail(moves[i], pp[i], pp[i], 0));
        return new MoveDetails(slots, false, []);
    }

    public void ApplyMoveDetails(int box, int slot, MoveDetailsEdit edit)
    {
        if (edit.PP is not { Count: 4 } pp) return;
        if (box == -1)
        {
            if ((uint)slot >= (uint)PartyCount) throw new InvalidOperationException("Cannot edit an empty slot.");
            var start = PartyRecordOffset(slot);
            for (var i = 0; i < 4; i++) _data[start + PartyPp + i] = (byte)Math.Clamp(pp[i], 0, 255);
            _dirty = true;
            return;
        }
        var record = BoxRecordForEdit(box, slot);
        if (!record.HasStoredPp)
            throw NotYet("current-PP editing for this PC record size (it stores no PP)");
        for (var i = 0; i < 4; i++) record.SetPp(i, pp[i]);
        WriteBoxRecord(box, slot, record);
        _dirty = true;
    }

    // ── Refused surfaces ──

    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format = null) => false;
    public SlotExport ExportSlot(int box, int slot) => throw NotYet("Entity export");
    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot) => throw NotYet("Moving Pokémon");
    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot) => throw NotYet("Duplicating Pokémon");
    public void ReleaseSlot(int box, int slot) => throw NotYet("Releasing Pokémon");
    public int PlaceLivingDex(byte[] compressedBundle) => throw NotYet("The living dex");
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false) => throw NotYet("Box sorting");
    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) => throw NotYet("The batch editor");
    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) => throw NotYet("The batch editor");
    public void SwapBoxes(int a, int b) => throw NotYet("Box swapping");
    public void DeleteBox(int box) => throw NotYet("Box management");
    public void ClearBox(int box) => throw NotYet("Box management");
    public IReadOnlyList<BagPouch> GetBag() => [];
    public IReadOnlyList<int> GetPouchLegalItems(string pouchName) => [];
    public int SetItemCount(string pouchName, int itemId, int count) => 0;
    public IReadOnlyList<CountedEntry> GetPokeBeans() => [];
    public int SetPokeBeanCount(int index, int count) => 0;
    public IReadOnlyList<UndergroundItem> GetGrandUndergroundItems() => [];
    public int SetGrandUndergroundItemCount(int itemId, int count) => 0;
    public DaycareInfo GetDaycare() => new(false, []);
    public DaycareWithdrawal WithdrawDaycareToFirstEmptyBox(int facility, int slot) => throw NotYet("The Day Care");
    public void UnlockAllLegalFashion() { }
    public int GetStylePoints() => 0;
    public void SetStylePoints(int value) { }
    public MysteryGiftInbox GetMysteryGiftInbox() => new(false, []);
    public TrainerRecordsInfo GetTrainerRecords() => new(false, []);
    public void RepairRTC() { }
    public MetInfo GetMetInfo(int box, int slot) => throw NotYet("Met/origin editing");
    public void ApplyMetEdit(int box, int slot, MetEdit edit) => throw NotYet("Met/origin editing");
    public IReadOnlyList<NamedChoice> GetLocationChoices(int box, int slot, bool egg) => [];
    public IReadOnlyList<NamedChoice> GetVersionChoices() => [];
    public IReadOnlyList<NamedChoice> GetLanguageChoices(int box, int slot) => [];
    public MoveShopInfo GetMoveShop(int box, int slot) => new(false, []);
    public void ApplyMoveShopEdit(int box, int slot, MoveShopEdit edit) { }
    public CosmeticInfo GetCosmetics(int box, int slot) => new(
        [], [], false, 0, 0, false, 0, false, 0, 0, false, 0, 0, false, false,
        false, 0, false, false, false, false, 0);
    public void ApplyCosmeticEdit(int box, int slot, CosmeticEdit edit) { }
    public PokerusInfo GetPokerus(int box, int slot) => new(false, PokerusStatus.Susceptible, 0, 0);
    public void SetPokerus(int box, int slot, PokerusStatus status) { }
    public IReadOnlyList<RibbonEntry> GetRibbons(int box, int slot) => [];
    public void SetRibbon(int box, int slot, string id, int value) { }
    public AffixedRibbonInfo GetAffixedRibbon(int box, int slot) => new(false, -1, string.Empty, []);
    public void SetAffixedRibbon(int box, int slot, int ribbonIndex) { }
    public IReadOnlyDictionary<string, int> GetObtainableRibbonMaxima(int box, int slot) => new Dictionary<string, int>();
    public int AwardAllObtainableRibbons(int box, int slot) => 0;
    public IReadOnlyList<CompassSetting> GetCompassSettings() => [];
    public bool SetCompassSetting(string id, int choiceIndex) => false;
    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) =>
        throw NotYet("Encounter cards");

    public void MakeActive() { }

    public void Dispose() => _disposed = true;

    private static NotSupportedException NotYet(string what) =>
        new($"{what} is not available for Pokémon Quetzal yet.");
}
