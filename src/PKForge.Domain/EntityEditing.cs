namespace PKForge.Domain;

/// <summary>An engine-owned mutable save document. All access goes through this session; callers never see engine types.</summary>
public interface ISaveEngineSession : IDisposable
{
    SaveSnapshot Snapshot { get; }
    EntityDetail ReadEntity(int box, int slot);
    void ApplyEdit(int box, int slot, EntityEdit edit);
    ReadOnlyMemory<byte> Serialize();

    /// <summary>Ability ids this species/form can legally have in the open save's game.</summary>
    IReadOnlyList<int> GetAbilityChoices(int species, int form);

    /// <summary>Console generation of the open save (1-9): selects the living dex bundle.</summary>
    int Generation { get; }

    /// <summary>The open game's own item name table, indexed by its item ids. Modern
    /// lists misname Gen 1-4 ids (Rare Candy et al); this is per-context truth.</summary>
    IReadOnlyList<string> GetItemNames();

    /// <summary>Display names of every form this species has in the open save's game,
    /// indexed by form id. One entry (or an empty name at 0) means no form choice.</summary>
    IReadOnlyList<string> GetFormChoices(int species);

    /// <summary>Moves (or swaps, when the target is occupied) two box slots.</summary>
    void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot);

    /// <summary>Type ids (0-17) of a species' base form in the open save's game.</summary>
    IReadOnlyList<int> GetSpeciesTypes(int species);

    /// <summary>Highest species id this save format can store. Species pickers stay
    /// inside it: no mod extends a save's species table.</summary>
    int MaxSpeciesId { get; }

    /// <summary>Base stats of a species' base form (HP/Atk/Def/SpA/SpD/Spe).</summary>
    BaseStats GetBaseStats(int species);

    /// <summary>Decrypted .pk* file bytes + canonical file name for the slot's mon.</summary>
    SlotExport ExportSlot(int box, int slot);

    /// <summary>Showdown-format text for the slot's mon.</summary>
    string GetShowdownText(int box, int slot);

    /// <summary>Showdown text for every mon in a box, blank-line separated.</summary>
    string ExportBoxShowdown(int box);

    /// <summary>Raw RNG facts speedrunners care about: PID, EC, IVs, nature, shiny.</summary>
    RngInfo GetRngInfo(int box, int slot);

    /// <summary>Per-format training caps: Gen 1/2 DVs max 15 with 65535 stat
    /// experience; Gen 3-5 EVs max 255; Gen 6+ EVs max 252. IVs cap at 31 elsewhere.</summary>
    TrainingCaps GetTrainingCaps();

    /// <summary>
    /// Changes a mon's nature without losing its shiny state, gender or ability slot.
    /// Gen 3/4 reroll the PID (PID-derived nature); Gen 5+ write the nature byte.
    /// False on Gen 1/2 (no natures) or empty slots.
    /// </summary>
    bool RerollNatureKeepShiny(int box, int slot, int nature);

    /// <summary>Species ids (1..Max) with no copy anywhere in PC storage.</summary>
    IReadOnlyList<int> GetMissingSpecies();

    /// <summary>Reads one dex cell's state.</summary>
    DexEntryState GetDexEntry(int species);

    /// <summary>Sets one dex cell (seen/caught).</summary>
    void SetDexEntry(int species, bool seen, bool caught);

    /// <summary>
    /// Rule-aware Nuzlocke view built from met data: first catch per route plus
    /// later duplicates, so runs can be audited after the fact.
    /// </summary>
    IReadOnlyList<NuzlockeCatch> GetNuzlockeReport();

    /// <summary>Empties the slot (release). Irreversible except via restore points.</summary>
    void ReleaseSlot(int box, int slot);

    /// <summary>
    /// Sorts the given boxes (null = every box). Mons compact to the front of the
    /// FIRST target box, overflow continues into the next; empties pool at the end.
    /// Party is untouched. One write on Save.
    /// </summary>
    /// <returns>How many mons were placed.</returns>
    /// <summary>
    /// Fills the storage with the pre-generated living dex bundle (built by tools/DexGen,
    /// shipped as an asset): one of each species, copied byte-for-byte. Zero on-device
    /// legalization. Returns how many mons were placed.
    /// </summary>
    int PlaceLivingDex(byte[] compressedBundle);

    int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null);

    /// <summary>Applies an instruction ("Prop=Value", $suggest/$rand/$shiny) to every non-empty
    /// slot in the given boxes (null = all boxes). Returns how many mons were touched.</summary>
    int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null);

    /// <summary>Display name of a box (wallpaper names like HEAL, FOREST); "BOX" default.</summary>
    string GetBoxName(int box);

    /// <summary>Swaps two boxes' entire contents (order management: box 1 with box 2).</summary>
    void SwapBoxes(int a, int b);

    /// <summary>Deletes a box by merging: its mons move to the first box with room (or are
    /// released when the storage is full). Box order closes the gap. Gen1-8 fixed-storage
    /// formats fall back to clearing the box instead.</summary>
    void DeleteBox(int box);

    /// <summary>Deletes the box outright (mon loss possible) - the explicit destructive path.</summary>
    void ClearBox(int box);

    /// <summary>Imports a .pk* file's bytes into the slot; false if the bytes are not a compatible entity.</summary>
    bool ImportSlot(int box, int slot, byte[] fileBytes);

    // ── Trainer card ──
    TrainerInfo GetTrainer();
    void SetTrainer(TrainerInfo trainer);
    /// <summary>Makes a Pokémon owned by the current save or a named trainer profile.
    /// Fixed-OT encounters are refused and the edit is committed only when it remains legal.</summary>
    GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile = null);

    // ── Pokédex ──
    DexProgress GetDexProgress();
    /// <summary>Marks every species seen and caught (complete dex).</summary>
    void CompleteDex();

    // ── Bag ──
    IReadOnlyList<BagPouch> GetBag();
    /// <summary>Item ids the pouch may legally contain (for the add-item picker).</summary>
    IReadOnlyList<int> GetPouchLegalItems(string pouchName);
    /// <summary>Sets an item's count in a pouch (0 removes; adds when absent), clamped
    /// to that game's per-item limit. Returns the count actually stored.</summary>
    int SetItemCount(string pouchName, int itemId, int count);

    // ── Game-specific inventories ──
    /// <summary>Poké Pelago's 15 Poké Bean counters (SM/USUM); empty for every other format.</summary>
    IReadOnlyList<CountedEntry> GetPokeBeans();
    /// <summary>Sets a Poké Bean counter, clamped to the game's 0-255 range.</summary>
    int SetPokeBeanCount(int index, int count);

    // ── Grand Underground (BDSP / Luminescent) ──
    /// <summary>Grand Underground spheres, treasures, statues, and pedestals. Empty outside BDSP-based saves.</summary>
    IReadOnlyList<UndergroundItem> GetGrandUndergroundItems();
    /// <summary>Sets one Grand Underground item's count, clamped to its game-defined stack maximum.</summary>
    int SetGrandUndergroundItemCount(int itemId, int count);

    // ── Day Care / Nursery ──
    /// <summary>Deposited Pokémon and egg availability for formats with a supported Day Care or Nursery.</summary>
    DaycareInfo GetDaycare();
    /// <summary>Withdraws a deposited Pokémon to the first empty PC box slot. No egg or RNG state is changed.</summary>
    DaycareWithdrawal WithdrawDaycareToFirstEmptyBox(int facility, int slot);

    // ── Fashion ──
    /// <summary>Whether this save supports PKForge's legal wardrobe-unlock action.</summary>
    bool SupportsLegalFashionUnlock { get; }
    /// <summary>Unlocks only clothing the current game/version can legitimately own.</summary>
    void UnlockAllLegalFashion();

    // ── Mystery Gift inbox ──
    /// <summary>Cards and received-gift records stored by the save. This surface is read-only.</summary>
    MysteryGiftInbox GetMysteryGiftInbox();

    // ── Trainer records ──
    /// <summary>Read-only trainer-stat records for formats exposing a stable PKHeX record table.</summary>
    TrainerRecordsInfo GetTrainerRecords();

    // ── Met / origin (the identity block behind legality) ──
    MetInfo GetMetInfo(int box, int slot);
    void ApplyMetEdit(int box, int slot, MetEdit edit);
    /// <summary>Location names valid for this mon's origin game (met, or egg-hatch when <paramref name="egg"/>).</summary>
    IReadOnlyList<NamedChoice> GetLocationChoices(int box, int slot, bool egg);
    /// <summary>Origin games this mon could carry.</summary>
    IReadOnlyList<NamedChoice> GetVersionChoices();
    /// <summary>Languages valid for this mon's generation.</summary>
    IReadOnlyList<NamedChoice> GetLanguageChoices(int box, int slot);

    // ── Potential: Tera type, Hyper Training, ability slot (gen-gated) ──
    PotentialInfo GetPotential(int box, int slot);
    void ApplyPotentialEdit(int box, int slot, PotentialEdit edit);
    /// <summary>Tera type choices (0-17 elemental, 99 Stellar), for the picker.</summary>
    IReadOnlyList<NamedChoice> GetTeraTypeChoices();

    // ── Move details: PP, PP Ups, and relearn slots ──
    MoveDetails GetMoveDetails(int box, int slot);
    void ApplyMoveDetails(int box, int slot, MoveDetailsEdit edit);

    // ── Legends: Arceus Move Shop ──
    /// <summary>Purchased and mastered Move Shop entries for a PLA Pokémon; unsupported formats return no entries.</summary>
    MoveShopInfo GetMoveShop(int box, int slot);
    /// <summary>Updates one species-permitted PLA Move Shop entry.</summary>
    void ApplyMoveShopEdit(int box, int slot, MoveShopEdit edit);

    // ── Cosmetics: format-gated visual, care, and display metadata ──
    CosmeticInfo GetCosmetics(int box, int slot);
    void ApplyCosmeticEdit(int box, int slot, CosmeticEdit edit);

    // ── Awards: Pokérus, ribbons, and marks ──
    PokerusInfo GetPokerus(int box, int slot);
    void SetPokerus(int box, int slot, PokerusStatus status);
    IReadOnlyList<RibbonEntry> GetRibbons(int box, int slot);
    /// <summary>Sets a boolean ribbon/mark to 0 or 1, or a counted ribbon to its format-specific range.</summary>
    void SetRibbon(int box, int slot, string id, int value);
    /// <summary>The ribbon or mark currently shown as this Pokémon's title in supported Gen 8+ formats.</summary>
    AffixedRibbonInfo GetAffixedRibbon(int box, int slot);
    /// <summary>Selects an already-owned ribbon/mark as the Pokémon's title; use -1 to clear it.</summary>
    void SetAffixedRibbon(int box, int slot, int ribbonIndex);
}

