using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App.Services;

/// <summary>Loads and caches bundled PKHeX sprite assets as Skia bitmaps. Fully offline; no CDN dependency.</summary>
public interface ISpriteService
{
    /// <summary>
    /// Bundled pixel sprite (PKHeX art) for the exact look, resolved by
    /// <see cref="SpriteCatalog.BundledCandidates"/>; null while a background load is pending.
    /// </summary>
    SKBitmap? GetSprite(SpriteLook look);

    /// <summary>Warms the cache for a look and invokes <paramref name="onLoaded"/> when ready.</summary>
    void Warm(SpriteLook look, Action onLoaded);

    /// <summary>Plain species / form / shiny look - for surfaces that genuinely know no more (dex grids).</summary>
    SKBitmap? GetSprite(int species, int form, bool shiny) => GetSprite(new SpriteLook(species, form, shiny));

    /// <inheritdoc cref="Warm(SpriteLook, Action)"/>
    void Warm(int species, int form, bool shiny, Action onLoaded) => Warm(new SpriteLook(species, form, shiny), onLoaded);

    /// <summary>Ball icon by PKHeX ball id; null while loading / unknown ball.</summary>
    SKBitmap? GetBall(int ball);

    /// <summary>Warms the ball-icon cache and invokes <paramref name="onLoaded"/> when ready.</summary>
    void WarmBall(int ball, Action onLoaded);

    /// <summary>
    /// Modern HOME-style render (512px, downloaded once and cached forever);
    /// null while loading / offline with no cache - callers fall back to the pixel sprite.
    /// </summary>
    /// Null too when HOME has no art for this exact form (see <see cref="SpriteCatalog.Home"/>).
    SKBitmap? GetHome(SpriteLook look);

    /// <summary>Warms the HOME render cache and invokes <paramref name="onLoaded"/> when ready.</summary>
    void WarmHome(SpriteLook look, Action onLoaded);

    /// <summary>
    /// Animated Showdown sprite lookup with three-state semantics: returns false while the
    /// answer is unknown (still loading - draw NOTHING, no fallback flash); true with a sprite
    /// when available; true with null when this exact form has no animation (fall back now).
    /// </summary>
    bool TryGetShowdown(SpriteLook look, out AnimatedSprite? sprite);

    /// <summary>Warms the animated-sprite cache and invokes <paramref name="onLoaded"/> when ready.</summary>
    void WarmShowdown(SpriteLook look, Action onLoaded);
}

/// <summary>Decoded animation: frames plus per-frame durations in milliseconds.</summary>
public sealed record AnimatedSprite(IReadOnlyList<SKBitmap> Frames, IReadOnlyList<int> DurationsMs)
{
    public int TotalDurationMs { get; } = Math.Max(1, DurationsMs.Sum());

    /// <summary>Frame for a given absolute time, looping.</summary>
    public SKBitmap FrameAt(long elapsedMs)
    {
        var t = (int)(elapsedMs % TotalDurationMs);
        for (var i = 0; i < Frames.Count; i++)
        {
            t -= DurationsMs[i];
            if (t < 0) return Frames[i];
        }
        return Frames[^1];
    }
}

public sealed class SpriteService : ISpriteService
{
    private const int MaxCacheEntries = 1024;
    private readonly Dictionary<string, SKBitmap?> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private readonly Queue<string> _eviction = new();
    private readonly Lock _gate = new();

    // Bounded concurrency: a burst of warm() calls (opening a full box, filling a
    // living dex, a big bank deposit) must not spawn dozens of simultaneous decodes
    // and starve the thread pool - that is what made navigation stutter.
    private static readonly SemaphoreSlim DecodeGate = new(Math.Max(2, Environment.ProcessorCount - 1));
    private static readonly SemaphoreSlim NetworkGate = new(4);

    public SKBitmap? GetSprite(SpriteLook look)
    {
        lock (_gate)
            return _cache.GetValueOrDefault(look.CacheKey);
    }

    public SKBitmap? GetBall(int ball)
    {
        lock (_gate)
            return _cache.GetValueOrDefault($"ball-{ball}");
    }

