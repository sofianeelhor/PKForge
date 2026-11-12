using System.Text;
using System.Xml.Linq;
using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App.Services;

/// <param name="Bounds">The opaque envelope of the animation, relative to the cell's center.</param>
/// <param name="Feet">Where the feet are (PMD's shadow), relative to the cell's center.</param>
public sealed record ParkSpriteFrame(SKBitmap Bitmap, SKRect Source, SKRect Bounds, SKPoint Feet);

/// <summary>A whole animation facing one way: each frame's cell in <paramref name="Bitmap"/>, its feet (from the cell's center) and duration.</summary>
public sealed record ParkSpriteClip(SKBitmap Bitmap, SKRectI[] Sources, SKPoint[] Feet, int[] Durations, bool Pending);

/// <summary>PMDCollab sprites and portraits, downloaded on first use and kept on disk. Walk and
/// Idle load with the species; every other animation loads the first time a pose asks for it,
/// and the pose falls back to a loaded one meanwhile. Never substitutes a different form or shiny state.</summary>
public sealed class PokeparkSpriteService
{
    private const string Root = "https://raw.githubusercontent.com/PMDCollab/SpriteCollab/master/";
    private const int Capacity = 64;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim _network = new(2);
    private readonly object _gate = new();
    private readonly Dictionary<string, Sheet?> _sheets = new();
    private readonly Dictionary<string, List<Action>> _pending = new();
    private readonly Dictionary<string, DateTime> _retryAfter = new();
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, SKBitmap?> _portraits = new();
    private readonly HashSet<string> _portraitLoads = new();

    private sealed record Animation(SKBitmap Bitmap, int Width, int Height, int[] Durations, int TotalDurationMs, SKPoint[] Feet, SKRect Bounds)
    {
        public int Rows => Bitmap.Height / Height;
    }

    private sealed class Sheet(string key, string directory, Dictionary<string, XElement> definitions, string credits, int shadowSize)
    {
        public int ShadowSize { get; } = shadowSize;
        public string Key { get; } = key;
        public string Directory { get; } = directory;
        public Dictionary<string, XElement> Definitions { get; } = definitions;
        public string Credits { get; } = credits;
        public Dictionary<string, Animation?> Animations { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Loading { get; } = new(StringComparer.Ordinal);
    }

    // PMDCollab numbers forms its own way: ParkSprites maps PKHeX's forms to its folders, and
    // gives nothing for a form or a shiny it has not drawn (the bundled still sprite shows).
    private static string? Key(int species, int form, bool shiny) => ParkSprites.Key(species, form, shiny);

    public static string GetSourceUrl(int species, int form, bool shiny) => Key(species, form, shiny) is { } key
        ? "https://github.com/PMDCollab/SpriteCollab/tree/master/sprite/" + key
        : "https://sprites.pmdcollab.org/";

    public string? GetAttribution(int species, int form, bool shiny)
    {
        var key = Key(species, form, shiny);
        lock (_gate) return key is not null && _sheets.TryGetValue(key, out var sheet) ? sheet?.Credits : null;
    }

    /// <summary>A standing or walking frame, for cards and bubbles.</summary>
    public ParkSpriteFrame? GetFrame(int species, int form, bool shiny, int direction, long elapsedMs, bool isWalking = true) =>
        GetFrame(species, form, shiny, isWalking ? ParkPose.Walk : ParkPose.Idle, direction, elapsedMs);

