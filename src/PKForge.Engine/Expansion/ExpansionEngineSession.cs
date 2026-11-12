using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine.Expansion;

/// <summary>
/// A pokeemerald-expansion save as a PKForge session. The envelope is retail Emerald's
/// (32 sectors, 0x08012025, 14 sectors per slot) but the mon codec is the expansion's
/// repacked substructs, so stock PKHeX reads the wrong species/EXP/ball for much of the
/// dex. This session reads the 80/100-byte records directly, edits only the fields the
/// ticket proves, and commits only the live slot's touched sectors — the rotating backup
/// slot and every unmapped byte stay exactly as found. Legality is not evaluated: hack
/// species and encounter tables do not exist in PKHeX.
/// </summary>
internal sealed class ExpansionEngineSession : ISaveEngineSession
{
    private readonly byte[] _data;
    private readonly byte[] _originalBytes;
    private readonly int[] _sections;
    private readonly ExpansionProfile _profile;
    private readonly ExpansionData _tables;
    private readonly string? _displayName;
    private readonly byte[] _saveBlock1;
    private readonly byte[] _storage;
    private bool _disposed;

    public ExpansionEngineSession(ReadOnlyMemory<byte> bytes, string? displayName)
    {
        _originalBytes = bytes.ToArray();
        _data = SramPadding.Trim(RetroArchSaveContainer.Decode(bytes.Span));
        if (_data.Length < ExpansionFormat.FileSize)
            throw new InvalidDataException("The save is smaller than a GBA flash dump.");
        _profile = ExpansionFormat.LooksLike(_data)
            ?? throw new InvalidDataException("These bytes are not a pokeemerald-expansion save the engine can prove.");
        _tables = _profile.TableData;
        _sections = ExpansionFormat.SectionOffsets(_data);
        _saveBlock1 = ExpansionFormat.ReadBlock(
            _data, _sections, _profile, ExpansionFormat.SaveBlock1FirstSection,
            ExpansionFormat.SaveBlock1Sections, _profile.SaveBlock1Size);
        _storage = ExpansionFormat.ReadBlock(
            _data, _sections, _profile, ExpansionFormat.StorageFirstSection,
            ExpansionFormat.StorageSections, _profile.StorageSize);
        _displayName = displayName;
    }

    private bool TeamRocket => _profile.Codec == ExpansionMonCodec.TeamRocketEdition;

    /// <summary>
    /// A party record's stats are recomputed (level, EVs, IVs) only where the tables reproduce
    /// the stats the game stored: Team Rocket Edition changed some species' base stats
    /// (Houndoom and Venusaur on the real save), and a recomputation from the expansion's would
    /// write stats the game never computes.
    /// </summary>
    private void RequireStatsProof(ExpansionMon mon)
    {
        if (TeamRocket && !mon.StoredStatsMatchTables)
            throw ExpansionMon.Unproven(
                $"The level, EVs and IVs of {_tables.SpeciesName(mon.Species)} in the party (this game changed its base stats, so its stats cannot be recomputed; move it to a box to level it there)");
    }

    /// <summary>A boxed Team Rocket Edition record entering the party would need stats from base
    /// stats this game may have changed.</summary>
    private void RequireWithdrawProof(int fromBox, int toBox)
    {
        if (TeamRocket && fromBox >= 0 && toBox == -1)
            throw ExpansionMon.Unproven(
                "Moving a Pokémon from a box into the party (this game changed some species' base stats, so its party stats cannot be computed; withdraw it in the game)");
    }

    public SaveSnapshot Snapshot => BuildSnapshot(_displayName);

    public int Generation => 3;
    public IReadOnlyList<string> GameNames => [_profile.GameName];

    /// <summary>Why this variant is read but never written (see <see cref="ExpansionProfile.ReadOnlyReason"/>), or null.</summary>
    internal string? ReadOnlyReason => _profile.ReadOnlyReason;
    public int MaxSpeciesId => _tables.MaxSpecies;
    public int BoxCount => _profile.BoxCount;
    public int BoxSlotCount => _profile.BoxSlotCount;

    private int PartyCount => Math.Min((int)_saveBlock1[_profile.PartyCountOffset], 6);

    private ExpansionMon PartyMon(int slot) =>
        new(_saveBlock1, _profile.PartyOffset + slot * _profile.PartyMonSize, true, _tables, _profile.Codec);

