using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine.Exceeded;

/// <summary>
/// An Emerald Exceeded save as a PKForge session (see <see cref="ExceededFormat"/>). Its
/// species, move and item ids are pokeemerald-expansion's (the shared expansion pack), but
/// its Pokémon record is its own, so this session reads the records directly and edits only
/// what the real saves prove: nickname, original trainer name, held item, moves and their
/// PP, a boxed Pokémon's EXP (its level), the trainer's name, gender and ids, and moving,
/// copying and releasing Pokémon. Everything else (IVs, EVs, nature, ability, ball, species)
/// is decoded nowhere and written never; party stats come from the hack's own formula,
/// which is unknown, so nothing that would need them is offered (a party Pokémon's level,
/// a boxed Pokémon joining the party). Each commit rewrites only the live slot's touched
/// sectors, re-sealed over the 4084-byte window every sector of the save validates under.
/// </summary>
internal sealed class ExceededEngineSession : ISaveEngineSession
{
    /// <summary>Raw ids up to this one are the national dex in every Exceeded build; past it
    /// the older build numbers its species differently (Data/README.md).</summary>
    private const int SharedSpeciesRange = 905;

    /// <summary>Move ids below the expansion's Z-moves (851 on), where the builds' PP agree.</summary>
    internal const int MaxEditableMove = 850;

    private readonly byte[] _data;
    private readonly byte[] _originalBytes;
    private readonly int[] _sections;
    private readonly byte[] _storage;
    private readonly Expansion.ExpansionData _tables;
    private readonly string? _displayName;
    private readonly bool _modernSpecies;
    private bool _disposed;

    public ExceededEngineSession(ReadOnlyMemory<byte> bytes, string? displayName)
    {
        _tables = Expansion.ExpansionData.Instance;
        _originalBytes = bytes.ToArray();
        _data = SramPadding.Trim(RetroArchSaveContainer.Decode(bytes.Span));
        if (!ExceededFormat.Matches(_data))
            throw new InvalidDataException("These bytes are not an Emerald Exceeded save the engine can prove.");
        _sections = ExceededFormat.SectionOffsets(_data)!;
        _storage = ExceededFormat.ReadStorage(_data, _sections);
        _displayName = displayName;
        _modernSpecies = SpeciesTableAgrees();
    }

    public SaveSnapshot Snapshot => BuildSnapshot(_displayName);
    public int Generation => 3;
    public IReadOnlyList<string> GameNames => [ExceededFormat.GameName];
    public int MaxSpeciesId => _tables.MaxSpecies;
    public int BoxCount => ExceededFormat.BoxCount;
    public int BoxSlotCount => ExceededFormat.BoxSlotCount;

    private int PartySector => _sections[ExceededFormat.SaveBlock1Section];
    private int PartyCount => Math.Min((int)_data[PartySector + ExceededFormat.PartyCountOffset], 6);

    private int PartyRecordOffset(int slot) => PartySector + ExceededFormat.PartyOffset + slot * ExceededFormat.PartyMonSize;

    private static int BoxRecordOffset(int box, int slot) =>
        ExceededFormat.StorageBoxOffset + (box * ExceededFormat.BoxSlotCount + slot) * ExceededFormat.BoxMonSize;

    private static bool BoxSlotExists(int box, int slot) =>
        (uint)box < ExceededFormat.BoxCount && (uint)slot < ExceededFormat.BoxSlotCount;

    private ExceededMon? TryMon(int box, int slot)
    {
        if (box == -1)
            return (uint)slot < (uint)PartyCount ? new ExceededMon(_data, PartyRecordOffset(slot), true) : null;
        return BoxSlotExists(box, slot) ? new ExceededMon(_storage, BoxRecordOffset(box, slot), false) : null;
    }

    private void CommitParty() => ExceededFormat.Seal(_data, PartySector);

    private void CommitBox(int box, int slot) =>
        ExceededFormat.WriteStorageRange(_data, _sections, _storage, BoxRecordOffset(box, slot), ExceededFormat.BoxMonSize);

