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
}

/// <summary>A persistable SAF folder grant, opaque to the domain layer.</summary>
public sealed record PickedFolder(string TreeId, string DisplayName);

/// <summary>
/// A save file discovered inside a granted emulator folder. <see cref="RequiresExtraCare"/>
/// flags NAND/SD-structured saves (Azahar/Eden) whose in-place writes are the delicate path.
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
    string? PlayTime = null);

/// <summary>Selects a folder through the host platform, persisting read/write access.</summary>
public interface IFolderPicker
{
    ValueTask<PickedFolder?> PickFolderAsync(CancellationToken cancellationToken = default);
}

/// <summary>Scans a granted emulator folder for parseable Pokémon saves.</summary>
public interface IEmulatorDetectionService
{
    ValueTask<EmulatorScanResult> ScanAsync(string treeId, EmulatorKind kind, CancellationToken cancellationToken = default);
}

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
