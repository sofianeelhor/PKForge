using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>
/// Downloads the complete offline sprite pack: animated Showdown sprites, HOME renders and
/// item icons. The fast path is one archive (built by tools/SpritePack, published as a
/// release asset): a single resumable download, checked against its SHA-256, then unpacked
/// file by file into the same caches the app already reads. Whatever is still missing after
/// that, or everything when the archive is unavailable, is fetched file by file. Purely
/// additive and resumable: existing files are never touched.
/// </summary>
public sealed class SpritePackDownloader(ISpriteService sprites, IGameDataService data)
{
    /// <summary>Rough size of the full pack; shown before starting.</summary>
    public const string SizeHint = "~600 MB";

    /// <summary>
    /// One downloadable archive: its release asset, exact size and SHA-256 (printed by
    /// tools/SpritePack). A new archive gets a new release tag, so an app build always
    /// verifies exactly the bytes it expects, and an archive never changes once published.
    /// </summary>
    private sealed record Archive(string Name, string Url, long Bytes, string Sha256)
    {
        /// <summary>Written once this archive is unpacked; the main pack's name predates add-ons and is kept.</summary>
        public string DoneMarker(string root) => Path.Combine(root, "spritepack", Sha256[..16] + ".done");
    }

    // The main pack: Showdown animations, HOME renders, item icons. Falls back to per-file downloads.
    private static readonly Archive Main = new("Sprite pack",
        "https://github.com/sofianeelhor/PKForge/releases/download/sprites-1/pkforge-sprites.zip",
        625_017_170, "611f384b2475f24946ecd51ba8a726aafffc04736f0cedf47236a1ac00ff6cd7");

    // Add-ons: archive only, no per-file source. A new add-on is appended here; players who
    // have the others fetch just that one. Missing add-ons change nothing: their art is optional.
    private static readonly Archive[] AddOns =
    [
        // BDSP-style box icons, normal and shiny (Team Luminescent's bdsp-shiny-icons, MIT),
        // drawn in Brilliant Diamond / Shining Pearl and Luminescent Platinum boxes.
        new("BDSP icons", "https://github.com/sofianeelhor/PKForge/releases/download/bdsp-icons-1/pkforge-bdsp-icons.zip",
            44_217_786, "cdbc291dae933b57f7c28018410b5162aed67342a9847843a8586fa741823baf"),
    ];

    /// <summary>Below this many missing files, fetching them one by one beats a full archive.</summary>
    private const int ArchiveThreshold = 400;

    private const int Parallelism = 16;

    private static readonly HttpClient ArchiveHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>How many pack files this device still lacks; 0 once the pack is complete.</summary>
    public int CountMissing()
    {
        var root = FileSystem.AppDataDirectory;
        return SpritePack.Entries([]).Count(e => !File.Exists(Path.Combine(root, e.CachePath))) + WantedItems(root).Count;
    }

    // Many item names have no icon upstream. Once the archive is in, the ones it lacks are
    // known absent: counting them again would refetch the whole archive or send ~1800
    // requests that can only fail. The UI still asks for them on demand, as before.
    private List<string> WantedItems(string root) => File.Exists(Main.DoneMarker(root))
        ? []
        : [.. data.ItemNames.Where(n => SpritePack.ItemSlug(n).Length > 0).Distinct().Where(n => !ItemArt.IsCachedOrKnownMissing(n))];

    /// <summary>One pack as the sprite pack screen shows it: what it brings, its size, how much of it is here.</summary>
    public sealed record PackStatus(string Name, string About, long Bytes, bool IsMain, bool Installed, int Missing, bool Started);

    /// <summary>The main pack, then each add-on, with where each stands on this device. Reads the disk: call off the UI thread.</summary>
    public IReadOnlyList<PackStatus> Packs()
    {
        var root = FileSystem.AppDataDirectory;
        var missing = CountMissing();
        var list = new List<PackStatus>
        {
            new(Main.Name, "Animated battle sprites, HOME pictures and item icons for every Pokémon, to use offline.",
                Main.Bytes, true, missing == 0, missing, File.Exists(Main.DoneMarker(root)) || HasFiles(root, "home") || HasFiles(root, "showdown")),
        };
        foreach (var addOn in AddOns)
        {
            var done = File.Exists(addOn.DoneMarker(root));
            list.Add(new(addOn.Name, addOn.Name == "BDSP icons"
                    ? "Box icons in the Brilliant Diamond / Shining Pearl style, normal and shiny, for those games' boxes."
                    : "Extra art for PKForge.",
                addOn.Bytes, false, done, done ? 0 : 1, done));
        }
        return list;
    }

    private static bool HasFiles(string root, string folder)
    {
        var path = Path.Combine(root, folder);
        return Directory.Exists(path) && Directory.EnumerateFiles(path).Any();
    }