    private void Commit(int box, int slot)
    {
        if (box == -1) CommitParty();
        else CommitBox(box, slot);
    }

    // ── Species table ──

    /// <summary>
    /// The older Exceeded build numbers its species past 905 differently from the expansion
    /// pack (its "Iron Hands" is the pack's Hisuian Lilligant). A save whose records past 905
    /// carry the name of a different species than the pack's is that build: those species
    /// then show as "Species N" and keep their EXP. No contradiction: the pack is trusted.
    /// </summary>
    private bool SpeciesTableAgrees()
    {
        var names = GameInfo.GetStrings("en").specieslist;
        foreach (var mon in AllMons())
        {
            if (mon.Species <= SharedSpeciesRange)
                continue;
            var nickname = Normalize(mon.Nickname);
            var expected = _tables.NationalIdOf(mon.Species);
            for (var national = 1; national < names.Length; national++)
            {
                if (national == expected || names[national].Length == 0)
                    continue;
                var name = Normalize(names[national]);
                if (name.Length > 0 && (name == nickname || (nickname.Length >= 10 && name.StartsWith(nickname, StringComparison.Ordinal))))
                    return false;
            }
        }
        return true;

        static string Normalize(string text) =>
            new(text.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private IEnumerable<ExceededMon> AllMons()
    {
        for (var slot = 0; slot < PartyCount; slot++)
            yield return new ExceededMon(_data, PartyRecordOffset(slot), true);
        for (var box = 0; box < ExceededFormat.BoxCount; box++)
        for (var slot = 0; slot < ExceededFormat.BoxSlotCount; slot++)
        {
            var mon = new ExceededMon(_storage, BoxRecordOffset(box, slot), false);
            if (!mon.IsEmpty)
                yield return mon;
        }
    }

    /// <summary>Whether this record's species id means the same Pokémon in the pack.</summary>
    private bool SpeciesKnown(ExceededMon mon) =>
        mon.Species is > 0 and var species && species <= _tables.MaxSpecies && (species <= SharedSpeciesRange || _modernSpecies);

    private int NationalOf(ExceededMon mon) => SpeciesKnown(mon) ? _tables.NationalIdOf(mon.Species) : 0;

    private string SpeciesNameOf(ExceededMon mon) => SpeciesKnown(mon) ? _tables.SpeciesName(mon.Species) : $"Species {mon.Species}";

    private int LevelOf(ExceededMon mon) => mon.IsParty
        ? mon.PartyLevel
        : SpeciesKnown(mon) ? _tables.LevelForExperience(mon.Species, mon.Experience) : 0;

    private int GenderOf(ExceededMon mon) =>
        SpeciesKnown(mon) ? Expansion.ExpansionMon.GenderOf(mon.Pid, _tables.GenderRatio(mon.Species)) : 2;

    internal IEnumerable<SlotImage> SlotImages()
    {
        for (var box = -1; box < ExceededFormat.BoxCount; box++)
        for (var slot = 0; slot < (box == -1 ? 6 : ExceededFormat.BoxSlotCount); slot++)
        {
            var mon = TryMon(box, slot);
            yield return mon is null
                ? new SlotImage(new SlotRef(box, slot), [], Empty: true, Valid: true)
                : new SlotImage(new SlotRef(box, slot), mon.RawRecord(), mon.IsEmpty, mon.IsEmpty || mon.Species <= _tables.MaxSpecies);
        }
    }

    // ── Reading and editing ──

    private static readonly int[] Zeros = [0, 0, 0, 0, 0, 0];

    public EntityDetail ReadEntity(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return new EntityDetail(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0,
                Zeros, Zeros, false, 0, string.Empty);
        var moves = mon.Moves;
        return new EntityDetail(
            box, slot, false, NationalOf(mon), SpeciesNameOf(mon), 0, mon.Nickname, LevelOf(mon),
            mon.Nature, 0, mon.HeldItem,
            MoveToNational(moves[0]), MoveToNational(moves[1]), MoveToNational(moves[2]), MoveToNational(moves[3]),
            Zeros, Zeros, mon.IsShiny, 0, mon.OriginalTrainerName,
            SpeciesKnown(mon) ? _tables.Types(mon.Species) : [], GenderOf(mon), mon.Friendship,
            mon.Stats, mon.CurrentHp, mon.StatusCondition,
            // The record's ability is decoded nowhere (Data/README.md): say so instead of "(None)".
            AbilityName: "Not read from this save",
            MoveNames: HackNames.UnmappedMoves(moves, MoveToNational, MoveName));
    }

    /// <summary>The PKHeX id of a raw expansion move, below the Z-moves where both builds agree; 0 past it.</summary>
    private int MoveToNational(int move) => move is > 0 and <= MaxEditableMove ? _tables.MoveToNational(move) : 0;

    private string MoveName(int move) => move <= MaxEditableMove ? _tables.MoveName(move) : $"Move #{move}";

    /// <summary>The raw expansion id of a PKHeX move, or 0 when this engine cannot write it.</summary>
    private int MoveFromNational(int national) =>
        _tables.MoveFromNational(national) is > 0 and <= MaxEditableMove and var raw ? raw : 0;

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        edit = NameCharset.FitGen3Edit(edit); // charset first: a name the game cannot hold is refused, not emptied
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            throw new NotSupportedException("Emerald Exceeded Pokémon cannot be created by PKForge: only the ones already in the save can be edited.");
        var current = ReadEntity(box, slot);
        RefuseUnprovenChanges(mon, current, edit);

        var before = mon.RawRecord();
        if (edit.Nickname is { Length: > 0 } nickname && nickname != mon.Nickname)
            mon.Nickname = nickname;
        if (edit.OriginalTrainer is { Length: > 0 } ot && ot != mon.OriginalTrainerName)
            mon.OriginalTrainerName = ot;
        if (edit.HeldItem is { } item && item != mon.HeldItem)
            mon.HeldItem = item;
        ApplyMoves(mon, edit);
        if (edit.Level is { } level && Math.Clamp(level, 1, 100) != current.Level)
            mon.Experience = _tables.ExperienceAtLevel(mon.Species, Math.Clamp(level, 1, 100));

        if (!mon.RawRecord().AsSpan().SequenceEqual(before))
            Commit(box, slot);
    }

    /// <summary>Throws, before anything is written, for a change this engine cannot make safely.</summary>
    private void RefuseUnprovenChanges(ExceededMon mon, EntityDetail current, EntityEdit edit)
    {
        string? refused = null;
        if (edit.Species is { } species && species != current.Species) refused = "species";
        else if (edit.Nature is { } nature && nature != current.Nature) refused = "nature";
        else if (edit.Ability is { } ability && ability != current.Ability) refused = "ability";
        else if (edit.IVs is { Count: 6 } ivs && !ivs.SequenceEqual(current.IVs)) refused = "IVs";
        else if (edit.EVs is { Count: 6 } evs && !evs.SequenceEqual(current.EVs)) refused = "EVs";
        else if (edit.IsShiny is { } shiny && shiny != current.IsShiny) refused = "shininess";
        else if (edit.Ball is { } ball && ball != current.Ball) refused = "Poké Ball";
        else if (edit.Gender is { } gender && gender != current.Gender) refused = "gender";
        else if (edit.Friendship is { } friendship && friendship != current.Friendship) refused = "friendship";
        if (refused is not null)
            throw NotYet($"Changing a Pokémon's {refused}");

        if (edit.HeldItem is { } item && item != mon.HeldItem && !_tables.IsKnownItem(item))
            throw new NotSupportedException($"Item {item} is not in Emerald Exceeded's item table.");
        int?[] moves = [edit.Move1, edit.Move2, edit.Move3, edit.Move4];
        var stored = mon.Moves;
        // Edits speak PKHeX ids; the echo of a move shown as its national id (or as 0, unnamed) keeps the stored one.
        bool Kept(int i) => moves[i] is not { } move || move == MoveToNational(stored[i]);
        for (var i = 0; i < 4; i++)
            if (!Kept(i) && !StoresMove(moves[i]!.Value))
                throw new NotSupportedException($"Move {moves[i]} cannot be written to an Emerald Exceeded save.");
        if (Enumerable.Range(0, 4).All(i => (Kept(i) ? stored[i] : moves[i]!.Value) == 0))
            throw new NotSupportedException("A Pokémon must keep at least one move.");
        if (edit.Level is { } level && Math.Clamp(level, 1, 100) != current.Level)
        {
            if (mon.IsParty)
                throw NotYet("Changing a party Pokémon's level (Emerald Exceeded computes party stats with a formula of its own; " +
                    "move it to a box first, the game recalculates it when it rejoins the party)");
            if (!SpeciesKnown(mon))
                throw NotYet("Changing the level of a species this Exceeded build numbers differently");
        }
    }

    private void ApplyMoves(ExceededMon mon, EntityEdit edit)
    {
        int?[] wanted = [edit.Move1, edit.Move2, edit.Move3, edit.Move4];
        var current = mon.Moves;
        for (var i = 0; i < 4; i++)
        {
            if (wanted[i] is not { } national || national == MoveToNational(current[i])) continue;
            var move = MoveFromNational(national);
            mon.SetMove(i, move, move == 0 ? 0 : _tables.MovePp(move));
        }
    }

    // ── Copy / move / release ──

    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = TryMon(fromBox, fromSlot);
        if (source is null || source.IsEmpty || toBox == -1 || !BoxSlotExists(toBox, toSlot))
            return false;
        var target = new ExceededMon(_storage, BoxRecordOffset(toBox, toSlot), false);
        if (!target.IsEmpty)
            return false;
        source.BoxPart.CopyTo(_storage.AsSpan(BoxRecordOffset(toBox, toSlot)));
        CommitBox(toBox, toSlot);
        return true;
    }

    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (fromBox == toBox && fromSlot == toSlot)
            return;
        var source = TryMon(fromBox, fromSlot) ?? throw new InvalidOperationException("The source slot is empty.");
        if (source.IsEmpty)
            throw new InvalidOperationException("The source slot is empty.");
        if (toBox == -1)
        {
            if (fromBox != -1)
                throw NotYet("Moving a boxed Pokémon into the party (the game computes party stats with a formula of its own; " +
                    "withdraw it in the game instead)");
            MoveWithinParty(fromSlot, toSlot);
            return;
        }
        if (!BoxSlotExists(toBox, toSlot))
            throw new InvalidOperationException("The destination slot does not exist.");
        var targetOffset = BoxRecordOffset(toBox, toSlot);
        var target = new ExceededMon(_storage, targetOffset, false);
        if (fromBox == -1)
        {
            if (!target.IsEmpty)
                throw NotYet("Swapping a party Pokémon with a boxed one (the boxed one would need party stats)");
            if (PartyCount <= 1)
                throw new InvalidOperationException("The party must keep at least one Pokémon.");
            source.BoxPart.CopyTo(_storage.AsSpan(targetOffset));
            CompactPartyFrom(fromSlot);
            CommitParty();
            CommitBox(toBox, toSlot);
            return;
        }

