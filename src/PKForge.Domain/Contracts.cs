namespace PKForge.Domain;

public interface ISaveEngine
{
    SaveSnapshot Open(ReadOnlyMemory<byte> bytes, string? displayName = null);
    ISaveEngineSession OpenSession(ReadOnlyMemory<byte> bytes, string? displayName = null);
    ReadOnlyMemory<byte> Serialize(SaveSnapshot snapshot);
    bool Validate(ReadOnlyMemory<byte> bytes);

    /// <summary>Cheap metadata probe for detection listings; null when the bytes are not a save.</summary>
    SaveDescription? TryDescribe(ReadOnlyMemory<byte> bytes);

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
}

public sealed record SaveDescription(string GameName, int Generation, string TrainerName, string PlayTime);

public interface IBackupService
{
    ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, CancellationToken cancellationToken = default);
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
}

/// <summary>Commits validated bytes only after a durable backup has completed.</summary>
public interface ISafeSaveWriter
{
    ValueTask<SaveWriteReceipt> WriteAsync(string documentId, SaveSnapshot original, ReadOnlyMemory<byte> candidate, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Bank: the app's own cross-game vault. Entities are stored as raw decrypted bytes
/// with provenance, never lossily normalized (brief §7). Unlimited boxes; the index is
/// written atomically and survives everything.
/// </summary>
public interface IBankService
{
    IReadOnlyList<BankEntry> GetAll();
    int BoxCount { get; }
    BankEntry Add(byte[] data, BankEntryInfo info);
    byte[] GetData(Guid id);
    void Move(Guid id, int box, int slot);
    void Remove(Guid id);
    /// <summary>Replaces an entry's bytes and facts in place, keeping its id, box and slot (edit).</summary>
    void Replace(Guid id, byte[] data, BankEntryInfo info);
    /// <summary>Adds one more empty box.</summary>
    void AddBox();
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

public sealed record SlotSummary(int Box, int Slot, int? Species, string? Nickname, bool IsShiny, bool IsLegal, int Form = 0);

public sealed record BackupReceipt(string BackupId, DateTimeOffset CreatedUtc, string Sha256);

public sealed record BackupInfo(
    string BackupId,
    DateTimeOffset CreatedUtc,
    string Sha256,
    string? DisplayName,
    string Format,
    int Generation,
    long SizeBytes);

public sealed record PickedDocument(string DocumentId, string DisplayName);

public sealed record SaveSession(PickedDocument Document, SaveSnapshot Snapshot);

public sealed record SaveWriteReceipt(string BackupId, string OriginalSha256, string WrittenSha256, DateTimeOffset WrittenUtc);

public sealed record BankEntity(
    Guid EntityId,
    string Format,
    int Generation,
    ReadOnlyMemory<byte> RawBytes,
    string? Nickname,
    int Species,
    DateTimeOffset AddedUtc,
    string SourceKind);
