using System.Security.Cryptography;

namespace PKForge.Domain;

/// <summary>One file found in a granted folder tree: its path below the folder root ("Bank/01 - Box 1/001 - Bulbasaur.pk1",
/// always '/'-separated), its size and when the provider says it last changed.</summary>
public sealed record FolderEntry(string Path, long Size, DateTimeOffset Modified);

/// <summary>
/// Reads and writes a whole granted folder tree by relative path (the PKForge folder mirror).
/// Kept apart from <see cref="IFolderFileAccess"/>, whose flat contract several features rely on.
/// </summary>
public interface IFolderTreeAccess
{
    /// <summary>Every file below the folder, recursively. Folders themselves are not listed.</summary>
    ValueTask<IReadOnlyList<FolderEntry>> ListTreeAsync(string treeId, CancellationToken cancellationToken = default);

    ValueTask<byte[]> ReadAsync(string treeId, string path, CancellationToken cancellationToken = default);

    /// <summary>Creates the file (and any missing folder on its way) or overwrites it.</summary>
    ValueTask WriteAsync(string treeId, string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    /// <summary>Deletes the file; false when it was already gone.</summary>
    ValueTask<bool> DeleteAsync(string treeId, string path, CancellationToken cancellationToken = default);
}

public enum MirrorActionKind
{
    /// <summary>Local is newer: copy it to the folder.</summary>
    Push,
    /// <summary>The folder is newer: copy it into PKForge.</summary>
    Pull,
    /// <summary>Deleted in the folder, unchanged here since the last sync.</summary>
    DeleteLocal,
    /// <summary>Deleted here, unchanged in the folder since the last sync.</summary>
    DeleteRemote,
    /// <summary>Changed on both sides: the folder's copy is kept as <see cref="MirrorAction.ConflictPath"/>
    /// (on both sides), PKForge's copy keeps the name.</summary>
    Conflict,
}

public sealed record MirrorAction(MirrorActionKind Kind, string Path, string? ConflictPath = null);

/// <summary>
/// The two-way mirror decision, pure: what to do with each path given its content hash here,
/// in the folder, and at the last successful sync. Data that was not synced is never
/// overwritten or deleted: a deletion only happens when the other side still holds exactly
/// what both sides agreed on last time, and a path with no record of the last sync is new.
/// </summary>
public static class MirrorPlan
{
    public static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <param name="local">path → hash of PKForge's side.</param>
    /// <param name="remote">path → hash of the folder's side.</param>
    /// <param name="synced">path → hash both sides held after the last successful sync; null on
    /// a first sync, which is then a plain union and never deletes anything.</param>
    /// <param name="today">the date written into conflict copies' names.</param>
    public static IReadOnlyList<MirrorAction> Compute(
        IReadOnlyDictionary<string, string> local,
        IReadOnlyDictionary<string, string> remote,
        IReadOnlyDictionary<string, string>? synced,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        var taken = new HashSet<string>(local.Keys.Concat(remote.Keys), PathComparer);
        var paths = new SortedSet<string>(local.Keys, StringComparer.Ordinal);
        foreach (var path in remote.Keys)
            if (!local.ContainsKey(path)) paths.Add(path);

        var actions = new List<MirrorAction>();
        foreach (var path in paths)
        {
            var l = local.TryGetValue(path, out var lh) ? lh : null;
            var r = remote.TryGetValue(path, out var rh) ? rh : null;
            var m = synced is not null && synced.TryGetValue(path, out var mh) ? mh : null;
            var action = Decide(l, r, m);
            if (action is null) continue;
            actions.Add(action == MirrorActionKind.Conflict
                ? new MirrorAction(action.Value, path, ConflictName(path, today, taken))
                : new MirrorAction(action.Value, path));
        }
        return actions;
    }

    private static MirrorActionKind? Decide(string? local, string? remote, string? synced)
    {
        if (local is not null && remote is not null)
        {
            if (Same(local, remote)) return null;
            if (synced is not null && Same(local, synced)) return MirrorActionKind.Pull;
            if (synced is not null && Same(remote, synced)) return MirrorActionKind.Push;
            return MirrorActionKind.Conflict;
        }
        if (local is not null)
            // Gone from the folder: deleted there only when this side is what was synced.
            return synced is not null && Same(local, synced) ? MirrorActionKind.DeleteLocal : MirrorActionKind.Push;
        if (remote is not null)
            return synced is not null && Same(remote, synced) ? MirrorActionKind.DeleteRemote : MirrorActionKind.Pull;
        return null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Bank/01 - Box 1/025 - Pikachu (conflict 2026-11-10).pk7", then " 2", " 3"... while the
    /// name is taken; the chosen name joins <paramref name="taken"/>.</summary>
    public static string ConflictName(string path, DateOnly today, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var slash = path.LastIndexOf('/');
        var folder = slash < 0 ? "" : path[..(slash + 1)];
        var name = path[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        var stamp = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var candidate = $"{folder}{stem} (conflict {stamp}){extension}";
        for (var n = 2; taken.Contains(candidate); n++)
            candidate = $"{folder}{stem} (conflict {stamp} {n}){extension}";
        taken.Add(candidate);
        return candidate;
    }

    /// <summary>The content hash both sides are compared by (SHA-256, hex).</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

/// <summary>What one side held for a path after the last successful sync, plus the folder's
/// size and date then, so an unchanged folder file is not read again to learn its hash.</summary>
public sealed record MirrorRecord(string Hash, long RemoteSize, long RemoteModifiedMs, long LocalSize = -1, long LocalModifiedMs = -1);

/// <summary>The outcome of one sync, for the settings row and the log.</summary>
public sealed record MirrorReport(
    DateTimeOffset FinishedUtc,
    int Pushed,
    int Pulled,
    int DeletedRemote,
    int MovedToRecentlyRemoved,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> LeftAlone,
    string? Error = null)
{
    public bool Failed => Error is not null;

    public string Summary => Failed
        ? $"Sync failed: {Error}"
        : $"{Pushed} sent, {Pulled} brought in, {DeletedRemote + MovedToRecentlyRemoved} removed"
          + (Conflicts.Count > 0 ? $", {Conflicts.Count} conflict{(Conflicts.Count == 1 ? "" : "s")}" : "")
          + (LeftAlone.Count > 0 ? $", {LeftAlone.Count} left alone" : "");
}
