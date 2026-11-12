namespace PKForge.Domain;

public enum EmulatorKind
{
    // Persisted in watched roots and scan caches. Never renumber existing entries.
    RetroArch = 0,
    MelonDS = 1,
    Azahar = 2,
    Eden = 3,
    Linkboy = 4,
    Dolphin = 5,
    DraStic = 6,
    PizzaBoyGba = 7,
    PizzaBoyGbc = 8,
    /// <summary>Citra MMJ (weihuoya fork, org.citra.emu): same SDMC layout as Azahar, rooted at citra-emu/.</summary>
    CitraMmj = 9,
    /// <summary>
    /// One save file the player picked with "Open a save file" (a persisted SAF document
    /// grant, not a folder): kept as a watched root whose TreeId is the document URI, so it
    /// stays on the shelf across launches until the player unlinks it.
    /// </summary>
    LinkedFile = 10,
    /// <summary>My Boy! (com.fastemulator.gba): raw .sav battery saves in MyBoy/save on shared storage.</summary>
    MyBoy = 11,
    /// <summary>My OldBoy! (com.fastemulator.gbc): raw .sav battery saves in MyOldBoy/save on shared storage.</summary>
    MyOldBoy = 12,
    /// <summary>PokeDaisy (mGBA and melonDS front end): standard .sav/.srm saves in the folder chosen at its setup.</summary>
    PokeDaisy = 13,
}

/// <summary>A persistable SAF folder grant, opaque to the domain layer.</summary>
public sealed record PickedFolder(string TreeId, string DisplayName);

/// <summary>
/// A save file discovered inside a granted emulator folder. <see cref="RequiresExtraCare"/>
/// flags NAND/SD-structured saves (Azahar/Citra MMJ/Eden) whose in-place writes are the delicate path.
/// </summary>
public sealed record DetectedSave(
    string DocumentId,
    string FileName,
    string GameLabel,
    EmulatorKind Emulator,
    bool RequiresExtraCare,
    DateTimeOffset? LastModified,
    int Generation = 0,
    string? TrainerName = null,
    string? PlayTime = null,
    SaveIdentityGuess? Guess = null,
    string? RomFileName = null,
    string? FolderHint = null,
    ResolvedSaveIdentity? Identity = null,
    string? Language = null,
    bool IsCopy = false,
    int? TrainerId = null)
{
    // IsCopy: found in a backup or hidden folder, or named as a backup (see SaveNoise). It
    // starts in Hidden saves rather than on the shelf, until the player shows it.

    /// <summary>
    /// The name handed to the engine: the chosen game's label, so the edition of a
    /// shared layout (FireRed vs LeafGreen, Ruby vs Sapphire) follows the user's choice
    /// and never the file name or a custom display name.
    /// </summary>
    public string EngineHint => Identity?.GameLabel ?? Guess?.Label ?? GameLabel;

    /// <summary>The engine route: the user's game choice, else what the bytes call for.</summary>
    public SaveFormat Format => Identity?.Format ?? Guess?.Format ?? SaveFormat.Auto;

    /// <summary>Label used for box-art lookup (never a custom name).</summary>
    public string? ArtLabel => Identity is { } identity ? identity.ArtLabel : Guess is { } guess ? guess.ArtLabel : GameLabel;

    /// <summary>The player's own art key for this save (<see cref="CustomGameArt"/>), or null.</summary>
    public string? CustomArtKey => Identity?.ArtKey;
}

/// <summary>Selects a folder through the host platform, persisting read/write access.</summary>
public interface IFolderPicker
{
    ValueTask<PickedFolder?> PickFolderAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and writes named files inside an <see cref="IFolderPicker"/> grant (bank
/// archives, bulk moves). List is flat: only files directly inside the folder.
/// </summary>
public interface IFolderFileAccess
{
    ValueTask<IReadOnlyList<PickedDocument>> ListFilesAsync(string treeId, CancellationToken cancellationToken = default);

    ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>Creates a file with that name in the folder, or overwrites the existing one.</summary>
    ValueTask WriteFileAsync(string treeId, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a NEW file in the folder of the granted document <paramref name="documentId"/>,
    /// under <paramref name="fileName"/> or a free name next to it; never overwrites. Returns
    /// the name written. Throws <see cref="NotSupportedException"/> where that folder cannot be reached.
    /// </summary>
    ValueTask<string> WriteBesideAsync(string documentId, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This file's folder cannot be written to.");
}

/// <summary>Scans a granted emulator folder for parseable Pokémon saves.</summary>
public interface IEmulatorDetectionService
{
    ValueTask<EmulatorScanResult> ScanAsync(string treeId, EmulatorKind kind, CancellationToken cancellationToken = default);
}

/// <summary>Optional streaming scan contract for platforms that can publish saves as they are found.</summary>
public interface IIncrementalEmulatorDetectionService : IEmulatorDetectionService
{
    IAsyncEnumerable<EmulatorScanUpdate> ScanIncrementalAsync(
        string treeId, EmulatorKind kind, CancellationToken cancellationToken = default);
}

/// <summary>A save discovery or completion event emitted by an incremental scan.</summary>
public sealed record EmulatorScanUpdate(
    DetectedSave? Save,
    bool IsComplete,
    int FilesSeen = 0,
    int SavesFound = 0,
    IReadOnlyList<string>? RejectedCandidates = null,
    IReadOnlyList<string>? Diagnostics = null);

/// <summary>
/// Scan outcome plus the evidence: how many files were walked, which looked like saves,
/// and which candidates failed to parse (the user-visible answer to "why wasn't my save found?").
/// </summary>
public sealed record EmulatorScanResult(
    IReadOnlyList<DetectedSave> Saves,
    int FilesSeen,
    IReadOnlyList<string> RejectedCandidates,
    IReadOnlyList<string>? Diagnostics = null);

/// <summary>Remembers granted emulator roots across launches so detection reruns automatically.</summary>
public interface IWatchedRootStore
{
    IReadOnlyList<WatchedRoot> GetRoots();
    void AddRoot(WatchedRoot root);
    void RemoveRoot(WatchedRoot root);
}

public sealed record WatchedRoot(EmulatorKind Kind, string TreeId, string DisplayName);
