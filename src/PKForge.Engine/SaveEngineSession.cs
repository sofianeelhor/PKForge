using PKForge.Domain;
using PKHeX.Core;

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
        return _save.GetBoxSlotAtIndex(box, slot);
    }

    public EntityDetail ReadEntity(int box, int slot)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        var entity = _save.GetBoxSlotAtIndex(box, slot);
        if (entity.Species == 0)
            return new EntityDetail(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);

        Span<int> ivs = stackalloc int[6];
        Span<int> evs = stackalloc int[6];
        entity.GetIVs(ivs);
        entity.GetEVs(evs);
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
            ivs.ToArray(), evs.ToArray(),
            entity.IsShiny,
            entity.Ball,
            entity.OriginalTrainerName,
            GetTypes(entity.Species, entity.Form),
            entity.Gender,
            entity.CurrentFriendship,
            ComputeStats(entity));
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
        var entity = _save.GetBoxSlotAtIndex(box, slot);
        if (entity.Species == 0)
            throw new InvalidOperationException("Cannot edit an empty slot.");

        if (edit.Species is { } species)
        {
            entity.Species = (ushort)species;
            entity.Form = 0;
        }
        if (edit.Nickname is { } nickname)
        {
            entity.Nickname = nickname;
            entity.IsNicknamed = true;
        }
        if (edit.Level is { } level)
            entity.CurrentLevel = (byte)Math.Clamp(level, 1, 100);
        if (edit.Nature is { } nature)
            entity.Nature = (Nature)nature;
        if (edit.Ability is { } ability)
            entity.Ability = ability;
        if (edit.HeldItem is { } item)
            entity.HeldItem = item;
        if (edit.Move1 is { } m1) entity.Move1 = (ushort)m1;
        if (edit.Move2 is { } m2) entity.Move2 = (ushort)m2;
        if (edit.Move3 is { } m3) entity.Move3 = (ushort)m3;
        if (edit.Move4 is { } m4) entity.Move4 = (ushort)m4;
        if (edit.IVs is { Count: 6 } ivs)
            entity.SetIVs(ivs.ToArray());
        if (edit.EVs is { Count: 6 } evs)
            entity.SetEVs(evs.ToArray());
        if (edit.IsShiny is { } shiny)
        {
            if (shiny && !entity.IsShiny) entity.SetShiny();
            else if (!shiny && entity.IsShiny) entity.SetUnshiny();
        }
        if (edit.Ball is { } ball)
            entity.Ball = (byte)ball;
        if (edit.OriginalTrainer is { } ot)
            entity.OriginalTrainerName = ot;
        if (edit.Gender is { } gender)
            entity.Gender = (byte)Math.Clamp(gender, 0, 2);
        if (edit.Friendship is { } friendship)
            entity.CurrentFriendship = (byte)Math.Clamp(friendship, 0, 255);

        entity.RefreshChecksum();
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
        var source = _save.GetBoxSlotAtIndex(fromBox, fromSlot);
        var target = _save.GetBoxSlotAtIndex(toBox, toSlot);
        _save.SetBoxSlotAtIndex(target, fromBox, fromSlot);
        _save.SetBoxSlotAtIndex(source, toBox, toSlot);
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
        var entity = _save.GetBoxSlotAtIndex(box, slot);
        var data = new byte[entity.SIZE_PARTY];
        entity.WriteDecryptedDataParty(data);
        var safeName = string.Concat(entity.FileName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_'));
        return new SlotExport(data, safeName);
    }

    public string GetShowdownText(int box, int slot)
    {
        ThrowIfDisposed();
        return ShowdownParsing.GetShowdownText(_save.GetBoxSlotAtIndex(box, slot));
    }

    public void ReleaseSlot(int box, int slot)
    {
        ThrowIfDisposed();
        _save.SetBoxSlotAtIndex(_save.BlankPKM, box, slot);
    }

    public bool ImportSlot(int box, int slot, byte[] fileBytes)
    {
        ThrowIfDisposed();
        var imported = EntityFormat.GetFromBytes(fileBytes);
        if (imported is null) return false;
        var converted = EntityConverter.ConvertToType(imported, _save.PKMType, out _);
        if (converted is null) return false;
        _save.SetBoxSlotAtIndex(converted, box, slot);
        return true;
    }

    public TrainerInfo GetTrainer()
    {
        ThrowIfDisposed();
        return new TrainerInfo(_save.OT, _save.TID16, _save.SID16, _save.Money, _save.Gender);
    }

    public void SetTrainer(TrainerInfo trainer)
    {
        ThrowIfDisposed();
        _save.OT = trainer.Name;
        _save.TID16 = (ushort)Math.Clamp(trainer.TID, 0, ushort.MaxValue);
        _save.SID16 = (ushort)Math.Clamp(trainer.SID, 0, ushort.MaxValue);
        _save.Money = trainer.Money;
        _save.Gender = (byte)Math.Clamp(trainer.Gender, 0, 1);
    }

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
        {
            _save.SetSeen(species, true);
            _save.SetCaught(species, true);
        }
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

    public IReadOnlyList<int> GetPouchLegalItems(string pouchName)
    {
        ThrowIfDisposed();
        var bag = _save.Inventory;
        var pouch = bag.Pouches.FirstOrDefault(p => p.Type.ToString() == pouchName);
        if (pouch is null) return [];
        return bag.Info.GetItems(pouch.Type).ToArray().Select(id => (int)id).ToList();
    }

    public void SetItemCount(string pouchName, int itemId, int count)
    {
        ThrowIfDisposed();
        var bag = _save.Inventory;
        var pouch = bag.Pouches.FirstOrDefault(p => p.Type.ToString() == pouchName)
            ?? throw new InvalidOperationException($"No pouch named {pouchName}.");

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
    }

    public MetInfo GetMetInfo(int box, int slot)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        var e = _save.GetBoxSlotAtIndex(box, slot);
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
        var e = _save.GetBoxSlotAtIndex(box, slot);
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
        _save.SetBoxSlotAtIndex(e, box, slot);
    }

    private static DateOnly? ParseDate(string value) =>
        DateOnly.TryParse(value, out var date) ? date : null;

    public IReadOnlyList<NamedChoice> GetLocationChoices(int box, int slot, bool egg)
    {
        ThrowIfDisposed();
        ValidateCoordinates(box, slot);
        var e = _save.GetBoxSlotAtIndex(box, slot);
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
        var e = _save.GetBoxSlotAtIndex(box, slot);
        return GameInfo.LanguageDataSource(e.Generation, e.Context)
            .Select(c => new NamedChoice(c.Value, c.Text))
            .ToList();
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
        var e = _save.GetBoxSlotAtIndex(box, slot);
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
        var e = _save.GetBoxSlotAtIndex(box, slot);
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
        _save.SetBoxSlotAtIndex(e, box, slot);
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
        var slots = new List<SlotSummary>(_save.BoxCount * _save.BoxSlotCount);
        for (var box = 0; box < _save.BoxCount; box++)
        for (var slot = 0; slot < _save.BoxSlotCount; slot++)
        {
            var entity = _save.GetBoxSlotAtIndex(box, slot);
            slots.Add(new SlotSummary(box, slot, entity.Species == 0 ? null : entity.Species,
                entity.IsNicknamed ? entity.Nickname : null, entity.IsShiny,
                entity.Species == 0 || entity.Valid, entity.Form));
        }
        return new SaveSnapshot(_save.Context.ToString(), _save.Generation, _originalBytes, slots, displayName);
    }

    private void ValidateCoordinates(int box, int slot)
    {
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