    /// <param name="direction">0 south, then SE, E, NE, N, NW, W, SW (native PMD row order).</param>
    /// <param name="elapsedMs">Time since the pose started: one-shot animations hold their last frame.</param>
    public ParkSpriteFrame? GetFrame(int species, int form, bool shiny, ParkPose pose, int direction, long elapsedMs)
    {
        var key = Key(species, form, shiny);
        Animation? animation = null;
        var name = "";
        lock (_gate)
        {
            if (key is null || !_sheets.TryGetValue(key, out var sheet) || sheet is null) return null;
            foreach (var candidate in ParkPoses.Animations(pose))
            {
                if (!sheet.Definitions.ContainsKey(candidate)) continue;
                if (sheet.Animations.TryGetValue(candidate, out var loaded))
                {
                    if (loaded is null) continue; // missing or broken upstream: try the next one
                    animation = loaded; name = candidate;
                    break;
                }
                if (sheet.Loading.Add(candidate)) _ = LoadAnimationAsync(sheet, candidate);
            }
            if (animation is null && (sheet.Animations.GetValueOrDefault("Idle") ?? sheet.Animations.GetValueOrDefault("Walk")) is { } fallback)
            {
                animation = fallback; name = "Idle";
            }
        }
        if (animation is null) return null;
        // A fallback to Idle loops even for a one-shot pose: a frozen Idle looks broken.
        var loops = ParkPoses.Loops(pose) || name is "Idle" or "Walk";
        var time = (int)Math.Max(0, elapsedMs);
        time = loops ? time % animation.TotalDurationMs : Math.Min(time, animation.TotalDurationMs - 1);
        var frame = 0;
        while (frame < animation.Durations.Length - 1 && time >= animation.Durations[frame]) time -= animation.Durations[frame++];
        var row = animation.Rows == 1 ? 0 : ((direction % 8) + 8) % 8;
        return new(animation.Bitmap, SKRect.Create(frame * animation.Width, row * animation.Height, animation.Width, animation.Height),
            animation.Bounds, animation.Feet[row * animation.Durations.Length + frame]);
    }

    /// <summary>
    /// Every frame of a pose's animation facing one way (for the living park widget, which cuts them
    /// into its own sheet). <see cref="ParkSpriteClip.Pending"/>: a better animation for the pose is
    /// still loading, and this is the stand-in. Null before the species has loaded.
    /// </summary>
    public ParkSpriteClip? GetClip(int species, int form, bool shiny, ParkPose pose, int direction)
    {
        var key = Key(species, form, shiny);
        Animation? animation = null;
        var pending = false;
        lock (_gate)
        {
            if (key is null || !_sheets.TryGetValue(key, out var sheet) || sheet is null) return null;
            foreach (var candidate in ParkPoses.Animations(pose))
            {
                if (!sheet.Definitions.ContainsKey(candidate)) continue;
                if (sheet.Animations.TryGetValue(candidate, out var loaded))
                {
                    if (loaded is null) continue;
                    animation = loaded;
                    break;
                }
                pending = true;
                if (sheet.Loading.Add(candidate)) _ = LoadAnimationAsync(sheet, candidate);
            }
            animation ??= sheet.Animations.GetValueOrDefault("Idle") ?? sheet.Animations.GetValueOrDefault("Walk");
        }
        if (animation is null) return null;
        var row = animation.Rows == 1 ? 0 : ((direction % 8) + 8) % 8;
        var count = animation.Durations.Length;
        var sources = new SKRectI[count];
        var feet = new SKPoint[count];
        for (var i = 0; i < count; i++)
        {
            sources[i] = SKRectI.Create(i * animation.Width, row * animation.Height, animation.Width, animation.Height);
            feet[i] = animation.Feet[row * count + i];
        }
        return new(animation.Bitmap, sources, feet, [.. animation.Durations], pending);
    }

    /// <summary>How big to draw the species (issue #48, see <see cref="ParkSpriteScale"/>): from the
    /// height of its standing animation, the same for every pose; 1 until that has loaded.</summary>
    public float GetScale(int species, int form, bool shiny)
    {
        var key = Key(species, form, shiny);
        lock (_gate)
        {
            if (key is null || !_sheets.TryGetValue(key, out var sheet) || sheet is null) return 1;
            var standing = sheet.Animations.GetValueOrDefault("Idle") ?? sheet.Animations.GetValueOrDefault("Walk");
            return standing is null ? 1 : ParkSpriteScale.For(standing.Bounds.Height);
        }
    }

