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

    /// <summary>Display names of every form this species has in the open save's game,
    /// indexed by form id. One entry (or an empty name at 0) means no form choice.</summary>
    IReadOnlyList<string> GetFormChoices(int species);

    /// <summary>Moves (or swaps, when the target is occupied) two box slots.</summary>
    void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot);

    /// <summary>Type ids (0-17) of a species' base form in the open save's game.</summary>
    IReadOnlyList<int> GetSpeciesTypes(int species);

    /// <summary>Base stats of a species' base form (HP/Atk/Def/SpA/SpD/Spe).</summary>
    BaseStats GetBaseStats(int species);

    /// <summary>Decrypted .pk* file bytes + canonical file name for the slot's mon.</summary>
    SlotExport ExportSlot(int box, int slot);

    /// <summary>Showdown-format text for the slot's mon.</summary>
    string GetShowdownText(int box, int slot);

    /// <summary>Empties the slot (release). Irreversible except via restore points.</summary>
    void ReleaseSlot(int box, int slot);

    /// <summary>Imports a .pk* file's bytes into the slot; false if the bytes are not a compatible entity.</summary>
    bool ImportSlot(int box, int slot, byte[] fileBytes);

    // ── Trainer card ──
    TrainerInfo GetTrainer();
    void SetTrainer(TrainerInfo trainer);

    // ── Pokédex ──
    DexProgress GetDexProgress();
    /// <summary>Marks every species seen and caught (complete dex).</summary>
    void CompleteDex();

    // ── Bag ──
    IReadOnlyList<BagPouch> GetBag();
    /// <summary>Item ids the pouch may legally contain (for the add-item picker).</summary>
    IReadOnlyList<int> GetPouchLegalItems(string pouchName);
    /// <summary>Sets an item's count in a pouch (0 removes; adds when absent).</summary>
    void SetItemCount(string pouchName, int itemId, int count);

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
    IReadOnlyList<NamedChoice> AbilitySlots);

/// <summary>A partial potential mutation; only non-null fields apply.</summary>
public sealed record PotentialEdit(
    int? TeraType = null,
    IReadOnlyList<bool>? HyperTrained = null,
    int? AbilitySlot = null);

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
    GenerationOutcome FillLivingDex(ISaveEngineSession session, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default);

    /// <summary>Generates a legal mon as raw bytes + facts, without touching the save (bank deposits).</summary>
    GeneratedEntity? GenerateData(ISaveEngineSession session, GenerationRequest request);

    /// <inheritdoc cref="GenerateData"/>
    GeneratedEntity? GenerateDataFromShowdown(ISaveEngineSession session, string showdownText);
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

public sealed record DexProgress(int Seen, int Caught, int Total);

public sealed record BagPouch(string Name, IReadOnlyList<BagItem> Items);

public sealed record BagItem(int Id, int Count);

/// <summary>Common editable fields of one Pokémon entity.</summary>
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
    IReadOnlyList<int> IVs,
    IReadOnlyList<int> EVs,
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
