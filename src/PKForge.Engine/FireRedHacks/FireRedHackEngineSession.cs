using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine.FireRedHacks;

/// <summary>
/// Fire Red Extended and Crystal Advance Redux as PKForge sessions (see <see cref="FireRedHackFormat"/>
/// for the save shape and <see cref="FireRedHackTables"/> for what each id is proven to mean).
///
/// Editable: nickname, original trainer, held item, moves (with their PP), level where the
/// growth rate is proven, IVs, EVs, nature, shininess, gender, friendship, ball, and moving,
/// copying and releasing Pokémon between the party and the 14 boxes. Every write re-encodes
/// only the touched records, rewrites only the touched sectors of the live slot, and re-seals
/// them with the hack's own checksum window; the backup slot, footers and counters stay.
///
/// Never written, because the ROM holds the answer and the saves cannot prove it: species
/// changes, abilities, party stats that do not already match FireRed's formula, and (Fire Red
/// Extended) a boxed Pokémon's withdrawal into the party, whose stats the game computes from
/// base stats PKForge does not have. Withdrawing it in-game does that correctly.
/// </summary>
internal sealed class FireRedHackEngineSession : ISaveEngineSession
{
    private readonly byte[] _originalBytes;
    private readonly byte[] _data;
    private readonly int[] _live;
    private readonly byte[] _sb1;
    private readonly byte[] _storage;
    private readonly HashSet<int> _dirty = [];
    private readonly string? _displayName;
    private readonly int[] _windows;
    private bool _disposed;