/// <summary>A pick-list entry the engine hands the UI: stable id + display name.</summary>
public sealed record NamedChoice(int Id, string Name);

/// <summary>The met / origin block of one Pokémon. Dates are "yyyy-MM-dd" or empty when unset.</summary>
public sealed record MetInfo(
    int MetLocation, string MetLocationName, int MetLevel, string MetDate, bool SupportsMetDate,
    bool IsEgg, int EggLocation, string EggLocationName, string EggDate, bool SupportsEggDate,
    int Version, string VersionName, int Language, string LanguageName,
    bool Fateful, int TID, int SID);

/// <summary>A partial met/origin mutation; only non-null fields apply. Dates: null = no change, "" = clear.</summary>
public sealed record MetEdit(
    int? MetLocation = null,
    int? MetLevel = null,
    string? MetDate = null,
    bool? IsEgg = null,
    int? EggLocation = null,
    string? EggDate = null,
    int? Version = null,
    int? Language = null,
    bool? Fateful = null,
    int? TID = null,
    int? SID = null);

/// <summary>
/// The potential block of one Pokémon: Tera type (Gen IX), Hyper Training (Gen VII+),
/// and ability slot (ability capsule / patch semantics). Each surface is reported as
/// unsupported when the mon's format cannot carry it, so the UI only shows what applies.
/// HyperTrained uses the app's stat order: HP, Atk, Def, SpA, SpD, Spe.
/// </summary>
public sealed record PotentialInfo(
    bool SupportsTera,
    int TeraType,
    string TeraTypeName,
    string TeraTypeOriginalName,
    bool TeraLocked,
    bool SupportsHyperTrain,
    IReadOnlyList<bool> HyperTrained,
    bool SupportsAbilitySlot,
    int AbilitySlot,
    IReadOnlyList<NamedChoice> AbilitySlots,
    bool SupportsAwakening,
    IReadOnlyList<int> Awakening,
    bool SupportsGanbaru,
    IReadOnlyList<int> Ganbaru,
    IReadOnlyList<int> GanbaruMaximums);

