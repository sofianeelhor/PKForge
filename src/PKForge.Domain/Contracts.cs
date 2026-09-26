namespace PKForge.Domain;

public interface ISaveEngine
{
    SaveSnapshot Open(ReadOnlyMemory<byte> bytes, string? displayName = null);
    ISaveEngineSession OpenSession(ReadOnlyMemory<byte> bytes, string? displayName = null);
    ReadOnlyMemory<byte> Serialize(SaveSnapshot snapshot);
    bool Validate(ReadOnlyMemory<byte> bytes);

    /// <summary>Cheap metadata probe for detection listings; null when the bytes are not a save.</summary>
    SaveDescription? TryDescribe(ReadOnlyMemory<byte> bytes, string? displayName = null);

    /// <summary>
    /// Describes loose .pk* bytes for a bank deposit; null when unrecognizable.
    /// <paramref name="format"/> is the exact entity format the bytes were exported as
    /// (<see cref="SlotExport.Format"/>, a file extension such as ".pb8", or a stored
    /// <see cref="BankEntryInfo.Format"/>); it is recorded on the result so the bytes are
    /// never re-read as a same-size sibling (PK8/PB8, PK6/PK7, PK9/PA9).
    /// </summary>
    BankEntryInfo? TryDescribeEntity(byte[] bytes, string sourceName, string? format = null);

    /// <summary>
    /// Opens a single loose entity (e.g. a bank mon) for editing in its own throwaway
    /// save context, so the full editor - legality, ability choices, stats - works on it.
    /// The mon sits at box 0, slot 0. Null when the bytes are not a recognizable entity.
    /// </summary>
    /// <remarks><paramref name="format"/>: the entity's exact format (e.g. a bank entry's
    /// <see cref="BankEntryInfo.Format"/>); null reads the bytes by PKHeX's heuristics.</remarks>
    ISaveEngineSession? OpenEntitySession(byte[] entityBytes, string? displayName = null, string? format = null);

    /// <summary>
    /// Opens a blank throwaway save of the given generation (1-9) with a placeholder
    /// trainer identity (OT "PKForge"). Used to generate Pokémon with no game connected;
    /// the identity is editable afterwards. Never serialized back to any file.
    /// </summary>
    ISaveEngineSession OpenBlankSession(int generation, string? displayName = null);

    /// <summary>
    /// Why these bytes are risky to write back with the layout they were opened as (e.g. a
    /// ROM hack whose layout the engine does not know, parsed as vanilla Gen 3), or null
    /// when the engine is confident. SafeSaveWriter refuses such writes until confirmed.
    /// </summary>
    string? DescribeLayoutRisk(ReadOnlyMemory<byte> bytes) => null;

    /// <summary>
    /// The typed verdict behind <see cref="DescribeLayoutRisk"/>: null when the engine is
    /// confident, otherwise whether the user may knowingly proceed (a suspected ROM hack on
    /// a vanilla layout) or a vanilla write provably corrupts the file.
    /// </summary>
    LayoutRisk? AssessLayoutRisk(ReadOnlyMemory<byte> bytes) =>
        DescribeLayoutRisk(bytes) is { } reason ? new LayoutRisk(LayoutRiskKind.CorruptingLayout, reason) : null;

    /// <summary>
    /// Structural diff sanity check of a candidate against the bytes it was derived from:
    /// same format route, no readable Pokémon turned unreadable, and (when a scope is
    /// given) no slot outside the scope changed. Null when safe, otherwise the reason.
    /// </summary>
    string? CheckWriteSafety(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> candidate, WriteScope? scope) => null;
}

/// <summary>How far the engine trusts a save's layout; see <see cref="ISaveEngine.AssessLayoutRisk"/>.</summary>
public enum LayoutRiskKind
{
    None = 0,
    /// <summary>The layout round-trips as vanilla and every Pokémon decodes, but species ids
    /// fall outside the game's range: a ROM hack on the vanilla layout. Writes are refused
    /// until the user accepts the risk.</summary>
    SuspectedHack = 1,
    /// <summary>A vanilla write provably corrupts the file (checksums follow another layout,
    /// or the Pokémon are in a foreign format). Always refused; no confirmation lifts it.</summary>
    CorruptingLayout = 2,
}

