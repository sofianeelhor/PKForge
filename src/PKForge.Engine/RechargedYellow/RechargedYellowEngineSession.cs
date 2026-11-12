using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine.RechargedYellow;

/// <summary>
/// A Pokémon Recharged Yellow save as a first-class PKForge session.
///
/// The hack keeps retail Gen 3 Pokémon records and PC storage but repacks
/// SaveBlock1, so PKHeX's Emerald offsets put the party at the wrong place (the
/// reported "PC shows, team does not" bug) and money, flags and vars elsewhere.
/// This session builds a retail-shaped image for PKHeX to edit, then maps the
/// edited fields back into the hack's layout. The party and boxes are therefore
/// edited by the real PKHeX engine (stats, crypto, legality-free Gen 3 data),
/// while the session owns the hack's own bag offsets and capacities.
///
/// Not supported, and refused rather than silently written to scratch space: the
/// Day Care (the hack stores none), the RTC repair flow (the clock moved into an
/// inserted SaveBlock2 region), Mystery Gift storage, the Battle Frontier records
/// and trainer Battle Points (moved out of SaveBlock2).
/// </summary>
internal sealed class RechargedYellowEngineSession : ISaveEngineSession
{
    public const string SnapshotTag = "PK3";
    public const string GameName = "Recharged Yellow";

    private readonly byte[] _originalBytes;
    private readonly byte[] _payload;
    private readonly int[] _sections;
    private readonly RechargedYellowFormat.Profile _profile;
    private readonly SaveEngineSession _inner;
    private readonly string? _displayName;

    private uint? _moneyOverride;
    private ushort? _coinsOverride;
    private bool _disposed;

    public RechargedYellowEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _originalBytes = bytes.ToArray();
        var decoded = SramPadding.Trim(RetroArchSaveContainer.Decode(bytes.Span));
        if (decoded.Length != RechargedYellowFormat.FileSize || !RechargedYellowFormat.LooksLike(decoded))
            throw new InvalidDataException("These bytes are not a Pokémon Recharged Yellow save.");

        _payload = decoded.ToArray();
        if (!RechargedYellowFormat.TryGetLayout(_payload, out _, out _sections, out _profile))
            throw new InvalidDataException("This Recharged Yellow save has no complete live save slot.");

