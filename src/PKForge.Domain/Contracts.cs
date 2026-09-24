namespace PKForge.Domain;

public interface ISaveEngine
{
    SaveSnapshot Open(ReadOnlyMemory<byte> bytes, string? displayName = null);
    ISaveEngineSession OpenSession(ReadOnlyMemory<byte> bytes, string? displayName = null);
    ReadOnlyMemory<byte> Serialize(SaveSnapshot snapshot);
    bool Validate(ReadOnlyMemory<byte> bytes);

    /// <summary>Cheap metadata probe for detection listings; null when the bytes are not a save.</summary>
    SaveDescription? TryDescribe(ReadOnlyMemory<byte> bytes, string? displayName = null);

    /// <summary>Describes loose .pk* bytes for a bank deposit; null when unrecognizable.</summary>
    BankEntryInfo? TryDescribeEntity(byte[] bytes, string sourceName);

    /// <summary>
    /// Opens a single loose entity (e.g. a bank mon) for editing in its own throwaway
    /// save context, so the full editor - legality, ability choices, stats - works on it.
    /// The mon sits at box 0, slot 0. Null when the bytes are not a recognizable entity.
    /// </summary>
    ISaveEngineSession? OpenEntitySession(byte[] entityBytes, string? displayName = null);

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
    /// Applies a batch of slot placements in one index write - what the organizer's sorts and
    /// bulk moves write. Every id must exist, no two placements may share a slot, and every
    /// target slot must either be free or held by an entry that is itself being placed here;
    /// stored bytes never move. Returns how many entries actually changed slot.
    /// </summary>
    int Place(IReadOnlyList<(Guid Id, int Box, int Slot)> placements);
}

/// <summary>Descriptive facts captured at deposit time (display without parsing bytes).</summary>
public sealed record BankEntryInfo(
    int Species, int Form, bool Shiny, string Nickname, int Level, int Generation, string SourceName);

public sealed record BankEntry(
    Guid Id, int Box, int Slot, BankEntryInfo Info, DateTimeOffset AddedUtc);

public sealed record SaveSnapshot(
    string Format,
    int Generation,
    ReadOnlyMemory<byte> OriginalBytes,
    IReadOnlyList<SlotSummary> Slots,
    string? DisplayName);

public sealed record SlotSummary(int Box, int Slot, int? Species, string? Nickname, bool IsShiny, bool IsLegal, int Form = 0, bool IsEgg = false);

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
