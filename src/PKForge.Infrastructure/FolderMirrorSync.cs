using System.Text;
using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>A file PKForge holds, as it appears in the mirror folder. <see cref="Size"/> and
/// <see cref="ModifiedMs"/> are the private file's stamp (-1 when generated in memory).</summary>
public sealed record LocalMirrorFile(string Path, string Hash, Func<byte[]> Read, long Size = -1, long ModifiedMs = -1);

/// <summary>A file brought in from the folder.</summary>
public sealed record IncomingFile(string Path, byte[] Bytes);

/// <summary>What a source did with the changes handed to it. Paths in <see cref="LeftAlone"/>
/// (with why) were not taken in: they stay in the folder as they are and are not recorded as synced.</summary>
public sealed record MirrorApplied(IReadOnlyList<(string Path, string Reason)> LeftAlone, int MovedToRecentlyRemoved)
{
    public static readonly MirrorApplied Nothing = new([], 0);
}

/// <summary>
/// One part of PKForge's private data as it appears in the PKForge folder (the Bank, the
/// album, item presets, pinned saves). A source owns every folder path under its
/// <see cref="Prefix"/> and turns folder changes into its own store's changes.
/// </summary>
public interface IMirrorSource
{
    /// <summary>"Bank/", "Album/", or one exact path ("Items/item-presets.json").</summary>
    string Prefix { get; }

    ValueTask<IReadOnlyList<LocalMirrorFile>> SnapshotAsync(IReadOnlyDictionary<string, MirrorRecord> records, CancellationToken cancellationToken);

    /// <summary>Takes in files pulled from the folder and folder deletions. A deletion never
    /// destroys anything: it goes to Recently removed (or, for saves, only unpins).</summary>
    ValueTask<MirrorApplied> ApplyAsync(IReadOnlyList<IncomingFile> pulled, IReadOnlyList<string> deleted, CancellationToken cancellationToken);

    /// <summary>Hidden files written into the folder after each sync (the bank mapping).</summary>
    IReadOnlyList<(string Path, byte[] Bytes)> Extras() => [];

    /// <summary>
    /// A path changed on both sides. A source that can merge the two takes the folder's copy in
    /// here and returns its own merged bytes, which then go to the folder under the same path (no
    /// conflict copy). Null, the default: the folder's copy is kept under a conflict name.
    /// </summary>
    ValueTask<byte[]?> MergeConflictAsync(string path, byte[] theirs, CancellationToken cancellationToken) =>
        ValueTask.FromResult<byte[]?>(null);

    /// <summary>The sync is over and these are the records both sides now agree on.</summary>
    void Settled(IReadOnlyDictionary<string, MirrorRecord> records) { }
}

/// <summary>The persisted mirror state: one record per path both sides agreed on.</summary>
public sealed class MirrorState
{
    public Dictionary<string, MirrorRecord> Records { get; init; } = new(MirrorPlan.PathComparer);

    public static MirrorState? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var records = JsonSerializer.Deserialize<Dictionary<string, MirrorRecord>>(File.ReadAllText(path));
            // A record without a hash is no agreement: that path is treated as never synced (a union).
            return records is null ? null : new MirrorState
            {
                Records = new(records.Where(pair => pair.Value?.Hash is { Length: > 0 }), MirrorPlan.PathComparer),
            };
        }
        catch (Exception error) when (error is JsonException or IOException or NotSupportedException)
        {
            return null; // an unreadable state is a first sync again: a union, never a deletion
        }
    }

    public void Save(string path)
    {
        AtomicFile.WriteText(path, JsonSerializer.Serialize(Records));
    }
}

/// <summary>
/// Runs one two-way sync between PKForge's private data (through its <see cref="IMirrorSource"/>s)
/// and the PKForge folder. Paths no source owns, hidden files, README.txt and Exports/ are
/// never touched by the plan.
/// </summary>
public static class FolderMirrorSync
{
    public const string ReadmePath = "README.txt";

