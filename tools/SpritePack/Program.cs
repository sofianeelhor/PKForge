// Builds the offline sprite pack archive the app downloads in one request.
//
//   dotnet run --project tools/SpritePack -- <output-dir> [<staging-dir>]
//
// Every file comes from PKForge.Domain.SpritePack.Entries, the same list the app's per-file
// fallback reads, so names always match the on-device caches. Files are staged on disk first
// (a rerun only fetches what is missing). HOME renders are re-encoded as lossless WebP under
// their .png cache names, about half the size: the app decodes them with Skia, which reads the
// format from the bytes, and every file is checked to decode to exactly the original pixels.
// Entries are written in a stable order. Prints the archive's size and SHA-256 for
// SpritePackDownloader.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using PKForge.Domain;
using PKHeX.Core;
using SkiaSharp;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: SpritePack <output-dir> [<staging-dir>]");
    return 2;
}

var output = Path.GetFullPath(args[0]);
var staging = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(output, "staging"));
Directory.CreateDirectory(output);

var entries = SpritePack.Entries(GameInfo.GetStrings("en").itemlist);
Console.WriteLine($"{entries.Count} pack files");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var missing = new List<string>();
var done = 0;
await Parallel.ForEachAsync(entries, new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (entry, token) =>
{
    var path = Path.Combine(staging, entry.CachePath);
    if (!File.Exists(path) && !await FetchAsync(http, entry.Url, path, token))
        lock (missing) missing.Add(entry.CachePath);
    var count = Interlocked.Increment(ref done);
    if (count % 500 == 0) Console.WriteLine($"  {count} / {entries.Count}");
});

// Item names PokeAPI has no icon for are expected; any missing sprite means the table is stale.
var missingSprites = missing.Where(p => !p.StartsWith("items/", StringComparison.Ordinal)).Order().ToList();
if (missingSprites.Count > 0)
{
    Console.Error.WriteLine($"{missingSprites.Count} sprites are missing upstream:");
    foreach (var path in missingSprites) Console.Error.WriteLine($"  {path}");
    return 1;
}

var archive = Path.Combine(output, "pkforge-sprites.zip");
var temporary = archive + ".tmp";
var stored = 0;
File.Delete(temporary); // left by an interrupted run
using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
{
    foreach (var entry in entries.OrderBy(e => e.CachePath, StringComparer.Ordinal))
    {
        var path = Path.Combine(staging, entry.CachePath);
        if (!File.Exists(path)) continue;
        if (!SpritePack.IsSafeEntryName(entry.CachePath))
            throw new InvalidOperationException($"Unsafe pack name: {entry.CachePath}");
        var home = entry.CachePath.StartsWith("home/", StringComparison.Ordinal);
        var bytes = home ? LosslessWebp(path) : await File.ReadAllBytesAsync(path);
        // WebP is already entropy coded; GIF still gains a little from deflate.
        var item = zip.CreateEntry(entry.CachePath, home ? CompressionLevel.NoCompression : CompressionLevel.SmallestSize);
        item.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var target = item.Open();
        await target.WriteAsync(bytes);
        stored++;
    }
}
File.Move(temporary, archive, overwrite: true);

await using var stream = File.OpenRead(archive);
var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
Console.WriteLine($"{stored} files, {missing.Count} item icons absent upstream");
Console.WriteLine($"archive: {archive}");
Console.WriteLine($"bytes:   {stream.Length}");
Console.WriteLine($"sha256:  {hash}");
return 0;

// Checked on the unpremultiplied pixels: every visible pixel (alpha > 0) must come back
// exactly. Lossless WebP drops the color hidden under fully transparent pixels, which is
// never drawn. (A premultiplied compare is not meaningful: Skia's PNG and WebP decoders
// round the premultiply step differently.)
static byte[] LosslessWebp(string pngPath)
{
    var png = File.ReadAllBytes(pngPath);
    using var source = Decode(png);
    using var data = source.PeekPixels().Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
        ?? throw new InvalidOperationException($"WebP encoding failed: {pngPath}");
    var webp = data.ToArray();
    using var roundTrip = Decode(webp);
    var a = source.GetPixelSpan();
    var b = roundTrip.GetPixelSpan();
    if (a.Length != b.Length) throw new InvalidOperationException($"WebP size differs: {pngPath}");
    for (var i = 0; i < a.Length; i += 4)
    {
        if (a[i + 3] == 0 && b[i + 3] == 0) continue;
        if (!a.Slice(i, 4).SequenceEqual(b.Slice(i, 4)))
            throw new InvalidOperationException($"WebP changes pixel {i / 4}: {pngPath}");
    }
    return webp;
}

static SKBitmap Decode(byte[] bytes)
{
    using var codec = SKCodec.Create(new SKMemoryStream(bytes)) ?? throw new InvalidDataException("Unreadable image.");
    var info = codec.Info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Unpremul);
    var bitmap = new SKBitmap(info);
    if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
        throw new InvalidDataException("Image decode failed.");
    return bitmap;
}

static async Task<bool> FetchAsync(HttpClient http, string url, string path, CancellationToken token)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            response.EnsureSuccessStatusCode();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var part = path + ".part";
            await using (var target = File.Create(part))
                await response.Content.CopyToAsync(target, token);
            File.Move(part, path, overwrite: true);
            return true;
        }
        catch (HttpRequestException) when (attempt < 4)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token);
        }
    }
}