    /// <summary>Downloads one pack: the main one (with its add-ons, as always) or a single add-on.</summary>
    public async Task RunPackAsync(string name, Action<string, double> onProgress, CancellationToken cancellationToken)
    {
        if (name == Main.Name)
        {
            await RunAsync(onProgress, cancellationToken).ConfigureAwait(false);
            return;
        }
        var root = FileSystem.AppDataDirectory;
        if (AddOns.FirstOrDefault(a => a.Name == name) is not { } addOn || File.Exists(addOn.DoneMarker(root))) return;
        await AddOnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await TryArchiveAsync(root, addOn, onProgress, cancellationToken).ConfigureAwait(false))
                throw new IOException($"{addOn.Name} could not be downloaded. Check the connection and the free space, then try again.");
            await File.WriteAllTextAsync(addOn.DoneMarker(root), addOn.Url, cancellationToken).ConfigureAwait(false);
            sprites.ForgetAddOnLookups();
        }
        finally { AddOnGate.Release(); }
    }

    /// <summary>
    /// Removes a pack's files from this device, to free the space. The main pack's sprites come
    /// back one by one when a screen shows them (online), as before any download; an add-on's
    /// art simply stops being drawn until it is downloaded again.
    /// </summary>
    public void DeletePack(string name)
    {
        var root = FileSystem.AppDataDirectory;
        string[] folders;
        Archive archive;
        if (name == Main.Name) { folders = ["home", "showdown", "items"]; archive = Main; }
        else if (AddOns.FirstOrDefault(a => a.Name == name) is { } addOn) { folders = [BdspIcons.Folder]; archive = addOn; }
        else return;
        TryDelete(archive.DoneMarker(root));
        foreach (var folder in folders)
        {
            var path = Path.Combine(root, folder);
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("sprites", $"Deleting {folder}: {error.Message}");
            }
        }
        sprites.ForgetAddOnLookups();
        AppLog.Info("sprites", $"Player deleted the {name} pack");
    }

    public async Task RunAsync(Action<string, double> onProgress, CancellationToken cancellationToken)
    {
        var root = FileSystem.AppDataDirectory;
        var spriteEntries = SpritePack.Entries([]);
        var missing = spriteEntries.Count(e => !File.Exists(Path.Combine(root, e.CachePath))) + WantedItems(root).Count;
        if (missing >= ArchiveThreshold && await TryArchiveAsync(root, Main, onProgress, cancellationToken).ConfigureAwait(false))
            await File.WriteAllTextAsync(Main.DoneMarker(root), Main.Url, cancellationToken).ConfigureAwait(false);

        await DownloadMissingAsync(root, spriteEntries, WantedItems(root), onProgress, cancellationToken).ConfigureAwait(false);

        await FetchAddOnsAsync(root, onProgress, cancellationToken).ConfigureAwait(false);
    }

    // One add-on download at a time: the quiet one and a full download share archive files.
    private static readonly SemaphoreSlim AddOnGate = new(1, 1);
    private DateTime _quietRetryAfter = DateTime.MinValue;

    /// <summary>
    /// Fetches the add-ons in the background for players who already have the main pack, so
    /// new art arrives with an update and nobody has to download anything again. Silent: no
    /// overlay, no status, only on Wi-Fi or Ethernet (never mobile data), and any failure is
    /// logged and retried later. Whoever has no main pack gets add-ons with it instead.
    /// </summary>
    public async Task FetchAddOnsQuietlyAsync()
    {
        var root = FileSystem.AppDataDirectory;
        if (DateTime.UtcNow < _quietRetryAfter || !File.Exists(Main.DoneMarker(root))
            || AddOns.All(a => File.Exists(a.DoneMarker(root))))
            return;
        try
        {
            var network = Connectivity.Current;
            if (network.NetworkAccess != NetworkAccess.Internet
                || !network.ConnectionProfiles.Any(p => p is ConnectionProfile.WiFi or ConnectionProfile.Ethernet))
                return;
            if (!await Task.Run(() => FetchAddOnsAsync(root, static (_, _) => { }, CancellationToken.None)).ConfigureAwait(false))
            {
                _quietRetryAfter = DateTime.UtcNow.AddMinutes(15);
                AppLog.Warn("sprites", "Add-on download did not finish; will retry later");
            }
        }
        catch (Exception error)
        {
            _quietRetryAfter = DateTime.UtcNow.AddMinutes(15);
            AppLog.Error("sprites", "Add-on download failed", error);
        }
    }

    /// <summary>Downloads every add-on not yet unpacked; true when all of them are in.</summary>
    private async Task<bool> FetchAddOnsAsync(string root, Action<string, double> onProgress, CancellationToken cancellationToken)
    {
        await AddOnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var complete = true;
            foreach (var addOn in AddOns.Where(a => !File.Exists(a.DoneMarker(root))))
            {
                if (await TryArchiveAsync(root, addOn, onProgress, cancellationToken).ConfigureAwait(false))
                    await File.WriteAllTextAsync(addOn.DoneMarker(root), addOn.Url, cancellationToken).ConfigureAwait(false);
                else
                    complete = false;
            }
            sprites.ForgetAddOnLookups(); // icons that were absent a minute ago may be here now
            return complete;
        }
        finally { AddOnGate.Release(); }
    }

    /// <summary>
    /// The one-request path; true once the archive is unpacked. Any failure (offline, asset
    /// not published, damaged download, not enough space) returns false: the per-file pass
    /// that follows covers everything.
    /// </summary>
    private static async Task<bool> TryArchiveAsync(string root, Archive source, Action<string, double> onProgress, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(root, "spritepack");
        var archive = Path.Combine(folder, Path.GetFileName(new Uri(source.Url).LocalPath));
        var partial = archive + ".part";
        try
        {
            Directory.CreateDirectory(folder);
            // The archive and its unpacked files coexist until unpacking ends.
            var needed = source.Bytes * 2 + (64L << 20) - (File.Exists(partial) ? new FileInfo(partial).Length : 0);
            if (new DriveInfo(root).AvailableFreeSpace < needed) return false;

            if (!File.Exists(archive))
            {
                await DownloadArchiveAsync(partial, source, onProgress, cancellationToken).ConfigureAwait(false);
                onProgress($"{source.Name} · checking the download…", 1);
                if (!await MatchesAsync(partial, source, cancellationToken).ConfigureAwait(false))
                {
                    File.Delete(partial);
                    return false;
                }
                File.Move(partial, archive, overwrite: true);
            }

            await UnpackAsync(archive, root, source.Name, onProgress, cancellationToken).ConfigureAwait(false);
            File.Delete(archive);
            return true;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                      && !cancellationToken.IsCancellationRequested)
        {
            // A damaged archive is not worth resuming; a cut download is.
            if (error is InvalidDataException) TryDelete(archive);
            return false;
        }
    }

    /// <summary>Downloads into <paramref name="partial"/>, resuming from what an earlier run left.</summary>
    private static async Task DownloadArchiveAsync(string partial, Archive pack, Action<string, double> onProgress, CancellationToken cancellationToken)
    {
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > pack.Bytes) { File.Delete(partial); offset = 0; }
        if (offset == pack.Bytes) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, pack.Url);
        if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
        using var response = await ArchiveHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent) offset = 0; // no range support: start over

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        var written = offset;
        var lastReport = 0L;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (written + read > pack.Bytes) throw new InvalidDataException($"{pack.Name} is larger than expected.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            if (written - lastReport >= 1 << 20 || written == pack.Bytes)
            {
                lastReport = written;
                onProgress($"{pack.Name} · downloading {written >> 20} / {pack.Bytes >> 20} MB", (double)written / pack.Bytes);
            }
        }
    }

    private static async Task<bool> MatchesAsync(string path, Archive source, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != source.Bytes) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash) == source.Sha256;
    }

    /// <summary>Writes every entry the device lacks, each through a temporary file and a rename.</summary>
    private static async Task UnpackAsync(string archive, string root, string name, Action<string, double> onProgress, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(archive);
        var total = zip.Entries.Count;
        var done = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;
            if (SpritePack.IsSafeEntryName(entry.FullName))
            {
                var target = Path.Combine(root, entry.FullName);
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temporary = target + ".part";
                    await using (var source = entry.Open())
                    await using (var output = File.Create(temporary))
                        await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, target, overwrite: true);
                }
            }
            if (done % 100 == 0 || done == total)
                onProgress($"{name} · unpacking {done} / {total}", (double)done / total);
        }
    }

    /// <summary>The per-file pass: only what is still missing, a bounded number at a time.</summary>
    private async Task DownloadMissingAsync(string root, IReadOnlyList<SpritePack.Entry> spriteEntries, IReadOnlyList<string> itemNames,
        Action<string, double> onProgress, CancellationToken cancellationToken)
    {
        var units = new List<Func<Task>>();
        foreach (var entry in spriteEntries)
        {
            if (!File.Exists(Path.Combine(root, entry.CachePath)))
                units.Add(() => sprites.DownloadPackFileAsync(entry, cancellationToken));
        }
        foreach (var name in itemNames)
        {
            if (!ItemArt.IsCachedOrKnownMissing(name))
                units.Add(() => ItemArt.GetAsync(name));
        }

        var total = units.Count;
        if (total == 0) return;
        var done = 0;
        var gate = new SemaphoreSlim(Parallelism);
        var tasks = units.Select(async unit =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await unit().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
                var current = Interlocked.Increment(ref done);
                if (current % 20 == 0 || current == total)
                    onProgress($"{current} / {total}", (double)current / total);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }
}