    private int? ResolveBoxSlot(int box, int slot)
    {
        if ((uint)box >= (uint)_profile.BoxCount || (uint)slot >= (uint)_profile.BoxSlotCount)
            return null;
        return _profile.StorageBoxOffset + (box * _profile.BoxSlotCount + slot) * _profile.BoxMonSize;
    }

    private ExpansionMon? TryMon(int box, int slot)
    {
        if (box == -1)
            return (uint)slot >= (uint)PartyCount ? null : PartyMon(slot);
        return ResolveBoxSlot(box, slot) is { } offset
            ? new ExpansionMon(_storage, offset, false, _tables, _profile.Codec)
            : null;
    }

    private int RecordOffset(int box, int slot) => box == -1
        ? _profile.PartyOffset + slot * _profile.PartyMonSize
        : ResolveBoxSlot(box, slot) ?? throw new InvalidOperationException("That slot does not exist.");

    private void CommitPartySection()
    {
        var length = ExpansionFormat.Window(_profile, ExpansionFormat.SaveBlock1FirstSection);
        _saveBlock1.AsSpan(0, length).CopyTo(_data.AsSpan(_sections[ExpansionFormat.SaveBlock1FirstSection], length));
        ExpansionFormat.WriteChecksum(_data, _sections[ExpansionFormat.SaveBlock1FirstSection], length);
    }

    private void CommitStorageRange(int offset, int length) =>
        ExpansionFormat.WriteBlockRange(_data, _sections, _profile, ExpansionFormat.StorageFirstSection,
            ExpansionFormat.StorageSections, offset, _storage.AsSpan(offset, length));

    private void CommitSection(int sectionId) =>
        ExpansionFormat.CommitSection(_data, _sections, _profile, sectionId);

    internal IEnumerable<SlotImage> SlotImages()
    {
        for (var box = -1; box < _profile.BoxCount; box++)
        for (var slot = 0; slot < (box == -1 ? 6 : _profile.BoxSlotCount); slot++)
        {
            var mon = TryMon(box, slot);
            yield return mon is null
                ? new SlotImage(new SlotRef(box, slot), [], Empty: true, Valid: true)
                : new SlotImage(new SlotRef(box, slot), mon.RawRecord(), mon.IsEmpty, mon.IsEmpty || mon.LooksValid);
        }
    }

    // ── Reading and editing ──

    public EntityDetail ReadEntity(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return Empty(box, slot);
        var moves = mon.Moves;
        var species = _tables.NationalIdOf(mon.Species);
        return new EntityDetail(
            box, slot, false, species, _tables.SpeciesName(mon.Species), 0, mon.Nickname, mon.Level,
            mon.Nature, mon.ActiveAbility, mon.HeldItem,
            _tables.MoveToNational(moves[0]), _tables.MoveToNational(moves[1]),
            _tables.MoveToNational(moves[2]), _tables.MoveToNational(moves[3]),
            mon.Ivs, mon.Evs, mon.IsShiny, mon.Ball, mon.OriginalTrainerName,
            _tables.Types(mon.Species), mon.Gender, mon.Friendship, mon.Stats, mon.CurrentHp,
            (int)mon.StatusCondition,
            AbilityName: mon.ActiveAbility > ExpansionData.LastNationalAbility ? _tables.AbilityName(mon.ActiveAbility) : null,
            MoveNames: HackNames.UnmappedMoves(moves, _tables.MoveToNational, _tables.MoveName));
    }

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        edit = NameCharset.FitGen3Edit(edit); // charset first: a name the game cannot hold is refused, not emptied
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mon = TryMon(box, slot);
        if (mon is null && box == -1 && slot == PartyCount && PartyCount < 6 && edit.Species is > 0)
        {
            mon = new ExpansionMon(_saveBlock1, _profile.PartyOffset + slot * _profile.PartyMonSize, true, _tables, _profile.Codec);
        }
        if (mon is null)
            throw new InvalidOperationException("Cannot edit an empty slot.");

        var before = mon.RawRecord();
        var wasEmpty = mon.IsEmpty;
        if (wasEmpty)
        {
            if (edit.Species is not { } requested || requested <= 0)
                throw new InvalidOperationException("An empty slot needs a species before it can be edited.");
            var romSpecies = _tables.SpeciesFromNational(requested);
            if (romSpecies <= 0)
                throw new NotSupportedException($"Species {requested} is not in the expansion species table.");
            mon.SetSpecies(romSpecies);
            var trainer = GetTrainer();
            mon.InitializeHeader((uint)((trainer.SID << 16) | (trainer.TID & 0xFFFF)), trainer.Name);
        }