    /// <summary>PMD's shadow size for the species (0 small, 1 medium, 2 large), once loaded.</summary>
    public int? GetShadowSize(int species, int form, bool shiny)
    {
        var key = Key(species, form, shiny);
        lock (_gate) return key is not null && _sheets.TryGetValue(key, out var sheet) ? sheet?.ShadowSize : null;
    }

    /// <summary>A 40×40 portrait for an emotion (Normal, Happy, Sad, Angry, Surprised, Sigh…),
    /// or the Normal one while that emotion loads or if the species has none.</summary>
    public SKBitmap? GetPortrait(int species, int form, bool shiny, string emotion = "Normal")
    {
        // Portraits are shared by the shiny: few shiny portraits exist upstream.
        if (ParkSprites.PortraitKey(species, form) is not { } key) return null;
        lock (_gate)
        {
            foreach (var wanted in emotion == "Normal" ? ["Normal"] : new[] { emotion, "Normal" })
            {
                var id = key + "/" + wanted;
                if (_portraits.TryGetValue(id, out var portrait)) { if (portrait is not null) return portrait; continue; }
                if (_portraitLoads.Add(id)) _ = LoadPortraitAsync(key, wanted, id);
            }
            return null;
        }
    }

    /// <summary>Loads the species' animation list, Walk and Idle. Callback may run off the UI thread.</summary>
    public void Warm(int species, int form, bool shiny, Action onLoaded)
    {
        var key = Key(species, form, shiny);
        if (key is null) return;
        lock (_gate)
        {
            if (_sheets.TryGetValue(key, out var known) && (known is not null || _retryAfter.GetValueOrDefault(key) > DateTime.UtcNow)) return;
            if (_pending.TryGetValue(key, out var listeners))
            {
                if (listeners.Count < 16 && !listeners.Contains(onLoaded)) listeners.Add(onLoaded);
                return;
            }
            if (_pending.Count >= 24) return;
            _pending[key] = [onLoaded];
        }
        _ = LoadSheetAsync(key);
    }

    private static string CacheFolder(string key) => Path.Combine(FileSystem.CacheDirectory, "pokepark-pmd-v1", key.Replace('/', '_'));

