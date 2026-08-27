namespace PKForge.App.Services;

/// <summary>
/// Item sprites from the PokeAPI sprite database, keyed by the item's English name
/// ("Master Ball" → items/master-ball.png). Fetched once, cached forever.
/// Misses expire after a day: a network blip must not blind an item forever.
/// </summary>
public static class ItemArt
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim Gate = new(6);
    private static TimeSpan MissTtl { get; } = TimeSpan.FromHours(20);

    /// <summary>
    /// PokeAPI slugs are plain ascii with dashes. Names arrive with diacritics
    /// ("Poké Doll") that must fold to their base letters (poke-doll) - the old
    /// filter turned é into a dash and every accented item 404'd forever.
    /// </summary>
    public static string Slug(string itemName)
    {
        var folded = itemName.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (char.GetUnicodeCategory(ch) is not System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append('-');
        }
        return sb.ToString().Replace("--", "-").Trim('-');
    }

    /// <summary>Local path of the item's sprite, or null (unknown item / offline first time).</summary>
    public static async Task<string?> GetAsync(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        var slug = Slug(itemName);
        var directory = Path.Combine(FileSystem.AppDataDirectory, "items");
        var cache = Path.Combine(directory, slug + ".png");
        var miss = cache + ".miss";
        if (File.Exists(cache)) return cache;
        if (IsFreshMiss(miss)) return null;

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(cache)) return cache;
            Directory.CreateDirectory(directory);
            var bytes = await Http.GetByteArrayAsync(
                $"https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/items/{slug}.png").ConfigureAwait(false);
            await File.WriteAllBytesAsync(cache, bytes).ConfigureAwait(false);
            return cache;
        }
        catch
        {
            try { await File.WriteAllTextAsync(miss, DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false); }
            catch { }
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsFreshMiss(string miss)
    {
        try
        {
            if (!File.Exists(miss)) return false;
            var written = DateTimeOffset.Parse(File.ReadAllText(miss), System.Globalization.CultureInfo.InvariantCulture);
            if (DateTimeOffset.UtcNow - written < MissTtl) return true;
            File.Delete(miss); // expired: let the next look retry
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A tiny pixel capsule tile drawn once and reused whenever a sprite is missing,
    /// so bag rows never render blank. Pure-local: no network, no dependency on names.
    /// </summary>
    public static string PlaceholderPath()
    {
        var path = Path.Combine(FileSystem.AppDataDirectory, "items", "_placeholder.png");
        if (File.Exists(path)) return path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 24x24: navy plate, cyan rim, white question dot - reads as "item" at row size.
            var px = new byte[24 * 24 * 4];
            for (var y = 0; y < 24; y++)
                for (var x = 0; x < 24; x++)
                {
                    var edge = x is 0 or 23 || y is 0 or 23;
                    var inner = x is >= 2 and <= 21 && y is >= 2 and <= 21;
                    var r = edge ? (byte)0x2E : inner ? (byte)0x14 : (byte)0x20;
                    var g = edge ? (byte)0x8A : inner ? (byte)0x2E : (byte)0x3C;
                    var b = edge ? (byte)0xC8 : inner ? (byte)0x6E : (byte)0x88;
                    // question-mark dot cluster
                    if (inner && y is >= 8 and <= 15 && ((y < 11 && x is >= 10 and <= 14) || (y is 11 or 14 && x is >= 12 and <= 14) || (y is >= 12 and <= 13 && x == 13) || (y is >= 14 and <= 16 && x is >= 13 and <= 14)))
                    { r = 0xFC; g = 0xFD; b = 0xFE; }
                    var o = (y * 24 + x) * 4;
                    px[o] = r; px[o + 1] = g; px[o + 2] = b; px[o + 3] = 0xFF;
                }
            File.WriteAllBytes(path, EncodePng(px, 24, 24));
            return path;
        }
        catch
        {
            return path; // best effort: a blank row beats a crash
        }
    }

    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        var stride = 1 + width * 4;
        var raw = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            raw[y * stride] = 0; // filter: none
            Array.Copy(rgba, y * width * 4, raw, y * stride + 1, width * 4);
        }
        using var output = new MemoryStream();
        output.Write("\x89PNG\r\n\x1a\n"u8);
        Span<byte> ihdr = stackalloc byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        WriteChunk(output, "IHDR"u8, ihdr.ToArray());
        WriteChunk(output, "IDAT"u8, Deflate(raw));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream to, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> header = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        to.Write(header);
        to.Write(type);
        to.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (var b in type) crc = CrcStep(crc, b);
        foreach (var b in data) crc = CrcStep(crc, b);
        Span<byte> tail = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tail, ~crc);
        to.Write(tail);
    }

    private static uint CrcStep(uint crc, byte b)
    {
        crc ^= b;
        for (var k = 0; k < 8; k++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var zipped = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(zipped, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);
        return zipped.ToArray();
    }

    /// <summary>
    /// Upgrade sweep: misses recorded by the old broken slugger (é mangled to a dash)
    /// poisoned items forever. One launch, wipe them all so every item retries.
    /// </summary>
    public static void PurgeLegacyMisses()
    {
        try
        {
            var directory = Path.Combine(FileSystem.AppDataDirectory, "items");
            if (!Directory.Exists(directory)) return;
            foreach (var miss in Directory.EnumerateFiles(directory, "*.miss"))
                File.Delete(miss);
        }
        catch
        {
            // Never block startup on a cache sweep.
        }
    }
}
