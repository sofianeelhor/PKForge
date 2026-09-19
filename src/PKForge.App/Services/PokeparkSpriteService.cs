using System.Text;
using System.Xml.Linq;
using SkiaSharp;

namespace PKForge.App.Services;

public sealed record ParkSpriteFrame(SKBitmap Bitmap, SKRect Source, SKRect Bounds);

/// <summary>Optional PMDCollab walking sheets; never substitutes a different form or shiny state.</summary>
public sealed class PokeparkSpriteService
{
    private const string Root = "https://raw.githubusercontent.com/PMDCollab/SpriteCollab/master/sprite/";
    private const int Capacity = 64;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim _network = new(2);
    private readonly object _gate = new();
    private readonly Dictionary<string, Sheet?> _sheets = new();
    private readonly Dictionary<string, List<Action>> _pending = new();
    private readonly Dictionary<string, DateTime> _retryAfter = new();
    private readonly Queue<string> _order = new();
    private sealed record Animation(SKBitmap Bitmap, int Width, int Height, int[] Durations);
    private sealed record Sheet(Animation Walk, Animation? Idle, string Credits, SKRect Bounds);

    // PMDCollab form IDs are not PKHeX form IDs. Until explicit mappings exist, use
    // bundled PKHeX sprites for every nonzero form rather than show the wrong creature.
    private static string? Key(int species, int form, bool shiny) => species is > 0 and <= 1025 && form == 0
        ? $"{species:D4}" + (shiny ? "/0000/0001" : "") : null;

    public static string GetSourceUrl(int species, int form, bool shiny) => Key(species, form, shiny) is { } key
        ? "https://github.com/PMDCollab/SpriteCollab/tree/master/sprite/" + key
        : "https://sprites.pmdcollab.org/";

    public string? GetAttribution(int species, int form, bool shiny)
    {
        var key = Key(species, form, shiny);
        lock (_gate) return key is not null && _sheets.TryGetValue(key, out var sheet) ? sheet?.Credits : null;
    }

    /// <param name="direction">0 south, then SE, E, NE, N, NW, W, SW (native PMD row order).</param>
    public ParkSpriteFrame? GetFrame(int species, int form, bool shiny, int direction, long elapsedMs, bool isWalking = true)
    {
        var key = Key(species, form, shiny);
        Sheet? sheet;
        lock (_gate)
            if (key is null || !_sheets.TryGetValue(key, out sheet) || sheet is null) return null;
        var animation = isWalking ? sheet.Walk : sheet.Idle ?? sheet.Walk;
        // Missing Idle must hold a pose, never play a walking cycle while stationary.
        var time = !isWalking && sheet.Idle is null ? 0 : (int)(Math.Max(0, elapsedMs) % animation.Durations.Sum());
        var frame = 0;
        while (frame < animation.Durations.Length - 1 && time >= animation.Durations[frame]) time -= animation.Durations[frame++];
        var row = animation.Bitmap.Height == animation.Height ? 0 : ((direction % 8) + 8) % 8;
        return new(animation.Bitmap, SKRect.Create(frame * animation.Width, row * animation.Height, animation.Width, animation.Height), sheet.Bounds);
    }

    /// <summary>Loads asynchronously. Callback may run off the UI thread; caller dispatches repaint.</summary>
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
        _ = LoadAsync(key);
    }

    private async Task LoadAsync(string key)
    {
        Sheet? sheet = null;
        await _network.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(FileSystem.CacheDirectory, "pokepark-pmd-v1", key.Replace('/', '_'));
            Directory.CreateDirectory(directory);
            var xml = await ReadAsync(key, directory, "AnimData.xml", 256 * 1024).ConfigureAwait(false);
            var document = XDocument.Parse(Encoding.UTF8.GetString(xml));
            var animations = document.Root?.Element("Anims")?.Elements("Anim").ToArray() ?? [];
            async Task<Animation?> LoadAnimation(string requested)
            {
                var anim = animations.FirstOrDefault(a => (string?)a.Element("Name") == requested);
                var visited = new HashSet<string>();
                while (anim?.Element("CopyOf") is { } copy)
                {
                    if (!visited.Add(copy.Value)) return null;
                    anim = animations.FirstOrDefault(a => (string?)a.Element("Name") == copy.Value);
                }
                if (anim is null) return null;
                var name = (string?)anim.Element("Name");
                if (string.IsNullOrEmpty(name) || !name.All(char.IsAsciiLetter)) return null;
                var width = (int?)anim.Element("FrameWidth") ?? 0;
                var height = (int?)anim.Element("FrameHeight") ?? 0;
                var durations = anim.Element("Durations")?.Elements("Duration").Select(d => Math.Clamp((int)d, 1, 600) * 1000 / 60).ToArray() ?? [];
                if (width is < 1 or > 512 || height is < 1 or > 512 || durations.Length is < 1 or > 64) return null;
                var png = await ReadAsync(key, directory, name + "-Anim.png", 2 * 1024 * 1024).ConfigureAwait(false);
                using var data = SKData.CreateCopy(png);
                using var codec = SKCodec.Create(data);
                if (codec is null || codec.Info.Width != width * durations.Length ||
                    (codec.Info.Height != height && codec.Info.Height != height * 8) ||
                    (long)codec.Info.Width * codec.Info.Height > 262_144) return null;
                var bitmap = SKBitmap.Decode(png);
                return bitmap is null ? null : new(bitmap, width, height, durations);
            }
            var walk = await LoadAnimation("Walk").ConfigureAwait(false);
            if (walk is null) return;
            var credits = await ReadAsync(key, directory, "credits.txt", 256 * 1024).ConfigureAwait(false);
            Animation? idle = null;
            try { idle = await LoadAnimation("Idle").ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or FormatException or OverflowException) { }
            sheet = new(walk, idle, Encoding.UTF8.GetString(credits), VisualBounds(walk, idle));

        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or System.Xml.XmlException or FormatException or OverflowException or UnauthorizedAccessException)
        {
            // Offline, incomplete or malformed upstream asset: bundled sprite remains usable.
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

    /// <summary>One stable opaque envelope shared by all poses and both animations.
    /// Coordinates are relative to each cell's center, so differing transparent padding
    /// never changes the resident's scale or anchor when switching frames.</summary>
    private static SKRect VisualBounds(Animation walk, Animation? idle)
    {
        var left = float.PositiveInfinity; var top = float.PositiveInfinity;
        var right = float.NegativeInfinity; var bottom = float.NegativeInfinity;
        void Include(Animation animation)
        {
            var pixels = animation.Bitmap.Pixels;
            var bitmapWidth = animation.Bitmap.Width;
            for (var index = 0; index < pixels.Length; index++)
            {
                if (pixels[index].Alpha == 0) continue;
                var x = index % bitmapWidth % animation.Width - animation.Width / 2f;
                var y = index / bitmapWidth % animation.Height - animation.Height / 2f;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
            }
        }
        Include(walk);
        if (idle is not null) Include(idle);
        // An entirely transparent upstream sheet still needs a finite, nonempty scale.
        return float.IsFinite(left) ? new(left, top, right, bottom)
            : new(-walk.Width / 2f, -walk.Height / 2f, walk.Width / 2f, walk.Height / 2f);
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

    private static async Task<byte[]> ReadAsync(string key, string directory, string file, int limit)
    {
        var path = Path.Combine(directory, file);
        if (File.Exists(path) && new FileInfo(path).Length <= limit)
            return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        using var response = await Http.GetAsync(Root + key + "/" + file, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
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
