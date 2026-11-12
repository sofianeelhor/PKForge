using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;
using PKForge.Engine.RadicalRed;

namespace PKForge.Engine.EliteRedux;

/// <summary>
/// A Pokémon Elite Redux save as a PKForge session (see <see cref="EliteReduxFormat"/> for the
/// envelope and <see cref="EliteReduxMon"/> for the Pokémon record). Reads the trainer, play
/// time, money, party, the 26 boxes and their names, and the bag (one ownership bit per item).
///
/// Writes are surgical: a change is copied into its own sector(s), which are re-checksummed
/// in place; every other byte, footer and the slot's rotation stay as found. What is written
/// is only what the source proves: the Pokémon fields listed on <see cref="EliteReduxMon"/>,
/// the trainer card, play time, money, coins and bag bits. A party member's stats are stored,
/// so an edit that changes them (species, level, nature, EVs, Speed IV) recomputes them with
/// the game's own formula, and is refused when that formula does not reproduce the stats the
/// game stored (another build's base stats, or a difficulty with zero IVs). A box Pokémon is
/// never moved into the party (the game builds the party record itself on withdrawal).
/// A save written by another build than the pinned one opens read-only.
/// </summary>
internal sealed class EliteReduxEngineSession : ISaveEngineSession
{
    public const string GameName = "Elite Redux";
    private const string SnapshotTag = "ELITEREDUX";
    private const int MaxMoney = 9_999_999;
    private const int MaxCoins = 9999;

    private readonly byte[] _originalBytes;
    private readonly byte[] _data;
    private readonly int[] _sections;
    private readonly byte[] _saveBlock2;
    private readonly byte[] _saveBlock1;
    private readonly byte[] _storage;
    private readonly EliteReduxData _tables;
    private readonly string? _displayName;
    private bool _disposed;