    private async Task LoadSheetAsync(string key)
    {
        Sheet? sheet = null;
        await _network.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = CacheFolder(key);
            Directory.CreateDirectory(directory);
            var xml = await ReadAsync("sprite/" + key, directory, "AnimData.xml", 256 * 1024).ConfigureAwait(false);
            var document = XDocument.Parse(Encoding.UTF8.GetString(xml));
            var elements = document.Root?.Element("Anims")?.Elements("Anim").ToArray() ?? [];
            var definitions = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var element in elements)
            {
                // CopyOf points an animation at another one's sheet and timings.
                var name = (string?)element.Element("Name");
                var resolved = element;
                var visited = new HashSet<string>();
                while (resolved?.Element("CopyOf") is { } copy && visited.Add(copy.Value))
                    resolved = elements.FirstOrDefault(a => (string?)a.Element("Name") == copy.Value);
                if (name is not null && resolved?.Element("CopyOf") is null && resolved is not null) definitions[name] = resolved;
            }
            var credits = await ReadAsync("sprite/" + key, directory, "credits.txt", 256 * 1024).ConfigureAwait(false);
            var shadowSize = Math.Clamp((int?)document.Root?.Element("ShadowSize") ?? 1, 0, 2);
            sheet = new Sheet(key, directory, definitions, Encoding.UTF8.GetString(credits), shadowSize);
            var walk = await DecodeAnimationAsync(sheet, "Walk").ConfigureAwait(false);
            if (walk is null) { sheet = null; return; }
            Animation? idle = null;
            try { idle = await DecodeAnimationAsync(sheet, "Idle").ConfigureAwait(false); }
            catch (Exception ex) when (IsDownloadFailure(ex)) { }
            sheet.Animations["Walk"] = walk;
            sheet.Animations["Idle"] = idle;
        }
        catch (Exception ex) when (IsDownloadFailure(ex))
        {
            // Offline, incomplete or malformed upstream asset: the bundled sprite stays in use.
            sheet = null;
        }
        finally
        {
            TrimDiskCache();
            _network.Release();
            List<Action> listeners;
            lock (_gate)
            {
                if (!_sheets.ContainsKey(key)) _order.Enqueue(key);
                _sheets[key] = sheet;
                _retryAfter[key] = DateTime.UtcNow.AddMinutes(5);
                while (_sheets.Count > Capacity && _order.TryDequeue(out var oldest))
                {
                    // Frames can still be in use by a painter; SKBitmap's finalizer owns disposal.
                    _sheets.Remove(oldest);
                    _retryAfter.Remove(oldest);
                }
                listeners = _pending[key];
                _pending.Remove(key);
            }
            foreach (var listener in listeners)
                try { listener(); } catch (Exception) { /* A closed view must not crash a background load. */ }
        }
    }

    private async Task LoadAnimationAsync(Sheet sheet, string name)
    {
        Animation? animation = null;
        await _network.WaitAsync().ConfigureAwait(false);
        try { animation = await DecodeAnimationAsync(sheet, name).ConfigureAwait(false); }
        catch (Exception ex) when (IsDownloadFailure(ex)) { }
        finally
        {
            _network.Release();
            // A failed animation stays null: poses use their next choice from now on.
            lock (_gate) sheet.Animations[name] = animation;
        }
    }

    private static async Task<Animation?> DecodeAnimationAsync(Sheet sheet, string requested)
    {
        if (!sheet.Definitions.TryGetValue(requested, out var anim)) return null;
        var name = (string?)anim.Element("Name");
        if (string.IsNullOrEmpty(name) || !name.All(char.IsAsciiLetterOrDigit)) return null;
        var width = (int?)anim.Element("FrameWidth") ?? 0;
        var height = (int?)anim.Element("FrameHeight") ?? 0;
        var durations = anim.Element("Durations")?.Elements("Duration").Select(d => Math.Clamp((int)d, 1, 600) * 1000 / 60).ToArray() ?? [];
        if (width is < 1 or > 512 || height is < 1 or > 512 || durations.Length is < 1 or > 64) return null;
        var png = await ReadAsync("sprite/" + sheet.Key, sheet.Directory, name + "-Anim.png", 4 * 1024 * 1024).ConfigureAwait(false);
        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width != width * durations.Length ||
            (codec.Info.Height != height && codec.Info.Height != height * 8) ||
            (long)codec.Info.Width * codec.Info.Height > 1_048_576) return null;
        var bitmap = SKBitmap.Decode(png);
        if (bitmap is null) return null;
        var rows = bitmap.Height / height;
        var feet = new SKPoint[rows * durations.Length];
        Array.Fill(feet, new SKPoint(0, 4));
        try
        {
            // PMD draws the shadow under the feet: its middle is where the Pokémon stands.
            var shadowPng = await ReadAsync("sprite/" + sheet.Key, sheet.Directory, name + "-Shadow.png", 1024 * 1024).ConfigureAwait(false);
            using var shadow = SKBitmap.Decode(shadowPng);
            if (shadow is not null && shadow.Width == bitmap.Width && shadow.Height == bitmap.Height)
            {
                var pixels = shadow.Pixels;
                for (var row = 0; row < rows; row++)
                for (var frame = 0; frame < durations.Length; frame++)
                {
                    float sx = 0, sy = 0; var count = 0;
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                        if (pixels[(row * height + y) * shadow.Width + frame * width + x].Alpha > 0) { sx += x; sy += y; count++; }
                    if (count > 0) feet[row * durations.Length + frame] = new(sx / count + .5f - width / 2f, sy / count + .5f - height / 2f);
                }
            }
        }
        catch (Exception ex) when (IsDownloadFailure(ex)) { }
        return new(bitmap, width, height, durations, durations.Sum(), feet, VisualBounds(bitmap, width, height));
    }

    private async Task LoadPortraitAsync(string key, string emotion, string id)
    {
        SKBitmap? portrait = null;
        await _network.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(CacheFolder(key), "portrait");
            Directory.CreateDirectory(directory);
            var png = await ReadAsync("portrait/" + key, directory, emotion + ".png", 64 * 1024).ConfigureAwait(false);
            portrait = SKBitmap.Decode(png);
            if (portrait is { Width: not 40 } or { Height: not 40 }) portrait = null;
        }
        catch (Exception ex) when (IsDownloadFailure(ex)) { }
        finally
        {
            _network.Release();
            lock (_gate) _portraits[id] = portrait;
        }
    }

    private static bool IsDownloadFailure(Exception ex) => ex is HttpRequestException or IOException or TaskCanceledException
        or System.Xml.XmlException or FormatException or OverflowException or UnauthorizedAccessException;

    /// <summary>The opaque envelope of every frame, relative to the cell's center.</summary>
    private static SKRect VisualBounds(SKBitmap bitmap, int width, int height)
    {
        var left = float.PositiveInfinity; var top = float.PositiveInfinity;
        var right = float.NegativeInfinity; var bottom = float.NegativeInfinity;
        var pixels = bitmap.Pixels;
        for (var index = 0; index < pixels.Length; index++)
        {
            if (pixels[index].Alpha == 0) continue;
            var x = index % bitmap.Width % width - width / 2f;
            var y = index / bitmap.Width % height - height / 2f;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
        }
        // An entirely transparent upstream sheet still needs a finite, nonempty envelope.
        return float.IsFinite(left) ? new(left, top, right, bottom) : new(-width / 2f, -height / 2f, width / 2f, height / 2f);
    }

    private static void TrimDiskCache()
    {
        try
        {
            var root = new DirectoryInfo(Path.Combine(FileSystem.CacheDirectory, "pokepark-pmd-v1"));
            if (!root.Exists) return;
            foreach (var folder in root.GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Skip(Capacity))
                try { folder.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private Dictionary<string, string>? _creditNames;

    /// <summary>The artists' names by their Discord id (PMDCollab's credit_names.txt), fetched once
    /// and cached for a week; an empty table offline.</summary>
    public async Task<IReadOnlyDictionary<string, string>> CreditNamesAsync()
    {
        if (_creditNames is { } known) return known;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var directory = CacheFolder("names");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "credit_names.txt");
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(7)) File.Delete(path);
            var text = Encoding.UTF8.GetString(await ReadAsync("", directory, "credit_names.txt", 1024 * 1024).ConfigureAwait(false));
            foreach (var line in text.Split('\n').Skip(1))
            {
                var cells = line.TrimEnd('\r').Split('\t');
                if (cells.Length >= 2 && cells[0].Length > 0) names[cells[1]] = cells[0];
            }
        }
        catch (Exception ex) when (IsDownloadFailure(ex)) { }
        return _creditNames = names;
    }

    /// <summary>Who drew a sprite, by name, from its credits (ids resolved with <paramref name="names"/>).</summary>
    public static IReadOnlyList<string> Artists(string? credits, IReadOnlyDictionary<string, string> names) => credits is null ? []
        : credits.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t')).Where(parts => parts.Length > 1)
            .Select(parts => parts[1].Trim()).Select(id => names.TryGetValue(id, out var name) ? name : id.StartsWith('<') ? "" : id)
            .Where(name => name.Length > 0).Distinct().ToArray();

    private static async Task<byte[]> ReadAsync(string remote, string directory, string file, int limit)
    {
        var path = Path.Combine(directory, file);
        if (File.Exists(path) && new FileInfo(path).Length <= limit)
            return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        using var response = await Http.GetAsync(Root + (remote.Length > 0 ? remote + "/" : "") + file, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new IOException("PMD asset exceeds size limit.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > limit) throw new IOException("PMD asset exceeds size limit.");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        await File.WriteAllBytesAsync(path + ".tmp", bytes).ConfigureAwait(false);
        File.Move(path + ".tmp", path, true);
        return bytes;
    }
}