    public static async Task<(MirrorReport Report, MirrorState State)> SyncAsync(
        IFolderTreeAccess folder, string treeId, IReadOnlyList<IMirrorSource> sources, MirrorState? previous,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var records = new Dictionary<string, MirrorRecord>(previous?.Records ?? [], MirrorPlan.PathComparer);
        var firstSync = previous is null;
        int pushed = 0, pulled = 0, deletedRemote = 0, removed = 0;
        var conflicts = new List<string>();
        var leftAlone = new Dictionary<string, string>(MirrorPlan.PathComparer);
        var today = DateOnly.FromDateTime(now.LocalDateTime);

        var listing = await folder.ListTreeAsync(treeId, cancellationToken).ConfigureAwait(false);
        var remoteBytes = new Dictionary<string, byte[]>(MirrorPlan.PathComparer);
        var remote = new Dictionary<string, string>(MirrorPlan.PathComparer);
        // The stamps the hashes above belong to, and the paths this sync writes: only those
        // stamps are recorded, so an edit made in the folder while the sync runs is read next time.
        var listed = new Dictionary<string, FolderEntry>(MirrorPlan.PathComparer);
        var written = new HashSet<string>(MirrorPlan.PathComparer);
        foreach (var file in listing)
        {
            if (OwnerOf(sources, file.Path) is null) continue;
            listed[file.Path] = file;
            if (records.TryGetValue(file.Path, out var known) && known.RemoteSize == file.Size
                && known.RemoteModifiedMs == file.Modified.ToUnixTimeMilliseconds())
            {
                remote[file.Path] = known.Hash;
                continue;
            }
            var bytes = await folder.ReadAsync(treeId, file.Path, cancellationToken).ConfigureAwait(false);
            remoteBytes[file.Path] = bytes;
            remote[file.Path] = MirrorPlan.Hash(bytes);
        }

        // A second pass settles what the first one changed (a dropped .pk takes its Bank name).
        for (var pass = 0; pass < 2; pass++)
        {
            var localFiles = new Dictionary<string, LocalMirrorFile>(MirrorPlan.PathComparer);
            foreach (var source in sources)
                foreach (var file in await source.SnapshotAsync(records, cancellationToken).ConfigureAwait(false))
                    localFiles[file.Path] = file;
            var local = localFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Hash, MirrorPlan.PathComparer);

            var synced = firstSync && pass == 0 ? null : records.ToDictionary(pair => pair.Key, pair => pair.Value.Hash, MirrorPlan.PathComparer);
            var plan = MirrorPlan.Compute(local, remote, synced, today)
                .Where(action => !leftAlone.ContainsKey(action.Path) && OwnerOf(sources, action.Path) is not null)
                .ToList();

            // Paths already equal on both sides are synced now, whatever their history.
            foreach (var (path, hash) in local)
                if (remote.TryGetValue(path, out var other) && string.Equals(hash, other, StringComparison.OrdinalIgnoreCase))
                    records[path] = Stamped(records, path, hash, localFiles[path]);
            if (plan.Count == 0) break;

            var incoming = new Dictionary<IMirrorSource, (List<IncomingFile> Pulled, List<string> Deleted)>();
            (List<IncomingFile> Pulled, List<string> Deleted) For(string path)
            {
                var owner = OwnerOf(sources, path)!;
                if (!incoming.TryGetValue(owner, out var lists)) incoming[owner] = lists = ([], []);
                return lists;
            }
            async Task<byte[]> RemoteBytes(string path) => remoteBytes.TryGetValue(path, out var cached)
                ? cached
                : remoteBytes[path] = await folder.ReadAsync(treeId, path, cancellationToken).ConfigureAwait(false);

            foreach (var action in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (action.Kind)
                {
                    case MirrorActionKind.Push:
                    {
                        // The hash of the bytes actually sent, not of an earlier read of them.
                        var mine = localFiles[action.Path].Read();
                        var hash = MirrorPlan.Hash(mine);
                        written.Add(action.Path);
                        await folder.WriteAsync(treeId, action.Path, mine, cancellationToken).ConfigureAwait(false);
                        remote[action.Path] = hash;
                        records[action.Path] = Stamped(records, action.Path, hash, localFiles[action.Path]);
                        if (pass == 0) pushed++; // the second pass only renames what the first brought in
                        break;
                    }
                    case MirrorActionKind.DeleteRemote:
                    {
                        // Read again right before deleting: a file edited there since it was hashed
                        // is brought in instead (a released Pokémon comes back), never deleted.
                        var current = await folder.ReadAsync(treeId, action.Path, cancellationToken).ConfigureAwait(false);
                        var hash = MirrorPlan.Hash(current);
                        if (!records.TryGetValue(action.Path, out var agreed) || !string.Equals(agreed.Hash, hash, StringComparison.OrdinalIgnoreCase))
                        {
                            remoteBytes[action.Path] = current;
                            remote[action.Path] = hash;
                            written.Add(action.Path); // its stamp is not the one first listed
                            For(action.Path).Pulled.Add(new IncomingFile(action.Path, current));
                            break;
                        }
                        await folder.DeleteAsync(treeId, action.Path, cancellationToken).ConfigureAwait(false);
                        remote.Remove(action.Path);
                        records.Remove(action.Path);
                        if (pass == 0) deletedRemote++;
                        break;
                    }
                    case MirrorActionKind.Pull:
                        For(action.Path).Pulled.Add(new IncomingFile(action.Path, await RemoteBytes(action.Path)));
                        break;
                    case MirrorActionKind.DeleteLocal:
                        For(action.Path).Deleted.Add(action.Path);
                        break;
                    case MirrorActionKind.Conflict:
                        var theirs = await RemoteBytes(action.Path);
                        // A source that merges (the Item Bank, Pokémon History) takes both sides in one file.
                        if (await OwnerOf(sources, action.Path)!.MergeConflictAsync(action.Path, theirs, cancellationToken).ConfigureAwait(false) is { } merged)
                        {
                            var hash = MirrorPlan.Hash(merged);
                            written.Add(action.Path);
                            await folder.WriteAsync(treeId, action.Path, merged, cancellationToken).ConfigureAwait(false);
                            remote[action.Path] = hash;
                            records[action.Path] = new MirrorRecord(hash, -1, -1);
                            if (pass == 0) { pulled++; pushed++; }
                            break;
                        }
                        // Otherwise the folder's copy is kept under the conflict name, PKForge's keeps the path.
                        var copy = action.ConflictPath!;
                        written.Add(copy);
                        written.Add(action.Path);
                        await folder.WriteAsync(treeId, copy, theirs, cancellationToken).ConfigureAwait(false);
                        remoteBytes[copy] = theirs;
                        remote[copy] = remote[action.Path];
                        await folder.WriteAsync(treeId, action.Path, localFiles[action.Path].Read(), cancellationToken).ConfigureAwait(false);
                        remote[action.Path] = local[action.Path];
                        records[action.Path] = Stamped(records, action.Path, local[action.Path], localFiles[action.Path]);
                        // A copy no part of PKForge reads (a second presets file) just stays in the folder.
                        if (OwnerOf(sources, copy) is not null) For(copy).Pulled.Add(new IncomingFile(copy, theirs));
                        else leftAlone[copy] = "the folder's copy from a conflict; PKForge kept its own";
                        conflicts.Add(action.Path);
                        break;
                }
            }

            foreach (var (source, (pulledFiles, deletedPaths)) in incoming)
            {
                var applied = await source.ApplyAsync(pulledFiles, deletedPaths, cancellationToken).ConfigureAwait(false);
                var refused = applied.LeftAlone.ToDictionary(item => item.Path, item => item.Reason, MirrorPlan.PathComparer);
                foreach (var (path, reason) in refused) leftAlone[path] = reason;
                removed += applied.MovedToRecentlyRemoved;
                foreach (var file in pulledFiles.Where(file => !refused.ContainsKey(file.Path)))
                {
                    records[file.Path] = new MirrorRecord(remote[file.Path], -1, -1);
                    pulled++;
                }
                foreach (var path in deletedPaths.Where(path => !refused.ContainsKey(path)))
                    records.Remove(path);
            }
        }

