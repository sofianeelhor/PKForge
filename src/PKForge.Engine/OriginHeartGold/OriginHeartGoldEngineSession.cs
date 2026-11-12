using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;
using static PKForge.Engine.OriginHeartGold.OriginHeartGoldFormat;

namespace PKForge.Engine.OriginHeartGold;

/// <summary>
/// A Pokémon Origin HeartGold save as a PKForge session. Stock PKHeX refuses the file (its
/// blocks are larger than HeartGold's); this session reads the hack's own block sizes,
/// 24 boxes and PK4 records with modern ids (see <see cref="OriginHeartGoldFormat"/>).
///
/// What it writes is only what has been proven on the real save to survive a reopen with
/// valid CRCs, and only bytes whose meaning is the retail one: a Pokémon's nickname,
/// friendship, and (boxed Pokémon) IVs and EVs; moving, swapping, copying and releasing
/// Pokémon between box slots; the trainer's name and money; play time. Every write goes to
/// the current copy of the touched block, with its CRC recomputed; the backup partition is
/// never touched. A box edit marks every box changed (the retail HGSS rule PKHeX also follows)
/// so the game rewrites the whole PC on its next save instead of only the boxes it changed.
///
/// Everything else is read-only, with the reason given when an edit asks for it: the hack's
/// base stats, learnsets, move PP and item tables are not public, so species, level, moves,
/// held item, nature, shininess, ability and party stats are never rewritten. The ability
/// slot byte (0x15) and the ability id (0x42) are both kept exactly as stored.
/// </summary>
internal sealed class OriginHeartGoldEngineSession : ISaveEngineSession
{
    internal const string GameName = "Origin HeartGold";

    private const string StatsReason =
        "Origin HeartGold reworked base stats, growth and learnsets and does not publish them, so PKForge cannot recompute what this would change";

    private readonly byte[] _originalBytes;
    private readonly byte[] _data;
    private readonly Layout _layout;
    private readonly byte[] _general;
    private readonly byte[] _storage;
    private readonly string? _displayName;
    private readonly string? _storageRefusal;
    private readonly string? _partyRefusal;
    private bool _generalDirty;
    private bool _storageDirty;
    private bool _disposed;