    public EliteReduxEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _originalBytes = bytes.ToArray();
        _data = SramPadding.Trim(RetroArchSaveContainer.Decode(bytes.Span));
        if (_data.Length < EliteReduxFormat.FileSize || !EliteReduxFormat.LooksLike(_data))
            throw new InvalidDataException("These bytes are not a Pokémon Elite Redux save.");
        _sections = EliteReduxFormat.Sections(_data)!;
        _tables = EliteReduxData.Instance;
        _saveBlock2 = EliteReduxFormat.ReadBlock(_data, _sections, EliteReduxFormat.SaveBlock2First, EliteReduxFormat.SaveBlock2Sections);
        _saveBlock1 = EliteReduxFormat.ReadBlock(_data, _sections, EliteReduxFormat.SaveBlock1First, EliteReduxFormat.SaveBlock1Sections);
        _storage = EliteReduxFormat.ReadBlock(_data, _sections, EliteReduxFormat.StorageFirst, EliteReduxFormat.StorageSections);
        _displayName = displayName;
    }

    /// <summary>The build that last wrote the save (VAR_SAVE_VERSION).</summary>
    internal int SaveVersion => BinaryPrimitives.ReadUInt16LittleEndian(_saveBlock1.AsSpan(EliteReduxFormat.SaveVersionOffset));

    /// <summary>Why this save opens read-only, or null when it is the pinned build.</summary>
    internal string? ReadOnlyReason => SaveVersion == EliteReduxFormat.PinnedVersion
        ? null
        : $"this Elite Redux save was written by game version {SaveVersion}, and PKForge's Elite Redux tables and offsets are proven for version {EliteReduxFormat.PinnedVersion} only. It opens read-only.";

    public SaveSnapshot Snapshot => BuildSnapshot();
    public int Generation => 3;
    public IReadOnlyList<string> GameNames => [GameName];
    public int MaxSpeciesId => 1025;
    public int BoxCount => EliteReduxFormat.BoxCount;
    public int BoxSlotCount => EliteReduxFormat.BoxSlots;
    public bool SupportsBoxTools => false;
    public bool SupportsLegalityAnalysis => false;
    /// <summary>Abilities are stored as a slot of the species, never as a free id.</summary>
    public bool StoresAnyAbility => false;
    public bool StoresMove(int move) => move == 0 || (move > 0 && _tables.MoveFromNational(move) is > 0 and < 2048);

    private int PartyCount => Math.Min((int)_saveBlock1[EliteReduxFormat.PartyCountOffset], 6);

    // ── Slot plumbing ──

    private static int PartyRecord(int slot) => EliteReduxFormat.PartyOffset + slot * EliteReduxMon.PartySize;
    private static int BoxRecord(int box, int slot) =>
        EliteReduxFormat.BoxesOffset + (box * EliteReduxFormat.BoxSlots + slot) * EliteReduxMon.BoxSize;

    private static bool InBox(int box, int slot) =>
        (uint)box < EliteReduxFormat.BoxCount && (uint)slot < EliteReduxFormat.BoxSlots;

    private EliteReduxMon PartyMon(int slot) => new(_saveBlock1, PartyRecord(slot), true);
    private EliteReduxMon BoxMon(int box, int slot) => new(_storage, BoxRecord(box, slot), false);

    /// <summary>The Pokémon at a slot (box -1 is the party), or null for a slot that does not hold one.</summary>
    private EliteReduxMon? TryMon(int box, int slot)
    {
        if (box == -1)
            return (uint)slot < (uint)PartyCount ? PartyMon(slot) : null;
        return InBox(box, slot) ? BoxMon(box, slot) : null;
    }

    private void CommitSaveBlock2(int offset, int length) =>
        EliteReduxFormat.WriteBlockRange(_data, _sections, EliteReduxFormat.SaveBlock2First, _saveBlock2, offset, length);

    private void CommitSaveBlock1(int offset, int length) =>
        EliteReduxFormat.WriteBlockRange(_data, _sections, EliteReduxFormat.SaveBlock1First, _saveBlock1, offset, length);

    private void CommitStorage(int offset, int length) =>
        EliteReduxFormat.WriteBlockRange(_data, _sections, EliteReduxFormat.StorageFirst, _storage, offset, length);

    private void CommitParty() =>
        CommitSaveBlock1(EliteReduxFormat.PartyCountOffset, EliteReduxFormat.PartyOffset - EliteReduxFormat.PartyCountOffset + 6 * EliteReduxMon.PartySize);

    private void Commit(int box, int slot)
    {
        if (box == -1) CommitParty();
        else CommitStorage(BoxRecord(box, slot), EliteReduxMon.BoxSize);
    }

    private void RequireWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReadOnlyReason is { } reason)
            throw new InvalidOperationException(char.ToUpperInvariant(reason[0]) + reason[1..]);
    }

    /// <summary>Raw bytes of every slot (party first, then the PC) for the write-safety diff.</summary>
    internal IEnumerable<SlotImage> SlotImages()
    {
        for (var box = -1; box < EliteReduxFormat.BoxCount; box++)
        for (var slot = 0; slot < (box == -1 ? 6 : EliteReduxFormat.BoxSlots); slot++)
        {
            var mon = TryMon(box, slot);
            yield return mon is null
                ? new SlotImage(new SlotRef(box, slot), [], Empty: true, Valid: true)
                : new SlotImage(new SlotRef(box, slot), mon.Raw(), mon.IsEmpty, mon.IsEmpty || mon.LooksValid(_tables));
        }
    }

    // ── Reading ──

    /// <summary>National species, PKHeX form and sprite traits; a hack-only species reads as 0.</summary>
    private (int National, int Form, SpriteTraits Traits) LookOf(EliteReduxMon mon)
    {
        var national = _tables.NationalIdOf(mon.Species);
        var (form, traits) = RomhackForms.Resolve(national, _tables.SpeciesName(mon.Species));
        return (national, form, traits with { Female = _tables.GenderOf(mon.Pid, mon.Species) == 1 });
    }

    private int LevelOf(EliteReduxMon mon) => mon.IsParty ? mon.StoredLevel : mon.Level;

    private string NicknameOf(EliteReduxMon mon) =>
        mon.Nickname is { Length: > 0 } nickname ? nickname : _tables.SpeciesName(mon.Species);

    public EntityDetail ReadEntity(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return Empty(box, slot);
        var look = LookOf(mon);
        var moves = mon.Moves;
        return new EntityDetail(
            box, slot, false,
            look.National,
            _tables.SpeciesName(mon.Species),
            look.Form,
            NicknameOf(mon),
            LevelOf(mon),
            mon.Nature,
            _tables.AbilityToNational(_tables.Ability(mon.Species, mon.AbilitySlot)),
            mon.HeldItem,
            _tables.MoveToNational(moves[0]), _tables.MoveToNational(moves[1]),
            _tables.MoveToNational(moves[2]), _tables.MoveToNational(moves[3]),
            mon.Ivs, mon.Evs,
            mon.IsShiny,
            _tables.BallToNational(mon.Ball),
            mon.OriginalTrainerName,
            _tables.Types(mon.Species),
            _tables.GenderOf(mon.Pid, mon.Species),
            mon.Friendship,
            mon.StoredStats ?? ComputeStats(mon.Species, mon.Level, mon.Nature, mon.Evs, mon.SpeedDown),
            mon.IsParty ? mon.CurrentHp : 0,
            (int)mon.Status,
            look.Traits,
            // The hack's own abilities and moves have no PKHeX id: shown by their Elite Redux name.
            AbilityName: UnmappedAbility(mon),
            MoveNames: HackNames.UnmappedMoves(moves, _tables.MoveToNational, _tables.MoveName));
    }

    private string? UnmappedAbility(EliteReduxMon mon)
    {
        var ability = _tables.Ability(mon.Species, mon.AbilitySlot);
        return ability != 0 && _tables.AbilityToNational(ability) == 0 ? _tables.AbilityName(ability) : null;
    }

    private static EntityDetail Empty(int box, int slot) =>
        new(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0,
            [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);

    // ── Stats (src/pokemon.c CalculateMonStatsMaster) ──

    /// <summary>The game's stat formula with its fixed IVs (31, Speed 0 under speedDown) and the
    /// nature table, in the app's order (HP, Atk, Def, SpA, SpD, Spe).</summary>
    internal int[] ComputeStats(int species, int level, int nature, IReadOnlyList<int> evs, bool speedDown)
    {
        var b = _tables.BaseStats(species);
        var hp = _tables.NationalIdOf(species) == (int)Species.Shedinja
            ? 1
            : ((2 * b[0] + 31 + evs[0] / 4) * level / 100) + level + 10;
        int Stat(int index, int iv) => (2 * b[index] + iv + evs[index] / 4) * level / 100 + 5;
        // gNatureStatTable order: Atk, Def, Spe, SpA, SpD (nature / 5 raises, nature % 5 lowers).
        int[] raw = [Stat(1, 31), Stat(2, 31), Stat(5, speedDown ? 0 : 31), Stat(3, 31), Stat(4, 31)];
        var up = nature / 5;
        var down = nature % 5;
        for (var i = 0; i < 5 && up != down; i++)
        {
            if (i == up) raw[i] = raw[i] * 110 / 100;
            else if (i == down) raw[i] = raw[i] * 90 / 100;
        }
        return [hp, raw[0], raw[1], raw[3], raw[4], raw[2]];
    }

    /// <summary>True when the game's formula, fed this record and PKForge's tables, gives back
    /// the level and stats the game stored: the proof a stat-changing edit needs.</summary>
    private bool StatsProven(EliteReduxMon mon) =>
        mon.IsParty && mon.StoredLevel == mon.Level && mon.StoredStats is { } stored
        && stored.SequenceEqual(ComputeStats(mon.Species, mon.Level, mon.Nature, mon.Evs, mon.SpeedDown));

    /// <summary>The game's recompute after a change: level, stats, and the HP difference.</summary>
    private void RecomputeParty(EliteReduxMon mon)
    {
        var oldMax = mon.StoredStats![0];
        var current = mon.CurrentHp;
        var level = mon.Level;
        var stats = ComputeStats(mon.Species, level, mon.Nature, mon.Evs, mon.SpeedDown);
        mon.StoredLevel = level;
        mon.SetStoredStats(stats);
        if (_tables.NationalIdOf(mon.Species) == (int)Species.Shedinja)
        {
            if (current != 0 || oldMax == 0) mon.CurrentHp = 1;
            return;
        }
        if (current == 0 && oldMax == 0) mon.CurrentHp = stats[0];
        else if (current != 0) mon.CurrentHp = Math.Max(1, current + stats[0] - oldMax);
    }

    /// <summary>CalculatePPWithBonus with the three PP Ups every Pokémon has.</summary>
    private int MaxPp(int move) => _tables.MovePp(move) + _tables.MovePp(move) * 60 / 100;

    // ── Editing ──

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        edit = NameCharset.FitGen3Edit(edit); // charset first: a name the game cannot hold is refused, not emptied
        RequireWritable();
        ArgumentNullException.ThrowIfNull(edit);
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            throw new InvalidOperationException("Cannot edit an empty slot.");

        // Every requested change is resolved and validated before a byte moves: a refused edit changes nothing.
        var look = LookOf(mon);
        var species = mon.Species;
        if (edit.Species is > 0 and var national && national != look.National)
        {
            species = _tables.SpeciesFromNational(national);
            if (species <= 0)
                throw new NotSupportedException($"Elite Redux has no {SpeciesName(national)}.");
        }
        var nickname = edit.Nickname is { Length: > 0 } name && name != NicknameOf(mon) ? name : null;
        if (nickname is { Length: > 12 })
            throw new ArgumentException("An Elite Redux nickname holds at most 12 characters.");
        var level = edit.Level is { } wantedLevel && Math.Clamp(wantedLevel, 1, 100) != LevelOf(mon) ? Math.Clamp(wantedLevel, 1, 100) : (int?)null;
        var nature = edit.Nature is { } wantedNature && wantedNature != mon.Nature ? wantedNature : (int?)null;
        if (nature is < 0 or > 24)
            throw new ArgumentOutOfRangeException(nameof(edit), "A nature is 0-24.");

        var abilitySlot = mon.AbilitySlot;
        var currentAbility = _tables.AbilityToNational(_tables.Ability(mon.Species, mon.AbilitySlot));
        if (edit.Ability is { } ability && ability != currentAbility)
        {
            var found = Enumerable.Range(0, 3).Where(s => _tables.AbilityToNational(_tables.Ability(species, s)) == ability).Cast<int?>().FirstOrDefault();
            abilitySlot = found ?? throw new NotSupportedException("That ability is not one of this Pokémon's three ability slots.");
        }
        else if (species != mon.Species && abilitySlot > 2)
            abilitySlot = 0;

        var item = edit.HeldItem is { } wantedItem && wantedItem != mon.HeldItem ? wantedItem : (int?)null;
        if (item is { } newItem && (newItem is < 0 or > 1023 || !_tables.IsKnownItem(newItem)))
            throw new NotSupportedException($"Item {newItem} is not an Elite Redux item.");

        int?[] wantedMoves = [edit.Move1, edit.Move2, edit.Move3, edit.Move4];
        var moves = mon.Moves;
        var movesChanged = new bool[4];
        for (var i = 0; i < 4; i++)
        {
            if (wantedMoves[i] is not { } move || move == _tables.MoveToNational(moves[i]))
                continue;
            var rom = _tables.MoveFromNational(move);
            if (move != 0 && rom is <= 0 or >= 2048)
                throw new NotSupportedException($"Elite Redux has no move {move}.");
            moves[i] = rom;
            movesChanged[i] = true;
        }

        bool? speedDown = null;
        if (edit.IVs is { Count: 6 } ivs && !ivs.SequenceEqual(mon.Ivs))
        {
            if (ivs.Take(5).Any(iv => iv != 31) || ivs[5] is not (0 or 31))
                throw new NotSupportedException("Elite Redux keeps no IVs: every IV is 31, and only Speed can be lowered to 0.");
            speedDown = ivs[5] == 0;
        }
        var evs = edit.EVs is { Count: 6 } wantedEvs && !wantedEvs.SequenceEqual(mon.Evs) ? wantedEvs : null;
        if (evs is not null && evs.Any(ev => ev is < 0 or > 255))
            throw new ArgumentOutOfRangeException(nameof(edit), "An EV is 0-255.");

        var ballItem = mon.Ball;
        if (edit.Ball is { } ball && ball != _tables.BallToNational(mon.Ball))
            ballItem = _tables.BallFromNational(ball) is > 0 and var id ? id : throw new NotSupportedException("Elite Redux has no such ball.");
        var trainerName = edit.OriginalTrainer is { Length: > 0 } ot && ot != mon.OriginalTrainerName ? ot : null;
        if (trainerName is { Length: > 7 })
            throw new ArgumentException("An original trainer name holds at most 7 characters.");
        if (edit.Gender is { } gender && gender != _tables.GenderOf(mon.Pid, mon.Species) && _tables.GenderRatio(species) is not (0 or 254 or 255))
            throw new NotSupportedException("A Pokémon's gender comes from its personality value in Elite Redux; PKForge does not reroll it.");
        var friendship = edit.Friendship is { } wantedFriendship && wantedFriendship != mon.Friendship ? Math.Clamp(wantedFriendship, 0, 255) : (int?)null;

        var statsChange = species != mon.Species || level is not null || nature is not null || evs is not null || speedDown is not null;
        if (mon.IsParty && statsChange && !StatsProven(mon))
            throw new InvalidOperationException(
                "This party Pokémon's stored stats do not match Elite Redux's stat formula with PKForge's tables (another build's base stats, " +
                "or a difficulty without IVs), so its species, level, nature and EVs are not changed here. Deposit it in a box first.");

        // Apply.
        var before = mon.Raw();
        if (species != mon.Species) mon.Species = species;
        if (nickname is not null) mon.Nickname = nickname;
        if (level is { } newLevel) mon.Experience = EliteReduxMon.ExperienceAt(newLevel);
        if (nature is { } newNature) mon.Nature = newNature;
        if (abilitySlot != mon.AbilitySlot) mon.AbilitySlot = abilitySlot;
        if (item is { } heldItem) mon.HeldItem = heldItem;
        for (var i = 0; i < 4; i++)
        {
            if (!movesChanged[i]) continue;
            mon.SetMove(i, moves[i]);
            mon.SetPp(i, moves[i] == 0 ? 0 : MaxPp(moves[i]));
        }
        if (speedDown is { } down) mon.SetSpeedDown(down);
        if (evs is not null) mon.SetEvs(evs);
        if (edit.IsShiny is { } shiny && shiny != mon.IsShiny) mon.ShinyKind = shiny ? 1 : 0;
        if (ballItem != mon.Ball) mon.Ball = ballItem;
        if (trainerName is not null) mon.OriginalTrainerName = trainerName;
        if (friendship is { } newFriendship) mon.Friendship = newFriendship;
        if (mon.IsParty && statsChange) RecomputeParty(mon);

        if (!mon.Raw().AsSpan().SequenceEqual(before))
            Commit(box, slot);
    }

    private static string SpeciesName(int national)
    {
        var names = GameInfo.GetStrings("en").specieslist;
        return (uint)national < (uint)names.Length ? names[national] : $"species {national}";
    }

    public bool RerollNatureKeepShiny(int box, int slot, int nature)
    {
        // The nature is its own field in Elite Redux: no personality reroll is involved.
        if (TryMon(box, slot) is not { } mon || mon.IsEmpty || nature is < 0 or > 24)
            return false;
        ApplyEdit(box, slot, new EntityEdit(Nature: nature));
        return true;
    }

    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile = null)
    {
        RequireWritable();
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return new GenerationOutcome(false, "That slot is empty.");
        var trainer = GetTrainer();
        var name = profile?.OriginalTrainer ?? trainer.Name;
        var before = mon.Raw();
        mon.OtId = profile is null
            ? (uint)((trainer.SID << 16) | (trainer.TID & 0xFFFF))
            : (uint)((profile.SID << 16) | (profile.TID & 0xFFFF));
        if (name != mon.OriginalTrainerName) mon.OriginalTrainerName = name;
        if (!mon.Raw().AsSpan().SequenceEqual(before))
            Commit(box, slot);
        return new GenerationOutcome(true, $"{NicknameOf(mon)} now belongs to {name}.");
    }

    // ── Moving, copying, releasing ──

    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        RequireWritable();
        if (fromBox == toBox && fromSlot == toSlot)
            return;
        var source = TryMon(fromBox, fromSlot);
        if (source is null || source.IsEmpty)
            throw new InvalidOperationException("The source slot is empty.");
        if (toBox == -1)
        {
            if (fromBox != -1)
                throw new NotSupportedException("Elite Redux builds a party Pokémon's stats itself when it is withdrawn: take it out of the box in the game.");
            MoveWithinParty(fromSlot, toSlot);
            return;
        }
        if (!InBox(toBox, toSlot))
            throw new InvalidOperationException("The destination slot does not exist.");
        var target = BoxMon(toBox, toSlot);
        if (fromBox == -1)
        {
            // A deposit, as the game does it: the party record's first 52 bytes are the box record.
            if (!target.IsEmpty)
                throw new InvalidOperationException("Deposit into an empty box slot.");
            if (PartyCount <= 1)
                throw new InvalidOperationException("The party cannot be left empty.");
            target.Load(source.Raw().AsSpan(0, EliteReduxMon.BoxSize));
            CompactPartyFrom(fromSlot);
            CommitParty();
            Commit(toBox, toSlot);
            return;
        }
        var moving = source.Raw();
        source.Load(target.Raw());
        target.Load(moving);
        Commit(fromBox, fromSlot);
        Commit(toBox, toSlot);
    }

    private void MoveWithinParty(int fromSlot, int toSlot)
    {
        var count = PartyCount;
        if ((uint)fromSlot >= (uint)count)
            throw new InvalidOperationException("That party slot is empty.");
        var target = toSlot < 0 || toSlot >= count ? count - 1 : toSlot;
        if (target == fromSlot)
            return;
        var source = PartyMon(fromSlot).Raw();
        PartyMon(fromSlot).Load(PartyMon(target).Raw());
        PartyMon(target).Load(source);
        CommitParty();
    }

    private void CompactPartyFrom(int removedSlot)
    {
        var count = PartyCount;
        for (var i = removedSlot; i < count - 1; i++)
            PartyMon(i).Load(PartyMon(i + 1).Raw());
        _saveBlock1.AsSpan(PartyRecord(count - 1), EliteReduxMon.PartySize).Clear();
        _saveBlock1[EliteReduxFormat.PartyCountOffset] = (byte)(count - 1);
    }

    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        RequireWritable();
        var source = TryMon(fromBox, fromSlot);
        if (source is null || source.IsEmpty || !source.LooksValid(_tables) || toBox == -1 || !InBox(toBox, toSlot))
            return false;
        var target = BoxMon(toBox, toSlot);
        if (!target.IsEmpty)
            return false;
        target.Load(source.Raw().AsSpan(0, EliteReduxMon.BoxSize));
        Commit(toBox, toSlot);
        return true;
    }

    public void ReleaseSlot(int box, int slot)
    {
        RequireWritable();
        if (TryMon(box, slot) is not { IsEmpty: false } mon)
            return;
        if (box == -1)
        {
            if (PartyCount <= 1)
                throw new InvalidOperationException("The party cannot be left empty.");
            CompactPartyFrom(slot);
            CommitParty();
            return;
        }
        _storage.AsSpan(BoxRecord(box, slot), EliteReduxMon.BoxSize).Clear();
        Commit(box, slot);
    }

    public void ClearBox(int box)
    {
        RequireWritable();
        if ((uint)box >= EliteReduxFormat.BoxCount)
            throw new InvalidOperationException("That box does not exist.");
        var first = BoxRecord(box, 0);
        var length = EliteReduxFormat.BoxSlots * EliteReduxMon.BoxSize;
        _storage.AsSpan(first, length).Clear();
        CommitStorage(first, length);
    }

    // ── Trainer, money, play time ──

    public TrainerInfo GetTrainer()
    {
        var name = StringConverter3.GetString(_saveBlock2.AsSpan(EliteReduxFormat.PlayerNameOffset, 8), jp: false);
        var tid = BinaryPrimitives.ReadUInt16LittleEndian(_saveBlock2.AsSpan(EliteReduxFormat.TrainerIdOffset));
        var sid = BinaryPrimitives.ReadUInt16LittleEndian(_saveBlock2.AsSpan(EliteReduxFormat.TrainerIdOffset + 2));
        // The game resets its encryption key to 0 on every map load (load_save.c), so money is stored plain.
        var money = BinaryPrimitives.ReadUInt32LittleEndian(_saveBlock1.AsSpan(EliteReduxFormat.MoneyOffset));
        return new TrainerInfo(name, tid, sid, money, _saveBlock2[EliteReduxFormat.PlayerGenderOffset]);
    }

    public void SetTrainer(TrainerInfo trainer)
    {
        if (trainer?.Name is { Length: > 0 } typedName)
            trainer = trainer with { Name = NameCharset.FitGen3(typedName, 0, NameField.TrainerName) };
        RequireWritable();
        ArgumentNullException.ThrowIfNull(trainer);
        var current = GetTrainer();
        if (trainer.Name is { Length: > 0 } name && name != current.Name)
        {
            if (name.Length > 7)
                throw new ArgumentException("A trainer name holds at most 7 characters.");
            var field = _saveBlock2.AsSpan(EliteReduxFormat.PlayerNameOffset, 8);
            field.Fill(0xFF);
            StringConverter3.SetString(field, name, 7, jp: false);
        }
        if (trainer.Gender is 0 or 1 && trainer.Gender != current.Gender)
            _saveBlock2[EliteReduxFormat.PlayerGenderOffset] = (byte)trainer.Gender;
        if (trainer.TID != current.TID)
            BinaryPrimitives.WriteUInt16LittleEndian(_saveBlock2.AsSpan(EliteReduxFormat.TrainerIdOffset), (ushort)trainer.TID);
        if (trainer.SID != current.SID)
            BinaryPrimitives.WriteUInt16LittleEndian(_saveBlock2.AsSpan(EliteReduxFormat.TrainerIdOffset + 2), (ushort)trainer.SID);
        CommitSaveBlock2(0, EliteReduxFormat.PlayTimeOffset);
        if (trainer.Money != current.Money)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_saveBlock1.AsSpan(EliteReduxFormat.MoneyOffset), Math.Min(trainer.Money, MaxMoney));
            CommitSaveBlock1(EliteReduxFormat.MoneyOffset, 4);
        }
    }

    internal string PlayTimeText() =>
        $"{BinaryPrimitives.ReadUInt16LittleEndian(_saveBlock2.AsSpan(EliteReduxFormat.PlayTimeOffset))}:{_saveBlock2[EliteReduxFormat.PlayTimeOffset + 2]:00}:{_saveBlock2[EliteReduxFormat.PlayTimeOffset + 3]:00}";

    private int Coins => BinaryPrimitives.ReadUInt16LittleEndian(_saveBlock1.AsSpan(EliteReduxFormat.CoinsOffset));

    public TrainerStats GetTrainerStats() => new(
        true, BinaryPrimitives.ReadUInt16LittleEndian(_saveBlock2.AsSpan(EliteReduxFormat.PlayTimeOffset)),
        _saveBlock2[EliteReduxFormat.PlayTimeOffset + 2], ushort.MaxValue,
        false, 0, 0,
        true, Coins, MaxCoins);

    public void SetTrainerStats(TrainerStatsEdit edit)
    {
        RequireWritable();
        ArgumentNullException.ThrowIfNull(edit);
        var time = _saveBlock2.AsSpan(EliteReduxFormat.PlayTimeOffset);
        var timeChanged = false;
        if (edit.PlayTimeHours is { } hours && hours != BinaryPrimitives.ReadUInt16LittleEndian(time))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(time, (ushort)Math.Clamp(hours, 0, ushort.MaxValue));
            timeChanged = true;
        }
        if (edit.PlayTimeMinutes is { } minutes && minutes != time[2])
        {
            time[2] = (byte)Math.Clamp(minutes, 0, 59);
            timeChanged = true;
        }
        if (timeChanged)
            CommitSaveBlock2(EliteReduxFormat.PlayTimeOffset, 3);
        if (edit.Coins is { } coins && coins != Coins)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_saveBlock1.AsSpan(EliteReduxFormat.CoinsOffset), (ushort)Math.Clamp(coins, 0, MaxCoins));
            CommitSaveBlock1(EliteReduxFormat.CoinsOffset, 2);
        }
    }

    public string GetBoxName(int box)
    {
        if ((uint)box >= EliteReduxFormat.BoxCount)
            return string.Empty;
        var name = StringConverter3.GetString(_storage.AsSpan(EliteReduxFormat.BoxNamesOffset + box * EliteReduxFormat.BoxNameLength, EliteReduxFormat.BoxNameLength), jp: false).Trim();
        return name.Length > 0 ? name : $"Box {box + 1}";
    }

    // ── Snapshot ──

    private SaveSnapshot BuildSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var slots = new List<SlotSummary>(EliteReduxFormat.BoxCount * EliteReduxFormat.BoxSlots + 6);
        for (var box = 0; box < EliteReduxFormat.BoxCount; box++)
        for (var slot = 0; slot < EliteReduxFormat.BoxSlots; slot++)
            slots.Add(Summary(box, slot, TryMon(box, slot)));
        for (var slot = 0; slot < 6; slot++)
            slots.Add(Summary(-1, slot, TryMon(-1, slot)));
        return new SaveSnapshot(SnapshotTag, 3, _originalBytes.ToArray(), slots, _displayName);
    }

    private SlotSummary Summary(int box, int slot, EliteReduxMon? mon)
    {
        if (mon is null || mon.IsEmpty || !mon.LooksValid(_tables))
            return new SlotSummary(box, slot, null, null, false, true);
        var (national, form, traits) = LookOf(mon);
        return new SlotSummary(box, slot, national, NicknameOf(mon), mon.IsShiny, true, form, mon.IsEgg, mon.HeldItem, traits);
    }

    public ReadOnlyMemory<byte> Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SramPadding.Restore(_data, _originalBytes);
    }

    // ── Tables ──

    private int RomSpecies(int species, int form = 0) => _tables.SpeciesFromNational(species, form);

    public IReadOnlyList<int> GetAbilityChoices(int species, int form)
    {
        var rom = RomSpecies(species, form);
        var choices = new List<int>();
        for (var slot = 0; slot < 3; slot++)
            if (_tables.AbilityToNational(_tables.Ability(rom, slot)) is > 0 and var national && !choices.Contains(national))
                choices.Add(national);
        return choices.Count > 0 ? choices : [0];
    }

    public IReadOnlyList<string> GetItemNames()
    {
        var names = new string[_tables.MaxItem + 1];
        for (var id = 0; id < names.Length; id++)
            names[id] = _tables.IsKnownItem(id) ? _tables.ItemName(id) : string.Empty;
        return HackNames.WithNoItem(names);
    }

    public IReadOnlyList<string> GetFormChoices(int species) => [""];
    public IReadOnlyList<int> GetSpeciesTypes(int species) => _tables.Types(RomSpecies(species));

    public BaseStats GetBaseStats(int species)
    {
        var stats = _tables.BaseStats(RomSpecies(species));
        return new BaseStats(stats[0], stats[1], stats[2], stats[3], stats[4], stats[5]);
    }

    public string GetShowdownText(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return string.Empty;
        var lines = new List<string>
        {
            NicknameOf(mon) == _tables.SpeciesName(mon.Species) ? _tables.SpeciesName(mon.Species) : $"{NicknameOf(mon)} ({_tables.SpeciesName(mon.Species)})",
        };
        if (mon.HeldItem != 0) lines[0] += $" @ {_tables.ItemName(mon.HeldItem)}";
        lines.Add($"Ability: {_tables.AbilityName(_tables.Ability(mon.Species, mon.AbilitySlot))}");
        var innates = _tables.Innates(mon.Species).Where(id => id != 0).Select(_tables.AbilityName).ToArray();
        if (innates.Length > 0) lines.Add($"Innates: {string.Join(" / ", innates)}");
        lines.Add($"Level: {LevelOf(mon)}");
        if (mon.IsShiny) lines.Add("Shiny: Yes");
        var evs = mon.Evs;
        string[] statNames = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];
        var evText = string.Join(" / ", Enumerable.Range(0, 6).Where(i => evs[i] > 0).Select(i => $"{evs[i]} {statNames[i]}"));
        if (evText.Length > 0) lines.Add($"EVs: {evText}");
        lines.Add($"{GameInfo.GetStrings("en").natures[mon.Nature % 25]} Nature");
        if (mon.SpeedDown) lines.Add("IVs: 0 Spe");
        foreach (var move in mon.Moves)
            if (move != 0)
                lines.Add($"- {_tables.MoveName(move)}");
        return string.Join("\n", lines);
    }

    public string ExportBoxShowdown(int box) =>
        string.Join("\n\n", Enumerable.Range(0, EliteReduxFormat.BoxSlots).Select(slot => GetShowdownText(box, slot)).Where(text => text.Length > 0));

    public RngInfo GetRngInfo(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return new RngInfo(0, null, 0, false, false, [0, 0, 0, 0, 0, 0], 0, 2);
        return new RngInfo(mon.Pid, null, mon.Nature, mon.IsShiny, false, mon.Ivs,
            _tables.AbilityToNational(_tables.Ability(mon.Species, mon.AbilitySlot)), _tables.GenderOf(mon.Pid, mon.Species));
    }

    public TrainingCaps GetTrainingCaps() => new(31, 255);

    // ── Bag: one ownership bit per item (item.c HasItem/SetItem); quantities do not exist ──

    private static readonly (string Pouch, string Pocket)[] Pouches =
    [
        ("Items", "ITEMS"), ("Medicine", "MEDICINE"), ("BattleItems", "BATTLE"), ("Balls", "POKE_BALLS"),
        ("TMHMs", "TM_HM"), ("Berries", "BERRIES"), ("MegaStones", "MEGA_STONES"), ("KeyItems", "KEY_ITEMS"),
    ];

    private bool BagReadable => SaveVersion >= EliteReduxFormat.ItemFlagsSinceVersion;

    private bool HasItem(int item) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(_saveBlock1.AsSpan(EliteReduxFormat.ItemFlagsOffset + item / 16 * 2)) & (1 << (item % 16))) != 0;

    public IReadOnlyList<BagPouch> GetBag()
    {
        if (!BagReadable)
            return [];
        return [.. Pouches.Select(pouch => new BagPouch(pouch.Pouch, [.. PocketItems(pouch.Pocket).Where(HasItem).Select(id => new BagItem(id, 1))]))];
    }

    private IEnumerable<int> PocketItems(string pocket) =>
        _tables.ItemIds.Where(id => id is > 0 and < EliteReduxFormat.ItemFlagWords * 16 && _tables.Pocket(id) == pocket).Order();

    public IReadOnlyList<int> GetPouchLegalItems(string pouchName) =>
        Array.Find(Pouches, pouch => pouch.Pouch.Equals(pouchName, StringComparison.OrdinalIgnoreCase)) is { Pocket: not null } found
            ? [.. PocketItems(found.Pocket)]
            : [];

    public int SetItemCount(string pouchName, int itemId, int count)
    {
        RequireWritable();
        if (!BagReadable || !GetPouchLegalItems(pouchName).Contains(itemId))
            throw new NotSupportedException("That item does not belong in this Elite Redux pocket.");
        var offset = EliteReduxFormat.ItemFlagsOffset + itemId / 16 * 2;
        var word = BinaryPrimitives.ReadUInt16LittleEndian(_saveBlock1.AsSpan(offset));
        var bit = (ushort)(1 << (itemId % 16));
        var updated = count > 0 ? (ushort)(word | bit) : (ushort)(word & ~bit);
        if (updated != word)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_saveBlock1.AsSpan(offset), updated);
            CommitSaveBlock1(offset, 2);
        }
        return count > 0 ? 1 : 0;
    }

    // ── Potential: the ability slot ──

    public PotentialInfo GetPotential(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return new PotentialInfo(false, 0, "", "", false, false, [false, false, false, false, false, false],
                false, 0, [], false, [], false, [], []);
        var slots = _tables.AbilitySlots(mon.Species);
        var choices = new List<NamedChoice>();
        for (var i = 0; i < 3; i++)
            if (slots[i] != 0 && (i == 0 || slots[i] != slots[i - 1]))
                choices.Add(new NamedChoice(i, _tables.AbilityName(slots[i])));
        return new PotentialInfo(false, 0, "", "", false, false, [false, false, false, false, false, false],
            true, mon.AbilitySlot, choices, false, [], false, [], []);
    }

    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.TeraType is not null || edit.HyperTrained is not null || edit.Awakening is not null || edit.Ganbaru is not null)
            throw NotYet("Tera type, Hyper Training and awakening");
        if (edit.AbilitySlot is not { } wanted)
            return;
        RequireWritable();
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || wanted is < 0 or > 2 || wanted == mon.AbilitySlot)
            return;
        mon.AbilitySlot = wanted;
        Commit(box, slot);
    }

    public IReadOnlyList<NamedChoice> GetTeraTypeChoices() => [];

    // ── Moves: PP is stored for the party, PP Ups are always three ──

    public MoveDetails GetMoveDetails(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return new MoveDetails([], false, []);
        var details = new List<MoveSlotDetail>(4);
        for (var i = 0; i < 4; i++)
        {
            var move = mon.Move(i);
            var max = move == 0 ? 0 : MaxPp(move);
            details.Add(new MoveSlotDetail(_tables.MoveToNational(move), mon.IsParty ? mon.Pp(i) : max, max, move == 0 ? 0 : 3));
        }
        return new MoveDetails(details, false, []);
    }

    public void ApplyMoveDetails(int box, int slot, MoveDetailsEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.RelearnMoves is not null)
            throw NotYet("Relearn moves");
        if (edit.PPUps is { Count: 4 } ups && TryMon(box, slot) is { IsEmpty: false } current
            && Enumerable.Range(0, 4).Any(i => ups[i] != (current.Move(i) == 0 ? 0 : 3)))
            throw new NotSupportedException("Elite Redux gives every move three PP Ups; they are not stored.");
        if (edit.PP is not { Count: 4 } pp)
            return;
        RequireWritable();
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.IsParty)
            return;
        var before = mon.Raw();
        for (var i = 0; i < 4; i++)
            mon.SetPp(i, mon.Move(i) == 0 ? 0 : Math.Clamp(pp[i], 0, MaxPp(mon.Move(i))));
        if (!mon.Raw().AsSpan().SequenceEqual(before))
            Commit(box, slot);
    }

    // ── Not available for Elite Redux ──

    public IReadOnlyList<int> GetMissingSpecies() => [];
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];
    public DexEntryState GetDexEntry(int species) => new(false, false);
    public bool IsDexSpecies(int species) => false;
    public DexProgress GetDexProgress() => new(0, 0, 0);
    public void SetDexEntry(int species, bool seen, bool caught) => throw NotYet("The Pokédex editor");
    public void CompleteDex() => throw NotYet("The complete-dex action");

    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false) => throw NotYet("Box sorting");
    public int PlaceLivingDex(byte[] compressedBundle) => throw NotYet("The living dex");
    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) => throw NotYet("The batch editor");
    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) => throw NotYet("The batch editor");
    public void SwapBoxes(int a, int b) => throw NotYet("Box swapping");
    public void DeleteBox(int box) => throw NotYet("Box management");
    public SlotExport ExportSlot(int box, int slot) => throw NotYet("Pokémon export");
    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format = null) => throw NotYet("Pokémon import");

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
    public void ApplyCosmeticEdit(int box, int slot, CosmeticEdit edit) => throw NotYet("Cosmetic editing");

    public PokerusInfo GetPokerus(int box, int slot) => new(false, PokerusStatus.Susceptible, 0, 0);
    public void SetPokerus(int box, int slot, PokerusStatus status) => throw NotYet("Pokérus (the game has none)");
    public IReadOnlyList<RibbonEntry> GetRibbons(int box, int slot) => [];
    public void SetRibbon(int box, int slot, string id, int value) => throw NotYet("Ribbons (the game has none)");
    public AffixedRibbonInfo GetAffixedRibbon(int box, int slot) => new(false, -1, string.Empty, []);
    public void SetAffixedRibbon(int box, int slot, int ribbonIndex) => throw NotYet("Title ribbons");
    public IReadOnlyDictionary<string, int> GetObtainableRibbonMaxima(int box, int slot) => new Dictionary<string, int>();
    public int AwardAllObtainableRibbons(int box, int slot) => 0;

    public bool SupportsCompassSettings => false;
    public IReadOnlyList<CompassSetting> GetCompassSettings() => [];
    public bool SetCompassSetting(string id, int choiceIndex) => false;

    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) =>
        throw NotYet("Encounter cards");

    public void Dispose() => _disposed = true;

    private static NotSupportedException NotYet(string what) => new($"{what} is not available for Elite Redux saves.");
}
