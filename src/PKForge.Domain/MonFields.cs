namespace PKForge.Domain;

// Per-Pokémon fields beyond the core editor row set: form and form argument, the Gen 8+
// stat nature (mint), shiny type and raw PID / encryption constant, OT gender and the
// handling trainer, Gen 6+ memories, and Gen 8/9 Technical Records. Every record is
// format-gated by the engine: a field the format does not store is simply absent.

/// <summary>One form of the current species, flagged the way PKHeX judges it for this game.</summary>
/// <param name="InGame">The game's personal table has this form (PKHeX <c>IsPresentInGame</c>).</param>
/// <param name="BattleOnly">Megas, Primals and other forms that cannot exist outside a battle.</param>
public sealed record FormChoice(int Form, string Name, bool InGame, bool BattleOnly);

/// <summary>The form list for a species with more than one form.</summary>
public sealed record FormFieldInfo(int Species, int Form, IReadOnlyList<FormChoice> Choices);

/// <summary>What a form change did: the follow-up edits it made and, when the result is no longer legal, why.</summary>
public sealed record FormChangeResult(bool Changed, IReadOnlyList<string> Notes, string? Warning);

/// <summary>How a species' form argument is stored (PKHeX <c>FormArgumentType</c>).</summary>
public enum FormArgumentKind { None, Raw, TripleParty, Triple, Named }

/// <summary>
/// A form argument (Furfrou/Hoopa days left, Alcremie topping, Yamask damage, Gimmighoul
/// coins, Primeape Rage Fist uses…). <paramref name="Max"/> is PKHeX's edge maximum.
/// Triple kinds keep days remaining / elapsed / the longest streak separately.
/// </summary>
public sealed record FormArgumentField(
    FormArgumentKind Kind, string Label, uint Value, uint Max,
    int Remain, int Elapsed, int Maximum, IReadOnlyList<string> Names);

/// <summary>Star or square: Sword/Shield draw a square for a shiny XOR of 0.</summary>
public enum ShinyKind { None, Star, Square }

/// <summary>Shiny state and the raw personality values.</summary>
/// <param name="SupportsKind">Sword/Shield, the one context PKHeX draws star and square apart (<c>IsSquareShinyDifferentiated</c>).</param>
/// <param name="EncryptionConstant">Null for Gen 1-5 formats, which have no separate EC.</param>
/// <param name="PidLinked">Gen 3-5 formats derive nature/gender/ability/shininess from the PID.</param>
public sealed record ShinyField(bool IsShiny, ShinyKind Kind, bool SupportsKind, uint Pid, uint? EncryptionConstant, bool PidLinked);

/// <summary>What a raw PID / EC write changed besides the value itself.</summary>
public sealed record PersonalityEditResult(bool Changed, IReadOnlyList<string> Changes);

/// <summary>OT gender and the handling trainer block.</summary>
/// <param name="OtGender">0 male, 1 female; null for formats that do not store it.</param>
/// <param name="HasHandler">Gen 6+ formats keep a handling trainer.</param>
/// <param name="HandlerLanguage">Null unless the format stores it (<c>IHandlerLanguage</c>, Gen 8+).</param>
/// <param name="CurrentHandler">0 = with its original trainer, 1 = with the handling trainer.</param>
public sealed record TrainerFields(
    int? OtGender, int OtFriendship,
    bool HasHandler, string HandlerName, int HandlerGender, int? HandlerLanguage, int HandlerFriendship,
    int CurrentHandler, int MaxNameLength);

/// <summary>A partial trainer/handler mutation; only non-null fields apply.</summary>
public sealed record TrainerFieldsEdit(
    int? OtGender = null, int? OtFriendship = null,
    string? HandlerName = null, int? HandlerGender = null, int? HandlerLanguage = null,
    int? HandlerFriendship = null, int? CurrentHandler = null);

/// <summary>One memory choice; <paramref name="Legal"/> follows PKHeX's MemoryContext for this trainer slot.</summary>
public sealed record MemoryChoice(int Id, string Name, bool Legal);

/// <summary>A trainer memory (OT or handler) and its rendered in-game sentence.</summary>
/// <param name="Editable">False for eggs, pre-Gen 6 origins (OT) and a Pokémon never handled (HT).</param>
public sealed record MemoryField(
    bool Handler, bool Editable, string? Reason,
    int Memory, int Intensity, int Feeling, int Variable, string Text);

/// <summary>Everything a memory editor lists for one memory id.</summary>
/// <param name="ArgumentCategory">"Species", "Area", "Item", "Move", "Location" or empty when the memory takes none.</param>
public sealed record MemoryOptions(
    IReadOnlyList<MemoryChoice> Memories, string ArgumentCategory, IReadOnlyList<NamedChoice> Arguments,
    IReadOnlyList<MemoryChoice> Intensities, IReadOnlyList<MemoryChoice> Feelings);

/// <summary>A full memory write for the OT or handler slot.</summary>
public sealed record MemoryEdit(bool Handler, int Memory, int Intensity, int Feeling, int Variable);

/// <summary>One Technical Record. <paramref name="Permitted"/>: learnable by this species or its evolution chain.</summary>
public sealed record TechRecordEntry(int Index, string Label, int Move, string Name, int Type, bool Learned, bool Permitted);

/// <summary>The summary's read-only view of the fields above; null members are not stored by the format.
/// <paramref name="StatNature"/> is set only when a mint makes it differ from the nature.</summary>
public sealed record MonFieldSummary(
    string? FormArgument,
    int? StatNature,
    string? StatNatureName,
    ShinyKind Shiny,
    uint? Pid,
    uint? EncryptionConstant,
    int? OtGender,
    string? HandlerName,
    int? HandlerGender,
    string? HandlerLanguage,
    int? HandlerFriendship,
    int? OtFriendship,
    bool? WithHandler,
    string? OtMemory,
    string? HandlerMemory,
    int? TechRecordCount,
    IReadOnlyList<string> TechRecordNames);