/// <summary>A partial potential mutation; only non-null fields apply.</summary>
public sealed record PotentialEdit(
    int? TeraType = null,
    IReadOnlyList<bool>? HyperTrained = null,
    int? AbilitySlot = null,
    IReadOnlyList<int>? Awakening = null,
    IReadOnlyList<int>? Ganbaru = null);

public sealed record MoveSlotDetail(int Move, int PP, int MaxPP, int PPUps);
public sealed record MoveDetails(IReadOnlyList<MoveSlotDetail> Moves, bool SupportsRelearn, IReadOnlyList<int> RelearnMoves);
public sealed record MoveDetailsEdit(IReadOnlyList<int>? PP = null, IReadOnlyList<int>? PPUps = null, IReadOnlyList<int>? RelearnMoves = null);

/// <summary>Format-gated Day Care / Nursery data. The game owns breeding and egg state; PKForge only displays it.</summary>
public sealed record DaycareInfo(bool Supported, IReadOnlyList<DaycareFacility> Facilities);
public sealed record DaycareFacility(string Name, bool EggAvailable, IReadOnlyList<DaycareSlot> Slots);
public sealed record DaycareSlot(int Index, bool Occupied, string SpeciesName, string Nickname, int Level, uint? Experience);
/// <summary>Destination of a successful Day Care withdrawal.</summary>
public sealed record DaycareWithdrawal(int Box, int Slot, string SpeciesName);