        if (edit.Species is { } species)
        {
            var romSpecies = _tables.SpeciesFromNational(species);
            if (romSpecies > 0 && romSpecies != mon.Species)
            {
                if (TeamRocket)
                    throw ExpansionMon.Unproven("The species (this game changed some species' stats and abilities, so another species' data is not proven)");
                mon.SetSpecies(romSpecies);
            }
        }
        var statsChange = (edit.Level is { } wantedLevel && Math.Clamp(wantedLevel, 1, 100) != mon.Level)
            || (edit.EVs is { Count: 6 } wantedEvs && !wantedEvs.SequenceEqual(mon.Evs))
            || (edit.IVs is { Count: 6 } wantedIvs && !wantedIvs.SequenceEqual(mon.Ivs));
        if (statsChange)
            RequireStatsProof(mon);
        if (edit.Nickname is { Length: > 0 } nickname && nickname != mon.Nickname)
            mon.Nickname = nickname;
        if (edit.Level is { } level && Math.Clamp(level, 1, 100) != mon.Level)
            mon.SetLevel(level);
        if (edit.Nature is { } nature && nature != mon.Nature)
            mon.RerollPid(nature: nature);
        if (edit.Ability is { } ability && ability != mon.ActiveAbility)
            mon.SetAbilityId(ability);
        if (edit.HeldItem is { } item && item != mon.HeldItem && _tables.IsKnownItem(item))
            mon.SetHeldItem(item);
        ApplyMoves(mon, edit);
        if (edit.IVs is { Count: 6 } ivs && !ivs.SequenceEqual(mon.Ivs))
            mon.SetIvs(ivs);
        if (edit.EVs is { Count: 6 } evs && !evs.SequenceEqual(mon.Evs))
            mon.SetEvs(evs);
        if (edit.IsShiny is { } shiny && shiny != mon.IsShiny)
            mon.RerollPid(shiny: shiny);
        if (edit.Ball is { } ball && ball != mon.Ball)
            mon.SetBall(ball);
        if (edit.OriginalTrainer is { Length: > 0 } ot && ot != mon.OriginalTrainerName)
            mon.OriginalTrainerName = ot;
        if (edit.Gender is 0 or 1 && edit.Gender != mon.Gender)
            mon.RerollPid(gender: edit.Gender);
        if (edit.Friendship is { } friendship && friendship != mon.Friendship)
            mon.SetFriendship(friendship);