/// <summary>One layout verdict and its human-readable reason.</summary>
public sealed record LayoutRisk(LayoutRiskKind Kind, string Reason)
{
    public bool UserMayProceed => Kind == LayoutRiskKind.SuspectedHack;
}

/// <param name="Language">The cartridge language the save records ("FR"), when it records one reliably.</param>
public sealed record SaveDescription(string GameName, int Generation, string TrainerName, string PlayTime, string? Language = null);

public interface IBackupService
{
    /// <summary>The change description shown in the restore point list (what this point undoes).</summary>
    ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, string? changeDescription = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default);
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(string backupId, CancellationToken cancellationToken = default);
}

public interface ISaveFileAccess
{
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default);
    ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
}

/// <summary>Selects a document through the host platform without exposing platform types.</summary>
public interface IDocumentPicker
{
    ValueTask<PickedDocument?> PickSaveAsync(CancellationToken cancellationToken = default);

    /// <summary>Multi-file selection (bulk .pk import); empty when cancelled.</summary>
    ValueTask<IReadOnlyList<PickedDocument>> PickManyAsync(CancellationToken cancellationToken = default);
}

/// <summary>Owns the currently opened save and its source document identity.</summary>
public interface ISaveSessionService
{
    SaveSession? Current { get; }
    ISaveEngineSession? CurrentSession { get; }
    ValueTask<SaveSession> OpenAsync(PickedDocument document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the tracked baseline of <paramref name="documentId"/> to the bytes that were
    /// just written, so the NEXT restore point captures the state before the next change
    /// instead of the state from when the save was opened.
    /// </summary>
    void MarkWritten(string documentId, ReadOnlyMemory<byte> written);

    /// <summary>
    /// Discards whatever the live engine session holds beyond the tracked baseline (the bytes
    /// last read or written), reopening it from that baseline. Used when a mutation failed
    /// half-way, so its partial edits can never ride along with the next write.
    /// </summary>
    void RevertToBaseline();

    /// <summary>Drops the open save: nothing stale can be written back once the file changed underneath it.</summary>
    void Close();
}

/// <summary>Commits validated bytes only after a durable backup has completed.</summary>
public interface ISafeSaveWriter
{
    /// <summary>
    /// No-op when the candidate equals the tracked baseline: no backup, no write, and a
    /// receipt with <see cref="SaveWriteReceipt.Changed"/> false, so nothing is written
    /// (and no restore point is created) when a mutation produced identical bytes.
    /// </summary>
    ValueTask<SaveWriteReceipt> WriteAsync(string documentId, SaveSnapshot original, ReadOnlyMemory<byte> candidate, string? changeDescription = null, CancellationToken cancellationToken = default);

    /// <summary>As <see cref="WriteAsync"/>, but declares which slots the mutation targeted
    /// so every other slot is verified unchanged before anything is written.</summary>
    ValueTask<SaveWriteReceipt> WriteScopedAsync(string documentId, SaveSnapshot original, ReadOnlyMemory<byte> candidate, WriteScope scope, string? changeDescription = null, CancellationToken cancellationToken = default)
        => WriteAsync(documentId, original, candidate, changeDescription, cancellationToken);

    /// <summary>Records that the user explicitly accepted writing a save the engine flagged as
    /// a suspected ROM hack (<see cref="LayoutRiskKind.SuspectedHack"/>). A corrupting layout
    /// stays refused regardless.</summary>
    void ConfirmLayoutRisk(string documentId) { }

    /// <summary>Withdraws <see cref="ConfirmLayoutRisk"/>: the save becomes read-only again.</summary>
    void RevokeLayoutRisk(string documentId) { }