        var image = RechargedYellowFormat.BuildVirtual(_payload, _sections, _profile);
        _inner = new SaveEngineSession(image, displayName);
        _displayName = displayName;
    }

    public void MakeActive() => _inner.MakeActive();

    public SaveSnapshot Snapshot
    {
        get
        {
            ThrowIfDisposed();
            var snapshot = _inner.Snapshot;
            return snapshot with
            {
                Format = SnapshotTag,
                OriginalBytes = _originalBytes.ToArray(),
                DisplayName = _displayName,
            };
        }
    }

    public int Generation => 3;
    public IReadOnlyList<string> GameNames => [GameName];
    public int MaxSpeciesId => _inner.MaxSpeciesId;
    public bool SupportsBoxTools => false;
    public bool SupportsRTCRepair => false;
    public bool SupportsLegalFashionUnlock => false;
    public bool SupportsStylePoints => false;
    public bool SupportsLegalityAnalysis => false;
    /// <summary>Abilities are stored as a slot, never as a free id: HaX cannot pick an arbitrary one.</summary>
    public bool StoresAnyAbility => false;

    public bool StoresMove(int move) => _inner.StoresMove(move);
    public bool SupportsCompassSettings => false;
    public SaveBadges? GetBadges() { ThrowIfDisposed(); return _inner.GetBadges(); }
    public IReadOnlyList<JournalMon> GetJournalRoster() { ThrowIfDisposed(); return _inner.GetJournalRoster(); }

    public EntityDetail ReadEntity(int box, int slot) { ThrowIfDisposed(); return _inner.ReadEntity(box, slot); }
    public void ApplyEdit(int box, int slot, EntityEdit edit) { ThrowIfDisposed(); _inner.ApplyEdit(box, slot, edit); }
    public IReadOnlyList<int> GetAbilityChoices(int species, int form) { ThrowIfDisposed(); return _inner.GetAbilityChoices(species, form); }
    public IReadOnlyList<string> GetItemNames() { ThrowIfDisposed(); return _inner.GetItemNames(); }
    public IReadOnlyList<string> GetFormChoices(int species) { ThrowIfDisposed(); return _inner.GetFormChoices(species); }
    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot) { ThrowIfDisposed(); _inner.MoveSlot(fromBox, fromSlot, toBox, toSlot); }
    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot) { ThrowIfDisposed(); return _inner.DuplicateSlot(fromBox, fromSlot, toBox, toSlot); }
    public IReadOnlyList<int> GetSpeciesTypes(int species) { ThrowIfDisposed(); return _inner.GetSpeciesTypes(species); }
    public BaseStats GetBaseStats(int species) { ThrowIfDisposed(); return _inner.GetBaseStats(species); }
    public SlotExport ExportSlot(int box, int slot) { ThrowIfDisposed(); return _inner.ExportSlot(box, slot); }
    public string GetShowdownText(int box, int slot) { ThrowIfDisposed(); return _inner.GetShowdownText(box, slot); }
    public string ExportBoxShowdown(int box) { ThrowIfDisposed(); return _inner.ExportBoxShowdown(box); }
    public RngInfo GetRngInfo(int box, int slot) { ThrowIfDisposed(); return _inner.GetRngInfo(box, slot); }
    public TrainingCaps GetTrainingCaps() { ThrowIfDisposed(); return _inner.GetTrainingCaps(); }
    public bool RerollNatureKeepShiny(int box, int slot, int nature) { ThrowIfDisposed(); return _inner.RerollNatureKeepShiny(box, slot, nature); }
    public IReadOnlyList<int> GetMissingSpecies() { ThrowIfDisposed(); return _inner.GetMissingSpecies(); }
    public DexEntryState GetDexEntry(int species) { ThrowIfDisposed(); return _inner.GetDexEntry(species); }
    public void SetDexEntry(int species, bool seen, bool caught) { ThrowIfDisposed(); _inner.SetDexEntry(species, seen, caught); }
    public bool IsDexSpecies(int species) { ThrowIfDisposed(); return _inner.IsDexSpecies(species); }
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() { ThrowIfDisposed(); return _inner.GetNuzlockeReport(); }
    public void ReleaseSlot(int box, int slot) { ThrowIfDisposed(); _inner.ReleaseSlot(box, slot); }
    public int PlaceLivingDex(byte[] compressedBundle) { ThrowIfDisposed(); return _inner.PlaceLivingDex(compressedBundle); }
    public string? LivingDexBundle => _inner.LivingDexBundle;
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false) { ThrowIfDisposed(); return _inner.SortBoxes(criteria, boxes, reverse); }
    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) { ThrowIfDisposed(); return _inner.BatchApply(instructions, boxes); }
    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) { ThrowIfDisposed(); return _inner.BatchApplySlots(slots, instructions); }
    public string GetBoxName(int box)
    {
        ThrowIfDisposed();
        if ((uint)box >= (uint)RechargedYellowFormat.BoxCount)
            return "BOX";
        var pc = RechargedYellowFormat.ReadBlock(_payload, _sections, 5, _profile.PcChunks);
        var name = StringConverter3.GetString(pc.AsSpan(_profile.BoxNamesOffset + (box * 9), 9), jp: false).Trim();
        return name.Length == 0 ? "BOX" : name;
    }
    public void SwapBoxes(int a, int b) { ThrowIfDisposed(); _inner.SwapBoxes(a, b); }
    public void DeleteBox(int box) { ThrowIfDisposed(); _inner.DeleteBox(box); }
    public void ClearBox(int box) { ThrowIfDisposed(); _inner.ClearBox(box); }
    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format = null) { ThrowIfDisposed(); return _inner.ImportSlot(box, slot, fileBytes, format); }
    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile = null) { ThrowIfDisposed(); return _inner.MakeMine(box, slot, profile); }
    public DexProgress GetDexProgress() { ThrowIfDisposed(); return _inner.GetDexProgress(); }
    public void CompleteDex() { ThrowIfDisposed(); _inner.CompleteDex(); }
    public IReadOnlyList<CountedEntry> GetPokeBeans() { ThrowIfDisposed(); return _inner.GetPokeBeans(); }
    public int SetPokeBeanCount(int index, int count) { ThrowIfDisposed(); return _inner.SetPokeBeanCount(index, count); }
    public IReadOnlyList<UndergroundItem> GetGrandUndergroundItems() { ThrowIfDisposed(); return _inner.GetGrandUndergroundItems(); }
    public int SetGrandUndergroundItemCount(int itemId, int count) { ThrowIfDisposed(); return _inner.SetGrandUndergroundItemCount(itemId, count); }
    public DaycareInfo GetDaycare() => new(false, []);
    public DaycareWithdrawal WithdrawDaycareToFirstEmptyBox(int facility, int slot) =>
        throw new InvalidOperationException("Recharged Yellow stores no Day Care Pokémon.");
    public void UnlockAllLegalFashion() { }
    public int GetStylePoints() => 0;
    public void SetStylePoints(int value) { }
    public MysteryGiftInbox GetMysteryGiftInbox() => new(false, []);
    public TrainerRecordsInfo GetTrainerRecords() { ThrowIfDisposed(); return _inner.GetTrainerRecords(); }
    public MetInfo GetMetInfo(int box, int slot) { ThrowIfDisposed(); return _inner.GetMetInfo(box, slot); }
    public void ApplyMetEdit(int box, int slot, MetEdit edit) { ThrowIfDisposed(); _inner.ApplyMetEdit(box, slot, edit); }
    public IReadOnlyList<NamedChoice> GetLocationChoices(int box, int slot, bool egg) { ThrowIfDisposed(); return _inner.GetLocationChoices(box, slot, egg); }
    public IReadOnlyList<NamedChoice> GetVersionChoices() { ThrowIfDisposed(); return _inner.GetVersionChoices(); }
    public IReadOnlyList<NamedChoice> GetLanguageChoices(int box, int slot) { ThrowIfDisposed(); return _inner.GetLanguageChoices(box, slot); }
    public PotentialInfo GetPotential(int box, int slot) { ThrowIfDisposed(); return _inner.GetPotential(box, slot); }
    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit) { ThrowIfDisposed(); _inner.ApplyPotentialEdit(box, slot, edit); }
    public IReadOnlyList<NamedChoice> GetTeraTypeChoices() { ThrowIfDisposed(); return _inner.GetTeraTypeChoices(); }
    public MoveDetails GetMoveDetails(int box, int slot) { ThrowIfDisposed(); return _inner.GetMoveDetails(box, slot); }
    public void ApplyMoveDetails(int box, int slot, MoveDetailsEdit edit) { ThrowIfDisposed(); _inner.ApplyMoveDetails(box, slot, edit); }
    public MoveShopInfo GetMoveShop(int box, int slot) { ThrowIfDisposed(); return _inner.GetMoveShop(box, slot); }
    public void ApplyMoveShopEdit(int box, int slot, MoveShopEdit edit) { ThrowIfDisposed(); _inner.ApplyMoveShopEdit(box, slot, edit); }
    public CosmeticInfo GetCosmetics(int box, int slot) { ThrowIfDisposed(); return _inner.GetCosmetics(box, slot); }
    public void ApplyCosmeticEdit(int box, int slot, CosmeticEdit edit) { ThrowIfDisposed(); _inner.ApplyCosmeticEdit(box, slot, edit); }
    public PokerusInfo GetPokerus(int box, int slot) { ThrowIfDisposed(); return _inner.GetPokerus(box, slot); }
    public void SetPokerus(int box, int slot, PokerusStatus status) { ThrowIfDisposed(); _inner.SetPokerus(box, slot, status); }
    public IReadOnlyList<RibbonEntry> GetRibbons(int box, int slot) { ThrowIfDisposed(); return _inner.GetRibbons(box, slot); }
    public void SetRibbon(int box, int slot, string id, int value) { ThrowIfDisposed(); _inner.SetRibbon(box, slot, id, value); }
    public AffixedRibbonInfo GetAffixedRibbon(int box, int slot) { ThrowIfDisposed(); return _inner.GetAffixedRibbon(box, slot); }
    public void SetAffixedRibbon(int box, int slot, int ribbonIndex) { ThrowIfDisposed(); _inner.SetAffixedRibbon(box, slot, ribbonIndex); }
    public IReadOnlyDictionary<string, int> GetObtainableRibbonMaxima(int box, int slot) { ThrowIfDisposed(); return _inner.GetObtainableRibbonMaxima(box, slot); }
    public int AwardAllObtainableRibbons(int box, int slot) { ThrowIfDisposed(); return _inner.AwardAllObtainableRibbons(box, slot); }
    public IReadOnlyList<CompassSetting> GetCompassSettings() => [];
    public bool SetCompassSetting(string id, int choiceIndex) => false;
    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) =>
        new(false, "Recharged Yellow has no offline encounter table.");

    public TrainerInfo GetTrainer()
    {
        ThrowIfDisposed();
        var trainer = _inner.GetTrainer();
        var money = _moneyOverride ?? BinaryPrimitives.ReadUInt32LittleEndian(ReadSb1().AsSpan(RechargedYellowFormat.MoneyOffset));
        return trainer with { Money = money };
    }

    public void SetTrainer(TrainerInfo trainer)
    {
        ThrowIfDisposed();
        // PKHeX caps retail money at 999,999; the hack allows 9,999,999. Its own
        // value is written back after the mapped fields, so the cap is harmless.
        // Assign the override only after the inner edit succeeds, so a rejected
        // trainer name cannot leave an unrequested money change behind.
        _inner.SetTrainer(trainer with { Money = Math.Min(trainer.Money, 999_999u) });
        _moneyOverride = trainer.Money;
    }

    public TrainerStats GetTrainerStats()
    {
        ThrowIfDisposed();
        var stats = _inner.GetTrainerStats();
        var coins = _coinsOverride ?? BinaryPrimitives.ReadUInt16LittleEndian(ReadSb1().AsSpan(RechargedYellowFormat.CoinsOffset));
        return new TrainerStats(
            stats.SupportsPlayTime, stats.PlayTimeHours, stats.PlayTimeMinutes, stats.PlayTimeHoursMax,
            false, 0, 0,
            true, coins, 9999);
    }

    public void SetTrainerStats(TrainerStatsEdit edit)
    {
        ThrowIfDisposed();
        if (edit.Coins is { } coins)
            _coinsOverride = (ushort)Math.Clamp(coins, 0, 9999);
        // BP and coins have no retail SaveBlock2 home in this hack; only playtime
        // travels through PKHeX.
        _inner.SetTrainerStats(edit with { BP = null, Coins = null });
    }

    public void RepairRTC() =>
        throw new InvalidOperationException("Recharged Yellow uses an accelerated in-game clock stored in its own SaveBlock2 region.");

    public IReadOnlyList<BagPouch> GetBag()
    {
        ThrowIfDisposed();
        var sb1 = ReadSb1();
        var result = new List<BagPouch>(RechargedYellowFormat.Pockets.Length);
        foreach (var pocket in RechargedYellowFormat.Pockets)
        {
            var items = new List<BagItem>();
            for (var slot = 0; slot < pocket.Capacity; slot++)
            {
                var offset = pocket.Offset + (slot * 4);
                var id = BinaryPrimitives.ReadUInt16LittleEndian(sb1.AsSpan(offset));
                var count = BinaryPrimitives.ReadUInt16LittleEndian(sb1.AsSpan(offset + 2));
                // Pockets are dense: the first empty entry ends the list. Bytes past
                // it belong to another structure (the v1.9 Medicine array is empty
                // even though an item-shaped word appears at slot 58).
                if (id == 0 || count == 0)
                    break;
                items.Add(new BagItem(id, count));
            }
            result.Add(new BagPouch(pocket.Name, items));
        }
        return result;
    }

    public IReadOnlyList<int> GetPouchLegalItems(string pouchName)
    {
        ThrowIfDisposed();
        // The hack's Medicine pocket is a separate array; Gen 3's item inventory
        // table has no Medicine set, so the Items list is the closest legal set.
        if (string.Equals(pouchName, "Medicine", StringComparison.Ordinal))
            return _inner.GetPouchLegalItems("Items");
        return _inner.GetPouchLegalItems(pouchName);
    }

    public int SetItemCount(string pouchName, int itemId, int count)
    {
        ThrowIfDisposed();
        if (itemId is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(itemId));

        var pocket = RechargedYellowFormat.Pockets.FirstOrDefault(p => string.Equals(p.Name, pouchName, StringComparison.Ordinal));
        if (pocket.Name is null)
            throw new InvalidOperationException($"No pouch named {pouchName}.");

        count = Math.Clamp(count, 0, pocket.MaxCount);
        var sb1 = ReadSb1();
        // The editor sees exactly the list GetBag shows: the dense prefix up to
        // the first empty entry. Bytes past it are never read, moved or cleared
        // (the v1.9 Medicine array shows an item-shaped word at entry 58 behind
        // an empty list, which belongs to something else).
        var length = DenseLength(sb1, pocket);
        var existing = -1;
        for (var slot = 0; slot < length && existing < 0; slot++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(sb1.AsSpan(pocket.Offset + (slot * 4))) == itemId)
                existing = slot;

        if (existing >= 0)
        {
            var offset = pocket.Offset + (existing * 4);
            if (count <= 0)
            {
                // Close the gap inside the list; the freed last entry is cleared.
                var end = pocket.Offset + (length * 4);
                sb1.AsSpan(offset + 4, end - offset - 4).CopyTo(sb1.AsSpan(offset));
                sb1.AsSpan(end - 4, 4).Clear();
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(sb1.AsSpan(offset + 2), (ushort)count);
            }
        }
        else if (count > 0)
        {
            if (length >= pocket.Capacity)
                throw new InvalidOperationException("The pouch is full.");
            // The new entry goes right after the list. It must land on a truly
            // blank entry, and must not join the list to unread bytes behind it.
            var offset = pocket.Offset + (length * 4);
            var joinsStrayBytes = length + 1 < pocket.Capacity
                && BinaryPrimitives.ReadUInt16LittleEndian(sb1.AsSpan(offset + 4)) != 0
                && BinaryPrimitives.ReadUInt16LittleEndian(sb1.AsSpan(offset + 6)) != 0;
            if (BinaryPrimitives.ReadUInt32LittleEndian(sb1.AsSpan(offset)) != 0 || joinsStrayBytes)
                throw new InvalidOperationException(
                    $"The {pouchName} pouch has bytes after its last item that PKForge cannot place safely.");
            BinaryPrimitives.WriteUInt16LittleEndian(sb1.AsSpan(offset), (ushort)itemId);
            BinaryPrimitives.WriteUInt16LittleEndian(sb1.AsSpan(offset + 2), (ushort)count);
        }
        else
        {
            return 0;
        }

        WriteSb1(sb1);
        return count;
    }

    /// <summary>The number of entries before the first empty one, the list the
    /// bag view shows.</summary>
    private static int DenseLength(byte[] sb1, RechargedYellowFormat.Pocket pocket)
    {
        for (var slot = 0; slot < pocket.Capacity; slot++)
        {
            var offset = pocket.Offset + (slot * 4);
            if (BinaryPrimitives.ReadUInt16LittleEndian(sb1.AsSpan(offset)) == 0
                || BinaryPrimitives.ReadUInt16LittleEndian(sb1.AsSpan(offset + 2)) == 0)
                return slot;
        }
        return pocket.Capacity;
    }

    public ReadOnlyMemory<byte> Serialize()
    {
        ThrowIfDisposed();
        var edited = _inner.Serialize();
        var result = RechargedYellowFormat.Materialize(_payload, edited.Span, _sections, _profile, _moneyOverride, _coinsOverride);
        return SramPadding.Restore(result, _originalBytes);
    }

    /// <summary>Slot images for the write-safety diff, read through the same PKHeX view the editor uses.</summary>
    internal IEnumerable<SlotImage> SlotImages()
    {
        for (var slot = 0; slot < 6; slot++)
        {
            var pk = _inner.GetEntity(-1, slot);
            yield return Image(new SlotRef(-1, slot), pk);
        }
        for (var box = 0; box < RechargedYellowFormat.BoxCount; box++)
        for (var slot = 0; slot < RechargedYellowFormat.BoxSlotCount; slot++)
        {
            var pk = _inner.GetEntity(box, slot);
            yield return Image(new SlotRef(box, slot), pk);
        }

        static SlotImage Image(SlotRef where, PKM pk)
        {
            var data = pk.Data.ToArray();
            var species = pk is G3PKM g3 ? g3.SpeciesInternal : pk.Species;
            var empty = data.All(b => b == 0) || (species == 0 && pk.ChecksumValid);
            return new SlotImage(where, data, empty, empty || pk.ChecksumValid);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _inner.Dispose();
    }

    private byte[] ReadSb1() => RechargedYellowFormat.ReadBlock(_payload, _sections, 1, _profile.Sb1Chunks);

    private void WriteSb1(byte[] sb1) => RechargedYellowFormat.WriteBlock(_payload, _sections, 1, _profile.Sb1Chunks, sb1);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