    public void WarmBall(int ball, Action onLoaded)
    {
        var key = $"ball-{ball}";
        lock (_gate)
        {
            if (_cache.ContainsKey(key)) { onLoaded(); return; }
            if (!_loading.Add(key)) return;
        }

        Task.Run(async () =>
        {
            SKBitmap? bitmap = null;
            try
            {
                await using var stream = await FileSystem.OpenAppPackageFileAsync($"balls/_ball{ball}.png").ConfigureAwait(false);
                bitmap = SKBitmap.Decode(stream);
            }
            catch
            {
                // Unknown ball id: cache the null so we don't retry forever.
            }
            lock (_gate)
            {
                _loading.Remove(key);
                _cache[key] = bitmap;
                _eviction.Enqueue(key);
            }
            onLoaded();
        });
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Dictionary<string, DateTime> _homeRetryAfter = new(StringComparer.Ordinal);

    public SKBitmap? GetHome(SpriteLook look)
    {
        var remote = SpriteCatalog.Home(look);
        if (remote is null) return null;
        lock (_gate)
            return _cache.GetValueOrDefault("home-" + remote.CacheName);
    }

    public void WarmHome(SpriteLook look, Action onLoaded)
    {
        // No HOME art for this exact form: nothing will ever load, and calling back would
        // only make the caller repaint and ask again. It keeps drawing the pixel chain.
        if (SpriteCatalog.Home(look) is not { } remote) return;
        var key = "home-" + remote.CacheName;
        lock (_gate)
        {
            if (_cache.ContainsKey(key)) { onLoaded(); return; }
            // A recent failure (offline) is not retried on every repaint.
            if (_homeRetryAfter.TryGetValue(key, out var after) && DateTime.UtcNow < after) return;
            if (!_loading.Add(key)) return;
        }

        Task.Run(async () =>
        {
            SKBitmap? bitmap = null;
            var diskPath = Path.Combine(FileSystem.AppDataDirectory, "home", remote.CacheName);
            await NetworkGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(diskPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
                    var bytes = await Http.GetByteArrayAsync(RemoteBase + "home/" + remote.Path).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(diskPath, bytes).ConfigureAwait(false);
                }
                bitmap = SKBitmap.Decode(diskPath);
            }
            catch
            {
                // Offline or missing render: leave the loading flag clear so a later
                // attempt can retry; callers keep using the pixel sprite meanwhile.
                lock (_gate)
                {
                    _loading.Remove(key);
                    _homeRetryAfter[key] = DateTime.UtcNow.AddMinutes(1);
                }
                onLoaded();
                return;
            }
            finally { NetworkGate.Release(); }
            lock (_gate)
            {
                _loading.Remove(key);
                _cache[key] = bitmap;
                _eviction.Enqueue(key);
            }
            onLoaded();
        });
    }

    private const string RemoteBase = "https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/other/";

    private readonly Dictionary<string, AnimatedSprite?> _animatedCache = new(StringComparer.Ordinal);

    public bool TryGetShowdown(SpriteLook look, out AnimatedSprite? sprite)
    {
        if (SpriteCatalog.Showdown(look) is not { } remote)
        {
            sprite = null;
            return true; // known: this exact form has no animation
        }
        lock (_gate)
            return _animatedCache.TryGetValue("sd-" + remote.CacheName, out sprite);
    }