    public FireRedHackEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _originalBytes = bytes.ToArray();
        // Emulator padding and mGBA's RTC footer sit past the flash chip; Serialize re-appends them.
        _data = SramPadding.Trim(RetroArchSaveContainer.Decode(bytes.Span));
        if (_data.Length != FireRedHackFormat.FileSize || FireRedHackFormat.Identify(_data) is not { } game)
            throw new InvalidDataException("These bytes are not a Fire Red Extended or Crystal Advance Redux save.");
        Game = game;
        Tables = FireRedHackTables.For(game);
        _windows = FireRedHackFormat.Windows(game);
        _live = FireRedHackFormat.LiveSectors(_data);
        _sb1 = FireRedHackFormat.ReadBlock(_data, _live, 1, FireRedHackFormat.SaveBlock1Size);
        _storage = FireRedHackFormat.ReadBlock(_data, _live, 5, FireRedHackFormat.StorageSize);
        _displayName = displayName;
    }

    internal FireRedHackGame Game { get; }
    internal FireRedHackTables Tables { get; }
    private bool Encrypted => Game == FireRedHackGame.FireRedExtended;
    private string GameName => Tables.GameName;

    /// <summary>Snapshot tag: each hack's own, never "PK3" (its records are not interchangeable retail PK3).</summary>
    internal string SnapshotTag => Game == FireRedHackGame.FireRedExtended ? "FIREREDEXTENDED" : "CRYSTALADVANCEREDUX";

    public SaveSnapshot Snapshot => BuildSnapshot();
    public int Generation => 3;
    public IReadOnlyList<string> GameNames => [GameName];
    /// <summary>Species pickers stay in the national range both hacks share with FireRed.</summary>
    public int MaxSpeciesId => 386;
    public int BoxCount => FireRedHackFormat.Boxes;
    public int BoxSlotCount => FireRedHackFormat.SlotsPerBox;
    public bool SupportsBoxTools => false;
    public bool SupportsLegalityAnalysis => false;
    public bool StoresAnyAbility => false;
    public bool StoresMove(int move) => move is >= 0 and <= FireRedHackTables.MaxKnownMove;
    public bool SupportsRTCRepair => false;
    public bool SupportsLegalFashionUnlock => false;
    public bool SupportsStylePoints => false;
    public bool SupportsCompassSettings => false;

    // ── Slot plumbing ──

    private int PartyCount => (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(_sb1.AsSpan(FireRedHackFormat.PartyCountOffset)), 6u);

    private static int PartyOffset(int slot) => FireRedHackFormat.PartyOffset + slot * FireRedHackFormat.PartyRecordSize;

    private static int BoxOffset(int box, int slot) =>
        FireRedHackFormat.BoxDataOffset + (box * FireRedHackFormat.SlotsPerBox + slot) * FireRedHackFormat.BoxRecordSize;

    private bool SlotExists(int box, int slot) =>
        box == -1 ? (uint)slot < 6 : (uint)box < FireRedHackFormat.Boxes && (uint)slot < FireRedHackFormat.SlotsPerBox;

    /// <summary>The record in a slot, or null when the slot is empty or does not exist.</summary>
    private FireRedHackMon? Read(int box, int slot)
    {
        if (!SlotExists(box, slot)) return null;
        if (box == -1)
            return slot < PartyCount ? new FireRedHackMon(_sb1.AsSpan(PartyOffset(slot), FireRedHackFormat.PartyRecordSize), Encrypted) : null;
        var mon = new FireRedHackMon(_storage.AsSpan(BoxOffset(box, slot), FireRedHackFormat.BoxRecordSize), Encrypted);
        return mon.IsEmpty ? null : mon;
    }

    private FireRedHackMon ReadForEdit(int box, int slot)
    {
        var mon = Read(box, slot) ?? throw new InvalidOperationException("Cannot edit an empty slot.");
        if (!mon.LooksValid)
            throw new InvalidOperationException($"This slot holds a record {GameName} cannot read back, so PKForge leaves it untouched.");
        return mon;
    }

    private void Write(int box, int slot, ReadOnlySpan<byte> record)
    {
        if (box == -1)
        {
            record.CopyTo(_sb1.AsSpan(PartyOffset(slot), FireRedHackFormat.PartyRecordSize));
            Touch(1, PartyOffset(slot), FireRedHackFormat.PartyRecordSize);
            return;
        }
        record[..FireRedHackFormat.BoxRecordSize].CopyTo(_storage.AsSpan(BoxOffset(box, slot), FireRedHackFormat.BoxRecordSize));
        Touch(5, BoxOffset(box, slot), FireRedHackFormat.BoxRecordSize);
    }

    private void ClearRecord(int box, int slot)
    {
        if (box == -1)
        {
            _sb1.AsSpan(PartyOffset(slot), FireRedHackFormat.PartyRecordSize).Clear();
            Touch(1, PartyOffset(slot), FireRedHackFormat.PartyRecordSize);
            return;
        }
        _storage.AsSpan(BoxOffset(box, slot), FireRedHackFormat.BoxRecordSize).Clear();
        Touch(5, BoxOffset(box, slot), FireRedHackFormat.BoxRecordSize);
    }

    private void SetPartyCount(int count)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_sb1.AsSpan(FireRedHackFormat.PartyCountOffset), (uint)count);
        Touch(1, FireRedHackFormat.PartyCountOffset, 4);
    }

    private void Touch(int firstId, int offset, int length)
    {
        foreach (var id in FireRedHackFormat.SectorsOf(firstId, offset, length))
            _dirty.Add(id);
    }

    // ── Reading ──

    /// <summary>Raw bytes of every slot (party first, then the PC) for the write-safety diff.</summary>
    internal IEnumerable<SlotImage> SlotImages()
    {
        for (var box = -1; box < FireRedHackFormat.Boxes; box++)
        for (var slot = 0; slot < (box == -1 ? 6 : FireRedHackFormat.SlotsPerBox); slot++)
        {
            var mon = Read(box, slot);
            yield return mon is null
                ? new SlotImage(new SlotRef(box, slot), [], Empty: true, Valid: true)
                : new SlotImage(new SlotRef(box, slot), mon.Record, false, mon.LooksValid);
        }
    }

    private int DisplayLevel(FireRedHackMon mon) =>
        mon.Party ? mon.Level : FireRedHackTables.LevelOf(mon.Species, mon.Experience) ?? FireRedHackTables.InferredLevel(mon.Experience);

    private int GenderOf(FireRedHackMon mon)
    {
        if (FireRedHackTables.RetailPersonal(mon.Species) is not { } personal)
            return 2;
        return personal.Gender switch
        {
            PersonalInfo.RatioMagicGenderless => 2,
            PersonalInfo.RatioMagicFemale => 1,
            PersonalInfo.RatioMagicMale => 0,
            var ratio => (mon.Pid & 0xFF) < ratio ? 1 : 0,
        };
    }

    /// <summary>Data PKForge may show as the hack's own: FireRed's for Crystal Advance Redux's
    /// retail species; nothing for Fire Red Extended, whose base data was modernised.</summary>
    private PersonalInfo3? ShownPersonal(int species) =>
        Game == FireRedHackGame.CrystalAdvanceRedux ? FireRedHackTables.RetailPersonal(species) : null;

    public EntityDetail ReadEntity(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mon = Read(box, slot);
        if (mon is null || !mon.LooksValid)
            return Empty(box, slot);
        var personal = ShownPersonal(mon.Species);
        var gender = GenderOf(mon);
        var level = DisplayLevel(mon);
        int[] ivs = [.. Enumerable.Range(0, 6).Select(mon.Iv)];
        int[] evs = [.. Enumerable.Range(0, 6).Select(mon.Ev)];
        IReadOnlyList<int>? stats = mon.Party ? mon.PartyStats
            : personal is not null && level > 0 ? FireRedHackTables.RetailStats(mon.Species, level, ivs, evs, mon.Nature) : null;
        return new EntityDetail(
            box, slot, false,
            Tables.NationalOf(mon.Species),
            Tables.SpeciesName(mon.Species),
            0,
            mon.Nickname,
            level,
            mon.Nature,
            personal is null ? 0 : personal.GetAbilityAtIndex(mon.AbilityBit),
            mon.HeldItem,
            FireRedHackTables.MoveToNational(mon.Move(0)), FireRedHackTables.MoveToNational(mon.Move(1)),
            FireRedHackTables.MoveToNational(mon.Move(2)), FireRedHackTables.MoveToNational(mon.Move(3)),
            ivs, evs,
            mon.IsShiny,
            mon.Ball,
            mon.OriginalTrainerName,
            personal is null ? null : [personal.Type1, personal.Type2],
            gender,
            mon.Friendship,
            stats,
            mon.Party ? mon.CurrentHp : 0,
            Traits: new SpriteTraits(Female: gender == 1),
            // Fire Red Extended's abilities live in the ROM: the save only says which slot.
            AbilityName: personal is null ? $"Ability slot {mon.AbilityBit + 1}" : null,
            MoveNames: HackNames.UnmappedMoves([mon.Move(0), mon.Move(1), mon.Move(2), mon.Move(3)],
                FireRedHackTables.MoveToNational, FireRedHackTables.MoveName));
    }

    private static EntityDetail Empty(int box, int slot) =>
        new(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);

    // ── Editing ──

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        edit = NameCharset.FitGen3Edit(edit); // charset first: a name the game cannot hold is refused, not emptied
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mon = ReadForEdit(box, slot);
        var before = mon.Record.ToArray();
        var shown = ReadEntity(box, slot);

        // Validate everything first: a refused edit changes nothing.
        if (edit.Species is { } species && species != shown.Species)
            throw new InvalidOperationException($"Changing a Pokémon's species is not available for {GameName}: its species data lives in the ROM.");
        if (edit.Ability is { } ability && ability != shown.Ability)
            throw new InvalidOperationException($"Abilities are not editable for {GameName}: which ability a slot means lives in the ROM.");
        if (edit.HeldItem is { } item && item != mon.HeldItem && !Tables.IsKnownItem(item))
            throw new InvalidOperationException($"Item {item} is not an item {GameName} is proven to have.");
        if (edit.Ball is { } ball && ball != mon.Ball && ball is < 1 or > 12)
            throw new InvalidOperationException($"Ball {ball} is not one of FireRed's balls.");
        if (edit.Nickname is { Length: > 10 })
            throw new InvalidOperationException("A nickname holds at most 10 characters.");
        if (edit.OriginalTrainer is { Length: > 7 })
            throw new InvalidOperationException("An original trainer name holds at most 7 characters.");
        int?[] wantedMoves = [edit.Move1, edit.Move2, edit.Move3, edit.Move4];
        int[] shownMoves = [shown.Move1, shown.Move2, shown.Move3, shown.Move4];
        for (var i = 0; i < 4; i++)
            if (wantedMoves[i] is { } move && move != shownMoves[i] && !StoresMove(move))
                throw new InvalidOperationException($"Move {move} is not a move {GameName} is proven to have.");
        // The editor echoes every field it was shown: only a different level is a change.
        var levelChange = edit.Level is { } level && level != shown.Level;
        if (levelChange && edit.Level is < 1 or > 100)
            throw new InvalidOperationException("A level is 1-100.");
        if (levelChange && FireRedHackTables.Growth(mon.Species) is null)
            throw new InvalidOperationException($"{shown.SpeciesName}'s growth rate in {GameName} is only in the ROM, so its level cannot be set.");
        var ivChange = edit.IVs is { Count: 6 } ivs && !ivs.SequenceEqual(shown.IVs);
        var evChange = edit.EVs is { Count: 6 } evs && !evs.SequenceEqual(shown.EVs);
        var natureChange = edit.Nature is { } nature && nature != mon.Nature;
        if (edit.Nature is < 0 or > 24)
            throw new InvalidOperationException("A nature is 0-24.");
        if (mon.Party && (levelChange || ivChange || evChange || natureChange) && !StatsProven(mon))
            throw new InvalidOperationException(
                $"This party Pokémon's stats do not follow FireRed's base stats, and {GameName}'s own are only in the ROM. " +
                "Put it in a box to change its level, IVs, EVs or nature: the game recomputes its stats when you withdraw it.");
        var genderChange = edit.Gender is 0 or 1 && edit.Gender != shown.Gender;
        if (genderChange && shown.Gender == 2)
            throw new InvalidOperationException($"{shown.SpeciesName}'s gender ratio in {GameName} is only in the ROM.");

        if (edit.Nickname is { Length: > 0 } nickname && nickname != mon.Nickname)
            mon.Nickname = nickname;
        if (edit.OriginalTrainer is { Length: > 0 } ot && ot != mon.OriginalTrainerName)
            mon.OriginalTrainerName = ot;
        if (edit.HeldItem is { } wantedItem && wantedItem != mon.HeldItem)
            mon.HeldItem = wantedItem;
        if (edit.Ball is { } wantedBall && wantedBall != mon.Ball)
            mon.Ball = wantedBall;
        if (edit.Friendship is { } friendship && friendship != mon.Friendship)
            mon.Friendship = Math.Clamp(friendship, 0, 255);
        for (var i = 0; i < 4; i++)
        {
            if (wantedMoves[i] is not { } move || move == shownMoves[i]) continue;
            mon.SetMove(i, move);
            mon.SetPp(i, FireRedHackTables.BasePp(move) * (5 + mon.PpUps(i)) / 5);
        }
        if (ivChange)
            for (var i = 0; i < 6; i++) mon.SetIv(i, edit.IVs![i]);
        if (evChange)
            for (var i = 0; i < 6; i++) mon.SetEv(i, edit.EVs![i]);
        if (natureChange || (edit.IsShiny is { } shiny && shiny != mon.IsShiny) || genderChange)
            RerollPid(mon, edit.Nature ?? mon.Nature, edit.IsShiny ?? mon.IsShiny, genderChange ? edit.Gender!.Value : null);
        if (levelChange)
        {
            var target = (byte)Math.Clamp(edit.Level!.Value, 1, 100);
            mon.Experience = Experience.GetEXP(target, FireRedHackTables.Growth(mon.Species)!.Value);
            if (mon.Party) mon.Level = target;
        }
        if (mon.Party && (levelChange || ivChange || evChange || natureChange))
            RecomputeStats(mon);

        var after = mon.Commit();
        if (!after.AsSpan().SequenceEqual(before))
            Write(box, slot, after);
    }

    /// <summary>The party Pokémon's stored stats equal FireRed's formula for its species, level,
    /// IVs, EVs and nature: the game's base stats for it are then FireRed's, so recomputing them
    /// after an edit writes what the game itself would.</summary>
    private static bool StatsProven(FireRedHackMon mon)
    {
        int[] ivs = [.. Enumerable.Range(0, 6).Select(mon.Iv)];
        int[] evs = [.. Enumerable.Range(0, 6).Select(mon.Ev)];
        return FireRedHackTables.RetailStats(mon.Species, mon.Level, ivs, evs, mon.Nature) is { } stats
            && stats.SequenceEqual(mon.PartyStats);
    }

    private static void RecomputeStats(FireRedHackMon mon)
    {
        int[] ivs = [.. Enumerable.Range(0, 6).Select(mon.Iv)];
        int[] evs = [.. Enumerable.Range(0, 6).Select(mon.Ev)];
        var stats = FireRedHackTables.RetailStats(mon.Species, mon.Level, ivs, evs, mon.Nature)!;
        var oldMax = mon.PartyStats[0];
        var hp = mon.CurrentHp == 0 ? 0 : Math.Clamp(mon.CurrentHp + stats[0] - oldMax, 1, stats[0]);
        mon.PartyStats = stats;
        mon.CurrentHp = hp;
    }

    /// <summary>A new PID with the wanted nature and shininess that keeps the low byte (and so the
    /// gender) unless a gender is asked for. The ability bit lives in the IVs in Gen 3, so it stays.</summary>
    private void RerollPid(FireRedHackMon mon, int nature, bool shiny, int? gender)
    {
        var personal = FireRedHackTables.RetailPersonal(mon.Species);
        if (SpeciesConverter.GetNational3((ushort)mon.Species) == (int)PKHeX.Core.Species.Unown && FireRedHackTables.IsRetailSpecies(mon.Species))
            throw new InvalidOperationException("Unown's letter comes from its PID, so PKForge does not reroll it.");
        var random = new Random(unchecked((int)mon.Pid));
        var lowByte = mon.Pid & 0xFF;
        for (var attempt = 0; attempt < 5_000_000; attempt++)
        {
            uint low;
            if (gender is { } wanted && personal is not null)
            {
                low = (uint)random.Next(256);
                if (((low < personal.Gender) ? 1 : 0) != wanted) continue;
            }
            else low = lowByte;
            var pid = (uint)random.Next(0x10000) << 16 | (uint)random.Next(256) << 8 | low;
            if (shiny)
            {
                // Pick the high half so the shiny value lands under 8.
                var lowHalf = pid & 0xFFFF;
                var high = (uint)((mon.OtId & 0xFFFF) ^ (mon.OtId >> 16) ^ lowHalf ^ (uint)random.Next(8)) & 0xFFFF;
                pid = high << 16 | lowHalf;
            }
            if (pid % 25 != nature || FireRedHackMon.ShinyValue(pid, mon.OtId) < 8 != shiny) continue;
            mon.Pid = pid;
            return;
        }
        throw new InvalidOperationException("Could not find a personality value with those traits.");
    }

    // ── Moving ──

    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (fromBox == toBox && fromSlot == toSlot) return;
        var source = ReadForEdit(fromBox, fromSlot);
        if (!SlotExists(toBox, toSlot)) throw new InvalidOperationException("The destination slot does not exist.");

        if (fromBox == -1 && toBox == -1)
        {
            var list = Enumerable.Range(0, PartyCount).Select(i => _sb1.AsSpan(PartyOffset(i), FireRedHackFormat.PartyRecordSize).ToArray()).ToList();
            if (toSlot < list.Count)
            {
                // Onto another party Pokémon: the two swap places, as in the games.
                (list[fromSlot], list[toSlot]) = (list[toSlot], list[fromSlot]);
            }
            else
            {
                // Onto an empty party slot: the party has no gaps, so it goes last.
                var moved = list[fromSlot];
                list.RemoveAt(fromSlot);
                list.Add(moved);
            }
            for (var i = 0; i < list.Count; i++) Write(-1, i, list[i]);
            return;
        }

        if (toBox == -1)
        {
            if (PartyCount >= 6) throw new InvalidOperationException("The party is full.");
            var party = ToParty(source);
            Write(-1, PartyCount, party);
            SetPartyCount(PartyCount + 1);
            ClearRecord(fromBox, fromSlot);
            return;
        }

        var occupant = Read(toBox, toSlot);
        if (occupant is not null && !occupant.LooksValid)
            throw new InvalidOperationException($"The destination holds a record {GameName} cannot read back, so PKForge leaves it untouched.");
        if (fromBox == -1)
        {
            // Party into a box: the box keeps the first 80 bytes, as the game deposits it.
            if (occupant is not null)
            {
                Write(-1, fromSlot, ToParty(occupant));
            }
            else
            {
                if (PartyCount <= 1) throw new InvalidOperationException("The party cannot be left empty.");
                CompactPartyFrom(fromSlot);
            }
            Write(toBox, toSlot, source.Record);
            return;
        }
        // Box to box: swap the two records byte for byte.
        var from = source.Record.ToArray();
        if (occupant is not null) Write(fromBox, fromSlot, occupant.Record);
        else ClearRecord(fromBox, fromSlot);
        Write(toBox, toSlot, from);
    }

    /// <summary>A box record as a party record, the way the game withdraws it: level from EXP,
    /// stats from base stats, full HP, no status. Only possible when FireRed's data is the
    /// hack's for that species (Crystal Advance Redux keeps FireRed's species data).</summary>
    private byte[] ToParty(FireRedHackMon boxed)
    {
        if (boxed.Party) return boxed.Record.ToArray();
        if (Game == FireRedHackGame.FireRedExtended || FireRedHackTables.LevelOf(boxed.Species, boxed.Experience) is not { } level)
            throw new InvalidOperationException(
                $"{GameName} computes a withdrawn Pokémon's stats from base stats only its ROM holds. Withdraw it in-game; PKForge can move it between boxes.");
        var record = new byte[FireRedHackFormat.PartyRecordSize];
        boxed.Record.AsSpan(0, FireRedHackFormat.BoxRecordSize).CopyTo(record);
        var party = new FireRedHackMon(record, Encrypted) { Level = level };
        int[] ivs = [.. Enumerable.Range(0, 6).Select(party.Iv)];
        int[] evs = [.. Enumerable.Range(0, 6).Select(party.Ev)];
        var stats = FireRedHackTables.RetailStats(party.Species, level, ivs, evs, party.Nature)!;
        party.PartyStats = stats;
        party.CurrentHp = stats[0];
        record[0x55] = 0xFF; // no mail
        return party.Record;
    }

    private void CompactPartyFrom(int removed)
    {
        var count = PartyCount;
        for (var i = removed; i < count - 1; i++)
            Write(-1, i, _sb1.AsSpan(PartyOffset(i + 1), FireRedHackFormat.PartyRecordSize).ToArray());
        ClearRecord(-1, count - 1);
        SetPartyCount(count - 1);
    }

    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = Read(fromBox, fromSlot);
        if (source is null || !source.LooksValid || !SlotExists(toBox, toSlot)) return false;
        if (toBox == -1)
        {
            if (PartyCount >= 6) return false;
            Write(-1, PartyCount, ToParty(source));
            SetPartyCount(PartyCount + 1);
            return true;
        }
        if (Read(toBox, toSlot) is not null) return false;
        Write(toBox, toSlot, source.Record);
        return true;
    }

    public void ReleaseSlot(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Read(box, slot) is null) return;
        if (box == -1)
        {
            if (PartyCount <= 1) throw new InvalidOperationException("The party cannot be left empty.");
            CompactPartyFrom(slot);
            return;
        }
        ClearRecord(box, slot);
    }

    public void ClearBox(int box)
    {
        for (var slot = 0; slot < FireRedHackFormat.SlotsPerBox; slot++)
            if (Read(box, slot) is not null) ClearRecord(box, slot);
    }

    // ── Snapshot / write-out ──

    private SaveSnapshot BuildSnapshot()
    {
        var slots = new List<SlotSummary>(FireRedHackFormat.Boxes * FireRedHackFormat.SlotsPerBox + 6);
        for (var box = 0; box < FireRedHackFormat.Boxes; box++)
        for (var slot = 0; slot < FireRedHackFormat.SlotsPerBox; slot++)
            slots.Add(Summary(box, slot));
        for (var slot = 0; slot < 6; slot++)
            slots.Add(Summary(-1, slot));
        return new SaveSnapshot(SnapshotTag, 3, _originalBytes.ToArray(), slots, _displayName);
    }

    private SlotSummary Summary(int box, int slot)
    {
        var mon = Read(box, slot);
        if (mon is null)
            return new SlotSummary(box, slot, null, null, false, true);
        if (!mon.LooksValid)
            return new SlotSummary(box, slot, 0, "?", false, false); // occupied, unreadable: never shown as free
        return new SlotSummary(box, slot, Tables.NationalOf(mon.Species), mon.Nickname, mon.IsShiny, true,
            HeldItem: mon.HeldItem, Traits: new SpriteTraits(Female: GenderOf(mon) == 1));
    }

    public ReadOnlyMemory<byte> Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dirty.Count == 0) return _originalBytes.ToArray();
        var data = _data.ToArray();
        FireRedHackFormat.WriteBlock(data, _live, 1, _sb1);
        FireRedHackFormat.WriteBlock(data, _live, 5, _storage);
        foreach (var id in _dirty)
            FireRedHackFormat.WriteChecksum(data, _live[id], _windows[id]);
        return SramPadding.Restore(data, _originalBytes);
    }

    // ── Trainer ──

    private int Sb2 => _live[0];

    public TrainerInfo GetTrainer() => new(
        FireRedHackFormat.ReadText(_data.AsSpan(Sb2, 7)),
        BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Sb2 + 0xA)),
        BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Sb2 + 0xC)),
        0,
        _data[Sb2 + 0x8]);

    public void SetTrainer(TrainerInfo trainer) => throw NotYet("The trainer editor");

    /// <summary>Play time as "h:mm:ss" from SaveBlock2 (hours u16 at 0xE, minutes 0x10, seconds 0x11).</summary>
    internal string PlayTimeText() =>
        $"{BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Sb2 + 0xE))}:{_data[Sb2 + 0x10]:00}:{_data[Sb2 + 0x11]:00}";

    public TrainerStats GetTrainerStats() => new(
        true, BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Sb2 + 0xE)), _data[Sb2 + 0x10], 999,
        false, 0, 0, false, 0, 0);

    public void SetTrainerStats(TrainerStatsEdit edit) => throw NotYet("Trainer statistics");

    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile = null) =>
        new(false, $"Re-assigning the original trainer is not available for {GameName} yet: edit the trainer name instead.");

    public string GetBoxName(int box)
    {
        if ((uint)box >= FireRedHackFormat.Boxes) return string.Empty;
        var name = FireRedHackFormat.ReadText(_storage.AsSpan(FireRedHackFormat.BoxNamesOffset + box * FireRedHackFormat.BoxNameLength, FireRedHackFormat.BoxNameLength));
        return string.IsNullOrWhiteSpace(name) ? $"BOX{box + 1}" : name;
    }

    // ── Species facts ──

    public IReadOnlyList<int> GetAbilityChoices(int species, int form) => [];
    public IReadOnlyList<string> GetItemNames() => HackNames.WithNoItem(Tables.ItemNames);
    public IReadOnlyList<string> GetFormChoices(int species) => [""];

    public IReadOnlyList<int> GetSpeciesTypes(int species)
    {
        if (Game != FireRedHackGame.CrystalAdvanceRedux || species is < 1 or > 386) return [];
        var personal = PersonalTable.FR[species];
        return [personal.Type1, personal.Type2];
    }

    public BaseStats GetBaseStats(int species)
    {
        if (Game != FireRedHackGame.CrystalAdvanceRedux || species is < 1 or > 386) return new BaseStats(0, 0, 0, 0, 0, 0);
        var p = PersonalTable.FR[species];
        return new BaseStats(p.HP, p.ATK, p.DEF, p.SPA, p.SPD, p.SPE);
    }

    public TrainingCaps GetTrainingCaps() => new(31, 255);

    public RngInfo GetRngInfo(int box, int slot)
    {
        var mon = Read(box, slot);
        if (mon is null || !mon.LooksValid)
            return new RngInfo(0, null, 0, false, false, [0, 0, 0, 0, 0, 0], 0, 2);
        var detail = ReadEntity(box, slot);
        return new RngInfo(mon.Pid, null, mon.Nature, mon.IsShiny, false, detail.IVs, mon.AbilityBit, detail.Gender);
    }

    public bool RerollNatureKeepShiny(int box, int slot, int nature)
    {
        if (nature is < 0 or > 24 || Read(box, slot) is not { LooksValid: true }) return false;
        ApplyEdit(box, slot, new EntityEdit(Nature: nature));
        return true;
    }

    public IReadOnlyList<int> GetMissingSpecies() => [];

    // ── Pokédex (FireRed's offsets are not proven under these hacks' chunking) ──

    public DexEntryState GetDexEntry(int species) => new(false, false);
    public void SetDexEntry(int species, bool seen, bool caught) { }
    public bool IsDexSpecies(int species) => false;
    public DexProgress GetDexProgress() => new(0, 0, 0);
    public void CompleteDex() => throw NotYet("The Pokédex");
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];

    // ── Showdown ──

    public string GetShowdownText(int box, int slot)
    {
        var mon = Read(box, slot);
        if (mon is null || !mon.LooksValid) return string.Empty;
        var text = $"{Tables.SpeciesName(mon.Species)}\nLevel: {DisplayLevel(mon)}\n{(Nature)mon.Nature} Nature";
        if (mon.HeldItem > 0) text += $"\nItem: {Tables.ItemName(mon.HeldItem)}";
        for (var i = 0; i < 4; i++)
            if (mon.Move(i) > 0) text += $"\n- {FireRedHackTables.MoveName(mon.Move(i))}";
        return text;
    }

    public string ExportBoxShowdown(int box) =>
        string.Join("\n\n", Enumerable.Range(0, FireRedHackFormat.SlotsPerBox).Select(slot => GetShowdownText(box, slot)).Where(text => text.Length > 0));

    // ── Moves ──

    public MoveDetails GetMoveDetails(int box, int slot)
    {
        var mon = Read(box, slot);
        if (mon is null || !mon.LooksValid) return new MoveDetails([], false, []);
        var slots = new List<MoveSlotDetail>(4);
        for (var i = 0; i < 4; i++)
        {
            var move = mon.Move(i);
            var max = FireRedHackTables.BasePp(move) * (5 + mon.PpUps(i)) / 5;
            slots.Add(new MoveSlotDetail(FireRedHackTables.MoveToNational(move), mon.Pp(i), max == 0 ? mon.Pp(i) : max, mon.PpUps(i)));
        }
        return new MoveDetails(slots, false, []);
    }

    public void ApplyMoveDetails(int box, int slot, MoveDetailsEdit edit)
    {
        if (edit.PP is not { Count: 4 } pp) return;
        var mon = ReadForEdit(box, slot);
        var before = mon.Record.ToArray();
        for (var i = 0; i < 4; i++)
            if (mon.Move(i) is > 0 and <= FireRedHackTables.MaxKnownMove)
                mon.SetPp(i, Math.Min(pp[i], FireRedHackTables.BasePp(mon.Move(i)) * (5 + mon.PpUps(i)) / 5));
        var after = mon.Commit();
        if (!after.AsSpan().SequenceEqual(before)) Write(box, slot, after);
    }

    public PotentialInfo GetPotential(int box, int slot) => new(
        SupportsTera: false, TeraType: 0, TeraTypeName: "", TeraTypeOriginalName: "", TeraLocked: false,
        SupportsHyperTrain: false, HyperTrained: [false, false, false, false, false, false],
        SupportsAbilitySlot: false, AbilitySlot: 0, AbilitySlots: [],
        SupportsAwakening: false, Awakening: [], SupportsGanbaru: false, Ganbaru: [], GanbaruMaximums: []);

    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit) { }
    public IReadOnlyList<NamedChoice> GetTeraTypeChoices() => [];

    // ── Not available ──

    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format = null) => false;
    public SlotExport ExportSlot(int box, int slot) => throw NotYet("Exporting a Pokémon");
    public int PlaceLivingDex(byte[] compressedBundle) => throw NotYet("The living dex");
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false) => throw NotYet("Box sorting");
    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) => throw NotYet("The batch editor");
    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) => throw NotYet("The batch editor");
    public void SwapBoxes(int a, int b) => throw NotYet("Box swapping");
    public void DeleteBox(int box) => throw NotYet("Box management");
    public IReadOnlyList<BagPouch> GetBag() => [];
    public IReadOnlyList<int> GetPouchLegalItems(string pouchName) => [];
    public int SetItemCount(string pouchName, int itemId, int count) => throw NotYet("The bag");
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
        new(false, $"{GameName} has no offline encounter table.");

    public void Dispose() => _disposed = true;

    private NotSupportedException NotYet(string what) => new($"{what} is not available for Pokémon {GameName} yet.");
}
