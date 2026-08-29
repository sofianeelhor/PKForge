using PKForge.Domain;
using PKHeX.Core;
using PKHeX.Core.AutoMod;

namespace PKForge.Engine;

/// <summary>Holds a pinned PKHeX <see cref="SaveFile"/> and exposes entity access without leaking engine types.</summary>
public sealed class SaveEngineSession : ISaveEngineSession
{
    private readonly SaveFile _save;
    private readonly byte[] _originalBytes;
    private bool _disposed;

    public SaveEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _originalBytes = bytes.ToArray();
        if (!SaveUtil.TryGetSaveFile(_originalBytes, out var save) || save is null)
            throw new InvalidDataException("The selected bytes are not a recognized save file.");
        _save = save;
        Snapshot = BuildSnapshot(displayName);
    }

    /// <summary>
    /// Wraps an already-built save (e.g. the throwaway save behind a single bank mon).
    /// We deliberately do NOT serialize it here: a freshly-built blank Gen3/4 save cannot
    /// be written out (SAV3.WriteSectors throws), and a standalone entity session is never
    /// written back to a save file - only its single mon is exported via ExportSlot.
    /// </summary>
    internal SaveEngineSession(SaveFile save, string? displayName)
    {
        _save = save;
        _originalBytes = [];
        Snapshot = BuildSnapshot(displayName);
    }

    public SaveSnapshot Snapshot { get; }

    internal PKM GetEntity(int box, int slot)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        return GetEntityCore(box, slot);
    }

    /// <summary>Box -1 addresses the party (0-5, compact like the games).</summary>
    private PKM GetEntityCore(int box, int slot)
        => box == -1 ? _save.GetPartySlotAtIndex(slot) : _save.GetBoxSlotAtIndex(box, slot);

    private void SetEntityCore(int box, int slot, PKM pk)
    {
        if (box == -1) _save.SetPartySlotAtIndex(pk, slot);
        else _save.SetBoxSlotAtIndex(pk, box, slot);
    }

    public EntityDetail ReadEntity(int box, int slot)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        var entity = GetEntityCore(box, slot);
        if (entity.Species == 0)
            return new EntityDetail(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);

        var ivs = GetIVsInAppOrder(entity);
        var evs = GetEVsInAppOrder(entity);
        return new EntityDetail(
            box, slot, false,
            entity.Species,
            SpeciesName.GetSpeciesName(entity.Species, (int)LanguageID.English),
            entity.Form,
            entity.Nickname,
            entity.CurrentLevel,
            (int)entity.Nature,
            entity.Ability,
            entity.HeldItem,
            entity.Move1, entity.Move2, entity.Move3, entity.Move4,
            ivs, evs,
            entity.IsShiny,
            entity.Ball,
            entity.OriginalTrainerName,
            GetTypes(entity.Species, entity.Form),
            entity.Gender,
            entity.CurrentFriendship,
            ComputeStats(entity),
            entity.Stat_HPCurrent,
            (int)entity.Status_Condition);
    }

    /// <summary>The mon's final battle stats (HP/Atk/Def/SpA/SpD/Spe) at its current level.</summary>
    private static int[] ComputeStats(PKM entity)
    {
        // Work on a clone: recomputing party stats must not mutate the stored slot.
        var clone = entity.Clone();
        clone.ResetPartyStats();
        return [clone.Stat_HPMax, clone.Stat_ATK, clone.Stat_DEF, clone.Stat_SPA, clone.Stat_SPD, clone.Stat_SPE];
    }

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(edit);
        ValidateCoordinates(box, slot);
        var entity = GetEntityCore(box, slot);
        if (entity.Species == 0)
            throw new InvalidOperationException("Cannot edit an empty slot.");

        var changed = false;
        if (edit.Species is { } species && entity.Species != species)
        {
            entity.Species = (ushort)species;
            entity.Form = 0;
            changed = true;
        }
        if (edit.Nickname is { } nickname && !string.Equals(entity.Nickname, nickname, StringComparison.Ordinal))
        {
            entity.Nickname = nickname;
            entity.IsNicknamed = true;
            changed = true;
        }
        if (edit.Level is { } level && entity.CurrentLevel != Math.Clamp(level, 1, 100))
        {
            entity.CurrentLevel = (byte)Math.Clamp(level, 1, 100);
            changed = true;
        }
        if (edit.Nature is { } nature && (int)entity.Nature != nature)
        {
            // Gen 3/4 natures are PID-derived with an empty setter: the only way to
            // change them is re-rolling the personality (PKHeX's own SetPIDNature).
            if (entity is G3PKM or G4PKM)
                entity.SetPIDNature((Nature)nature);
            else
                entity.Nature = (Nature)nature;
            changed = true;
        }
        if (edit.Ability is { } ability && entity.Ability != ability)
        {
            entity.Ability = ability;
            changed = true;
        }
        if (edit.HeldItem is { } item && entity.HeldItem != item)
        {
            entity.HeldItem = item;
            changed = true;
        }
        if (edit.Move1 is { } m1 && entity.Move1 != m1) { entity.Move1 = (ushort)m1; changed = true; }
        if (edit.Move2 is { } m2 && entity.Move2 != m2) { entity.Move2 = (ushort)m2; changed = true; }
        if (edit.Move3 is { } m3 && entity.Move3 != m3) { entity.Move3 = (ushort)m3; changed = true; }
        if (edit.Move4 is { } m4 && entity.Move4 != m4) { entity.Move4 = (ushort)m4; changed = true; }
        if (edit.IVs is { Count: 6 } ivs)
        {
            var values = ClampAll(ivs.ToArray(), TrainingCapsOf(entity).IvMax);
            if (!values.SequenceEqual(GetIVsInAppOrder(entity)))
            {
                SetIVsFromAppOrder(entity, values);
                changed = true;
            }
        }
        if (edit.EVs is { Count: 6 } evs)
        {
            var values = ClampAll(evs.ToArray(), TrainingCapsOf(entity).EvMax);
            if (!values.SequenceEqual(GetEVsInAppOrder(entity)))
            {
                SetEVsFromAppOrder(entity, values);
                changed = true;
            }
        }
        if (edit.IsShiny is { } shiny)
        {
            if (shiny && !entity.IsShiny) { entity.SetShiny(); changed = true; }
            else if (!shiny && entity.IsShiny) { entity.SetUnshiny(); changed = true; }
        }
        if (edit.Ball is { } ball && entity.Ball != ball)
        {
            entity.Ball = (byte)ball;
            changed = true;
        }
        if (edit.OriginalTrainer is { } ot && !string.Equals(entity.OriginalTrainerName, ot, StringComparison.Ordinal))
        {
            entity.OriginalTrainerName = ot;
            changed = true;
        }
        if (edit.Gender is { } gender && entity.Gender != Math.Clamp(gender, 0, 2))
        {
            entity.Gender = (byte)Math.Clamp(gender, 0, 2);
            changed = true;
        }
        if (edit.Friendship is { } friendship && entity.CurrentFriendship != Math.Clamp(friendship, 0, 255))
        {
            entity.CurrentFriendship = (byte)Math.Clamp(friendship, 0, 255);
            changed = true;
        }

        if (!changed)
            return;

        entity.RefreshChecksum();
        if (box == -1)
            _save.SetPartySlotAtIndex(entity, slot);
        else
            _save.SetBoxSlotAtIndex(entity, box, slot);
    }

    /// <summary>Engine-internal access for sibling adapters (legalizer); never leaves the assembly.</summary>
    internal PKHeX.Core.SaveFile SaveFile
    {
        get
        {
            ThrowIfDisposed();
            return _save;
        }
    }

    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ThrowIfDisposed();
        if (fromBox == toBox && fromSlot == toSlot) return;

        var source = GetEntityCore(fromBox, fromSlot);
        if (source.Species == 0) return;

        if (fromBox == -1 && toBox == -1)
        {
            // Reorder inside the party: remove at the source, insert at the target
            // (occupied target = swap feel, empty target = move there). Works for any
            // target index 0-5, no out-of-range possible on a compacted party.
            var party = _save.PartyData.ToList();
            if ((uint)fromSlot >= party.Count) return;
            var moving = party[fromSlot];
            party.RemoveAt(fromSlot);
            toSlot = Math.Min(toSlot, party.Count);
            party.Insert(toSlot, moving);
            _save.PartyData = party;
            return;
        }

        if (toBox == -1)
        {
            // Into the party: games append, they never swap into a slot.
            if (_save.PartyCount >= 6) return;
            // Same-format moves return null from the converter; clone instead of aliasing
            // the live slot we are about to empty.
            var moved = EntityConverter.ConvertToType(source, _save.PKMType, out _) ?? source.Clone();
            DeleteEntityCore(fromBox, fromSlot);
            InsertParty(moved);
            return;
        }

        var target = _save.GetBoxSlotAtIndex(toBox, toSlot);
        if (fromBox == -1)
        {
            // Out of the party: the mon lands at the box slot; a displaced box mon joins
            // the party (swap), refused when the party is full.
            if (target.Species != 0 && _save.PartyCount >= 6) return;
            var srcClone = source.Clone();
            _save.SetBoxSlotAtIndex(srcClone, toBox, toSlot);
            DeleteEntityCore(-1, fromSlot);
            if (target.Species != 0)
                InsertParty(EntityConverter.ConvertToType(target, _save.PKMType, out _) ?? target.Clone());
            return;
        }

        // Box to box: plain swap.
        _save.SetBoxSlotAtIndex(source, toBox, toSlot);
        _save.SetBoxSlotAtIndex(target, fromBox, fromSlot);
    }

    /// <summary>Appends a mon to the party (compacting, like the games).</summary>
    private void InsertParty(PKM pk)
    {
        var party = _save.PartyData.ToList();
        party.Add(pk);
        _save.PartyData = party;
    }

    /// <summary>Empties a slot; party slots compact instead of leaving a hole.</summary>
    private void DeleteEntityCore(int box, int slot)
    {
        if (box == -1)
        {
            var party = _save.PartyData.ToList();
            party.RemoveAt(slot);
            _save.PartyData = party;
        }
        else
        {
            _save.SetBoxSlotAtIndex(_save.BlankPKM, box, slot);
        }
    }

    public int Generation => _save.Generation;

    public int PlaceLivingDex(byte[] compressedBundle)
    {
        ThrowIfDisposed();
        using var input = new MemoryStream(compressedBundle);
        using var inflate = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        raw.Position = 0;
        using var reader = new BinaryReader(raw);
        var capacity = _save.BoxCount * _save.BoxSlotCount;
        var placed = 0;
        var bundleGeneration = reader.ReadInt32(); // header: generation, then count
        if (bundleGeneration != _save.Generation)
            return 0; // wrong bundle for this game: refuse, write nothing
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var species = reader.ReadUInt16();
            var length = reader.ReadInt32();
            var data = reader.ReadBytes(length);
            if (placed >= capacity) break;
            var mon = EntityFormat.GetFromBytes(data, _save.Context);
            if (mon is null || mon.Species == 0 || mon.Species != species) continue;
            _save.SetBoxSlotAtIndex(mon, placed / _save.BoxSlotCount, placed % _save.BoxSlotCount);
            placed++;
        }
        return placed;
    }

    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null)
    {
        ThrowIfDisposed();
        var targetBoxes = boxes ?? Enumerable.Range(0, _save.BoxCount).ToList();
        var slotsPerBox = _save.BoxSlotCount;

        // Read every mon from the target boxes once, in stable storage order.
        var mons = new List<PKM>(targetBoxes.Count * slotsPerBox);
        var orderedBoxes = targetBoxes.Where(b => (uint)b < (uint)_save.BoxCount).OrderBy(b => b).ToList();
        foreach (var box in orderedBoxes)
            for (var slot = 0; slot < slotsPerBox; slot++)
            {
                var mon = GetEntityCore(box, slot);
                // PKHeX entities can be backed by the save's mutable storage. Sorting
                // rewrites that same storage, so every source must be detached before
                // the first destination write or later entries can turn into garbage.
                if (mon.Species != 0) mons.Add(mon.Clone());
            }

        int TypeRank(PKM mon) => mon.PersonalInfo.Type1; // dex type order runs types 0..17
        int MetAge(PKM mon) => mon.MetDate?.DayNumber is { } day ? int.MaxValue - Math.Min(day, int.MaxValue - 1) : int.MaxValue;

        mons = criteria switch
        {
            SortCriteria.DexNumber => mons.OrderBy(m => m.Species).ThenBy(m => m.Form).ThenBy(m => m.TID16).ToList(),
            SortCriteria.Alphabetical => mons.OrderBy(m => GameInfo.Strings.specieslist[m.Species], StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Species).ToList(),
            SortCriteria.LevelDesc => mons.OrderByDescending(m => m.CurrentLevel).ThenBy(m => m.Species).ToList(),
            SortCriteria.IvTotalDesc => mons.OrderByDescending(m => m.IVTotal).ThenBy(m => m.Species).ToList(),
            SortCriteria.Type => mons.OrderBy(TypeRank).ThenBy(m => m.Species).ThenBy(m => m.Form).ToList(),
            SortCriteria.AgeOldest => mons.OrderBy(MetAge).ThenBy(m => m.Species).ToList(),
            SortCriteria.ShinyFirst => mons.OrderByDescending(m => m.IsShiny ? 1 : 0).ThenBy(m => m.Species).ThenBy(m => m.Form).ToList(),
            _ => mons,
        };

        // Compact: write back into the target boxes front-first, then blank the tails.
        var placed = 0;
        foreach (var box in orderedBoxes)
        {
            for (var slot = 0; slot < slotsPerBox; slot++)
            {
                if (placed < mons.Count)
                    SetEntityCore(box, slot, mons[placed++]);
                else
                    SetEntityCore(box, slot, _save.BlankPKM);
            }
        }
        return mons.Count;
    }

    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null)
    {
        ThrowIfDisposed();
        var targetBoxes = boxes ?? Enumerable.Range(0, _save.BoxCount).ToList();
        var touched = 0;
        foreach (var box in targetBoxes)
        {
            if ((uint)box >= (uint)_save.BoxCount) continue;
            for (var slot = 0; slot < _save.BoxSlotCount; slot++)
            {
                var entity = GetEntityCore(box, slot);
                if (entity.Species == 0) continue;
                // Gen 3-5 box slots are stored encrypted: the entity we hold is a
                // decrypted copy, so every touched mon must be written back (re-encrypted)
                // or the batch edit lands raw plaintext into the save's storage bytes.
                if (ApplyInstructions(entity, instructions))
                {
                    SetEntityCore(box, slot, entity);
                    touched++;
                }
            }
        }
        return touched;
    }

    /// <summary>Parses ".Prop=Value" instructions against one entity, PKHeX batch-editor style.</summary>
    private static bool ApplyInstructions(PKM entity, IReadOnlyList<string> instructions)
    {
        var changed = false;
        var rnd = Random.Shared;
        foreach (var raw in instructions)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            var prop = (eq > 0 ? line[..eq] : line).Trim().TrimStart('.').ToLowerInvariant();
            var value = eq > 0 ? line[(eq + 1)..].Trim() : "1";

            int ParseValue() => value switch
            {
                "$rand" => rnd.Next(0, 32),
                "$shiny" => 1,
                "$suggest" => 0,
                _ => int.TryParse(value, out var n) ? n : 0,
            };

            switch (prop)
            {
                case "level" or "lv": entity.CurrentLevel = (byte)Math.Clamp(ParseValue(), 1, 100); changed = true; break;
            case "nature":
            {
                // Same PID-derived rule as the single-mon editor: Gen 3/4 nature setters
                // are empty, so the batch editor must roll the personality instead or the
                // edit silently reverts on those games.
                var wanted = (Nature)Math.Clamp(ParseValue(), 0, 24);
                if (entity is G3PKM or G4PKM)
                    entity.SetPIDNature(wanted);
                else
                    entity.Nature = wanted;
                changed = true;
                break;
            }
            case "hypertrain" when entity is IHyperTrain ht:
                ht.HT_HP = ht.HT_ATK = ht.HT_DEF = ht.HT_SPA = ht.HT_SPD = ht.HT_SPE = true;
                changed = true;
                break;
                case "friendship": entity.CurrentFriendship = (byte)Math.Clamp(ParseValue(), 0, 255); changed = true; break;
                case "ball": entity.Ball = (byte)Math.Clamp(ParseValue(), 0, 100); changed = true; break;
                case "helditem" or "item": entity.HeldItem = ParseValue(); changed = true; break;
                case "move1": entity.Move1 = (ushort)ParseValue(); changed = true; break;
                case "move2": entity.Move2 = (ushort)ParseValue(); changed = true; break;
                case "move3": entity.Move3 = (ushort)ParseValue(); changed = true; break;
                case "move4": entity.Move4 = (ushort)ParseValue(); changed = true; break;
                case "iv_hp": case "iv_atk": case "iv_def": case "iv_spa": case "iv_spd": case "iv_spe":
                {
                    var ivs = GetIVsInAppOrder(entity);
                    var index = prop switch { "iv_hp" => 0, "iv_atk" => 1, "iv_def" => 2, "iv_spa" => 3, "iv_spd" => 4, _ => 5 };
                    ivs[index] = Math.Clamp(ParseValue(), 0, TrainingCapsOf(entity).IvMax);
                    SetIVsFromAppOrder(entity, ivs);
                    changed = true;
                    break;
                }
                case "ev_hp": case "ev_atk": case "ev_def": case "ev_spa": case "ev_spd": case "ev_spe":
                {
                    var evs = GetEVsInAppOrder(entity);
                    var index = prop switch { "ev_hp" => 0, "ev_atk" => 1, "ev_def" => 2, "ev_spa" => 3, "ev_spd" => 4, _ => 5 };
                    evs[index] = Math.Clamp(ParseValue(), 0, TrainingCapsOf(entity).EvMax);
                    SetEVsFromAppOrder(entity, evs);
                    changed = true;
                    break;
                }
                case "shiny":
                {
                    var want = value.Equals("yes", StringComparison.OrdinalIgnoreCase) || ParseValue() == 1;
                    if (want && !entity.IsShiny) entity.SetShiny();
                    else if (!want && entity.IsShiny) entity.SetUnshiny();
                    changed = true;
                    break;
                }
                case "nickname":
                    entity.Nickname = value;
                    entity.IsNicknamed = value.Length > 0;
                    changed = true;
                    break;
                case "ot" or "trainer":
                    entity.OriginalTrainerName = value;
                    changed = true;
                    break;
            }
        }
        if (changed) entity.RefreshChecksum();
        return changed;
    }

    public string GetBoxName(int box)
    {
        ThrowIfDisposed();
        if ((uint)box >= (uint)_save.BoxCount) return $"BOX {box + 1:00}";
        var name = _save is IBoxDetailName details ? details.GetBoxName(box) : null;
        return string.IsNullOrWhiteSpace(name) ? $"BOX {box + 1:00}" : name;
    }

    public void SwapBoxes(int a, int b)
    {
        ThrowIfDisposed();
        if (a == b) return;
        if (!_save.SwapBox(a, b))
            throw new InvalidOperationException("This save does not allow one of those boxes to move.");
    }

    public void DeleteBox(int box)
    {
        ThrowIfDisposed();
        if ((uint)box >= (uint)_save.BoxCount) return;
        // Merge into the first box with room; anything that fits nowhere is released.
        for (var slot = 0; slot < _save.BoxSlotCount; slot++)
        {
            var mon = GetEntityCore(box, slot);
            if (mon.Species == 0) continue;
            var landed = false;
            for (var targetBox = 0; targetBox < _save.BoxCount && !landed; targetBox++)
            {
                if (targetBox == box) continue;
                for (var targetSlot = 0; targetSlot < _save.BoxSlotCount; targetSlot++)
                {
                    if (GetEntityCore(targetBox, targetSlot).Species != 0) continue;
                    SetEntityCore(targetBox, targetSlot, mon);
                    landed = true;
                    break;
                }
            }
            SetEntityCore(box, slot, _save.BlankPKM);
        }

        // Save formats have a fixed physical box count. Logical deletion closes the
        // ordering gap and leaves one new empty box at the end. PKHeX's MoveBox assumes
        // boxes are packed at exactly 30*SIZE_STORED bytes, but Gen 5 boxes carry a
        // 16-byte gap (stride 4096 vs 4080), so MoveBox shears the storage and the
        // written save reloads as invalid species. SwapBox uses each format's exact
        // GetBoxOffset, so chaining adjacent swaps moves the box safely on every format.
        for (var target = box; target < _save.BoxCount - 1; target++)
        {
            if (!_save.SwapBox(target, target + 1))
                throw new InvalidOperationException("This save does not allow that box to move.");
        }
    }

    public void ClearBox(int box)
    {
        ThrowIfDisposed();
        if ((uint)box >= (uint)_save.BoxCount) return;
        for (var slot = 0; slot < _save.BoxSlotCount; slot++)
            SetEntityCore(box, slot, _save.BlankPKM);
    }

    public IReadOnlyList<int> GetSpeciesTypes(int species)
    {
        ThrowIfDisposed();
        return GetTypes((ushort)species, 0);
    }

    public BaseStats GetBaseStats(int species)
    {
        ThrowIfDisposed();
        var personal = _save.Personal.GetFormEntry((ushort)species, 0);
        return new BaseStats(personal.HP, personal.ATK, personal.DEF, personal.SPA, personal.SPD, personal.SPE);
    }

    public SlotExport ExportSlot(int box, int slot)
    {
        ThrowIfDisposed();
        var entity = GetEntityCore(box, slot);
        var data = new byte[entity.SIZE_PARTY];
        entity.WriteDecryptedDataParty(data);
        var safeName = string.Concat(entity.FileName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_'));
        return new SlotExport(data, safeName);
    }

    public string GetShowdownText(int box, int slot)
    {
        ThrowIfDisposed();
        return ShowdownParsing.GetShowdownText(GetEntityCore(box, slot));
    }

    public string ExportBoxShowdown(int box)
    {
        ThrowIfDisposed();
        if ((uint)box >= (uint)_save.BoxCount) return string.Empty;
        var sets = new List<string>();
        for (var slot = 0; slot < _save.BoxSlotCount; slot++)
        {
            var entity = GetEntityCore(box, slot);
            if (entity.Species != 0)
                sets.Add(ShowdownParsing.GetShowdownText(entity));
        }
        return string.Join("\n\n", sets);
    }

    public RngInfo GetRngInfo(int box, int slot)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        var entity = GetEntityCore(box, slot);
        return new RngInfo(
            entity.PID,
            entity.Format >= 6 ? entity.EncryptionConstant : null,
            (int)entity.Nature,
            entity.IsShiny,
            entity is not GBPKM,
            GetIVsInAppOrder(entity),
            entity.Ability,
            entity.Gender);
    }

    public TrainingCaps GetTrainingCaps() => _save.Context switch
    {
        EntityContext.Gen1 or EntityContext.Gen2 => new TrainingCaps(15, 65535),
        _ when _save.Generation <= 5 => new TrainingCaps(31, 255),
        _ => new TrainingCaps(31, 252),
    };

    /// <summary>Format truth: Gen 1/2 store 4-bit DVs and 16-bit stat experience;
    /// Gen 3-5 allow 255 EVs per stat; Gen 6+ enforce 252. Writing a bigger raw
    /// value would wrap the underlying storage, so every writer clamps to these.</summary>
    private static TrainingCaps TrainingCapsOf(PKM entity) => entity switch
    {
        GBPKM => new(15, 65535),
        _ when entity.Format is <= 5 => new(31, 255),
        _ => new(31, 252),
    };

    private static int[] ClampAll(int[] values, int max)
    {
        for (var i = 0; i < values.Length; i++)
            values[i] = Math.Clamp(values[i], 0, max);
        return values;
    }

    // PKHeX's array APIs use HP/ATK/DEF/SPE/SPA/SPD. PKForge's UI and domain use
    // the conventional display order HP/ATK/DEF/SPA/SPD/SPE. Keep that translation
    // at the engine boundary so every editor, preset and summary agrees on identity.
    private static int[] GetIVsInAppOrder(PKM entity) =>
        [entity.IV_HP, entity.IV_ATK, entity.IV_DEF, entity.IV_SPA, entity.IV_SPD, entity.IV_SPE];

    private static int[] GetEVsInAppOrder(PKM entity) =>
        [entity.EV_HP, entity.EV_ATK, entity.EV_DEF, entity.EV_SPA, entity.EV_SPD, entity.EV_SPE];

    private static void SetIVsFromAppOrder(PKM entity, IReadOnlyList<int> values) =>
        entity.SetIVs([values[0], values[1], values[2], values[5], values[3], values[4]]);

    private static void SetEVsFromAppOrder(PKM entity, IReadOnlyList<int> values) =>
        entity.SetEVs([values[0], values[1], values[2], values[5], values[3], values[4]]);

    public bool RerollNatureKeepShiny(int box, int slot, int nature)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        var wanted = (Nature)Math.Clamp(nature, 0, 24);
        var entity = GetEntityCore(box, slot);
        if (entity.Species == 0 || entity is GBPKM) return false;

        if (entity is not (G3PKM or G4PKM))
        {
            // Gen 5+ store nature as its own byte: nothing to preserve.
            entity.Nature = wanted;
            entity.RefreshChecksum();
            SetEntityCore(box, slot, entity);
            return true;
        }

        // Gen 3/4: nature is PID % 25. Search a new PID that keeps the shiny state,
        // ability bit, gender (and Unown letter on FR/LG) while landing on the nature.
        var work = entity.Clone();
        var wasShiny = work.IsShiny;
        var abilityBit = work.PID & 1;
        var gender = work.Gender;
        var personal = PersonalTable.B2W2[work.Species];
        var singleGender = PersonalInfo.IsSingleGender(personal.Gender);
        var unown = work.Version is GameVersion.FR or GameVersion.LG && work.Species == (int)Species.Unown;
        var rnd = Random.Shared;
        for (var attempt = 0; attempt < 5_000_000; attempt++)
        {
            var pid = rnd.Rand32();
            if (pid % 25 != (byte)wanted) continue;
            if ((pid & 1) != abilityBit) continue;
            if (unown && EntityPID.GetUnownForm3(pid) != work.Form) continue;
            if (!singleGender && EntityGender.GetFromPIDAndRatio(pid, personal.Gender) != gender) continue;
            work.PID = pid;
            if (work.IsShiny != wasShiny) continue;
            work.RefreshChecksum();
            SetEntityCore(box, slot, work);
            return true;
        }
        return false;
    }

    public IReadOnlyList<int> GetMissingSpecies()
    {
        ThrowIfDisposed();
        var owned = new HashSet<int>();
        for (var box = 0; box < _save.BoxCount; box++)
        for (var slot = 0; slot < _save.BoxSlotCount; slot++)
        {
            var species = GetEntityCore(box, slot).Species;
            if (species != 0) owned.Add(species);
        }
        var missing = new List<int>();
        for (ushort species = 1; species <= _save.MaxSpeciesID; species++)
            if (!owned.Contains(species)) missing.Add(species);
        return missing;
    }

    public DexEntryState GetDexEntry(int species)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(species, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(species, _save.MaxSpeciesID);
        var id = (ushort)species;
        return new DexEntryState(_save.GetSeen(id), _save.GetCaught(id));
    }

    public void SetDexEntry(int species, bool seen, bool caught)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(species, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(species, _save.MaxSpeciesID);
        var id = (ushort)species;
        SetDexFlagsCore(id, seen, caught);
    }

    /// <summary>Dex setters only exist on SaveFile for Gen 1/2/3/6 — every other
    /// generation writes its Zukan block directly or the edit silently no-ops.</summary>
    private void SetDexFlagsCore(ushort species, bool seen, bool caught)
    {
        switch (_save)
        {
            case SAV5 { Zukan: { } z5 }:
                if (seen)
                {
                    z5.SetSeen(species, 0, false, true);
                    z5.SetSeen(species, 1, false, true);
                }
                else
                {
                    z5.ClearSeen(species);
                }
                z5.SetCaught(species, caught);
                return;
            case SAV7 { Zukan: { } z7 }:
                z7.SetSeen(species, seen);
                z7.SetCaught(species, caught);
                return;
            case SAV4 { Dex: { } z4 }:
                z4.SetSeen(species, seen);
                z4.SetCaught(species, caught);
                return;
            case SAV8SWSH { Zukan: { } z8 }:
                for (var region = 0; region < 4; region++)
                    z8.SetSeenRegion(species, 0, region, seen);
                z8.SetCaught(species, caught);
                return;
            case SAV8BS { Zukan: { } z8b }:
                z8b.SetState(species, caught ? ZukanState8b.Caught
                    : seen ? ZukanState8b.Seen
                    : ZukanState8b.None);
                return;
            case SAV9SV { Zukan: { } z9 }:
            {
                if (caught)
                {
                    z9.SetDexEntryAll(species);
                }
                else if (!seen)
                {
                    z9.ClearDexEntryAll(species);
                }
                else if (z9.GetRevision() == (int)DexBlockMode9.Kitakami)
                {
                    // 2.0+ saves only expose the combined entry API.
                    z9.SetDexEntryAll(species);
                }
                else
                {
                    var entry = z9.DexPaldea.Get(species);
                    entry.SetSeen(true);
                    entry.SetCaught(false);
                }
                return;
            }
            default:
                _save.SetSeen(species, seen);
                _save.SetCaught(species, caught);
                return;
        }
    }

    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport()
    {
        ThrowIfDisposed();
        var catches = new List<(PKM Mon, int Box, int Slot)>();
        for (var box = 0; box < _save.BoxCount; box++)
        for (var slot = 0; slot < _save.BoxSlotCount; slot++)
        {
            var entity = GetEntityCore(box, slot);
            if (entity.Species != 0 && !entity.IsEgg && entity.MetLocation != 0)
                catches.Add((entity.Clone(), box, slot));
        }

        var report = new List<NuzlockeCatch>();
        foreach (var group in catches.GroupBy(c => c.Mon.MetLocation).OrderBy(g => g.Key))
        {
            var ordered = group
                .OrderBy(c => c.Mon.MetDate?.DayNumber ?? int.MaxValue)
                .ThenBy(c => c.Box).ThenBy(c => c.Slot).ToList();
            var first = ordered[0].Mon;
            var route = GameInfo.GetLocationName(false, first.MetLocation, first.Format, first.Generation, first.Version)
                ?? $"#{first.MetLocation}";
            for (var i = 0; i < ordered.Count; i++)
            {
                var mon = ordered[i].Mon;
                report.Add(new NuzlockeCatch(
                    route, mon.Species,
                    mon.IsNicknamed ? mon.Nickname : SpeciesName.GetSpeciesName(mon.Species, (int)LanguageID.English),
                    FirstCatch: i == 0,
                    mon.MetDate?.ToString("yyyy-MM-dd")));
            }
        }
        return report;
    }

    public void ReleaseSlot(int box, int slot)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        DeleteEntityCore(box, slot);
    }

    public bool ImportSlot(int box, int slot, byte[] fileBytes)
    {
        ThrowIfDisposed();
        var imported = EntityFormat.GetFromBytes(fileBytes);
        if (imported is null) return false;
        var converted = EntityConverter.ConvertToType(imported, _save.PKMType, out _);
        if (converted is null) return false;
        if (box == -1)
        {
            if (_save.PartyCount >= 6) return false;
            InsertParty(converted);
            return true;
        }
        _save.SetBoxSlotAtIndex(converted, box, slot);
        return true;
    }

    public TrainerInfo GetTrainer()
    {
        ThrowIfDisposed();
        return new TrainerInfo(_save.OT, _save.TID16, _save.SID16, ReadMoneySafe(), _save.Gender);
    }

    public void SetTrainer(TrainerInfo trainer)
    {
        ThrowIfDisposed();
        _save.OT = trainer.Name;
        if (!string.Equals(_save.OT, trainer.Name, StringComparison.Ordinal))
            throw new InvalidOperationException("This save's character set cannot store that trainer name.");
        _save.TID16 = (ushort)Math.Clamp(trainer.TID, 0, ushort.MaxValue);
        _save.SID16 = (ushort)Math.Clamp(trainer.SID, 0, ushort.MaxValue);
        // Some contexts (SV blanks, for example) do not carry a typed money block;
        // identity edits must still work there.
        if (ReadMoneySafe() != trainer.Money)
        {
            try { _save.Money = trainer.Money; }
            catch (ArgumentOutOfRangeException) { }
        }
        _save.Gender = (byte)Math.Clamp(trainer.Gender, 0, 1);
    }

    private uint ReadMoneySafe()
    {
        try { return _save.Money; }
        catch (ArgumentOutOfRangeException) { return 0; }
    }

    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile = null)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        var source = GetEntity(box, slot);
        if (source.Species == 0)
            return new GenerationOutcome(false, "Empty slot.");

        var entity = source.Clone();
        if (!MakeOwned(entity, profile, out var failure))
            return new GenerationOutcome(false, failure ?? "That ownership change is not legal, so nothing was changed.");

        if (box == -1)
            _save.SetPartySlotAtIndex(entity, slot);
        else
            _save.SetBoxSlotAtIndex(entity, box, slot);
        var ownerName = profile?.DisplayName ?? _save.OT;
        return new GenerationOutcome(true, $"Made yours using {ownerName}.");
    }

    /// <summary>Rewrites one entity's ownership in place. The caller must clone first
    /// when the source bytes must remain untouched on failure.</summary>
    internal bool MakeOwned(PKM entity, TrainerProfile? profile, out string? failure)
    {
        failure = null;
        var before = new LegalityAnalysis(entity);
        if (!IsPlayerOriginalTrainer(before.EncounterOriginal))
        {
            failure = "This Pokémon has a fixed event or in-game-trade OT and cannot be made yours legally.";
            return false;
        }

        var wasShiny = entity.IsShiny;
        entity.OriginalTrainerName = profile?.OriginalTrainer ?? _save.OT;
        entity.TID16 = (ushort)Math.Clamp(profile?.TID ?? _save.TID16, 0, ushort.MaxValue);
        entity.SID16 = entity.Format < 3 || entity.VC
            ? (ushort)0
            : (ushort)Math.Clamp(profile?.SID ?? _save.SID16, 0, ushort.MaxValue);
        entity.OriginalTrainerGender = (byte)Math.Clamp(profile?.Gender ?? _save.Gender, 0, 1);
        var ownerInfo = new SimpleTrainerInfo(_save)
        {
            OT = entity.OriginalTrainerName,
            TID16 = entity.TID16,
            SID16 = entity.SID16,
            Gender = entity.OriginalTrainerGender,
        };
        entity.SetHandlerAndMemory(ownerInfo, before.EncounterOriginal);
        // Handler repair follows PKHeX transfer rules (VC entities cannot store a SID,
        // for example); ownership is reapplied within those format limits.
        entity.OriginalTrainerName = ownerInfo.OT;
        entity.TID16 = ownerInfo.TID16;
        entity.SID16 = entity.Format < 3 || entity.VC ? (ushort)0 : ownerInfo.SID16;
        entity.OriginalTrainerGender = ownerInfo.Gender;

        if (entity.IsShiny != wasShiny)
        {
            if (wasShiny) entity.SetShiny();
            else entity.SetUnshiny();
        }
        if (entity is IObedienceLevel obedience)
            obedience.ObedienceLevel = obedience.GetSuggestedObedienceLevel(entity, (byte)entity.MetLevel);

        entity.RefreshChecksum();
        var after = new LegalityAnalysis(entity);
        if (!after.Valid)
        {
            var first = after.Report(verbose: false)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "no legal combination";
            failure = $"That ownership change would make this Pokémon illegal ({first}), so nothing was changed.";
            return false;
        }
        return true;
    }

    /// <summary>Mirrors PKHeX's trainer-name verifier: fixed-OT trades, event gifts,
    /// and fixed-ID encounters cannot be rewritten as a player catch.</summary>
    private static bool IsPlayerOriginalTrainer(IEncounterTemplate encounter) => encounter switch
    {
        IFixedTrainer { IsFixedTrainer: true } => false,
        MysteryGift { IsEgg: false } => false,
        ITrainerID16ReadOnly => false,
        _ => true,
    };

    public DexProgress GetDexProgress()
    {
        ThrowIfDisposed();
        var seen = 0;
        var caught = 0;
        for (ushort species = 1; species <= _save.MaxSpeciesID; species++)
        {
            if (_save.GetSeen(species)) seen++;
            if (_save.GetCaught(species)) caught++;
        }
        return new DexProgress(seen, caught, _save.MaxSpeciesID);
    }

    public void CompleteDex()
    {
        ThrowIfDisposed();
        for (ushort species = 1; species <= _save.MaxSpeciesID; species++)
            SetDexFlagsCore(species, seen: true, caught: true);
    }

    public IReadOnlyList<BagPouch> GetBag()
    {
        ThrowIfDisposed();
        return _save.Inventory.Pouches
            .Select(pouch => new BagPouch(
                pouch.Type.ToString(),
                pouch.Items.Where(item => item.Index != 0 && item.Count > 0)
                    .Select(item => new BagItem(item.Index, item.Count)).ToList()))
            .ToList();
    }

    /// <summary>Highest species id this game's dex supports (per its personal table).</summary>
    public int MaxSpeciesID => _save.MaxSpeciesID;

    public IReadOnlyList<string> GetItemNames()
    {
        ThrowIfDisposed();
        return GameInfo.Strings.GetItemStrings(_save.Context, _save.Version);
    }

    public IReadOnlyList<int> GetPouchLegalItems(string pouchName)
    {
        ThrowIfDisposed();
        var bag = _save.Inventory;
        var pouch = bag.Pouches.FirstOrDefault(p => p.Type.ToString() == pouchName);
        if (pouch is null) return [];
        return bag.Info.GetItems(pouch.Type).ToArray().Select(id => (int)id).ToList();
    }

    public int SetItemCount(string pouchName, int itemId, int count)
    {
        ThrowIfDisposed();
        var bag = _save.Inventory;
        var pouch = bag.Pouches.FirstOrDefault(p => p.Type.ToString() == pouchName)
            ?? throw new InvalidOperationException($"No pouch named {pouchName}.");
        count = bag.Clamp(pouch.Type, itemId, count);

        var existing = pouch.Items.FirstOrDefault(i => i.Index == itemId);
        if (existing is not null)
        {
            existing.Count = count;
            if (count <= 0) existing.Index = 0;
        }
        else if (count > 0)
        {
            var empty = pouch.Items.FirstOrDefault(i => i.Index == 0)
                ?? throw new InvalidOperationException("The pouch is full.");
            empty.Index = itemId;
            empty.Count = count;
        }
        bag.CopyTo(_save);
        return count;
    }

    public MetInfo GetMetInfo(int box, int slot)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        var e = GetEntityCore(box, slot);
        string Loc(bool egg, ushort loc) => GameInfo.GetLocationName(egg, loc, e.Format, e.Generation, e.Version) ?? $"#{loc}";
        var met = e.MetDate;
        var eggDate = e.EggMetDate;
        return new MetInfo(
            e.MetLocation, Loc(false, e.MetLocation), e.MetLevel,
            met?.ToString("yyyy-MM-dd") ?? "", met is not null || SupportsDate(e, egg: false),
            e.IsEgg, e.EggLocation, Loc(true, e.EggLocation),
            eggDate?.ToString("yyyy-MM-dd") ?? "", eggDate is not null || SupportsDate(e, egg: true),
            (int)e.Version, GameInfo.GetVersionName(e.Version),
            e.Language, LanguageName(e),
            e is IFatefulEncounter { FatefulEncounter: true },
            e.TID16, e.SID16);
    }

    // MetDate/EggMetDate return null both when unset AND when unsupported by the format;
    // a probe write tells the two apart so the UI only offers dates the format keeps.
    private static bool SupportsDate(PKM e, bool egg)
    {
        if (egg) { var v = e.EggMetDate; return v is not null || TryProbeEggDate(e); }
        var m = e.MetDate; return m is not null || TryProbeMetDate(e);
    }

    private static bool TryProbeMetDate(PKM e)
    {
        var original = e.MetDate;
        e.MetDate = new DateOnly(2000, 1, 1);
        var supported = e.MetDate is not null;
        e.MetDate = original;
        return supported;
    }

    private static bool TryProbeEggDate(PKM e)
    {
        var original = e.EggMetDate;
        e.EggMetDate = new DateOnly(2000, 1, 1);
        var supported = e.EggMetDate is not null;
        e.EggMetDate = original;
        return supported;
    }

    private static string LanguageName(PKM e)
    {
        foreach (var choice in GameInfo.LanguageDataSource(e.Generation, e.Context))
            if (choice.Value == e.Language) return choice.Text;
        return $"#{e.Language}";
    }

    public void ApplyMetEdit(int box, int slot, MetEdit edit)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(edit);
        ValidateCoordinates(box, slot);
        var e = GetEntityCore(box, slot);
        if (e.Species == 0) throw new InvalidOperationException("Cannot edit an empty slot.");

        if (edit.Version is { } version) e.Version = (GameVersion)version;
        if (edit.Language is { } language) e.Language = language;
        if (edit.MetLocation is { } metLoc) e.MetLocation = (ushort)Math.Clamp(metLoc, 0, ushort.MaxValue);
        if (edit.MetLevel is { } metLevel) e.MetLevel = (byte)Math.Clamp(metLevel, 0, 100);
        if (edit.EggLocation is { } eggLoc) e.EggLocation = (ushort)Math.Clamp(eggLoc, 0, ushort.MaxValue);
        if (edit.IsEgg is { } isEgg) e.IsEgg = isEgg;
        if (edit.Fateful is { } fateful && e is IFatefulEncounter f) f.FatefulEncounter = fateful;
        if (edit.TID is { } tid) e.TID16 = (ushort)Math.Clamp(tid, 0, ushort.MaxValue);
        if (edit.SID is { } sid) e.SID16 = (ushort)Math.Clamp(sid, 0, ushort.MaxValue);
        if (edit.MetDate is { } metDate) e.MetDate = ParseDate(metDate);
        if (edit.EggDate is { } eggDate) e.EggMetDate = ParseDate(eggDate);

        e.RefreshChecksum();
        SetEntityCore(box, slot, e);
    }

    private static DateOnly? ParseDate(string value) =>
        DateOnly.TryParse(value, out var date) ? date : null;

    public IReadOnlyList<NamedChoice> GetLocationChoices(int box, int slot, bool egg)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        var e = GetEntityCore(box, slot);
        return GameInfo.GetLocationList(e.Version, e.Context, egg)
            .Select(c => new NamedChoice(c.Value, c.Text))
            .ToList();
    }

    public IReadOnlyList<NamedChoice> GetVersionChoices()
    {
        ThrowIfDisposed();
        return GameInfo.Sources.VersionDataSource
            .Where(c => c.Value > 0)
            .Select(c => new NamedChoice(c.Value, c.Text))
            .ToList();
    }

    public IReadOnlyList<NamedChoice> GetLanguageChoices(int box, int slot)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        var e = GetEntityCore(box, slot);
        return GameInfo.LanguageDataSource(e.Generation, e.Context)
            .Select(c => new NamedChoice(c.Value, c.Text))
            .ToList();
    }

    public IReadOnlyList<string> GetFormChoices(int species)
    {
        ThrowIfDisposed();
        var strings = GameInfo.Strings; // app language, cached by the engine
        return FormConverter.GetFormList((ushort)species, strings.Types, strings.forms, _save.Context);
    }

    public IReadOnlyList<int> GetAbilityChoices(int species, int form)
    {
        ThrowIfDisposed();
        var personal = _save.Personal.GetFormEntry((ushort)species, (byte)form);
        var abilities = new List<int>(personal.AbilityCount);
        for (var i = 0; i < personal.AbilityCount; i++)
        {
            var ability = personal.GetAbilityAtIndex(i);
            if (ability != 0 && !abilities.Contains(ability))
                abilities.Add(ability);
        }
        return abilities;
    }

    // ── Potential: Tera type, Hyper Training, ability slot (gen-gated) ──

    private static readonly string[] AbilitySlotLabels = ["Slot 1", "Slot 2", "Hidden"];

    public PotentialInfo GetPotential(int box, int slot)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        var e = GetEntityCore(box, slot);
        var types = GameInfo.GetStrings("en").Types;
        string TypeName(int id) => id == TeraTypeUtil.Stellar ? types[TeraTypeUtil.StellarTypeDisplayStringIndex]
            : (uint)id < (uint)types.Count ? types[id] : $"#{id}";

        var supportsTera = e is ITeraType;
        var teraType = 0;
        var teraLocked = false;
        if (e is ITeraType tera)
        {
            teraType = (int)tera.GetTeraType();
            teraLocked = !TeraTypeUtil.CanChangeTeraType(e.Species);
        }

        var supportsHt = e is IHyperTrain;
        IReadOnlyList<bool> trained = e is IHyperTrain ht
            ? [ht.HT_HP, ht.HT_ATK, ht.HT_DEF, ht.HT_SPA, ht.HT_SPD, ht.HT_SPE]
            : new bool[6];

        // Ability slot (capsule / patch semantics): offered when the species has more than one slot.
        var personal = _save.Personal.GetFormEntry(e.Species, e.Form);
        var abilitySlot = e.AbilityNumber switch { 1 => 0, 2 => 1, 4 => 2, _ => 0 };
        var abilityNames = GameInfo.GetStrings("en").abilitylist;
        var slots = new List<NamedChoice>(personal.AbilityCount);
        for (var i = 0; i < personal.AbilityCount; i++)
        {
            var ability = personal.GetAbilityAtIndex(i);
            var name = (uint)ability < (uint)abilityNames.Length ? abilityNames[ability] : $"#{ability}";
            var label = (uint)i < (uint)AbilitySlotLabels.Length ? AbilitySlotLabels[i] : $"Slot {i + 1}";
            slots.Add(new NamedChoice(i, $"{label} · {name}"));
        }

        return new PotentialInfo(
            supportsTera, teraType, TypeName(teraType), supportsTera ? TypeName((int)((ITeraType)e).TeraTypeOriginal) : "",
            teraLocked, supportsHt, trained, personal.AbilityCount > 1, abilitySlot, slots);
    }

    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(edit);
        ValidateCoordinates(box, slot);
        var e = GetEntityCore(box, slot);
        if (e.Species == 0) throw new InvalidOperationException("Cannot edit an empty slot.");

        if (edit.TeraType is { } teraType)
        {
            if (e is not ITeraType tera)
                throw new InvalidOperationException("This format has no Tera Type data.");
            if (!TeraTypeUtil.CanChangeTeraType(e.Species))
                throw new InvalidOperationException("This Pokémon's Tera Type is fixed.");
            // Only 0-17 or the Stellar magic value; anything else falls back to Normal.
            var value = teraType == TeraTypeUtil.Stellar ? (byte)TeraTypeUtil.Stellar
                : (byte)Math.Clamp(teraType, 0, TeraTypeUtil.MaxType);
            tera.SetTeraType(value);
        }
        if (edit.HyperTrained is { Count: 6 } trained)
        {
            if (e is not IHyperTrain ht)
                throw new InvalidOperationException("This format has no Hyper Training data.");
            ht.HT_HP = trained[0];
            ht.HT_ATK = trained[1];
            ht.HT_DEF = trained[2];
            ht.HT_SPA = trained[3];
            ht.HT_SPD = trained[4];
            ht.HT_SPE = trained[5];
        }
        if (edit.AbilitySlot is { } abilitySlot)
        {
            var personal = _save.Personal.GetFormEntry(e.Species, e.Form);
            if ((uint)abilitySlot >= personal.AbilityCount)
                throw new InvalidOperationException("This species has no ability in that slot.");
            e.RefreshAbility(abilitySlot);
        }

        e.RefreshChecksum();
        SetEntityCore(box, slot, e);
    }

    public IReadOnlyList<NamedChoice> GetTeraTypeChoices()
    {
        ThrowIfDisposed();
        var types = GameInfo.GetStrings("en").Types;
        var choices = new List<NamedChoice>(TeraTypeUtil.MaxType + 2);
        for (var i = 0; i <= TeraTypeUtil.MaxType; i++)
            choices.Add(new NamedChoice(i, types[i]));
        choices.Add(new NamedChoice(TeraTypeUtil.Stellar, types[TeraTypeUtil.StellarTypeDisplayStringIndex]));
        return choices;
    }

    private int[] GetTypes(ushort species, byte form)
    {
        var personal = _save.Personal.GetFormEntry(species, form);
        return personal.Type1 == personal.Type2 ? [personal.Type1] : [personal.Type1, personal.Type2];
    }

    public ReadOnlyMemory<byte> Serialize()
    {
        ThrowIfDisposed();
        return _save.Write();
    }

    public bool ValidateUnchangedRoundTrip() => _save.Write().Span.SequenceEqual(_originalBytes);

    private SaveSnapshot BuildSnapshot(string? displayName)
    {
        var slots = new List<SlotSummary>(_save.BoxCount * _save.BoxSlotCount + 6);
        for (var box = 0; box < _save.BoxCount; box++)
        for (var slot = 0; slot < _save.BoxSlotCount; slot++)
        {
            var entity = _save.GetBoxSlotAtIndex(box, slot);
            slots.Add(new SlotSummary(box, slot, entity.Species == 0 ? null : entity.Species,
                entity.IsNicknamed ? entity.Nickname : null, entity.IsShiny,
                entity.Species == 0 || entity.Valid, entity.Form));
        }
        for (var i = 0; i < _save.PartyCount && i < 6; i++)
        {
            var partyMon = _save.GetPartySlotAtIndex(i);
            slots.Add(new SlotSummary(-1, i, partyMon.Species == 0 ? null : partyMon.Species,
                partyMon.Species == 0 ? null : partyMon.Nickname, partyMon.IsShiny, true, partyMon.Form));
        }
        return new SaveSnapshot(_save.Context.ToString(), _save.Generation, _originalBytes, slots, displayName);
    }

    private void ValidateCoordinates(int box, int slot)
    {
        if (box == -1)
        {
            if ((uint)slot >= 6u)
                throw new ArgumentOutOfRangeException($"Party slot {slot} is outside this save's storage.");
            return;
        }
        if ((uint)box >= (uint)_save.BoxCount || (uint)slot >= (uint)_save.BoxSlotCount)
            throw new ArgumentOutOfRangeException($"Box/slot {box}/{slot} is outside this save's storage.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SaveEngineSession));
    }

    public void Dispose() => _disposed = true;
}

/// <summary>Runs pinned PKHeX offline legality analysis and formats a human-readable report.</summary>
public sealed class LegalityService : ILegalityService
{
    public LegalityReport Analyze(ISaveEngineSession session, int box, int slot)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not SaveEngineSession engineSession)
            throw new ArgumentException("Session was not created by this engine.", nameof(session));

        var detail = engineSession.ReadEntity(box, slot);
        if (detail.IsEmpty)
            return new LegalityReport(true, ["Empty slot."]);

        var analysis = new LegalityAnalysis(engineSession.GetEntity(box, slot));
        var report = analysis.Report(verbose: false);
        var lines = report.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new LegalityReport(analysis.Valid, lines.Length == 0 ? ["No findings."] : lines);
    }
}