    public void WarmShowdown(SpriteLook look, Action onLoaded)
    {
        if (SpriteCatalog.Showdown(look) is not { } remote) return; // TryGetShowdown already says "none"
        var key = "sd-" + remote.CacheName;
        lock (_gate)
        {
            if (_animatedCache.ContainsKey(key)) { onLoaded(); return; }
            if (!_loading.Add(key)) return;
        }

        Task.Run(async () =>
        {
            AnimatedSprite? sprite = null;
            var diskPath = Path.Combine(FileSystem.AppDataDirectory, "showdown", remote.CacheName);
            await NetworkGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(diskPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
                    var bytes = await Http.GetByteArrayAsync(RemoteBase + "showdown/" + remote.Path).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(diskPath, bytes).ConfigureAwait(false);
                }
                sprite = DecodeGif(diskPath);
                lock (_gate)
                {
                    _loading.Remove(key);
                    _animatedCache[key] = sprite;
                }
            }
            catch
            {
                // Cache the miss so callers stop waiting and fall back immediately
                // (a fresh app launch retries in case it was just a network blip).
                lock (_gate)
                {
                    _loading.Remove(key);
                    _animatedCache[key] = null;
                }
            }
            finally { NetworkGate.Release(); }
            onLoaded();
        });
    }

    /// <summary>Decodes every GIF frame, compositing against the prior frame as GIF disposal expects.</summary>
    private static AnimatedSprite? DecodeGif(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null || codec.FrameCount <= 0) return null;

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var frameInfos = codec.FrameInfo;
        var frames = new List<SKBitmap>(codec.FrameCount);
        var durations = new List<int>(codec.FrameCount);

        for (var i = 0; i < codec.FrameCount; i++)
        {
            var bitmap = new SKBitmap(info);
            var options = new SKCodecOptions(i);
            if (i > 0 && frameInfos[i].RequiredFrame >= 0)
            {
                // Start from the required prior frame's pixels, as the GIF spec composites.
                var required = frames[frameInfos[i].RequiredFrame];
                required.CopyTo(bitmap);
                options = new SKCodecOptions(i) { PriorFrame = frameInfos[i].RequiredFrame };
            }
            codec.GetPixels(info, bitmap.GetPixels(), options);
            frames.Add(bitmap);
            durations.Add(Math.Max(20, frameInfos[i].Duration));
        }
        return frames.Count == 0 ? null : new AnimatedSprite(frames, durations);
    }

    public void Warm(SpriteLook look, Action onLoaded)
    {
        var key = look.CacheKey;
        lock (_gate)
        {
            if (_cache.ContainsKey(key)) { onLoaded(); return; }
            if (!_loading.Add(key)) return;
        }

        Task.Run(async () =>
        {
            SKBitmap? bitmap;
            await DecodeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                bitmap = await LoadWithFallbackAsync(look).ConfigureAwait(false);
            }
            finally { DecodeGate.Release(); }
            lock (_gate)
            {
                _loading.Remove(key);
                _cache[key] = bitmap;
                _eviction.Enqueue(key);
                while (_cache.Count > MaxCacheEntries && _eviction.TryDequeue(out var oldest))
                {
                    if (_cache.Remove(oldest, out var evicted))
                        evicted?.Dispose();
                }
            }
            onLoaded();
        });
    }

    private static async Task<SKBitmap?> LoadWithFallbackAsync(SpriteLook look)
    {
        // SpriteCatalog owns the naming and the fidelity order (exact form shiny → exact
        // form → PKHeX artwork → base form → "?"); the first bundled asset that exists wins.
        foreach (var candidate in SpriteCatalog.BundledCandidates(look))
        {
            try
            {
                await using var stream = await FileSystem.OpenAppPackageFileAsync(candidate.Path).ConfigureAwait(false);
                return TrimTransparentMargins(SKBitmap.Decode(stream));
            }
            catch (FileNotFoundException)
            {
                // Try next fallback.
            }
            catch (Exception)
            {
                return null; // Decode failure - don't retry other candidates.
            }
        }
        return null;
    }

    /// <summary>
    /// PKHeX sprite PNGs carry generous transparent padding; cropping to the opaque
    /// bounding box (plus a small breath) lets the creature fill its box slot.
    /// </summary>
    private static SKBitmap? TrimTransparentMargins(SKBitmap? source)
    {
        if (source is null) return null;

        // Scan the raw pixel buffer once instead of calling GetPixel per pixel: each
        // GetPixel is a separate interop hop, so the old loop cost thousands of them
        // per sprite and stalled bulk loads. Both 8888 layouts keep alpha in byte 3.
        var span = source.GetPixelSpan();
        var bpp = source.BytesPerPixel;
        if (span.IsEmpty || bpp < 4 || source.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888))
            return source; // unknown layout: skip trimming rather than risk misreading

        var width = source.Width;
        var height = source.Height;
        var rowBytes = source.RowBytes;
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                if (span[rowStart + x * bpp + 3] <= 16) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        if (right < 0) return source; // fully transparent - keep as-is

        const int breath = 2;
        left = Math.Max(0, left - breath);
        top = Math.Max(0, top - breath);
        right = Math.Min(source.Width - 1, right + breath);
        bottom = Math.Min(source.Height - 1, bottom + breath);
        if (left == 0 && top == 0 && right == source.Width - 1 && bottom == source.Height - 1)
            return source;

        var cropped = new SKBitmap(right - left + 1, bottom - top + 1);
        using var canvas = new SKCanvas(cropped);
        canvas.DrawBitmap(source, new SKRect(left, top, right + 1, bottom + 1), new SKRect(0, 0, cropped.Width, cropped.Height));
        source.Dispose();
        return cropped;
    }
}