    public OriginHeartGoldEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _originalBytes = bytes.ToArray();
        _data = RetroArchSaveContainer.Decode(bytes.Span);
        _layout = (LooksLike(_data) ? Parse(_data) : null)
            ?? throw new InvalidDataException("These bytes are not a Pokémon Origin HeartGold save.");
        _general = _data.AsSpan(_layout.General, GeneralSize).ToArray();
        _storage = _data.AsSpan(_layout.Storage, StorageSize).ToArray();
        _displayName = displayName;

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(_storage.AsSpan(BoxChangedFlagsOffset));
        if ((flags & ~AllBoxesChanged) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(_storage.AsSpan(CurrentBoxOffset)) >= BoxCount)
            _storageRefusal = "the PC's box bookkeeping does not read as HeartGold's (current box and changed-box flags), so PKForge will not rewrite the boxes";
        if (_general[PartyCountOffset] > 6)
            _partyRefusal = "the party count is not between 0 and 6";
    }

    public SaveSnapshot Snapshot => BuildSnapshot();
    public int Generation => 4;
    public IReadOnlyList<string> GameNames => ["Pokémon " + GameName];
    public int MaxSpeciesId => OriginHeartGoldData.MaxStoredSpecies;
    public int BoxSlotCount => SlotsPerBox;

    private int PartyCount => Math.Min((int)_general[PartyCountOffset], 6);

    private static int BoxRecordOffset(int box, int slot) => box * BoxStride + slot * StoredSize;
    private static int PartyRecordOffset(int slot) => PartyOffset + slot * PartySize;

    private OriginHeartGoldMon? TryMon(int box, int slot)
    {
        if (box == -1)
            return (uint)slot < (uint)PartyCount ? new OriginHeartGoldMon(_general.AsSpan(PartyRecordOffset(slot), PartySize)) : null;
        if ((uint)box >= BoxCount || (uint)slot >= SlotsPerBox)
            return null;
        return new OriginHeartGoldMon(_storage.AsSpan(BoxRecordOffset(box, slot), StoredSize));
    }

    private Span<byte> RawRecord(int box, int slot) => box == -1
        ? _general.AsSpan(PartyRecordOffset(slot), PartySize)
        : _storage.AsSpan(BoxRecordOffset(box, slot), StoredSize);

    private void Store(int box, int slot, OriginHeartGoldMon mon)
    {
        mon.Encrypted().CopyTo(RawRecord(box, slot));
        if (box == -1) _generalDirty = true;
        else _storageDirty = true;
    }

    // ── Snapshot and safety images ──

    private SaveSnapshot BuildSnapshot()
    {
        var slots = new List<SlotSummary>(BoxCount * SlotsPerBox + 6);
        for (var box = 0; box < BoxCount; box++)
        for (var slot = 0; slot < SlotsPerBox; slot++)
            slots.Add(SummaryOf(box, slot));
        for (var slot = 0; slot < 6; slot++)
            slots.Add(SummaryOf(-1, slot));
        return new SaveSnapshot("ORIGINHG", 4, _originalBytes.ToArray(), slots, _displayName);

        SlotSummary SummaryOf(int box, int slot)
        {
            var mon = TryMon(box, slot);
            if (mon is null || mon.IsEmpty)
                return new SlotSummary(box, slot, null, null, false, true);
            if (!mon.LooksValid)
                return new SlotSummary(box, slot, null, null, false, false);
            return new SlotSummary(box, slot, mon.Species, mon.IsNicknamed ? mon.Nickname : null, mon.IsShiny, true,
                mon.Form, mon.IsEgg, mon.HeldItem);
        }
    }

    internal IEnumerable<SlotImage> SlotImages()
    {
        for (var slot = 0; slot < 6; slot++)
        {
            var mon = TryMon(-1, slot);
            yield return mon is null
                ? new SlotImage(new SlotRef(-1, slot), [], Empty: true, Valid: true)
                : new SlotImage(new SlotRef(-1, slot), RawRecord(-1, slot).ToArray(), mon.IsEmpty, mon.IsEmpty || mon.LooksValid);
        }
        for (var box = 0; box < BoxCount; box++)
        for (var slot = 0; slot < SlotsPerBox; slot++)
        {
            var mon = TryMon(box, slot)!;
            yield return new SlotImage(new SlotRef(box, slot), RawRecord(box, slot).ToArray(), mon.IsEmpty, mon.IsEmpty || mon.LooksValid);
        }
    }

    // ── Reading ──

    public EntityDetail ReadEntity(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid)
            return new EntityDetail(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0,
                [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);
        var moves = mon.Moves;
        var level = LevelOf(mon);
        // Boxed Pokémon carry no stats, and the hack's base stats are not public: none are invented.
        var stats = mon.Party ? mon.PartyStats : null;
        return new EntityDetail(
            box, slot, false,
            mon.Species, OriginHeartGoldData.SpeciesName(mon.Species), mon.Form,
            mon.Nickname, level, mon.Nature, mon.AbilityId, mon.HeldItem,
            moves[0], moves[1], moves[2], moves[3],
            mon.Ivs, mon.Evs, mon.IsShiny, mon.Ball, mon.OriginalTrainerName,
            OriginHeartGoldData.Types(mon.Species, mon.Form), mon.Gender, mon.Friendship,
            stats, mon.Party ? mon.CurrentHp : 0, mon.StatusCondition);
    }

    /// <summary>A party Pokémon's stored level; a boxed one's from its EXP.</summary>
    private static int LevelOf(OriginHeartGoldMon mon) =>
        mon.Party ? mon.StoredLevel : OriginHeartGoldData.LevelFor(mon.Species, mon.Experience);

    // ── Editing (only the proven fields) ──

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid)
            throw new InvalidOperationException("Cannot edit an empty slot.");
        RequireWritable(box);

        // Refuse first, write nothing: the editor echoes every field, so only real changes count.
        var moves = mon.Moves;
        if (edit.Species is { } species && species != mon.Species) throw ReadOnly("The species");
        if (edit.Level is { } level && level != LevelOf(mon)) throw ReadOnly("The level", StatsReason);
        if (edit.Nature is { } nature && nature != mon.Nature) throw ReadOnly("The nature", "in this game it is derived from the Pokémon's PID, which also sets its shininess and data order");
        if (edit.Ability is { } ability && ability != mon.AbilityId) throw ReadOnly("The ability", "the hack keeps an ability slot and an ability id of its own, and its ability table is not public");
        if (edit.HeldItem is { } item && item != mon.HeldItem) throw ReadOnly("The held item", "the hack's item table is not public, so PKForge cannot tell which items it has");
        int?[] wantedMoves = [edit.Move1, edit.Move2, edit.Move3, edit.Move4];
        for (var i = 0; i < 4; i++)
            if (wantedMoves[i] is { } move && move != moves[i])
                throw ReadOnly("Moves", "the hack's move table and PP are not public");
        if (edit.IsShiny is { } shiny && shiny != mon.IsShiny) throw ReadOnly("Shininess", "in this game it is derived from the PID, which also sets the nature and data order");
        if (edit.Ball is { } ball && ball != mon.Ball) throw ReadOnly("The ball");
        if (edit.OriginalTrainer is { Length: > 0 } ot && ot != mon.OriginalTrainerName) throw ReadOnly("The original trainer");
        if (edit.Gender is { } gender && gender != mon.Gender) throw ReadOnly("The gender");
        var ivsChanged = edit.IVs is { Count: 6 } ivs && !ivs.SequenceEqual(mon.Ivs);
        var evsChanged = edit.EVs is { Count: 6 } evs && !evs.SequenceEqual(mon.Evs);
        if ((ivsChanged || evsChanged) && mon.Party)
            throw ReadOnly("IVs and EVs of a party Pokémon", "its stored stats would have to be recomputed from the hack's base stats, which are not public. Edit them while it is in a box");

        if (edit.Nickname is { Length: > 0 } typedNickname && typedNickname != mon.Nickname)
            edit = edit with { Nickname = NameCharset.FitGen4(typedNickname, 10, NameField.Nickname, mon.Language) };
        var changed = false;
        if (edit.Nickname is { Length: > 0 } nickname && nickname != mon.Nickname)
        {
            mon.SetNickname(nickname);
            mon.IsNicknamed = !string.Equals(mon.Nickname, OriginHeartGoldData.SpeciesName(mon.Species), StringComparison.Ordinal);
            changed = true;
        }
        if (edit.Friendship is { } friendship && friendship != mon.Friendship)
        {
            mon.Friendship = friendship;
            changed = true;
        }
        if (ivsChanged)
        {
            mon.Ivs = [.. edit.IVs!.Select(v => Math.Clamp(v, 0, 31))];
            changed = true;
        }
        if (evsChanged)
        {
            mon.Evs = [.. edit.EVs!.Select(v => Math.Clamp(v, 0, 255))];
            changed = true;
        }
        if (changed)
            Store(box, slot, mon);
    }

    private void RequireWritable(int box)
    {
        var refusal = box == -1 ? _partyRefusal : _storageRefusal;
        if (refusal is not null)
            throw new NotSupportedException($"This Pokémon Origin HeartGold save is read-only here: {refusal}. The file was not touched.");
    }

    private static NotSupportedException ReadOnly(string what, string? why = null) =>
        new($"{what} cannot be changed in a Pokémon Origin HeartGold save{(why is null ? "" : $": {why}")}. Nothing was changed.");

    private static NotSupportedException NotYet(string what) =>
        new($"{what} is not available for Pokémon Origin HeartGold.");

    // ── Box slots: whole records moved verbatim, never across the party boundary ──

    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (fromBox == toBox && fromSlot == toSlot)
            return;
        if (fromBox == -1 || toBox == -1)
            throw new NotSupportedException("Moving Pokémon in or out of the party is not available for Pokémon Origin HeartGold: " +
                "a party Pokémon carries stats the game computes from the hack's own base stats. Do it in the game's PC. Nothing was changed.");
        RequireWritable(toBox);
        if (TryMon(fromBox, fromSlot) is not { } source || source.IsEmpty)
            throw new InvalidOperationException("The source slot is empty.");
        if (TryMon(toBox, toSlot) is null)
            throw new InvalidOperationException("The destination slot does not exist.");
        var from = RawRecord(fromBox, fromSlot);
        var to = RawRecord(toBox, toSlot);
        var held = to.ToArray();
        from.CopyTo(to);
        held.CopyTo(from);
        _storageDirty = true;
    }

    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (fromBox == -1 || toBox == -1)
            return false;
        RequireWritable(toBox);
        if (TryMon(fromBox, fromSlot) is not { LooksValid: true, IsEmpty: false })
            return false;
        if (TryMon(toBox, toSlot) is not { IsEmpty: true })
            return false;
        RawRecord(fromBox, fromSlot).CopyTo(RawRecord(toBox, toSlot));
        _storageDirty = true;
        return true;
    }

    public void ReleaseSlot(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (box == -1)
            throw new NotSupportedException("Releasing a party Pokémon is not available for Pokémon Origin HeartGold. Release it in the game. Nothing was changed.");
        RequireWritable(box);
        if (TryMon(box, slot) is not { IsEmpty: false })
            return;
        OriginHeartGoldMon.EmptyBoxRecord().CopyTo(RawRecord(box, slot));
        _storageDirty = true;
    }

    public void ClearBox(int box)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWritable(box);
        if ((uint)box >= BoxCount) return;
        for (var slot = 0; slot < SlotsPerBox; slot++)
            ReleaseSlot(box, slot);
    }

    public void SwapBoxes(int a, int b) => throw NotYet("Box reordering");
    public void DeleteBox(int box) => throw NotYet("Box deletion");

    // ── Trainer ──

    public TrainerInfo GetTrainer()
    {
        var name = StringConverter4.GetString(_general.AsSpan(TrainerNameOffset, 16));
        return new TrainerInfo(name, BinaryPrimitives.ReadUInt16LittleEndian(_general.AsSpan(TrainerIdOffset)),
            BinaryPrimitives.ReadUInt16LittleEndian(_general.AsSpan(SecretIdOffset)),
            BinaryPrimitives.ReadUInt32LittleEndian(_general.AsSpan(MoneyOffset)), _general[GenderOffset]);
    }

    public void SetTrainer(TrainerInfo trainer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var current = GetTrainer();
        if (trainer.TID != current.TID || trainer.SID != current.SID)
            throw ReadOnly("The trainer ID", "every Pokémon the player caught records it");
        if (trainer.Gender != current.Gender)
            throw ReadOnly("The trainer's gender");
        if (trainer.Name is { Length: > 0 } name && name != current.Name)
        {
            name = NameCharset.FitGen4(name, 7, NameField.TrainerName, _general[LanguageOffset]);
            StringConverter4.SetString(_general.AsSpan(TrainerNameOffset, 16), name, 7, _general[LanguageOffset], StringConverterOption.ClearZero);
            _generalDirty = true;
        }
        if (trainer.Money != current.Money)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_general.AsSpan(MoneyOffset), Math.Min(trainer.Money, MaxMoney));
            _generalDirty = true;
        }
    }

    internal string PlayTimeText() =>
        $"{BinaryPrimitives.ReadUInt16LittleEndian(_general.AsSpan(PlayTimeHoursOffset))}:{_general[PlayTimeMinutesOffset]:00}:{_general[PlayTimeSecondsOffset]:00}";

    public TrainerStats GetTrainerStats() => new(
        true, BinaryPrimitives.ReadUInt16LittleEndian(_general.AsSpan(PlayTimeHoursOffset)), _general[PlayTimeMinutesOffset], 999,
        false, 0, 0, false, 0, 0);

    public void SetTrainerStats(TrainerStatsEdit edit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (edit.PlayTimeHours is null && edit.PlayTimeMinutes is null)
            return;
        if (edit.PlayTimeHours is { } hours)
            BinaryPrimitives.WriteUInt16LittleEndian(_general.AsSpan(PlayTimeHoursOffset), (ushort)Math.Clamp(hours, 0, 999));
        if (edit.PlayTimeMinutes is { } minutes)
            _general[PlayTimeMinutesOffset] = (byte)Math.Clamp(minutes, 0, 59);
        _generalDirty = true;
    }

    public SaveBadges? GetBadges() => null;

    public string GetBoxName(int box)
    {
        if ((uint)box >= BoxCount) return string.Empty;
        var name = StringConverter4.GetString(_storage.AsSpan(BoxNamesOffset + box * BoxNameSize, BoxNameSize));
        return string.IsNullOrWhiteSpace(name) ? $"BOX {box + 1}" : name;
    }

    // ── Bag (read-only: pocket starts are known only where the sample's pockets hold items) ──

    private readonly record struct Pocket(string Name, int Offset, int End);

    /// <summary>Pocket starts in the bag at general+0x644. Items, Key Items and TMs/HMs keep
    /// HeartGold's offsets; Medicine, Berries and Balls moved (the sample's runs start at
    /// 0x5E8, 0x688 and 0x788). Mail and Battle Items are not located.</summary>
    private static readonly Pocket[] Pockets =
    [
        new("Items", 0x000, 0x294),
        new("KeyItems", 0x294, 0x35C),
        new("TMHMs", 0x35C, 0x4F0),
        new("Medicine", 0x5E8, 0x688),
        new("Berries", 0x688, 0x788),
        new("Balls", 0x788, 0x7E8),
    ];

    public IReadOnlyList<BagPouch> GetBag() => [.. Pockets.Select(pocket =>
    {
        var items = new List<BagItem>();
        for (var offset = BagOffset + pocket.Offset; offset < BagOffset + pocket.End; offset += 4)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(_general.AsSpan(offset));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(_general.AsSpan(offset + 2));
            if (id == 0 || count == 0) break;
            items.Add(new BagItem(id, count));
        }
        return new BagPouch(pocket.Name, items);
    })];

    public IReadOnlyList<int> GetPouchLegalItems(string pouchName) => [];
    public int SetItemCount(string pouchName, int itemId, int count) =>
        throw NotYet("The bag editor (the hack resized its pockets and its item table is not public)");

    // ── Names, types, stats surface ──

    public IReadOnlyList<int> GetAbilityChoices(int species, int form)
    {
        var abilities = OriginHeartGoldData.Abilities(species, form);
        return abilities.Count > 0 ? abilities : [0];
    }

    public bool StoresAnyAbility => false;
    public bool StoresMove(int move) => false;
    public IReadOnlyList<string> GetItemNames() => HackNames.WithNoItem(OriginHeartGoldData.ItemNames());
    public IReadOnlyList<string> GetFormChoices(int species) => [""];
    public IReadOnlyList<int> GetSpeciesTypes(int species) => OriginHeartGoldData.Types(species, 0);
    public BaseStats GetBaseStats(int species) => OriginHeartGoldData.BaseStatsOf(species);
    public TrainingCaps GetTrainingCaps() => new(31, 255);

    public string GetShowdownText(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid) return string.Empty;
        var text = OriginHeartGoldData.SpeciesName(mon.Species);
        if (mon.HeldItem != 0) text += $" @ {OriginHeartGoldData.ItemName(mon.HeldItem)}";
        text += $"\nAbility: {OriginHeartGoldData.AbilityName(mon.AbilityId)}\nLevel: {LevelOf(mon)}\n{GameInfo.GetStrings("en").natures[mon.Nature]} Nature";
        foreach (var move in mon.Moves)
            if (move != 0)
                text += $"\n- {OriginHeartGoldData.MoveName(move)}";
        return text;
    }

    public string ExportBoxShowdown(int box) =>
        string.Join("\n\n", Enumerable.Range(0, SlotsPerBox).Select(slot => GetShowdownText(box, slot)).Where(text => text.Length > 0));

    public RngInfo GetRngInfo(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid)
            return new RngInfo(0, null, 0, false, false, [0, 0, 0, 0, 0, 0], 0, 2);
        return new RngInfo(mon.Pid, null, mon.Nature, mon.IsShiny, false, mon.Ivs, mon.AbilityId, mon.Gender);
    }

    public bool RerollNatureKeepShiny(int box, int slot, int nature) => false;

    public MoveDetails GetMoveDetails(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid)
            return new MoveDetails([], false, []);
        var moves = mon.Moves;
        var pp = mon.Pp;
        var ups = mon.PpUps;
        // The hack's base PP are not public: the stored PP is shown as its own maximum.
        return new MoveDetails([.. Enumerable.Range(0, 4).Select(i => new MoveSlotDetail(moves[i], pp[i], pp[i], ups[i]))], false, []);
    }

    public void ApplyMoveDetails(int box, int slot, MoveDetailsEdit edit)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid) return;
        if ((edit.PP is { } pp && !pp.SequenceEqual(mon.Pp)) || (edit.PPUps is { } ups && !ups.SequenceEqual(mon.PpUps)))
            throw ReadOnly("PP", "the hack's move PP are not public");
    }

    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile = null) =>
        new(false, "Changing a Pokémon's original trainer is not available for Pokémon Origin HeartGold.");

    // ── Import / export: the hack's own record, verbatim ──

    internal const string EntityFormat = "pk4ohg";

    public SlotExport ExportSlot(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty || !mon.LooksValid)
            throw new InvalidOperationException("That slot is empty.");
        // The decrypted 136-byte record. Its own format name keeps it out of retail games:
        // its species, moves, items and ability field mean something else there.
        return new SlotExport(mon.Plain()[..StoredSize], $"{OriginHeartGoldData.SpeciesName(mon.Species)}.{EntityFormat}", EntityFormat);
    }

    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format = null)
    {
        if (format != EntityFormat || fileBytes.Length != StoredSize || box == -1)
            return false;
        RequireWritable(box);
        if (TryMon(box, slot) is not { IsEmpty: true })
            return false;
        var encrypted = fileBytes.ToArray();
        PokeCrypto.Encrypt45(encrypted);
        var probe = new OriginHeartGoldMon(encrypted);
        if (!probe.LooksValid)
            return false;
        encrypted.CopyTo(RawRecord(box, slot));
        _storageDirty = true;
        return true;
    }

    // ── Serialization: the current copy of each touched block, CRC recomputed ──

    public ReadOnlyMemory<byte> Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_generalDirty && !_storageDirty)
            return _originalBytes.ToArray();
        if (_generalDirty)
        {
            _general.CopyTo(_data.AsSpan(_layout.General));
            WriteCrc(_data, _layout.General, GeneralSize);
        }
        if (_storageDirty)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_storage.AsSpan(BoxChangedFlagsOffset), AllBoxesChanged);
            _storage.CopyTo(_data.AsSpan(_layout.Storage));
            WriteCrc(_data, _layout.Storage, StorageSize);
        }
        return RetroArchSaveContainer.Repack(_data, _originalBytes);
    }

    // ── Surfaces this game does not have, or PKForge cannot prove ──

    public bool SupportsBoxTools => false;
    public bool SupportsLegalityAnalysis => false;
    public bool SupportsRTCRepair => false;
    public void RepairRTC() { }

    public IReadOnlyList<int> GetMissingSpecies() => [];
    // The hack grew its Pokédex past HeartGold's 493 and moved it: no cell is located.
    public DexEntryState GetDexEntry(int species) => new(false, false);
    public void SetDexEntry(int species, bool seen, bool caught) => throw NotYet("The Pokédex");
    public bool IsDexSpecies(int species) => false;
    public DexProgress GetDexProgress() => new(0, 0, 0);
    public void CompleteDex() => throw NotYet("The Pokédex");

    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];
    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) =>
        throw NotYet("Encounter cards");
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false) => throw NotYet("Box sorting");
    public int PlaceLivingDex(byte[] compressedBundle) => throw NotYet("The living dex");
    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) => throw NotYet("The batch editor");
    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) => throw NotYet("The batch editor");

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
            mon.IsEgg, mon.EggLocation, string.Empty, string.Empty, false,
            mon.Version, string.Empty, mon.Language, string.Empty, mon.FatefulEncounter, mon.TrainerId, mon.SecretId);
    }

    public void ApplyMetEdit(int box, int slot, MetEdit edit) => throw NotYet("Met/origin editing");
    public IReadOnlyList<NamedChoice> GetLocationChoices(int box, int slot, bool egg) => [];
    public IReadOnlyList<NamedChoice> GetVersionChoices() => [];
    public IReadOnlyList<NamedChoice> GetLanguageChoices(int box, int slot) => [];

    public PotentialInfo GetPotential(int box, int slot) => new(
        false, 0, string.Empty, string.Empty, false, false, [false, false, false, false, false, false],
        false, 0, [], false, [], false, [], []);

    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit)
    {
        if (edit.AbilitySlot is not null || edit.TeraType is not null || edit.HyperTrained is not null)
            throw ReadOnly("The ability slot", "the hack's ability table is not public");
    }

    public IReadOnlyList<NamedChoice> GetTeraTypeChoices() => [];
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

    public bool SupportsCompassSettings => false;
    public IReadOnlyList<CompassSetting> GetCompassSettings() => [];
    public bool SetCompassSetting(string id, int choiceIndex) => false;

    public void Dispose() => _disposed = true;
}
