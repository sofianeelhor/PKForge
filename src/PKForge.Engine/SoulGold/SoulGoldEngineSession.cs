using System.Buffers.Binary;
using System.Numerics;
using PKForge.Domain;
using PKHeX.Core;
using static PKForge.Engine.SoulGold.SoulGoldFormat;

namespace PKForge.Engine.SoulGold;

/// <summary>
/// A Pokémon Soul Gold save as a first-class PKForge session. The hack keeps
/// the stock-expansion 14-sector envelope, so PKHeX parses it as plain SAV3E
/// and shows a broken party/PC; this session speaks the hack's layout instead:
/// 96-byte party records with plaintext secure data at SaveBlock1+0x238, and
/// PC storage split across sectors 5-13, the overflow sector, the SaveBlock1
/// extension tail and the auxiliary sector, in the V19 (76-byte) or BX21
/// (68-byte) generation detected from the BX magic words.
///
/// Writes go to the selected (higher-counter) slot only: the rotating backup
/// slot and sectors 28-31 of the other slot stay byte-identical, every touched
/// checksum is recomputed (sector footers, the legacy extension rolling
/// checksum, the BX19 auxiliary record, the BX21 header), and unknown bytes are
/// preserved verbatim. When the storage generation cannot be proven from the
/// magic words, edits are refused rather than risk shifting 21 boxes by 8
/// bytes. Legality is not evaluated: hack species and encounter tables do not
/// exist in PKHeX, so this session is HaX-by-design like its siblings.
/// </summary>
internal sealed class SoulGoldEngineSession : ISaveEngineSession
{
    private readonly byte[] _data;
    private readonly byte[] _originalBytes;
    private readonly SlotLayout _slot;
    private readonly StorageEra _era;
    private readonly byte[] _sb2;   // SaveBlock2 window, 0xB30 bytes
    private readonly byte[] _sb1;   // SaveBlock1, 0x3C54 bytes
    private readonly byte[] _storage;
    private readonly byte[] _storageAtOpen;
    private readonly SoulGoldData _tables = SoulGoldData.Instance;
    private readonly string? _displayName;
    private readonly string? _writeRefusal;
    private bool _dirty;
    private bool _disposed;