/// <summary>Read-only Mystery Gift card storage exposed by formats PKHeX can describe safely.</summary>
public sealed record MysteryGiftInbox(bool Supported, IReadOnlyList<MysteryGiftCard> Cards);

/// <summary>One non-empty in-save Mystery Gift card or received-record entry.</summary>
public sealed record MysteryGiftCard(
    int Slot,
    int CardId,
    string Title,
    string Type,
    bool IsEntity,
    int Species,
    int Level,
    bool GiftUsed,
    bool IsReceivedRecord);

public sealed record TrainerRecordEntry(int Index, int Value, int Maximum);
public sealed record TrainerRecordsInfo(bool Supported, IReadOnlyList<TrainerRecordEntry> Records);

/// <summary>One Move Shop offering that the species can learn in Legends: Arceus.</summary>
public sealed record MoveShopEntry(int Index, int Move, bool Purchased, bool Mastered);
public sealed record MoveShopInfo(bool Supported, IReadOnlyList<MoveShopEntry> Entries);
/// <summary>Partial edit of one PLA Move Shop flag pair. Null leaves that flag unchanged.</summary>
public sealed record MoveShopEdit(int Index, bool? Purchased = null, bool? Mastered = null);

/// <summary>One player-applied box marking. Values are off/on in Gen 3-6 and
/// off/blue/pink in Gen 7 onward.</summary>
public sealed record CosmeticMarking(string Name, int Value, int MaxValue);

/// <summary>The six visual contest attributes: Cool, Beauty, Cute, Smart, Tough, and Sheen.</summary>
public sealed record CosmeticInfo(
    IReadOnlyList<CosmeticMarking> Markings,
    IReadOnlyList<int> ContestStats,
    bool SupportsSize, int HeightScalar, int WeightScalar, bool SupportsScale, int Scale,
    bool SupportsAffection, int OriginalTrainerAffection, int HandlingTrainerAffection,
    bool SupportsFullnessEnjoyment, int Fullness, int Enjoyment,
    bool SupportsFavorite, bool IsFavorite,
    bool SupportsDynamax, int DynamaxLevel, bool CanGigantamax,
    bool SupportsAlpha, bool IsAlpha,
    bool SupportsSociability, uint Sociability);