    /// <summary>The layout verdict for this document's bytes (cached per document), or null
    /// when the engine is confident.</summary>
    LayoutRisk? LayoutRiskOf(string documentId, SaveSnapshot original) => null;

    /// <summary>
    /// Why every ordinary write of this save would be refused before any slot is compared
    /// (read-only identity, unconfirmed layout risk), or null when it accepts writes. Moves
    /// out of a save ask first, so the destination is never written when the source cannot
    /// release the Pokémon afterwards.
    /// </summary>
    string? WhyWritesAreRefused(string documentId, SaveSnapshot original) => null;
}

/// <summary>A save slot address; box -1 is the party.</summary>
public readonly record struct SlotRef(int Box, int Slot);

/// <summary>
/// What a write is allowed to change. <see cref="Slots"/> lists the targeted slots (any
/// party slot unlocks the whole party, which compacts); <see cref="Unrestricted"/> is for
/// deliberate whole-file writes such as restoring a backup.
/// </summary>
public sealed record WriteScope(IReadOnlyCollection<SlotRef> Slots, bool Unrestricted = false)
{
    public static WriteScope Everything { get; } = new([], Unrestricted: true);
    public static WriteScope Only(params SlotRef[] slots) => new(slots);
}

/// <summary>A write refused by a structural safety check; the original was not touched.</summary>
public sealed class UnsafeSaveWriteException(string message, bool requiresConfirmation = false) : InvalidOperationException(message)
{
    /// <summary>True when the refusal lifts once the user confirms (layout ambiguity).</summary>
    public bool RequiresConfirmation { get; } = requiresConfirmation;
}

/// <summary>
/// The Bank: the app's own cross-game vault. Entities are stored as raw decrypted bytes
/// with provenance, never lossily normalized (brief §7). Unlimited boxes; the index is
/// written atomically and survives everything.
/// </summary>
public interface IBankService
{
    /// <summary>Slots in one box; the vault is a flat sequence of 30-slot boxes that auto-grow.</summary>
    const int SlotsPerBox = 30;