    public SoulGoldEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _originalBytes = bytes.ToArray();
        _data = SramPadding.Trim(RetroArchSaveContainer.Decode(bytes.Span));
        if (_data.Length < FileSize || !LooksLike(_data))
            throw new InvalidDataException("These bytes are not a Pokémon Soul Gold save.");
        _slot = NewestSlot(_data.AsSpan(0, FileSize))!;
        _sb2 = _data.AsSpan(_slot.Sections[0], SaveBlock2Size).ToArray();
        _sb1 = ReadSaveBlock1(_data, _slot);
        // The storage header word (storage 0) lives at the start of sector 5.
        _era = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_slot.Sections[5], 4)) == Bx21Magic
            ? StorageEra.Bx21
            : StorageEra.LegacyV19;
        _storage = ReadStorage(_data, _slot, _era);
        _storageAtOpen = _storage.ToArray();
        _displayName = displayName;

        // Write gating: the legacy extension magic must be a known word (or a
        // clean pre-BX16 zero) before any box byte may be rewritten.
        if (_era == StorageEra.LegacyV19)
        {
            var extension = BinaryPrimitives.ReadUInt32LittleEndian(_storage.AsSpan(LegacyBoxExtensionMagicOffset));
            var box18 = BinaryPrimitives.ReadUInt32LittleEndian(_storage.AsSpan(LegacyBox18MagicOffset));
            var known = extension is 0 or Bx16Magic or Bx17Magic or Bx18Magic or Bx19Magic
                && box18 is 0 or Bx17Magic or Bx18Magic or Bx19Magic
                && LegacyBoxCount(_storage) is >= 15 and <= 19;
            if (!known)
                _writeRefusal = "the PC storage generation marker is not a recognized BX word; " +
                    "refusing to write boxes whose layout cannot be proven";
        }
        else if (_slot.AuxSector < 0)
        {
            _writeRefusal = "the BX21 storage needs its auxiliary sector, which this save does not carry";
        }

        // Boxes that live in a tail sector the slot does not carry would be read
        // as erased bytes and their edits dropped on write.
        _writeRefusal ??= (_era == StorageEra.Bx21 || LegacyBoxCount(_storage) >= 16) && _slot.OverflowSector < 0
            ? "the PC storage needs its overflow sector, which this save does not carry"
            : _era == StorageEra.LegacyV19 && LegacyBoxCount(_storage) == 19 && _slot.AuxSector < 0
                ? "the 19th box lives in the auxiliary sector, which this save does not carry"
                : null;
    }

    public SaveSnapshot Snapshot => BuildSnapshot(_displayName);

    public int Generation => 3;
    public IReadOnlyList<string> GameNames => ["Pokémon Soul Gold"];
    public int MaxSpeciesId => _tables.MaxSpecies;
    public int BoxCount => _era == StorageEra.Bx21 ? 21 : LegacyBoxCount(_storage);
    public int BoxSlotCount => 30;

    private int BoxMonSize => _era == StorageEra.Bx21 ? BoxMonSizeBx21 : BoxMonSizeV19;

    private int BoxBase(int box) => _era == StorageEra.Bx21
        ? Bx21BoxesBase + box * Bx21BoxStride
        : LegacyBoxOffset(box);

    private int BoxNameBase(int box) => _era == StorageEra.Bx21
        ? Bx21BoxNamesBase + 9 * box
        : LegacyBoxNameOffset(box);

    private int PartyCount => Math.Min((int)_sb1[PartyCountOffset], 6);

    private SoulGoldMon PartyMon(int slot) => new(_sb1, PartyOffset + slot * PartyMonSize, true, _era, _tables);

    private SoulGoldMon? TryMon(int box, int slot)
    {
        if (box == -1)
            return (uint)slot >= (uint)PartyCount ? null : PartyMon(slot);
        if ((uint)box >= (uint)BoxCount || (uint)slot >= (uint)BoxSlotCount)
            return null;
        return new SoulGoldMon(_storage, BoxBase(box) + slot * BoxMonSize, false, _era, _tables);
    }

    // ── Snapshot / images ──

    private SaveSnapshot BuildSnapshot(string? displayName)
    {
        var slots = new List<SlotSummary>(BoxCount * BoxSlotCount + 6);
        for (var box = 0; box < BoxCount; box++)
        for (var slot = 0; slot < BoxSlotCount; slot++)
            slots.Add(SummaryOf(box, slot));
        for (var slot = 0; slot < 6; slot++)
            slots.Add(SummaryOf(-1, slot));
        return new SaveSnapshot("SOULGOLD", 3, _originalBytes.ToArray(), slots, displayName);

        SlotSummary SummaryOf(int box, int slot)
        {
            var mon = TryMon(box, slot);
            return mon is { LooksValid: true } valid && !valid.IsEmpty
                ? new SlotSummary(box, slot, _tables.NationalIdOf(valid.Species), valid.Nickname,
                    valid.IsShiny, true, HeldItem: valid.HeldItem)
                : new SlotSummary(box, slot, null, null, false, true);
        }
    }

    internal IEnumerable<SlotImage> SlotImages()
    {
        for (var box = -1; box < BoxCount; box++)
        for (var slot = 0; slot < (box == -1 ? 6 : BoxSlotCount); slot++)
        {
            var mon = TryMon(box, slot);
            yield return mon is null
                ? new SlotImage(new SlotRef(box, slot), [], Empty: true, Valid: true)
                : new SlotImage(new SlotRef(box, slot), mon.RawRecord(), mon.IsEmpty, mon.IsEmpty || mon.LooksValid);
        }
    }

    // ── Reading ──

    public EntityDetail ReadEntity(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid)
            return Empty(box, slot);
        var moves = mon.Moves;
        var ivs = mon.Ivs;
        var evs = mon.Evs;
        var level = mon.StoredOrDerivedLevel;
        var stats = mon.Party
            ? PartyStatsAppOrder(mon)
            : _tables.ComputeStats(mon.Species, level, mon.Nature, ivs, evs);
        return new EntityDetail(
            box, slot, false,
            _tables.NationalIdOf(mon.Species),
            _tables.SpeciesName(mon.Species),
            0, mon.Nickname, level,
            mon.Nature,
            ActiveAbility(mon),
            mon.HeldItem,
            // The record keeps the expansion's move ids; the editor speaks PKHeX's.
            _tables.MoveToNational(moves[0]), _tables.MoveToNational(moves[1]),
            _tables.MoveToNational(moves[2]), _tables.MoveToNational(moves[3]),
            ivs, evs, mon.IsShiny, mon.Ball, mon.OriginalTrainerName,
            _tables.Types(mon.Species),
            SoulGoldData.GenderOf(mon.Pid, _tables.GenderRatio(mon.Species)),
            mon.Friendship,
            stats,
            mon.Party ? mon.CurrentHp : stats[0],
            mon.Party ? (int)mon.StatusCondition : 0,
            AbilityName: ActiveAbility(mon) > SoulGoldData.LastNationalAbility ? _tables.AbilityName(ActiveAbility(mon)) : null,
            MoveNames: HackNames.UnmappedMoves(moves, _tables.MoveToNational, _tables.MoveName));

        static EntityDetail Empty(int box, int slot) =>
            new(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0,
                [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);
    }

    /// <summary>Party tail stats in app order HP, Atk, Def, SpA, SpD, Spe (the
    /// record keeps HP, maxHP, Atk, Def, Spe, SpA, SpD).</summary>
    private static int[] PartyStatsAppOrder(SoulGoldMon mon)
    {
        var stored = mon.PartyStats;
        return [stored[0], stored[2], stored[3], stored[5], stored[6], stored[4]];
    }

    private int ActiveAbility(SoulGoldMon mon)
    {
        var abilities = _tables.Abilities(mon.Species);
        var slot = mon.AbilitySlot is 1 or 2 ? mon.AbilitySlot : 0;
        return slot < 3 && abilities[slot] != 0 ? abilities[slot] : abilities[0];
    }

    // ── Editing ──

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        edit = NameCharset.FitGen3Edit(edit); // charset first: a name the game cannot hold is refused, not emptied
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWritable(box);
        var mon = TryMon(box, slot) ?? throw new InvalidOperationException("Cannot edit an empty slot.");
        var before = mon.RawRecord();

        if (edit.Species is { } species && species != _tables.NationalIdOf(mon.Species)
            && _tables.SpeciesFromNational(species) is > 0 and var romSpecies)
        {
            mon.Species = romSpecies;
            mon.MarkOccupied();
            mon.Nickname = _tables.SpeciesName(romSpecies);
        }
        if (edit.Nickname is { Length: > 0 } nickname && nickname != mon.Nickname)
            mon.Nickname = nickname;
        // Only a changed level rewrites EXP: the editor echoes every field, and
        // snapping EXP to the level floor on a no-op save erases progress.
        if (edit.Level is { } level && Math.Clamp(level, 1, 100) != mon.StoredOrDerivedLevel)
            SetLevel(mon, level);
        if (edit.Nature is { } nature && nature != mon.Nature)
            mon.Nature = nature;
        if (edit.Ability is { } ability && ability != ActiveAbility(mon))
            SetAbility(mon, ability);
        if (edit.HeldItem is { } item && item != mon.HeldItem && _tables.IsKnownItem(item))
            mon.HeldItem = item;
        int?[] wantedMoves = [edit.Move1, edit.Move2, edit.Move3, edit.Move4];
        var moves = mon.Moves;
        var movesChanged = false;
        for (var i = 0; i < 4; i++)
        {
            // Edits speak PKHeX ids: the echo of a stored move (its national id, or 0 for a
            // hack-only move PKHeX cannot name) keeps the stored one.
            if (wantedMoves[i] is not { } national || national == _tables.MoveToNational(moves[i]))
                continue;
            var wanted = _tables.MoveFromNational(national);
            if ((national != 0 && wanted == 0) || !_tables.IsKnownMove(wanted))
                continue;
            moves[i] = wanted;
            movesChanged = true;
        }
        if (movesChanged)
        {
            mon.Moves = moves;
            var pp = mon.Pp;
            for (var i = 0; i < 4; i++)
                pp[i] = moves[i] == 0 ? 0 : _tables.MoveBasePp(moves[i]);
            mon.Pp = pp;
        }
        if (edit.IVs is { Count: 6 } ivs && !ivs.SequenceEqual(mon.Ivs))
            mon.Ivs = [.. ivs.Select(v => Math.Clamp(v, 0, 31))];
        if (edit.EVs is { Count: 6 } evs && !evs.SequenceEqual(mon.Evs))
            mon.Evs = [.. evs.Select(v => Math.Clamp(v, 0, 255))];
        if (edit.IsShiny is { } shiny && shiny != mon.IsShiny)
            mon.IsShiny = shiny;
        if (edit.Ball is { } ball && ball != mon.Ball)
            mon.Ball = ball & 0x3F;
        if (edit.OriginalTrainer is { Length: > 0 } ot && ot != mon.OriginalTrainerName)
            mon.OriginalTrainerName = ot;
        if (edit.Friendship is { } friendship && friendship != mon.Friendship)
            mon.Friendship = Math.Clamp(friendship, 0, 255);

        if (mon.RawRecord().AsSpan().SequenceEqual(before))
            return;
        RecomputePartyTailIfLevelStatsRelevant(mon);
        _dirty = true;
    }

    /// <summary>Keeps the party tail honest after IV/EV/level edits: level byte,
    /// recomputed stats and clamped current HP.</summary>
    private void RecomputePartyTailIfLevelStatsRelevant(SoulGoldMon mon)
    {
        if (!mon.Party)
            return;
        var level = _tables.LevelForExperience(_tables.GrowthRate(mon.Species), mon.Experience);
        mon.Level = level;
        var stats = _tables.ComputeStats(mon.Species, level, mon.Nature, mon.Ivs, mon.Evs);
        var stored = mon.PartyStats;
        stored[0] = Math.Min(stored[0] == 0 ? stats[0] : stored[0], stats[0]);
        stored[1] = stats[0];
        stored[2] = stats[1];
        stored[3] = stats[2];
        stored[4] = stats[5];
        stored[5] = stats[3];
        stored[6] = stats[4];
        mon.WritePartyStats(stored);
    }

    private void SetLevel(SoulGoldMon mon, int level)
    {
        level = Math.Clamp(level, 1, 100);
        mon.Experience = SoulGoldData.ExperienceAtLevel(_tables.GrowthRate(mon.Species), level);
        if (mon.Party)
            mon.Level = level;
    }

    private void SetAbility(SoulGoldMon mon, int abilityId)
    {
        var abilities = _tables.Abilities(mon.Species);
        if (abilityId == abilities[2] && abilities[2] != 0)
            mon.AbilitySlot = 2;
        else if (abilityId == abilities[1] && abilities[1] != 0)
            mon.AbilitySlot = 1;
        else
            mon.AbilitySlot = 0;
    }

    private void RequireWritable(int box)
    {
        if (_writeRefusal is { } refusal)
            throw new NotSupportedException($"Pokémon Soul Gold writes are disabled: {refusal}.");
        if (box >= BoxCount)
            throw new NotSupportedException("That box does not exist in this save's storage generation.");
    }

    // ── Copy / move / release ──

    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWritable(toBox);
        var source = TryMon(fromBox, fromSlot);
        if (source is null || source.IsEmpty || !source.LooksValid)
            return false;
        SoulGoldMon target;
        if (toBox == -1)
        {
            if (PartyCount >= 6) return false;
            target = PartyMon(PartyCount);
        }
        else
        {
            if ((uint)toBox >= (uint)BoxCount || (uint)toSlot >= (uint)BoxSlotCount) return false;
            target = TryMon(toBox, toSlot)!;
            if (!target.IsEmpty) return false;
        }
        CopyBetween(source, target);
        if (toBox == -1)
        {
            _sb1[PartyCountOffset] = (byte)(PartyCount + 1);
        }
        _dirty = true;
        return true;
    }

    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWritable(toBox);
        if (fromBox == toBox && fromSlot == toSlot)
            return;
        var source = TryMon(fromBox, fromSlot) ?? throw new InvalidOperationException("The source slot is empty.");
        if (source.IsEmpty)
            throw new InvalidOperationException("The source slot is empty.");

        if (fromBox == -1 && toBox == -1)
        {
            var count = PartyCount;
            if ((uint)toSlot < (uint)count)
            {
                // Inside the party the two records swap places, as in the games.
                var from = _sb1.AsSpan(PartyOffset + fromSlot * PartyMonSize, PartyMonSize);
                var to = _sb1.AsSpan(PartyOffset + toSlot * PartyMonSize, PartyMonSize);
                var held = from.ToArray();
                to.CopyTo(from);
                held.CopyTo(to);
                _dirty = true;
                return;
            }
            // Dropped on an empty party slot: the Pokémon moves to the end of
            // the party, which stays dense; the count does not change.
            if (fromSlot == count - 1)
                return;
            var moving = source.RawRecord();
            CompactPartyFrom(fromSlot);
            moving.CopyTo(_sb1.AsSpan(PartyOffset + (count - 1) * PartyMonSize, PartyMonSize));
            _sb1[PartyCountOffset] = (byte)count;
            _dirty = true;
            return;
        }

        if (toBox == -1)
        {
            if (PartyCount >= 6)
                throw new InvalidOperationException("The party is full.");
            RequireReadable(source);
            CopyBetween(source, PartyMon(PartyCount));
            source.Clear();
            _sb1[PartyCountOffset] = (byte)(PartyCount + 1);
            _dirty = true;
            return;
        }

        var target = TryMon(toBox, toSlot) ?? throw new InvalidOperationException("The destination slot does not exist.");
        if (target.IsEmpty)
        {
            CopyBetween(source, target);
            if (fromBox == -1) CompactPartyFrom(fromSlot);
            else source.Clear();
        }
        else if (source.Party)
        {
            // A party Pokémon dropped on an occupied box slot swaps with it. The
            // occupant's party record (stats rebuilt like a withdrawal) is built
            // aside first, so nothing is written unless both halves exist.
            RequireReadable(target);
            var incoming = new SoulGoldMon(new byte[PartyMonSize], 0, true, _era, _tables);
            CopyBetween(new SoulGoldMon(target.RawRecord(), 0, false, _era, _tables), incoming);
            CopyBetween(source, target);
            incoming.Buffer.CopyTo(source.Buffer.AsSpan(source.Offset, PartyMonSize));
        }
        else
        {
            // Box to box: both records have the same size and swap verbatim.
            var held = target.RawRecord();
            CopyBetween(source, target);
            held.CopyTo(source.Buffer.AsSpan(source.Offset, source.Size));
        }
        _dirty = true;
    }

    /// <summary>A box record entering the party needs its stats computed from its
    /// species; an unreadable record has none to compute from.</summary>
    private static void RequireReadable(SoulGoldMon mon)
    {
        if (mon.IsEmpty || !mon.LooksValid)
            throw new InvalidOperationException(
                "That Pokémon's record cannot be read, so it cannot be moved into the party.");
    }

    private void CompactPartyFrom(int removedSlot)
    {
        var count = PartyCount;
        if ((uint)removedSlot >= (uint)count)
            return;
        for (var i = removedSlot; i < count - 1; i++)
            _sb1.AsSpan(PartyOffset + (i + 1) * PartyMonSize, PartyMonSize)
                .CopyTo(_sb1.AsSpan(PartyOffset + i * PartyMonSize));
        _sb1.AsSpan(PartyOffset + (count - 1) * PartyMonSize, PartyMonSize).Clear();
        _sb1[PartyCountOffset] = (byte)(count - 1);
    }

    /// <summary>Copies exactly as the game does: same store takes the whole
    /// record; crossing the party/box boundary copies the shared bytes (the
    /// box record is the party record's prefix) and, into the party, rebuilds
    /// the computed tail like a withdrawal.</summary>
    private void CopyBetween(SoulGoldMon source, SoulGoldMon target)
    {
        if (source.Party == target.Party)
        {
            source.Buffer.AsSpan(source.Offset, source.Size)
                .CopyTo(target.Buffer.AsSpan(target.Offset, target.Size));
            return;
        }
        if (!target.Party)
        {
            target.Clear();
            source.Buffer.AsSpan(source.Offset, target.Size)
                .CopyTo(target.Buffer.AsSpan(target.Offset, target.Size));
            return;
        }
        target.Clear();
        source.Buffer.AsSpan(source.Offset, source.Size)
            .CopyTo(target.Buffer.AsSpan(target.Offset, source.Size));
        target.Level = _tables.LevelForExperience(_tables.GrowthRate(target.Species), target.Experience);
        var stats = _tables.ComputeStats(target.Species, target.Level, target.Nature, target.Ivs, target.Evs);
        var stored = new[] { stats[0], stats[0], stats[1], stats[2], stats[5], stats[3], stats[4] };
        target.WritePartyStats(stored);
        target.CurrentHp = stats[0];
    }

    public void ReleaseSlot(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWritable(box);
        if (box == -1)
        {
            // An empty party slot has nothing to release: compacting from it would
            // remove the last real member (or write a count of 255 on an empty party).
            if ((uint)slot >= (uint)PartyCount)
                return;
            CompactPartyFrom(slot);
            _dirty = true;
            return;
        }
        if (TryMon(box, slot) is not { IsEmpty: false } mon)
            return;
        mon.Clear();
        _dirty = true;
    }

    public void ClearBox(int box)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWritable(box);
        if ((uint)box >= (uint)BoxCount) return;
        for (var slot = 0; slot < BoxSlotCount; slot++)
            TryMon(box, slot)?.Clear();
        _dirty = true;
    }

    // ── Trainer, box names, dex ──

    private uint SecurityKey => BinaryPrimitives.ReadUInt32LittleEndian(_sb2.AsSpan(EncryptionKeyOffset));

    public TrainerInfo GetTrainer()
    {
        var name = StringConverter3.GetString(_sb2.AsSpan(TrainerNameOffset, 7), jp: false);
        var tid = BinaryPrimitives.ReadUInt16LittleEndian(_sb2.AsSpan(TrainerIdOffset));
        var sid = BinaryPrimitives.ReadUInt16LittleEndian(_sb2.AsSpan(TrainerIdOffset + 2));
        var money = BinaryPrimitives.ReadUInt32LittleEndian(_sb1.AsSpan(MoneyOffset)) ^ SecurityKey;
        return new TrainerInfo(name.Length > 0 ? name : "PKForge", tid, sid, money, _sb2[TrainerGenderOffset]);
    }

    public void SetTrainer(TrainerInfo trainer) => throw NotYet("The trainer editor");

    /// <summary>Play time as "h:mm:ss", the form every other save shows on the shelf.</summary>
    internal string PlayTimeText() =>
        $"{BinaryPrimitives.ReadUInt16LittleEndian(_sb2.AsSpan(PlayTimeHoursOffset))}:{_sb2[PlayTimeMinutesOffset]:00}:{_sb2[PlayTimeMinutesOffset + 1]:00}";

    public TrainerStats GetTrainerStats() => new(
        true,
        BinaryPrimitives.ReadUInt16LittleEndian(_sb2.AsSpan(PlayTimeHoursOffset)),
        _sb2[PlayTimeMinutesOffset],
        65535,
        false, 0, 0,
        true, BinaryPrimitives.ReadUInt16LittleEndian(_sb1.AsSpan(CoinsOffset)) ^ (ushort)SecurityKey, 9999);

    public void SetTrainerStats(TrainerStatsEdit edit)
    {
        if (edit.PlayTimeHours is { } hours)
            BinaryPrimitives.WriteUInt16LittleEndian(_sb2.AsSpan(PlayTimeHoursOffset), (ushort)Math.Clamp(hours, 0, 65535));
        if (edit.PlayTimeMinutes is { } minutes)
            _sb2[PlayTimeMinutesOffset] = (byte)Math.Clamp(minutes, 0, 59);
        if (edit.Coins is { } coins)
        {
            var key = (ushort)SecurityKey;
            BinaryPrimitives.WriteUInt16LittleEndian(_sb1.AsSpan(CoinsOffset),
                (ushort)(Math.Clamp(coins, 0, 9999) ^ key));
        }
        _dirty = true;
    }

    public string GetBoxName(int box)
    {
        if ((uint)box >= (uint)BoxCount) return string.Empty;
        var name = StringConverter3.GetString(_storage.AsSpan(BoxNameBase(box), 9), jp: false);
        return string.IsNullOrWhiteSpace(name) ? $"BOX {box + 1}" : name;
    }

    public void SwapBoxes(int a, int b) => throw NotYet("Box swapping");
    public void DeleteBox(int box) => throw NotYet("Box management");

    public DexEntryState GetDexEntry(int nationalSpecies)
    {
        if (nationalSpecies is < 1 or > DexCapacity)
            return new DexEntryState(false, false);
        return new DexEntryState(DexBit(DexSeenOffset, nationalSpecies), DexBit(DexCaughtOffset, nationalSpecies));
    }

    public void SetDexEntry(int nationalSpecies, bool seen, bool caught)
    {
        if (nationalSpecies is < 1 or > DexCapacity)
            return;
        SetDexBit(DexSeenOffset, nationalSpecies, seen);
        SetDexBit(DexCaughtOffset, nationalSpecies, caught);
        _dirty = true;
    }

    public bool IsDexSpecies(int nationalSpecies) => nationalSpecies is >= 1 and <= DexCapacity;

    public DexProgress GetDexProgress() => new(CountDexBits(DexSeenOffset), CountDexBits(DexCaughtOffset), DexCapacity);

    public void CompleteDex()
    {
        for (var dex = 1; dex <= DexCapacity; dex++)
        {
            SetDexBit(DexSeenOffset, dex, true);
            SetDexBit(DexCaughtOffset, dex, true);
        }
        _dirty = true;
    }

    private bool DexBit(int baseOffset, int dexNumber)
    {
        var index = dexNumber - 1;
        return (_sb1[baseOffset + (index >> 3)] & (1 << (index & 7))) != 0;
    }

    private void SetDexBit(int baseOffset, int dexNumber, bool value)
    {
        var index = dexNumber - 1;
        ref var cell = ref _sb1[baseOffset + (index >> 3)];
        if (value) cell |= (byte)(1 << (index & 7));
        else cell &= (byte)~(1 << (index & 7));
    }

    private int CountDexBits(int baseOffset)
    {
        var count = 0;
        foreach (var cell in _sb1.AsSpan(baseOffset, DexBytes))
            count += BitOperations.PopCount(cell);
        return count;
    }

    // ── Bag (probe-confirmed pockets; quantities XOR the security key) ──

    private readonly record struct Pocket(string Name, int Offset, int Capacity, int MaxCount);

    private static readonly Pocket[] Pockets =
    [
        new("Items", 0x000, 150, 999),
        new("Medicine", 0x258, 65, 999),
        new("Key Items", 0x35C, 50, 999),
        new("Balls", 0x424, 27, 999),
        new("TMsHMs", 0x490, 128, 999),
        new("Mega Stones", 0x690, 35, 999),
        new("Battle Items", 0x71C, 100, 999),
        new("Berries", 0x8AC, 70, 999),
    ];

    private static string PocketNameOf(string pocket) => pocket switch
    {
        "POCKET_ITEMS" => "Items",
        "POCKET_MEDICINE" => "Medicine",
        "POCKET_KEY_ITEMS" => "Key Items",
        "POCKET_POKEBALLS" => "Balls",
        "POCKET_TM_HM" => "TMsHMs",
        "POCKET_MEGA_STONES" => "Mega Stones",
        "POCKET_BATTLE_ITEMS" => "Battle Items",
        "POCKET_BERRIES" => "Berries",
        _ => "",
    };

    private List<BagItem> ReadPocket(Pocket pocket)
    {
        var items = new List<BagItem>();
        var key = (ushort)SecurityKey;
        for (var slot = 0; slot < pocket.Capacity; slot++)
        {
            var offset = BagOffset + pocket.Offset + slot * 4;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(_sb1.AsSpan(offset));
            var raw = BinaryPrimitives.ReadUInt16LittleEndian(_sb1.AsSpan(offset + 2));
            // Two items-pocket quantities in the sample are stored un-XORed; a
            // reader accepts either and prefers the value in 1..999.
            var candidates = new[] { (ushort)(raw ^ key), raw };
            var count = candidates.FirstOrDefault(value => value is >= 1 and <= 999);
            if (id == 0 || id > _tables.MaxItem || count == 0)
                break;
            items.Add(new BagItem(id, count));
        }
        return items;
    }

    public IReadOnlyList<BagPouch> GetBag() =>
        [.. Pockets.Select(pocket => new BagPouch(pocket.Name, ReadPocket(pocket)))];

    public IReadOnlyList<int> GetPouchLegalItems(string pouchName) =>
        [.. Enumerable.Range(1, _tables.MaxItem)
            .Where(id => PocketNameOf(_tables.ItemPocket(id)) == pouchName)];

    public int SetItemCount(string pouchName, int itemId, int count)
    {
        var pocket = Array.Find(Pockets, p => p.Name.Equals(pouchName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(pocket.Name) || itemId <= 0 || itemId > _tables.MaxItem)
            return 0;
        var items = ReadPocket(pocket);
        // The list is rewritten from what was read. An entry past the readable
        // prefix (an unknown id, or a count neither form of which is 1-999) would
        // be erased by that rewrite, so the edit is refused instead.
        var readable = items.Count;
        for (var slot = readable; slot < pocket.Capacity; slot++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(_sb1.AsSpan(BagOffset + pocket.Offset + slot * 4)) != 0)
                throw new InvalidOperationException(
                    $"The {pocket.Name} pocket holds an entry PKForge cannot read; editing it would erase the entries after it.");
        var index = items.FindIndex(item => item.Id == itemId);
        if (count <= 0)
        {
            if (index < 0) return 0;
            items.RemoveAt(index);
        }
        else if (index >= 0)
        {
            items[index] = new BagItem(itemId, Math.Min(count, pocket.MaxCount));
        }
        else
        {
            if (items.Count >= pocket.Capacity) return 0;
            items.Add(new BagItem(itemId, Math.Min(count, pocket.MaxCount)));
        }

        var key = (ushort)SecurityKey;
        var original = ReadPocket(pocket);
        for (var slot = 0; slot < Math.Max(items.Count, readable); slot++)
        {
            var offset = BagOffset + pocket.Offset + slot * 4;
            if (slot < items.Count)
            {
                // An entry that did not change keeps its stored bytes (two sample
                // quantities are stored without the key and stay that way).
                if (slot < original.Count && original[slot] == items[slot])
                    continue;
                BinaryPrimitives.WriteUInt16LittleEndian(_sb1.AsSpan(offset), (ushort)items[slot].Id);
                BinaryPrimitives.WriteUInt16LittleEndian(_sb1.AsSpan(offset + 2),
                    (ushort)(items[slot].Count ^ key));
            }
            else
            {
                _sb1.AsSpan(offset, 4).Clear();
            }
        }
        _dirty = true;
        return items.FirstOrDefault(item => item.Id == itemId) is { } stored ? stored.Count : 0;
    }

    // ── Names / types / stats surface ──

    public IReadOnlyList<int> GetAbilityChoices(int species, int form)
    {
        var rom = _tables.SpeciesFromNational(species);
        var abilities = _tables.Abilities(rom);
        var choices = new List<int>();
        foreach (var ability in abilities)
            // A hack's own ability (past PKHeX's table) is shown by name, never offered by id.
            if (ability is > 0 and <= SoulGoldData.LastNationalAbility && !choices.Contains(ability))
                choices.Add(ability);
        return choices.Count > 0 ? choices : [0];
    }

    public IReadOnlyList<string> GetItemNames() => HackNames.WithNoItem(_tables.ItemNames());
    public IReadOnlyList<string> GetFormChoices(int species) => [""];
    public IReadOnlyList<int> GetSpeciesTypes(int nationalSpecies) =>
        _tables.Types(_tables.SpeciesFromNational(nationalSpecies));

    public BaseStats GetBaseStats(int nationalSpecies)
    {
        var stats = _tables.BaseStats(_tables.SpeciesFromNational(nationalSpecies));
        return new BaseStats(stats[0], stats[1], stats[2], stats[3], stats[4], stats[5]);
    }

    public string GetShowdownText(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid) return string.Empty;
        var text = $"{_tables.SpeciesName(mon.Species)}\nLevel: {mon.StoredOrDerivedLevel}\n{NatureName(mon.Nature)} Nature\nAbility: {_tables.AbilityName(ActiveAbility(mon))}";
        if (mon.HeldItem != 0)
            text += $"\nItem: {_tables.ItemName(mon.HeldItem)}";
        foreach (var move in mon.Moves)
            if (move != 0)
                text += $"\n- {_tables.MoveName(move)}";
        return text;
    }

    private static string NatureName(int nature) => nature switch
    {
        0 => "Hardy", 1 => "Lonely", 2 => "Brave", 3 => "Adamant", 4 => "Naughty",
        5 => "Bold", 6 => "Docile", 7 => "Relaxed", 8 => "Impish", 9 => "Lax",
        10 => "Timid", 11 => "Hasty", 12 => "Serious", 13 => "Jolly", 14 => "Naive",
        15 => "Modest", 16 => "Mild", 17 => "Quiet", 18 => "Bashful", 19 => "Rash",
        20 => "Calm", 21 => "Gentle", 22 => "Sassy", 23 => "Careful", _ => "Quirky",
    };

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

    public RngInfo GetRngInfo(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid)
            return new RngInfo(0, null, 0, false, false, [0, 0, 0, 0, 0, 0], 0, 2);
        return new RngInfo(mon.Pid, null, mon.Nature, mon.IsShiny, true,
            mon.Ivs, ActiveAbility(mon), SoulGoldData.GenderOf(mon.Pid, _tables.GenderRatio(mon.Species)));
    }

    public TrainingCaps GetTrainingCaps() => new(31, 255);

    public bool RerollNatureKeepShiny(int box, int slot, int nature)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid) return false;
        RequireWritable(box);
        // The nature lives in the header's hiddenNatureModifier, not the PID,
        // so changing it cannot touch shininess or identity.
        mon.Nature = nature;
        _dirty = true;
        return true;
    }

    public MoveDetails GetMoveDetails(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid)
            return new MoveDetails([], false, []);
        var moves = mon.Moves;
        var pp = mon.Pp;
        var bonuses = mon.PpBonuses;
        var slots = new List<MoveSlotDetail>(4);
        for (var i = 0; i < 4; i++)
        {
            var ups = (bonuses >> (i * 2)) & 3;
            var maxPp = _tables.MoveBasePp(moves[i]) * (5 + ups) / 5;
            slots.Add(new MoveSlotDetail(_tables.MoveToNational(moves[i]), pp[i], maxPp, ups));
        }
        return new MoveDetails(slots, true, []);
    }

    public void ApplyMoveDetails(int box, int slot, MoveDetailsEdit edit)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid) return;
        RequireWritable(box);
        if (edit.PP is { Count: 4 } pp)
        {
            var current = mon.Pp;
            for (var i = 0; i < 4; i++)
                current[i] = Math.Clamp(pp[i], 0, 255);
            mon.Pp = current;
            _dirty = true;
        }
        if (edit.PPUps is { Count: 4 } ups)
        {
            var packed = 0;
            for (var i = 0; i < 4; i++)
                packed |= Math.Clamp(ups[i], 0, 3) << (i * 2);
            mon.PpBonuses = packed;
            _dirty = true;
        }
    }

    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile = null)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid)
            return new GenerationOutcome(false, "That slot is empty.");
        RequireWritable(box);
        var trainer = GetTrainer();
        var name = profile?.OriginalTrainer ?? trainer.Name;
        mon.Otid = profile is null
            ? (uint)((trainer.SID << 16) | (trainer.TID & 0xFFFF))
            : (uint)((profile.SID << 16) | (profile.TID & 0xFFFF));
        mon.OriginalTrainerName = name;
        mon.SetMet(mon.MetLevel, otGender: trainer.Gender);
        _dirty = true;
        return new GenerationOutcome(true, $"{mon.Nickname} now belongs to {name}.");
    }

    // ── Import / export: the hack's own record bytes, verbatim ──

    public SlotExport ExportSlot(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid)
            throw new InvalidOperationException("That slot is empty.");
        // The box record the game itself stores, so the Bank keeps every byte.
        var bytes = mon.Party
            ? mon.RawRecord()[..BoxMonSize]
            : mon.RawRecord();
        return new SlotExport(bytes, $"{_tables.SpeciesName(mon.Species)}.pk3sg", null);
    }

    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format = null)
    {
        if (format != "pk3sg" || fileBytes.Length != BoxMonSize)
            return false; // no cross-format conversion: the codec is the hack's own
        RequireWritable(box);
        // A private copy: the caller's array is never aliased by a live record.
        var probe = new SoulGoldMon(fileBytes.AsSpan(0, BoxMonSize).ToArray(), 0, false, _era, _tables);
        if (!probe.LooksValid || probe.IsEmpty)
            return false;
        SoulGoldMon target;
        if (box == -1)
        {
            if (PartyCount >= 6) return false;
            // The box record is the source and the party slot the target: two
            // different buffers, so clearing the target cannot erase the source.
            CopyBetween(probe, PartyMon(PartyCount));
            _sb1[PartyCountOffset] = (byte)(PartyCount + 1);
        }
        else
        {
            if (TryMon(box, slot) is not { } destination || !destination.IsEmpty)
                return false;
            target = destination;
            fileBytes.AsSpan(0, BoxMonSize).CopyTo(target.Buffer.AsSpan(target.Offset, BoxMonSize));
        }
        _dirty = true;
        return true;
    }

    // ── Serialization: the newest slot only, every checksum recomputed ──

    public ReadOnlyMemory<byte> Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_dirty)
            return SramPadding.Restore(_data, _originalBytes);
        WriteBack();
        return SramPadding.Restore(_data, _originalBytes);
    }

    private void WriteBack()
    {
        var data = _data;

        // Every checksum that lives inside the storage image (the BX21 header,
        // the legacy extension checksum) is settled in the image first, so each
        // of the four homes below receives final bytes and its footer is
        // computed over them once. An unchanged image keeps the checksums the
        // game wrote (a refused save may hold tail bytes this session never read).
        if (!_storage.AsSpan().SequenceEqual(_storageAtOpen))
            SealStorageImage();

        // SaveBlock2 (sector 0's window).
        _sb2.AsSpan(0, SaveBlock2Size).CopyTo(data.AsSpan(_slot.Sections[0]));
        WriteChecksum(data, _slot.Sections[0], SaveBlock2Size);

        // The SaveBlock1 extension tail is the same bytes as storage
        // 39680..41623; the storage image is authoritative for box edits. Then
        // SaveBlock1 goes back across ids 1-4.
        _storage.AsSpan(StorageSb1TailBase, StorageSb1TailSize)
            .CopyTo(_sb1.AsSpan(StorageExtensionTailOffset, StorageSb1TailSize));
        var cursor = 0;
        for (var id = 1; id <= 4; id++)
        {
            _sb1.AsSpan(cursor, Windows[id]).CopyTo(data.AsSpan(_slot.Sections[id]));
            WriteChecksum(data, _slot.Sections[id], Windows[id]);
            cursor += Windows[id];
        }

        // Storage 0..35711 back into sectors 5-13, one 3968-byte chunk per
        // logical sector. Those sectors are not adjacent on flash (the rotation
        // and the footers sit between them), so the image is never copied as
        // one block.
        cursor = 0;
        for (var id = 5; id <= 13; id++)
        {
            _storage.AsSpan(cursor, 3968).CopyTo(data.AsSpan(_slot.Sections[id]));
            WriteChecksum(data, _slot.Sections[id], 3968);
            cursor += 3968;
        }

        // Overflow sector 30/31: storage 35712..39679. Footer id, signature and
        // counter stay as found; the sector is only touched when its bytes change.
        if (_slot.OverflowSector >= 0)
        {
            var overflow = data.AsSpan(_slot.OverflowSector, StorageOverflowSize);
            var image = _storage.AsSpan(StorageInSectors, StorageOverflowSize);
            if (!overflow.SequenceEqual(image))
            {
                image.CopyTo(overflow);
                WriteChecksum(data, _slot.OverflowSector, OverflowFooterWindow);
            }
        }

        // Auxiliary sector 28/29: only the generations that store boxes there.
        if (AuxCarriesStorage && _slot.AuxSector >= 0)
        {
            var auxTail = StorageSize - StorageAuxTailBase;
            var tail = data.AsSpan(_slot.AuxSector + StorageAuxDataOffset, auxTail);
            var image = _storage.AsSpan(StorageAuxTailBase, auxTail);
            if (!tail.SequenceEqual(image))
            {
                image.CopyTo(tail);
                WriteAuxRecordChecksum(data);
                WriteChecksum(data, _slot.AuxSector, StorageOverflowSize);
            }
        }
    }

    private int StorageSize => _era == StorageEra.Bx21 ? StorageSizeBx21 : StorageSizeV19;

    /// <summary>True when the auxiliary sector holds part of the storage image
    /// (BX21, and the legacy BX19 stage's box 18).</summary>
    private bool AuxCarriesStorage => _era != StorageEra.LegacyV19 || LegacyBoxCount(_storage) == 19;

    /// <summary>
    /// The overflow sector's footer checksum window. Legacy saves: the first
    /// 1324 bytes (PKMN_STORAGE_BX16_OVERFLOW_SIZE), the window the game's
    /// validator compares; the rest of the sector is covered by the extension
    /// checksum at overflow offset 1328. Proven on the sample: its BX19 slot's
    /// overflow footer (0x9550) is the sum over 1324 bytes and not over 3968.
    /// BX21: the whole 3968 bytes (src/save.c @e738033; no BX21 sample yet).
    /// </summary>
    private int OverflowFooterWindow => _era == StorageEra.Bx21 ? StorageOverflowSize : LegacyOverflowFooterWindow;

    /// <summary>The checksums kept inside the storage image itself: the BX21
    /// header payload checksum, or the legacy extension checksum (BX17: overflow
    /// tail only; BX18/BX19: seeded rolling sum over the overflow tail plus the
    /// SaveBlock1 tail), each with its inverse.</summary>
    private void SealStorageImage()
    {
        if (_era == StorageEra.Bx21)
        {
            var payload = RollingChecksum(0, _storage.AsSpan(16));
            BinaryPrimitives.WriteUInt16LittleEndian(_storage.AsSpan(8), payload);
            BinaryPrimitives.WriteUInt16LittleEndian(_storage.AsSpan(10), (ushort)~payload);
            return;
        }
        if (_slot.OverflowSector < 0)
            return; // no overflow sector: no extension stage exists to seal
        var box18 = BinaryPrimitives.ReadUInt32LittleEndian(_storage.AsSpan(LegacyBox18MagicOffset));
        ushort checksum;
        if (box18 == Bx17Magic)
        {
            checksum = Checksum(_storage.AsSpan(StorageInSectors + 1332, 2292), 2292);
        }
        else if (box18 is Bx18Magic or Bx19Magic)
        {
            uint total = box18 == Bx19Magic ? Bx19Magic : 0;
            total = Accumulate(total, _storage.AsSpan(StorageInSectors + 1332, StorageOverflowSize - 1332));
            total = Accumulate(total, _storage.AsSpan(StorageSb1TailBase, StorageSb1TailSize));
            checksum = (ushort)(total + (total >> 16));
        }
        else
        {
            return; // BX16 stage: no extension checksum exists yet
        }
        BinaryPrimitives.WriteUInt16LittleEndian(_storage.AsSpan(LegacyExtensionChecksumOffset), checksum);
        BinaryPrimitives.WriteUInt16LittleEndian(_storage.AsSpan(LegacyExtensionChecksumOffset + 2), (ushort)~checksum);
    }

    private static uint Accumulate(uint total, ReadOnlySpan<byte> data)
    {
        for (var offset = 0; offset < data.Length; offset += sizeof(uint))
            total += BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        return total;
    }

    /// <summary>The auxiliary record's own checksum pair, over the record as it
    /// now sits in the sector: BX19 bytes 12..3891 (the box-18 payload plus the
    /// Hall of Fame archive), BX21 bytes 12..3431; inverse at 10.</summary>
    private void WriteAuxRecordChecksum(byte[] data)
    {
        var length = _era == StorageEra.LegacyV19 ? 3880 : 3420;
        var checksum = RollingChecksum(0, data.AsSpan(_slot.AuxSector + 12, length));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(_slot.AuxSector + 8), checksum);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(_slot.AuxSector + 10), (ushort)~checksum);
    }

    // ── Unsupported surfaces ──

    public bool SupportsBoxTools => false;
    public bool SupportsLegalityAnalysis => false;
    /// <summary>Abilities are stored as a slot, never as a free id: HaX cannot pick an arbitrary one.</summary>
    public bool StoresAnyAbility => false;

    /// <summary>Whether a PKHeX move id has a twin in the hack's move table.</summary>
    public bool StoresMove(int move) => move == 0 || (_tables.MoveFromNational(move) is > 0 and var raw && _tables.IsKnownMove(raw));
    public bool SupportsRTCRepair => false;
    public void RepairRTC() { }

    public IReadOnlyList<int> GetMissingSpecies() => [];
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];
    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) =>
        throw NotYet("Encounter cards");
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false) =>
        throw NotYet("Box sorting");
    public int PlaceLivingDex(byte[] compressedBundle) => throw NotYet("The living dex");
    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) =>
        throw NotYet("The batch editor");
    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) =>
        throw NotYet("The batch editor");

    public IReadOnlyList<CountedEntry> GetPokeBeans() => [];
    public int SetPokeBeanCount(int index, int count) => 0;
    public IReadOnlyList<UndergroundItem> GetGrandUndergroundItems() => [];
    public int SetGrandUndergroundItemCount(int itemId, int count) => 0;
    public DaycareInfo GetDaycare() => new(false, []);
    public DaycareWithdrawal WithdrawDaycareToFirstEmptyBox(int facility, int slot) => throw NotYet("The Day Care");
    public bool SupportsLegalFashionUnlock => false;
    public void UnlockAllLegalFashion() { }
    public bool SupportsStylePoints => false;
    public int GetStylePoints() => 0;
    public void SetStylePoints(int value) { }
    public MysteryGiftInbox GetMysteryGiftInbox() => new(false, []);
    public TrainerRecordsInfo GetTrainerRecords() => new(false, []);

    public MetInfo GetMetInfo(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid)
            return new MetInfo(0, string.Empty, 0, string.Empty, false, false, 0, string.Empty, string.Empty, false,
                0, string.Empty, 0, string.Empty, false, 0, 0);
        return new MetInfo(mon.MetLocation, string.Empty, mon.MetLevel, string.Empty, false,
            mon.IsEggFlag, 0, string.Empty, string.Empty, false,
            mon.MetGame, string.Empty, mon.Language, string.Empty, false,
            (int)(mon.Otid & 0xFFFF), (int)(mon.Otid >> 16));
    }

    public void ApplyMetEdit(int box, int slot, MetEdit edit) => throw NotYet("Met/origin editing");
    public IReadOnlyList<NamedChoice> GetLocationChoices(int box, int slot, bool egg) => [];
    public IReadOnlyList<NamedChoice> GetVersionChoices() => [];
    public IReadOnlyList<NamedChoice> GetLanguageChoices(int box, int slot) => [];

    public PotentialInfo GetPotential(int box, int slot)
    {
        var mon = TryMon(box, slot);
        return new PotentialInfo(
            SupportsTera: true, TeraType: mon is { LooksValid: true } valid ? valid.TeraType : 0,
            TeraTypeName: string.Empty, TeraTypeOriginalName: string.Empty, TeraLocked: false,
            SupportsHyperTrain: false, HyperTrained: [false, false, false, false, false, false],
            SupportsAbilitySlot: mon is { LooksValid: true },
            AbilitySlot: mon is { LooksValid: true } valid2 ? valid2.AbilitySlot : 0,
            AbilitySlots: AbilitySlotChoices(box, slot),
            SupportsAwakening: false, Awakening: [], SupportsGanbaru: false, Ganbaru: [], GanbaruMaximums: []);
    }

    private IReadOnlyList<NamedChoice> AbilitySlotChoices(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is not { LooksValid: true }) return [];
        var abilities = _tables.Abilities(mon.Species);
        var choices = new List<NamedChoice>();
        for (var i = 0; i < 3; i++)
            if (abilities[i] != 0)
                choices.Add(new NamedChoice(i, _tables.AbilityName(abilities[i])));
        return choices;
    }

    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid) return;
        RequireWritable(box);
        if (edit.AbilitySlot is { } want && want is >= 0 and <= 2)
        {
            mon.AbilitySlot = want;
            _dirty = true;
        }
        if (edit.TeraType is { } tera)
        {
            mon.TeraType = tera;
            _dirty = true;
        }
    }

    public IReadOnlyList<NamedChoice> GetTeraTypeChoices() =>
        [.. Enumerable.Range(0, 18).Select(type => new NamedChoice(type, ((MoveType)type).ToString()))];

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
    public IReadOnlyDictionary<string, int> GetObtainableRibbonMaxima(int box, int slot) =>
        new Dictionary<string, int>();
    public int AwardAllObtainableRibbons(int box, int slot) => 0;

    public bool SupportsCompassSettings => false;
    public IReadOnlyList<CompassSetting> GetCompassSettings() => [];
    public bool SetCompassSetting(string id, int choiceIndex) => false;

    public void Dispose() => _disposed = true;

    private NotSupportedException NotYet(string what) =>
        new($"{what} is not available for Pokémon Soul Gold yet.");
}