        var sourceOffset = BoxRecordOffset(fromBox, fromSlot);
        var sourceCopy = _storage.AsSpan(sourceOffset, ExceededFormat.BoxMonSize).ToArray();
        _storage.AsSpan(targetOffset, ExceededFormat.BoxMonSize).CopyTo(_storage.AsSpan(sourceOffset));
        sourceCopy.CopyTo(_storage.AsSpan(targetOffset));
        CommitBox(fromBox, fromSlot);
        CommitBox(toBox, toSlot);
    }

    public void ReleaseSlot(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (box == -1)
        {
            if ((uint)slot >= (uint)PartyCount)
                return;
            if (PartyCount <= 1)
                throw new InvalidOperationException("The party must keep at least one Pokémon.");
            CompactPartyFrom(slot);
            CommitParty();
            return;
        }
        if (!BoxSlotExists(box, slot))
            return;
        _storage.AsSpan(BoxRecordOffset(box, slot), ExceededFormat.BoxMonSize).Clear();
        CommitBox(box, slot);
    }

    public void ClearBox(int box)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)box >= ExceededFormat.BoxCount)
            throw new InvalidOperationException("That box does not exist.");
        var first = BoxRecordOffset(box, 0);
        var length = ExceededFormat.BoxSlotCount * ExceededFormat.BoxMonSize;
        _storage.AsSpan(first, length).Clear();
        ExceededFormat.WriteStorageRange(_data, _sections, _storage, first, length);
    }

    private void MoveWithinParty(int fromSlot, int toSlot)
    {
        var count = PartyCount;
        if ((uint)fromSlot >= (uint)count)
            throw new InvalidOperationException("That party slot is empty.");
        var target = toSlot < 0 || toSlot >= count ? count - 1 : toSlot;
        if (target == fromSlot)
            return;
        var size = ExceededFormat.PartyMonSize;
        var moving = _data.AsSpan(PartyRecordOffset(fromSlot), size).ToArray();
        if (toSlot < 0 || toSlot >= count)
        {
            // Send it to the end, the others close up.
            for (var i = fromSlot; i < count - 1; i++)
                _data.AsSpan(PartyRecordOffset(i + 1), size).CopyTo(_data.AsSpan(PartyRecordOffset(i)));
        }
        else
        {
            _data.AsSpan(PartyRecordOffset(toSlot), size).CopyTo(_data.AsSpan(PartyRecordOffset(fromSlot)));
        }
        moving.CopyTo(_data.AsSpan(PartyRecordOffset(target)));
        CommitParty();
    }

    private void CompactPartyFrom(int removedSlot)
    {
        var count = PartyCount;
        var size = ExceededFormat.PartyMonSize;
        for (var i = removedSlot; i < count - 1; i++)
            _data.AsSpan(PartyRecordOffset(i + 1), size).CopyTo(_data.AsSpan(PartyRecordOffset(i)));
        _data.AsSpan(PartyRecordOffset(count - 1), size).Clear();
        _data[PartySector + ExceededFormat.PartyCountOffset] = (byte)(count - 1);
    }

    // ── Trainer / boxes ──

    private int Section0 => _sections[0];

    /// <summary>SaveBlock2+0xA4 (copied at +0x21C): the key that decodes the money and the
    /// coins (the coins read 0 on both samples, the bag counts read 999 and below).</summary>
    private uint SecurityKey => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Section0 + 0xA4));

    private uint Money => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(PartySector + 0x4C0)) ^ SecurityKey;

    public TrainerInfo GetTrainer()
    {
        var name = StringConverter3.GetString(_data.AsSpan(Section0, 8), jp: false);
        var tid = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Section0 + 0x0A));
        var sid = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Section0 + 0x0C));
        return new TrainerInfo(name.Length > 0 ? name : "PKForge", tid, sid, Money, _data[Section0 + 0x08]);
    }

    public void SetTrainer(TrainerInfo trainer)
    {
        if (trainer.Name is { Length: > 0 } typedName)
            trainer = trainer with { Name = NameCharset.FitGen3(typedName, 0, NameField.TrainerName) };
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (trainer.Money != Money)
            throw NotYet("Changing the money");
        var current = GetTrainer();
        if (current == trainer)
            return;
        var name = _data.AsSpan(Section0, 8);
        if (trainer.Name != current.Name)
        {
            name.Fill(0xFF);
            StringConverter3.SetString(name, trainer.Name, 7, jp: false);
        }
        _data[Section0 + 0x08] = (byte)Math.Clamp(trainer.Gender, 0, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(Section0 + 0x0A), (ushort)trainer.TID);
        BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(Section0 + 0x0C), (ushort)trainer.SID);
        ExceededFormat.Seal(_data, Section0);
    }

    /// <summary>Play time as "h:mm:ss", the form every other save shows on the shelf.</summary>
    internal string PlayTimeText() =>
        $"{BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Section0 + 0x0E))}:{_data[Section0 + 0x10]:00}:{_data[Section0 + 0x11]:00}";

    public TrainerStats GetTrainerStats() => new(false, 0, 0, 0, false, 0, 0, false, 0, 0);

    public void SetTrainerStats(TrainerStatsEdit edit) => throw NotYet("Trainer stat editing");

    public string GetBoxName(int box)
    {
        if ((uint)box >= ExceededFormat.BoxCount)
            return string.Empty;
        var name = StringConverter3.GetString(_storage.AsSpan(ExceededFormat.BoxNamesOffset + box * 9, 9), jp: false).Trim();
        return name.Length > 0 ? name : $"BOX{box + 1}";
    }

    // ── Metadata surfaces ──

    private SaveSnapshot BuildSnapshot(string? displayName)
    {
        var slots = new List<SlotSummary>(ExceededFormat.BoxCount * ExceededFormat.BoxSlotCount + 6);
        for (var box = 0; box < ExceededFormat.BoxCount; box++)
        for (var slot = 0; slot < ExceededFormat.BoxSlotCount; slot++)
            slots.Add(Summary(box, slot, TryMon(box, slot)));
        for (var slot = 0; slot < 6; slot++)
            slots.Add(Summary(-1, slot, TryMon(-1, slot)));
        return new SaveSnapshot("EXCEEDED", 3, _originalBytes.ToArray(), slots, displayName);
    }

    private SlotSummary Summary(int box, int slot, ExceededMon? mon) =>
        mon is null || mon.IsEmpty
            ? new SlotSummary(box, slot, null, null, false, true)
            : new SlotSummary(box, slot, NationalOf(mon), mon.Nickname, mon.IsShiny, true, 0, mon.IsEgg, mon.HeldItem);

    public ReadOnlyMemory<byte> Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SramPadding.Restore(_data, _originalBytes);
    }

    public IReadOnlyList<int> GetAbilityChoices(int species, int form) => [0];
    public bool StoresAnyAbility => false;

    /// <summary>The expansion's moves below its Z-moves: the range both Exceeded builds share.</summary>
    public bool StoresMove(int move) => move == 0 || MoveFromNational(move) > 0;

    public IReadOnlyList<string> GetItemNames() => HackNames.WithNoItem(_tables.ItemNames());
    public IReadOnlyList<string> GetFormChoices(int species) => [""];

    public IReadOnlyList<int> GetSpeciesTypes(int species)
    {
        var raw = _tables.SpeciesFromNational(species);
        return _tables.Types(raw <= 0 ? species : raw);
    }

    public BaseStats GetBaseStats(int species)
    {
        var raw = _tables.SpeciesFromNational(species);
        return _tables.BaseStats(raw <= 0 ? species : raw);
    }

    public string GetShowdownText(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return string.Empty;
        var text = $"{SpeciesNameOf(mon)}\nLevel: {LevelOf(mon)}";
        if (mon.HeldItem != 0)
            text += $"\nItem: {_tables.ItemName(mon.HeldItem)}";
        // The same names as the editor: past the Z-moves the builds disagree, so no pack name.
        foreach (var move in mon.Moves)
            if (move != 0)
                text += $"\n- {MoveName(move)}";
        return text;
    }

    public string ExportBoxShowdown(int box) =>
        string.Join("\n\n", Enumerable.Range(0, ExceededFormat.BoxSlotCount)
            .Select(slot => GetShowdownText(box, slot)).Where(text => text.Length > 0));

    public RngInfo GetRngInfo(int box, int slot)
    {
        var mon = TryMon(box, slot);
        return mon is null || mon.IsEmpty
            ? new RngInfo(0, null, 0, false, false, Zeros, 0, 2)
            : new RngInfo(mon.Pid, null, mon.Nature, mon.IsShiny, false, Zeros, 0, GenderOf(mon));
    }

    public TrainingCaps GetTrainingCaps() => new(31, 255);

    public bool RerollNatureKeepShiny(int box, int slot, int nature) => false;

    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile = null)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return new GenerationOutcome(false, "That slot is empty.");
        // The OT id decides shininess (and the hack's own checks), so only the name moves.
        var name = profile?.OriginalTrainer ?? GetTrainer().Name;
        mon.OriginalTrainerName = name;
        Commit(box, slot);
        return new GenerationOutcome(true, $"{mon.Nickname} now carries {name}'s name.");
    }

    public IReadOnlyList<int> GetMissingSpecies() => [];
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];
    public DexEntryState GetDexEntry(int species) => new(false, false);
    public bool IsDexSpecies(int species) => false;
    public DexProgress GetDexProgress() => new(0, 0, 0);
    public void SetDexEntry(int species, bool seen, bool caught) => throw NotYet("The Pokédex editor");
    public void CompleteDex() => throw NotYet("The complete-dex action");

    // ── Unsupported bulk / bank surfaces ──

    public bool SupportsBoxTools => false;
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false) => throw NotYet("Box sorting");
    public int PlaceLivingDex(byte[] compressedBundle) => throw NotYet("The living dex");
    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) => throw NotYet("The batch editor");
    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) => throw NotYet("The batch editor");
    public void SwapBoxes(int a, int b) => throw NotYet("Box swapping");
    public void DeleteBox(int box) => throw NotYet("Box management");
    public SlotExport ExportSlot(int box, int slot) => throw NotYet("Entity export");
    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format = null) => throw NotYet("Entity import");

    public IReadOnlyList<BagPouch> GetBag() => [];
    public IReadOnlyList<int> GetPouchLegalItems(string pouchName) => [];
    public int SetItemCount(string pouchName, int itemId, int count) => throw NotYet("The bag editor");
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

    public PotentialInfo GetPotential(int box, int slot) =>
        new(false, 0, "", "", false, false, [false, false, false, false, false, false], false, 0, [], false, [], false, [], []);

    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit) => throw NotYet("Ability, Tera and Hyper Training editing");
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
            // The pack's PP is the latest generation's; never report a maximum below what the save holds.
            var maxPp = Math.Max(_tables.MovePp(moves[i]) * (5 + ups[i]) / 5, pp[i]);
            slots.Add(new MoveSlotDetail(MoveToNational(moves[i]), pp[i], moves[i] == 0 ? 0 : maxPp, ups[i]));
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
        if (edit.PPUps is { Count: 4 } ups && !ups.SequenceEqual(mon.MovePpUps))
            throw NotYet("PP Up editing");
        if (edit.PP is not { Count: 4 } pp)
            return;
        var before = mon.RawRecord();
        var details = GetMoveDetails(box, slot).Moves;
        for (var i = 0; i < 4; i++)
            mon.SetPp(i, Math.Clamp(pp[i], 0, details[i].MaxPP));
        if (!mon.RawRecord().AsSpan().SequenceEqual(before))
            Commit(box, slot);
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
    public IReadOnlyDictionary<string, int> GetObtainableRibbonMaxima(int box, int slot) => new Dictionary<string, int>();
    public int AwardAllObtainableRibbons(int box, int slot) => 0;

    public bool SupportsLegalityAnalysis => false;
    public bool SupportsCompassSettings => false;
    public IReadOnlyList<CompassSetting> GetCompassSettings() => [];
    public bool SetCompassSetting(string id, int choiceIndex) => false;
    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) => throw NotYet("Encounter cards");

    public void Dispose() => _disposed = true;

    private static NotSupportedException NotYet(string what) => new($"{what} is not available for Emerald Exceeded saves.");
}