/// <summary>A partial cosmetic mutation. Only fields supported by the stored format are written.</summary>
public sealed record CosmeticEdit(
    IReadOnlyList<int>? Markings = null,
    IReadOnlyList<int>? ContestStats = null,
    int? HeightScalar = null,
    int? WeightScalar = null,
    int? Scale = null,
    int? OriginalTrainerAffection = null,
    int? HandlingTrainerAffection = null,
    int? Fullness = null,
    int? Enjoyment = null,
    bool? IsFavorite = null,
    int? DynamaxLevel = null,
    bool? CanGigantamax = null,
    bool? IsAlpha = null,
    uint? Sociability = null);

/// <summary>Pokérus lifecycle stored by games that support the mechanic.</summary>
public enum PokerusStatus
{
    Susceptible,
    Infectious,
    Cured,
}

public sealed record PokerusInfo(
    bool Supported,
    PokerusStatus Status,
    int Strain,
    int Days);

/// <summary>A format-supported ribbon, mark, or counted legacy ribbon.</summary>
public sealed record RibbonEntry(
    string Id,
    string Name,
    int Value,
    int MaxValue,
    bool IsMark);

/// <summary>A Gen 8+ title selection. Choices contains only ribbons and marks the Pokémon owns.</summary>
public sealed record AffixedRibbonInfo(
    bool Supported,
    int SelectedIndex,
    string SelectedName,
    IReadOnlyList<NamedChoice> Choices);

/// <summary>
/// The engine's own display-name tables (indexed by id), so the UI always speaks
/// names and never asks the user for raw ids.
/// </summary>
public interface IGameDataService
{
    IReadOnlyList<string> SpeciesNames { get; }
    IReadOnlyList<string> MoveNames { get; }
    IReadOnlyList<string> ItemNames { get; }
    IReadOnlyList<string> AbilityNames { get; }
    IReadOnlyList<string> NatureNames { get; }
    IReadOnlyList<string> BallNames { get; }
}

/// <summary>Runs offline legality analysis through the pinned engine.</summary>
public interface ILegalityService
{
    LegalityReport Analyze(ISaveEngineSession session, int box, int slot);
}

/// <summary>
/// Offline legal-Pokémon factory (Auto Legality Mod in-process): generates a legal mon
/// into a slot from a structured request or a pasted Showdown set, and repairs
/// illegal mons in place. All mutations stay in the session until safely written.
/// </summary>
public interface ILegalizerService
{
    GenerationOutcome Generate(ISaveEngineSession session, int box, int slot, GenerationRequest request);
    GenerationOutcome GenerateFromShowdown(ISaveEngineSession session, int box, int slot, string showdownText);
    GenerationOutcome LegalizeSlot(ISaveEngineSession session, int box, int slot);

    /// <summary>Fills the PC from box 0 slot 0 with a legal living dex (overwrites; caller confirms + backs up).</summary>
    GenerationOutcome FillLivingDex(ISaveEngineSession session, byte[] compressedBundle, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default);

    /// <summary>Generates a legal mon as raw bytes + facts, without touching the save (bank deposits).</summary>
    GeneratedEntity? GenerateData(ISaveEngineSession session, GenerationRequest request);

    /// <inheritdoc cref="GenerateData"/>
    GeneratedEntity? GenerateDataFromShowdown(ISaveEngineSession session, string showdownText);

    /// <summary>Generates legal mons for the requested species into the first empty PC slots.</summary>
    GenerationOutcome FillSpecies(ISaveEngineSession session, IReadOnlyList<int> species, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default);

    /// <summary>Generates hatchable eggs for the requested species into the first empty PC slots.</summary>
    GenerationOutcome GenerateEggs(ISaveEngineSession session, IReadOnlyList<int> species, EggOptions options, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default);
}

public sealed record GeneratedEntity(byte[] Data, BankEntryInfo Info);

/// <summary>
/// The offline Mystery Gift archive: every real event distribution the engine knows,
/// filtered to what the open save can receive.
/// </summary>
public interface IEventDatabaseService
{
    IReadOnlyList<EventGift> GetGifts(ISaveEngineSession session);
    GenerationOutcome Receive(ISaveEngineSession session, int giftId, int box, int slot);
}

public sealed record EventGift(int Id, string Title, string Header, int Species, int Level, bool Shiny);

/// <summary>What the user asked for; null fields mean "let the legalizer decide".</summary>
public sealed record GenerationRequest(
    int Species,
    int? Level,
    bool Shiny,
    int? Nature,
    int? Ability,
    int? Ball,
    IReadOnlyList<int>? Moves,
    int Form = 0);

