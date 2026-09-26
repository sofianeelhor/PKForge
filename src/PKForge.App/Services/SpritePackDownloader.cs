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

    // The archive these constants describe is printed by tools/SpritePack. A new archive
    // needs a new release tag, so an app build always verifies exactly the bytes it expects.
    private const string ArchiveUrl = "https://github.com/sofianeelhor/PKForge/releases/download/sprites-1/pkforge-sprites.zip";
    private const long ArchiveBytes = 625_017_170;
    private const string ArchiveSha256 = "611f384b2475f24946ecd51ba8a726aafffc04736f0cedf47236a1ac00ff6cd7";

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

    private string DoneMarker(string root) => Path.Combine(root, "spritepack", ArchiveSha256[..16] + ".done");

    // Many item names have no icon upstream. Once the archive is in, the ones it lacks are
    // known absent: counting them again would refetch the whole archive or send ~1800
    // requests that can only fail. The UI still asks for them on demand, as before.
    private List<string> WantedItems(string root) => File.Exists(DoneMarker(root))
        ? []
        : [.. data.ItemNames.Where(n => SpritePack.ItemSlug(n).Length > 0).Distinct().Where(n => !ItemArt.IsCachedOrKnownMissing(n))];

    public async Task RunAsync(Action<string, double> onProgress, CancellationToken cancellationToken)
    {
        var root = FileSystem.AppDataDirectory;
        var spriteEntries = SpritePack.Entries([]);
        var missing = spriteEntries.Count(e => !File.Exists(Path.Combine(root, e.CachePath))) + WantedItems(root).Count;
        if (missing >= ArchiveThreshold && await TryArchiveAsync(root, onProgress, cancellationToken).ConfigureAwait(false))
            await File.WriteAllTextAsync(DoneMarker(root), ArchiveUrl, cancellationToken).ConfigureAwait(false);

        await DownloadMissingAsync(root, spriteEntries, WantedItems(root), onProgress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one-request path; true once the archive is unpacked. Any failure (offline, asset
    /// not published, damaged download, not enough space) returns false: the per-file pass
    /// that follows covers everything.
    /// </summary>
    private static async Task<bool> TryArchiveAsync(string root, Action<string, double> onProgress, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(root, "spritepack");
        var archive = Path.Combine(folder, "pkforge-sprites.zip");
        var partial = archive + ".part";
        try
        {
            Directory.CreateDirectory(folder);
            // The archive and its unpacked files coexist until unpacking ends.
            var needed = ArchiveBytes * 2 + (64L << 20) - (File.Exists(partial) ? new FileInfo(partial).Length : 0);
            if (new DriveInfo(root).AvailableFreeSpace < needed) return false;

            if (!File.Exists(archive))
            {
                await DownloadArchiveAsync(partial, onProgress, cancellationToken).ConfigureAwait(false);
                onProgress("Checking the download…", 1);
                if (!await MatchesAsync(partial, cancellationToken).ConfigureAwait(false))
                {
                    File.Delete(partial);
                    return false;
                }
                File.Move(partial, archive, overwrite: true);
            }

            await UnpackAsync(archive, root, onProgress, cancellationToken).ConfigureAwait(false);
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
    private static async Task DownloadArchiveAsync(string partial, Action<string, double> onProgress, CancellationToken cancellationToken)
    {
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > ArchiveBytes) { File.Delete(partial); offset = 0; }
        if (offset == ArchiveBytes) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, ArchiveUrl);
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
            if (written + read > ArchiveBytes) throw new InvalidDataException("The sprite pack is larger than expected.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            if (written - lastReport >= 1 << 20 || written == ArchiveBytes)
            {
                lastReport = written;
                onProgress($"Downloading {written >> 20} / {ArchiveBytes >> 20} MB", (double)written / ArchiveBytes);
            }
        }
    }

    private static async Task<bool> MatchesAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != ArchiveBytes) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash) == ArchiveSha256;
    }

    /// <summary>Writes every entry the device lacks, each through a temporary file and a rename.</summary>
    private static async Task UnpackAsync(string archive, string root, Action<string, double> onProgress, CancellationToken cancellationToken)
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
                onProgress($"Unpacking {done} / {total}", (double)done / total);
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