        // The folder's stamps, so the next sync reads only what changed there. A path read at the
        // start keeps the stamp its hash was taken under; a path this sync wrote gets none, so the
        // next sync reads it once (a stamp listed now could already belong to someone else's edit).
        var after = await folder.ListTreeAsync(treeId, cancellationToken).ConfigureAwait(false);
        var present = after.ToDictionary(file => file.Path, MirrorPlan.PathComparer);
        foreach (var path in records.Keys.ToList())
        {
            if (!present.ContainsKey(path)) { records.Remove(path); continue; }
            records[path] = !written.Contains(path) && listed.TryGetValue(path, out var file)
                ? records[path] with { RemoteSize = file.Size, RemoteModifiedMs = file.Modified.ToUnixTimeMilliseconds() }
                : records[path] with { RemoteSize = -1, RemoteModifiedMs = -1 };
        }

        foreach (var source in sources) source.Settled(records);
        foreach (var source in sources)
            foreach (var (path, bytes) in source.Extras())
                await folder.WriteAsync(treeId, path, bytes, cancellationToken).ConfigureAwait(false);
        if (!present.ContainsKey(ReadmePath))
            await folder.WriteAsync(treeId, ReadmePath, Encoding.UTF8.GetBytes(Readme), cancellationToken).ConfigureAwait(false);