    IReadOnlyList<BankEntry> GetAll();
    int BoxCount { get; }
    BankEntry Add(byte[] data, BankEntryInfo info);
    byte[] GetData(Guid id);
    void Move(Guid id, int box, int slot);
    void Remove(Guid id);
    /// <summary>Removes every given entry in one index write (organizer releases); unknown ids
    /// are ignored. Returns how many were released.</summary>
    int RemoveMany(IReadOnlyList<Guid> ids);
    /// <summary>Replaces an entry's bytes and facts in place, keeping its id, box and slot (edit).</summary>
    void Replace(Guid id, byte[] data, BankEntryInfo info);
    /// <summary>Adds one more empty box.</summary>
    void AddBox();
    /// <summary>
    /// Rearranges whole boxes (swap, move, insert, delete) in one index write: every entry
    /// follows its box, bytes untouched. Throws when a deleted box still holds anything, so
    /// no entry can ever be lost this way.
    /// </summary>
    void RemapBoxes(BankBoxRemap remap);
    /// <summary>Rewrites the facts of several entries in one index write, bytes untouched
    /// (index migrations). Unknown ids are ignored; returns how many entries changed.</summary>
    int UpdateInfo(IReadOnlyList<(Guid Id, BankEntryInfo Info)> updates);
    /// <summary>The index migrations this bank has already been through (0 for an index written
    /// before the marker existed). A finished migration is never attempted again.</summary>
    int MigrationVersion { get; }
    /// <summary>Records that migration <paramref name="version"/> ran over every entry, applying its
    /// fixes in the same single index write. A fix lands only while the entry still holds
    /// <c>Expected</c>, so a migration computed off-lock never undoes a concurrent edit.
    /// Returns how many entries changed.</summary>
    int CompleteMigration(int version, IReadOnlyList<(Guid Id, BankEntryInfo Expected, BankEntryInfo Info)> updates);
    /// <summary>
    /// Applies a batch of slot placements in one index write - what the organizer's sorts and
    /// bulk moves write. Every id must exist, no two placements may share a slot, and every
    /// target slot must either be free or held by an entry that is itself being placed here;
    /// stored bytes never move. Returns how many entries actually changed slot.
    /// </summary>
    int Place(IReadOnlyList<(Guid Id, int Box, int Slot)> placements);
}

/// <summary>Descriptive facts captured at deposit time (display without parsing bytes).</summary>
/// <param name="Format">
/// The exact PKHeX entity type the bytes are ("PK8", "PB8", "PK6", "PA9", ...). Raw bytes alone
/// are ambiguous between same-size formats, so every re-read of bank bytes goes through this.
/// Null only on entries from an index written before the field existed and not yet migrated
/// (or whose format could not be proven); those fall back to PKHeX's heuristics.
/// </param>
public sealed record BankEntryInfo(
    int Species, int Form, bool Shiny, string Nickname, int Level, int Generation, string SourceName,
    string? Format = null, int? HeldItem = null, SpriteTraits? Traits = null)
{
    /// <summary>The sprite key of this entry. <see cref="Traits"/> (gender art, Alcremie sweet,
    /// cosplay) is null on entries indexed before the field existed until
    /// EntityBytes.MigrateSpriteTraits reads it from the stored bytes.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SpriteLook Look => new(Species, Form, Shiny, Traits ?? default);

    /// <summary>Held item id when known; null on entries deposited before the field existed
    /// (backfilled lazily from the stored bytes).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasItem => HeldItem is > 0;
}

public sealed record BankEntry(
    Guid Id, int Box, int Slot, BankEntryInfo Info, DateTimeOffset AddedUtc);

public sealed record SaveSnapshot(
    string Format,
    int Generation,
    ReadOnlyMemory<byte> OriginalBytes,
    IReadOnlyList<SlotSummary> Slots,
    string? DisplayName);

/// <param name="HeldItem">The held item's national (PKHeX) item id, 0 when empty. ROM-hack
/// sessions bridge their own ids to national ones; unknown bridges still read non-zero.</param>
public sealed record SlotSummary(int Box, int Slot, int? Species, string? Nickname, bool IsShiny, bool IsLegal, int Form = 0, bool IsEgg = false,
    int HeldItem = 0, SpriteTraits Traits = default)
{
    /// <summary>The sprite key of this slot (species 0 when empty).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SpriteLook Look => new(Species ?? 0, Form, IsShiny, Traits);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasItem => HeldItem != 0;
}

public sealed record BackupReceipt(string BackupId, DateTimeOffset CreatedUtc, string Sha256);

public sealed record BackupInfo(
    string BackupId,
    DateTimeOffset CreatedUtc,
    string Sha256,
    string? DisplayName,
    string Format,
    int Generation,
    long SizeBytes,
    string? ChangeDescription = null);

public sealed record PickedDocument(string DocumentId, string DisplayName);

public sealed record SaveSession(PickedDocument Document, SaveSnapshot Snapshot);

public sealed record SaveWriteReceipt(
    string BackupId,
    string OriginalSha256,
    string WrittenSha256,
    DateTimeOffset WrittenUtc,
    bool Changed = true);

public sealed record BankEntity(
    Guid EntityId,
    string Format,
    int Generation,
    ReadOnlyMemory<byte> RawBytes,
    string? Nickname,
    int Species,
    DateTimeOffset AddedUtc,
    string SourceKind);

/// <summary>File-name conventions for bank entries, shared by exports and archives.</summary>
public static class BankEntryFiles
{
    /// <summary>
    /// The PKHeX file extension for an entry: its recorded format (".pb8", ".pk6", ".pa9"), so a
    /// re-import reads the same type; ".pk{generation}" for an entry with no recorded format.
    /// </summary>
    public static string ExtensionFor(BankEntryInfo info) =>
        info.Format is { Length: > 0 } format ? "." + format.ToLowerInvariant() : $".pk{info.Generation}";
}