public sealed record GenerationOutcome(bool Success, string Message);

public sealed record BaseStats(int Hp, int Atk, int Def, int SpA, int SpD, int Spe);

public sealed record SlotExport(byte[] Data, string FileName);

public sealed record TrainerInfo(string Name, int TID, int SID, uint Money, int Gender);

/// <summary>A reusable ownership identity. DisplayName labels the preset and is never written into a save.</summary>
public sealed record TrainerProfile(string Id, string DisplayName, string OriginalTrainer, int TID, int SID, int Gender);

/// <summary>App-owned generation preference consumed by the engine without depending on MAUI.</summary>
public interface IGenerationOwnershipSettings
{
    bool UseCurrentTrainerForGeneration { get; }
}

public sealed record DexProgress(int Seen, int Caught, int Total);

public sealed record DexEntryState(bool Seen, bool Caught);

public sealed record RngInfo(
    uint Pid,
    uint? EncryptionConstant,
    int Nature,
    bool Shiny,
    bool NatureRerollSupported,
    IReadOnlyList<int> IVs, // HP, Atk, Def, SpA, SpD, Spe
    int Ability,
    int Gender);

public sealed record TrainingCaps(int IvMax, int EvMax);

public sealed record EggOptions(bool MaxIv, bool Shiny);

public sealed record NuzlockeCatch(string Route, int Species, string Name, bool FirstCatch, string? MetDate);

public sealed record BagPouch(string Name, IReadOnlyList<BagItem> Items);

public sealed record BagItem(int Id, int Count);

/// <summary>A named, game-owned counter such as a Poké Bean stack.</summary>
public sealed record CountedEntry(int Id, string Name, int Count, int MaxCount);

/// <summary>A fixed Grand Underground inventory entry in BDSP-based saves.</summary>
public sealed record UndergroundItem(int Id, string Name, string Type, int Count, int MaxCount);

/// <summary>Common editable fields of one Pokémon entity.</summary>
public enum SortCriteria
{
    /// <summary>National dex number, then form.</summary>
    DexNumber,
    /// <summary>Species display name, A-Z.</summary>
    Alphabetical,
    /// <summary>Current level, strongest first.</summary>
    LevelDesc,
    /// <summary>IV total, best first.</summary>
    IvTotalDesc,
    /// <summary>Primary type (dex type order), then dex number: type-run boxes.</summary>
    Type,
    /// <summary>Met date, oldest team first (nulls last).</summary>
    AgeOldest,
    /// <summary>Shinies first, then dex number.</summary>
    ShinyFirst,
}


public sealed record EntityDetail(
    int Box,
    int Slot,
    bool IsEmpty,
    int Species,
    string SpeciesName,
    int Form,
    string Nickname,
    int Level,
    int Nature,
    int Ability,
    int HeldItem,
    int Move1,
    int Move2,
    int Move3,
    int Move4,
    IReadOnlyList<int> IVs, // HP, Atk, Def, SpA, SpD, Spe
    IReadOnlyList<int> EVs, // HP, Atk, Def, SpA, SpD, Spe
    bool IsShiny,
    int Ball,
    string OriginalTrainer,
    IReadOnlyList<int>? Types = null,
    int Gender = 2,
    int Friendship = 0,
    IReadOnlyList<int>? Stats = null,
    int CurrentHp = 0,
    int StatusCondition = 0);

/// <summary>A partial entity mutation; only non-null fields are applied.</summary>
public sealed record EntityEdit(
    int? Species = null,
    string? Nickname = null,
    int? Level = null,
    int? Nature = null,
    int? Ability = null,
    int? HeldItem = null,
    int? Move1 = null,
    int? Move2 = null,
    int? Move3 = null,
    int? Move4 = null,
    IReadOnlyList<int>? IVs = null,
    IReadOnlyList<int>? EVs = null,
    bool? IsShiny = null,
    int? Ball = null,
    string? OriginalTrainer = null,
    int? Gender = null,
    int? Friendship = null);

/// <summary>Human-readable legality result for one slot.</summary>
public sealed record LegalityReport(bool Valid, IReadOnlyList<string> Lines);