        var report = new MirrorReport(now, pushed, pulled, deletedRemote, removed, conflicts,
            [.. leftAlone.Select(pair => $"{pair.Key}: {pair.Value}")]);
        return (report, new MirrorState { Records = records });
    }

    private static MirrorRecord Stamped(Dictionary<string, MirrorRecord> records, string path, string hash, LocalMirrorFile local) =>
        new(hash, records.TryGetValue(path, out var old) ? old.RemoteSize : -1, records.TryGetValue(path, out old) ? old.RemoteModifiedMs : -1,
            local.Size, local.ModifiedMs);

    private static bool Hidden(string path) => path.Split('/').Any(segment => segment.StartsWith('.'));

    private static IMirrorSource? OwnerOf(IReadOnlyList<IMirrorSource> sources, string path)
    {
        if (Hidden(path)) return null;
        foreach (var source in sources)
        {
            var prefix = source.Prefix;
            if (prefix.EndsWith('/') ? path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                     : string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase))
                return source;
        }
        return null;
    }

    public const string Readme = """
        This is your PKForge folder.

        PKForge keeps a copy of its data here and keeps both sides in sync: when the app
        changes something it lands here, and what you change here comes back into the app.
        You can back this folder up, sync it with Syncthing, or copy it to another device.

        Bank/      Your Bank: one folder per box, one file per Pokémon (PKHeX reads them).
                   Drop a .pk file in a box folder to deposit it; move a file to move the
                   Pokémon; edit one to update it. A file you delete here is never destroyed:
                   PKForge keeps it in Recently removed for 30 days.
                   .pkforge-bank.json is PKForge's map of the files: leave it be.
        Album/     Poképark photos and their captions.
        Saves/     Your pinned restore points, as ordinary .sav files, by game.
        Items/     Item presets.
        Exports/   Where PKForge puts .pk exports and full data archives.

        When a file changed both here and in the app since the last sync, both are kept:
        the folder's version is renamed "name (conflict YYYY-MM-DD)".
        """;
}