        mon.Commit();
        if (mon.RawRecord().AsSpan().SequenceEqual(before))
            return;
        if (box == -1)
        {
            if (wasEmpty && slot == PartyCount && PartyCount < 6)
                _saveBlock1[_profile.PartyCountOffset] = (byte)(PartyCount + 1);
            CommitPartySection();
        }
        else
        {
            CommitStorageRange(RecordOffset(box, slot), _profile.BoxMonSize);
        }
    }

    private void ApplyMoves(ExpansionMon mon, EntityEdit edit)
    {
        int?[] wanted = [edit.Move1, edit.Move2, edit.Move3, edit.Move4];
        var current = mon.Moves;
        for (var i = 0; i < 4; i++)
        {
            // The editor speaks PKHeX ids; the record keeps the expansion's.
            if (wanted[i] is not { } national || national == _tables.MoveToNational(current[i]))
                continue;
            var move = _tables.MoveFromNational(national);
            if ((national != 0 && move == 0) || !_tables.IsKnownMove(move))
                continue;
            mon.SetMove(i, move, move == 0 ? 0 : _tables.MovePp(move));
        }
    }

    private static EntityDetail Empty(int box, int slot) =>
        new(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0,
            [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);

    // ── Copy / move / release ──

    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWithdrawProof(fromBox, toBox);
        var source = TryMon(fromBox, fromSlot);
        if (source is null || source.IsEmpty || !source.LooksValid)
            return false;
        ExpansionMon? target;
        if (toBox == -1)
        {
            if (PartyCount >= 6)
                return false;
            target = new ExpansionMon(_saveBlock1, _profile.PartyOffset + PartyCount * _profile.PartyMonSize, true, _tables, _profile.Codec);
        }
        else
        {
            var offset = ResolveBoxSlot(toBox, toSlot);
            if (offset is null)
                return false;
            target = new ExpansionMon(_storage, offset.Value, false, _tables, _profile.Codec);
            if (!target.IsEmpty)
                return false;
        }
        target.CopyFrom(source);
        target.Commit();
        if (toBox == -1)
        {
            _saveBlock1[_profile.PartyCountOffset] = (byte)(PartyCount + 1);
            CommitPartySection();
        }
        else
            CommitStorageRange(RecordOffset(toBox, toSlot), _profile.BoxMonSize);
        return true;
    }

    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWithdrawProof(fromBox, toBox);
        if (fromBox == toBox && fromSlot == toSlot)
            return;
        var source = TryMon(fromBox, fromSlot) ?? throw new InvalidOperationException("The source slot is empty.");
        if (source.IsEmpty)
            throw new InvalidOperationException("The source slot is empty.");
        if (toBox == -1)
        {
            if (fromBox == -1)
            {
                MoveWithinParty(fromSlot, toSlot);
                return;
            }
            if (PartyCount >= 6)
                throw new InvalidOperationException("The party is full.");
            var destination = new ExpansionMon(_saveBlock1, _profile.PartyOffset + PartyCount * _profile.PartyMonSize, true, _tables, _profile.Codec);
            destination.CopyFrom(source);
            destination.Commit();
            source.Clear();
            source.Commit();
            _saveBlock1[_profile.PartyCountOffset] = (byte)(PartyCount + 1);
            CommitPartySection();
            CommitStorageRange(RecordOffset(fromBox, fromSlot), _profile.BoxMonSize);
            return;
        }

        var targetOffset = ResolveBoxSlot(toBox, toSlot) ?? throw new InvalidOperationException("The destination slot does not exist.");
        var target = new ExpansionMon(_storage, targetOffset, false, _tables, _profile.Codec);
        // A party Pokémon dropped on an occupied box slot swaps with it: the boxed
        // Pokémon enters the party, which needs the same stats proof as a withdrawal.
        if (fromBox == -1 && !target.IsEmpty)
            RequireWithdrawProof(toBox, -1);
        if (target.IsEmpty)
        {
            target.CopyFrom(source);
            target.Commit();
            source.Clear();
            source.Commit();
            if (fromBox == -1)
                CompactPartyFrom(fromSlot);
        }
        else
        {
            var sourceCopy = Clone(source);
            var targetCopy = Clone(target);
            target.CopyFrom(sourceCopy);
            target.Commit();
            source.CopyFrom(targetCopy);
            source.Commit();
        }
        if (fromBox == -1)
            CommitPartySection();
        else
            CommitStorageRange(RecordOffset(fromBox, fromSlot), _profile.BoxMonSize);
        CommitStorageRange(targetOffset, _profile.BoxMonSize);
    }

    public void ReleaseSlot(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (box == -1)
        {
            if ((uint)slot >= (uint)PartyCount)
                return;
            CompactPartyFrom(slot);
            CommitPartySection();
            return;
        }
        var mon = TryMon(box, slot);
        if (mon is null) return;
        mon.Clear();
        mon.Commit();
        CommitStorageRange(RecordOffset(box, slot), _profile.BoxMonSize);
    }

    private void MoveWithinParty(int fromSlot, int toSlot)
    {
        var count = PartyCount;
        if ((uint)fromSlot >= (uint)count)
            throw new InvalidOperationException("That party slot is empty.");
        if (toSlot < 0 || toSlot >= count)
        {
            if (fromSlot == count - 1)
                return;
            var raw = PartyMon(fromSlot).RawRecord();
            for (var i = fromSlot; i < count - 1; i++)
            {
                _saveBlock1.AsSpan(_profile.PartyOffset + (i + 1) * _profile.PartyMonSize, _profile.PartyMonSize)
                    .CopyTo(_saveBlock1.AsSpan(_profile.PartyOffset + i * _profile.PartyMonSize, _profile.PartyMonSize));
            }
            raw.CopyTo(_saveBlock1.AsSpan(_profile.PartyOffset + (count - 1) * _profile.PartyMonSize));
            CommitPartySection();
            return;
        }
        if (toSlot == fromSlot)
            return;
        var source = PartyMon(fromSlot);
        var target = PartyMon(toSlot);
        var sourceCopy = Clone(source);
        var targetCopy = Clone(target);
        source.CopyFrom(targetCopy);
        source.Commit();
        target.CopyFrom(sourceCopy);
        target.Commit();
        CommitPartySection();
    }

    private void CompactPartyFrom(int removedSlot)
    {
        var count = PartyCount;
        if ((uint)removedSlot >= (uint)count)
            return;
        for (var i = removedSlot; i < count - 1; i++)
        {
            var from = _saveBlock1.AsSpan(_profile.PartyOffset + (i + 1) * _profile.PartyMonSize, _profile.PartyMonSize);
            from.CopyTo(_saveBlock1.AsSpan(_profile.PartyOffset + i * _profile.PartyMonSize, _profile.PartyMonSize));
        }
        _saveBlock1.AsSpan(_profile.PartyOffset + (count - 1) * _profile.PartyMonSize, _profile.PartyMonSize).Clear();
        _saveBlock1[_profile.PartyCountOffset] = (byte)(count - 1);
    }

    public void ClearBox(int box)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var first = ResolveBoxSlot(box, 0) ?? throw new InvalidOperationException("That box does not exist.");
        _storage.AsSpan(first, _profile.BoxSlotCount * _profile.BoxMonSize).Clear();
        CommitStorageRange(first, _profile.BoxSlotCount * _profile.BoxMonSize);
    }

    private ExpansionMon Clone(ExpansionMon mon)
    {
        var raw = mon.RawRecord();
        var buffer = new byte[raw.Length];
        raw.CopyTo(buffer, 0);
        return new ExpansionMon(buffer, 0, mon.IsParty, _tables, _profile.Codec);
    }

    // ── Trainer / boxes / currency ──

    private int Section0 => _sections[0];

    private uint SecurityKey => BinaryPrimitives.ReadUInt32LittleEndian(
        _data.AsSpan(Section0 + _profile.EncryptionKeyOffset));

    public TrainerInfo GetTrainer()
    {
        var name = StringConverter3.GetString(_data.AsSpan(Section0, 8), jp: false);
        var gender = _data[Section0 + 0x08];
        var tid = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Section0 + 0x0A));
        var sid = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Section0 + 0x0C));
        var money = BinaryPrimitives.ReadUInt32LittleEndian(_saveBlock1.AsSpan(_profile.MoneyOffset)) ^ SecurityKey;
        return new TrainerInfo(name.Length > 0 ? name : "PKForge", tid, sid, money, gender);
    }

    public void SetTrainer(TrainerInfo trainer)
    {
        if (trainer.Name is { Length: > 0 } typedName)
            trainer = trainer with { Name = NameCharset.FitGen3(typedName, 0, NameField.TrainerName) };
        var name = _data.AsSpan(Section0, 8);
        name.Fill(0xFF);
        StringConverter3.SetString(name, trainer.Name, 8, jp: false);
        _data[Section0 + 0x08] = (byte)trainer.Gender;
        BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(Section0 + 0x0A), (ushort)trainer.TID);
        BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(Section0 + 0x0C), (ushort)trainer.SID);
        BinaryPrimitives.WriteUInt32LittleEndian(_saveBlock1.AsSpan(_profile.MoneyOffset), trainer.Money ^ SecurityKey);
        CommitSection(0);
        CommitPartySection();
    }

    /// <summary>Play time as "h:mm:ss", the form every other save shows on the shelf.</summary>
    internal string PlayTimeText() =>
        $"{BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Section0 + 0x0E))}:{_data[Section0 + 0x10]:00}:{_data[Section0 + 0x11]:00}";

    public TrainerStats GetTrainerStats()
    {
        var hours = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Section0 + 0x0E));
        var minutes = _data[Section0 + 0x10];
        var coins = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(_saveBlock1.AsSpan(_profile.CoinsOffset)) ^ (ushort)SecurityKey);
        return new TrainerStats(true, hours, minutes, ushort.MaxValue, false, 0, 0, true, coins, 9999);
    }

    public void SetTrainerStats(TrainerStatsEdit edit)
    {
        var changedSection0 = false;
        if (edit.PlayTimeHours is { } hours)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(Section0 + 0x0E), (ushort)Math.Clamp(hours, 0, ushort.MaxValue));
            changedSection0 = true;
        }
        if (edit.PlayTimeMinutes is { } minutes)
        {
            _data[Section0 + 0x10] = (byte)Math.Clamp(minutes, 0, 59);
            changedSection0 = true;
        }
        if (changedSection0)
            CommitSection(0);
        if (edit.Coins is { } coins)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                _saveBlock1.AsSpan(_profile.CoinsOffset),
                (ushort)(Math.Clamp(coins, 0, 9999) ^ (ushort)SecurityKey));
            CommitPartySection();
        }
    }

    public string GetBoxName(int box)
    {
        if ((uint)box >= (uint)_profile.BoxCount)
            return string.Empty;
        var offset = _profile.BoxNamesOffset + box * 9;
        if (offset + 9 > _storage.Length)
            return $"BOX{box + 1}";
        var name = StringConverter3.GetString(_storage.AsSpan(offset, 9), jp: false).Trim();
        return name.Length > 0 ? name : $"BOX{box + 1}";
    }

    // ── Metadata surfaces ──

    private SaveSnapshot BuildSnapshot(string? displayName)
    {
        var slots = new List<SlotSummary>(_profile.BoxCount * _profile.BoxSlotCount + 6);
        for (var box = 0; box < _profile.BoxCount; box++)
        for (var slot = 0; slot < _profile.BoxSlotCount; slot++)
            slots.Add(Summary(box, slot, TryMon(box, slot)));
        for (var slot = 0; slot < 6; slot++)
            slots.Add(Summary(-1, slot, TryMon(-1, slot)));
        return new SaveSnapshot(_profile.SnapshotTag, 3, _originalBytes.ToArray(), slots, displayName);
    }

    private SlotSummary Summary(int box, int slot, ExpansionMon? mon)
    {
        if (mon is null || mon.IsEmpty)
            return new SlotSummary(box, slot, null, null, false, true);
        return new SlotSummary(box, slot, _tables.NationalIdOf(mon.Species), mon.Nickname, mon.IsShiny,
            true, 0, mon.IsEgg, mon.HeldItem);
    }

    public ReadOnlyMemory<byte> Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SramPadding.Restore(_data, _originalBytes);
    }

    public IReadOnlyList<int> GetAbilityChoices(int species, int form)
    {
        var raw = _tables.SpeciesFromNational(species);
        if (raw <= 0) raw = species;
        var (first, second, hidden) = _tables.Abilities(raw);
        var choices = new List<int>();
        if (first != 0) choices.Add(first);
        if (second != 0 && second != first) choices.Add(second);
        if (hidden != 0 && hidden != first && hidden != second) choices.Add(hidden);
        // A hack's own ability (past PKHeX's table) is shown by name, never offered by id.
        choices.RemoveAll(ability => ability > ExpansionData.LastNationalAbility);
        return choices.Count > 0 ? choices : [0];
    }

    public IReadOnlyList<string> GetItemNames() => HackNames.WithNoItem(_tables.ItemNames());

    public IReadOnlyList<string> GetFormChoices(int species) => [""];

    public IReadOnlyList<int> GetSpeciesTypes(int species)
    {
        var raw = _tables.SpeciesFromNational(species);
        if (raw <= 0) raw = species;
        return _tables.Types(raw);
    }

    public BaseStats GetBaseStats(int species)
    {
        var raw = _tables.SpeciesFromNational(species);
        if (raw <= 0) raw = species;
        return _tables.BaseStats(raw);
    }

    public string GetShowdownText(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return string.Empty;
        var text = $"{_tables.SpeciesName(mon.Species)}\nLevel: {mon.Level}\n{NatureName(mon.Nature)} Nature\nAbility: {_tables.AbilityName(mon.ActiveAbility)}";
        if (mon.HeldItem != 0)
            text += $"\nItem: {_tables.ItemName(mon.HeldItem)}";
        foreach (var move in mon.Moves)
            if (move != 0)
                text += $"\n- {_tables.MoveName(move)}";
        return text;
    }

    public string ExportBoxShowdown(int box)
    {
        var parts = new List<string>();
        for (var slot = 0; slot < _profile.BoxSlotCount; slot++)
        {
            var text = GetShowdownText(box, slot);
            if (text.Length > 0)
                parts.Add(text);
        }
        return string.Join("\n\n", parts);
    }

    public RngInfo GetRngInfo(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return new RngInfo(0, null, 0, false, false, [0, 0, 0, 0, 0, 0], 0, 2);
        return new RngInfo(mon.Pid, null, mon.Nature, mon.IsShiny, false, mon.Ivs, mon.ActiveAbility, mon.Gender);
    }

    public TrainingCaps GetTrainingCaps() => new(31, 255);

    public bool RerollNatureKeepShiny(int box, int slot, int nature)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid || !mon.RerollPid(nature: nature))
            return false;
        mon.Commit();
        if (box == -1) CommitPartySection();
        else CommitStorageRange(RecordOffset(box, slot), _profile.BoxMonSize);
        return true;
    }

    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile = null)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return new GenerationOutcome(false, "That slot is empty.");
        var trainer = GetTrainer();
        var name = profile?.OriginalTrainer ?? trainer.Name;
        mon.Otid = profile is null
            ? (uint)((trainer.SID << 16) | (trainer.TID & 0xFFFF))
            : (uint)((profile.SID << 16) | (profile.TID & 0xFFFF));
        mon.OriginalTrainerName = name;
        mon.Commit();
        if (box == -1) CommitPartySection();
        else CommitStorageRange(RecordOffset(box, slot), _profile.BoxMonSize);
        return new GenerationOutcome(true, $"{mon.Nickname} now belongs to {name}.");
    }

    public IReadOnlyList<int> GetMissingSpecies() => [];
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];

    public DexEntryState GetDexEntry(int species) => new(false, false);
    public bool IsDexSpecies(int species) => false;
    public DexProgress GetDexProgress() => new(0, 0, 0);
    public void SetDexEntry(int species, bool seen, bool caught) =>
        throw NotYet("The Pokédex editor (the expansion's dex arrays are build-dependent and not proven for this save)");
    public void CompleteDex() => throw NotYet("The complete-dex action");

    // ── Unsupported bulk / bank surfaces ──

    public bool SupportsBoxTools => false;
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false) =>
        throw NotYet("Box sorting");
    public int PlaceLivingDex(byte[] compressedBundle) => throw NotYet("The living dex");
    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) =>
        throw NotYet("The batch editor");
    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) =>
        throw NotYet("The batch editor");
    public void SwapBoxes(int a, int b) => throw NotYet("Box swapping");
    public void DeleteBox(int box) => throw NotYet("Box management");

    public SlotExport ExportSlot(int box, int slot) => throw NotYet("Entity export");
    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format = null) => throw NotYet("Entity import");

    // ── Bag ──

    public IReadOnlyList<BagPouch> GetBag() => [];
    public IReadOnlyList<int> GetPouchLegalItems(string pouchName) => [];
    public int SetItemCount(string pouchName, int itemId, int count) =>
        throw NotYet("The bag editor (the expansion's pocket layout is not proven for this save)");

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

    public bool SupportsRTCRepair => false;
    public void RepairRTC() { }

    // ── Met / origin ──

    public MetInfo GetMetInfo(int box, int slot) => throw NotYet("Met/origin editing");
    public void ApplyMetEdit(int box, int slot, MetEdit edit) => throw NotYet("Met/origin editing");
    public IReadOnlyList<NamedChoice> GetLocationChoices(int box, int slot, bool egg) => [];
    public IReadOnlyList<NamedChoice> GetVersionChoices() => [];
    public IReadOnlyList<NamedChoice> GetLanguageChoices(int box, int slot) => [];

    // ── Potential: ability slot is stored; Tera/Hyper Training are refused ──

    public PotentialInfo GetPotential(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return new PotentialInfo(false, 0, "", "", false, false, [false, false, false, false, false, false],
                false, 0, [], false, [], false, [], []);
        var (first, second, hidden) = _tables.Abilities(mon.Species);
        var choices = new List<NamedChoice>();
        if (first != 0) choices.Add(new NamedChoice(0, _tables.AbilityName(first)));
        if (second != 0 && second != first) choices.Add(new NamedChoice(1, _tables.AbilityName(second)));
        if (hidden != 0 && hidden != first && hidden != second) choices.Add(new NamedChoice(2, _tables.AbilityName(hidden)));
        return new PotentialInfo(false, 0, "", "", false, false, [false, false, false, false, false, false],
            mon.KnowsAbilitySlotNames, mon.AbilitySlot, choices, false, [], false, [], []);
    }

    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit)
    {
        if (edit.TeraType is not null || edit.HyperTrained is not null || edit.Awakening is not null || edit.Ganbaru is not null)
            throw NotYet("Tera type, Hyper Training and awakening");
        if (edit.AbilitySlot is not { } slotValue)
            return;
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || slotValue == mon.AbilitySlot || !mon.SetAbilitySlot(slotValue))
            return;
        mon.Commit();
        if (box == -1) CommitPartySection();
        else CommitStorageRange(RecordOffset(box, slot), _profile.BoxMonSize);
    }

    public IReadOnlyList<NamedChoice> GetTeraTypeChoices() => [];

    public MoveDetails GetMoveDetails(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return new MoveDetails([], false, []);
        var moves = mon.Moves;
        var pp = mon.MovePp;
        var ups = mon.MovePpUps;
        var slots = new List<MoveSlotDetail>(4);
        for (var i = 0; i < 4; i++)
        {
            var basePp = _tables.MovePp(moves[i]);
            var maxPp = basePp * (5 + ups[i]) / 5;
            slots.Add(new MoveSlotDetail(_tables.MoveToNational(moves[i]), pp[i], maxPp, ups[i]));
        }
        return new MoveDetails(slots, false, []);
    }

    public void ApplyMoveDetails(int box, int slot, MoveDetailsEdit edit)
    {
        if (edit.RelearnMoves is not null)
            throw NotYet("Relearn-move editing");
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return;
        var before = mon.RawRecord();
        if (edit.PP is { Count: 4 } pp)
            for (var i = 0; i < 4; i++)
                mon.SetPp(i, pp[i]);
        if (edit.PPUps is { Count: 4 } ups && !ups.SequenceEqual(mon.MovePpUps))
            mon.SetPpUps(ups);
        mon.Commit();
        if (mon.RawRecord().AsSpan().SequenceEqual(before))
            return;
        if (box == -1) CommitPartySection();
        else CommitStorageRange(RecordOffset(box, slot), _profile.BoxMonSize);
    }

    public MoveShopInfo GetMoveShop(int box, int slot) => new(false, []);
    public void ApplyMoveShopEdit(int box, int slot, MoveShopEdit edit) { }

    public CosmeticInfo GetCosmetics(int box, int slot) => new(
        [], [], false, 0, 0, false, 0, false, 0, 0, false, 0, 0, false, false,
        false, 0, false, false, false, false, 0);
    public void ApplyCosmeticEdit(int box, int slot, CosmeticEdit edit) => throw NotYet("Cosmetic editing");

    public PokerusInfo GetPokerus(int box, int slot) => new(false, PokerusStatus.Susceptible, 0, 0);
    public void SetPokerus(int box, int slot, PokerusStatus status) => throw NotYet("Pokérus editing");
    public IReadOnlyList<RibbonEntry> GetRibbons(int box, int slot) => [];
    public void SetRibbon(int box, int slot, string id, int value) => throw NotYet("Ribbon editing");
    public AffixedRibbonInfo GetAffixedRibbon(int box, int slot) => new(false, -1, string.Empty, []);
    public void SetAffixedRibbon(int box, int slot, int ribbonIndex) => throw NotYet("Title ribbon editing");
    public IReadOnlyDictionary<string, int> GetObtainableRibbonMaxima(int box, int slot) =>
        new Dictionary<string, int>();
    public int AwardAllObtainableRibbons(int box, int slot) => 0;

    public bool SupportsLegalityAnalysis => false;

    /// <summary>Abilities are stored as a slot, never as a free id: HaX cannot pick an arbitrary one.</summary>

    public bool StoresAnyAbility => false;

    public bool StoresMove(int move) => move == 0 || (_tables.MoveFromNational(move) is > 0 and var raw && _tables.IsKnownMove(raw));

    public bool SupportsCompassSettings => false;
    public IReadOnlyList<CompassSetting> GetCompassSettings() => [];
    public bool SetCompassSetting(string id, int choiceIndex) => false;

    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) =>
        throw NotYet("Encounter cards");

    public void Dispose() => _disposed = true;

    private static NotSupportedException NotYet(string what) =>
        new($"{what} is not available for pokeemerald-expansion saves.");

    private static string NatureName(int nature) => nature switch
    {
        0 => "Hardy", 1 => "Lonely", 2 => "Brave", 3 => "Adamant", 4 => "Naughty",
        5 => "Bold", 6 => "Docile", 7 => "Relaxed", 8 => "Impish", 9 => "Lax",
        10 => "Timid", 11 => "Hasty", 12 => "Serious", 13 => "Jolly", 14 => "Naive",
        15 => "Modest", 16 => "Mild", 17 => "Quiet", 18 => "Bashful", 19 => "Rash",
        20 => "Calm", 21 => "Gentle", 22 => "Sassy", 23 => "Careful", _ => "Quirky",
    };
}
