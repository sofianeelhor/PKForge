using PKForge.App.Services;
using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>A deterministic little world: the same scene paints the park and widget frames.</summary>
public sealed class PokeparkScene(ISpriteService sprites, PokeparkSpriteService walking)
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKBitmap, FrameBounds> _visibleBounds = new();
    private readonly Dictionary<string, SKBitmap?> _environment = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lazy<Task>> _environmentLoads = new(StringComparer.Ordinal);
    private readonly object _environmentGate = new();
    private readonly Dictionary<(string Id, int Map), ResidentRoute> _routes = new();
    private sealed class ResidentRoute(ParkWander wander, bool animated)
    {
        public ParkWander Wander { get; } = wander;
        public bool Animated { get; } = animated;
        public long? Started { get; set; }
    }
    private int _environmentIndex;
    private float _drawWidth = 480, _drawHeight = 270;
    public static SKRect Viewport(float width, float height)
    {
        // The host is sized to the map's 16:9 surface by the page. Keep a
        // strict contain fit so no bezel/background leaks into the scene.
        var scale = Math.Min(width / ParkMap.Width, height / ParkMap.Height);
        var w = ParkMap.Width * scale; var h = ParkMap.Height * scale;
        return SKRect.Create((width - w) / 2, (height - h) / 2, w, h);
    }

    private IReadOnlyList<ParkPokemon> _residents = [];
    private readonly Dictionary<string, ParkInteraction> _interactions = new();
    public IReadOnlyList<ParkPokemon> Residents
    {
        get => _residents;
        set
        {
            _residents = value;
            _routes.Clear();
            var ids = value.Select(mon => mon.Id).ToHashSet();
            foreach (var id in _interactions.Keys.Where(id => !ids.Contains(id)).ToArray()) _interactions.Remove(id);
        }
    }

    public int SelectedIndex { get; set; } = -1;
    public int EnvironmentIndex
    {
        get => _environmentIndex;
        set => _environmentIndex = Math.Clamp(value, 0, 3);
    }
    public string EnvironmentName => ParkEnvironment.Get((ParkEnvironmentId)EnvironmentIndex).Name;
    public void Greet(int index, long time) => Interact(index, time, "Talk");
    public void Interact(int index, long time, string action)
    {
        if (index < 0 || index >= Residents.Count) return;
        var id = Residents[index].Id;
        if (!_interactions.TryGetValue(id, out var interaction))
            _interactions[id] = interaction = new ParkInteraction();
        interaction.Begin(time, action);
    }
    public ParkResidentState GetResidentState(int index, long time)
    {
        ParkInteraction? interaction = null;
        if (index >= 0 && index < Residents.Count) _interactions.TryGetValue(Residents[index].Id, out interaction);
        if (index < 0 || index >= Residents.Count) return default;
        var mon = Residents[index];
        var key = (mon.Id, EnvironmentIndex);
        var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
        if (!_routes.TryGetValue(key, out var route))
        {
            var allowed = ParkTraversal.Land;
            if (types.Contains(ParkType.Water)) allowed |= ParkTraversal.Water;
            if (types.Contains(ParkType.Fire)) allowed |= ParkTraversal.Lava;
            if (types.Contains(ParkType.Flying)) allowed |= ParkTraversal.Air;
            var preferred = EnvironmentIndex switch
            {
                0 or 1 when types.Contains(ParkType.Water) => ParkTerrain.Water,
                2 when types.Contains(ParkType.Fire) => ParkTerrain.Lava,
                3 when types.Contains(ParkType.Flying) => ParkTerrain.Air,
                _ => ParkTerrain.Land
            };
            uint seed = 2166136261;
            foreach (var letter in mon.Id) seed = (seed ^ letter) * 16777619;
            var isAnimated = walking.GetFrame(mon.Species, mon.Form, mon.Shiny, 0, 0) is not null;
            _routes[key] = route = new(new ParkWander(ParkMap.For(EnvironmentIndex), allowed, preferred,
                unchecked((int)(seed + (uint)EnvironmentIndex * 7919)), radius: 12), isAnimated);
        }
        var animated = route.Animated;
        var motionTime = interaction?.MotionTime(time) ?? time;
        if (animated) route.Started ??= motionTime;
        var state = route.Wander.GetState(animated ? Math.Max(0, motionTime - route.Started!.Value) : 0);
        if (!animated) state = state with { IsWalking = false, Activity = "Resting and enjoying the view" };
        if (ParkEnvironment.Get((ParkEnvironmentId)EnvironmentIndex).MoodBonus(types) > 0)
            state = state with { Mood = state.IsWalking ? "Playful" : "Delighted" };
        if (interaction?.IsActive(time) != true) return state;
        var (mood, activity) = interaction.Action switch
        {
            "Snack" => ("Delighted", "Enjoying a berry picnic"),
            "Play" => ("Playful", "Bouncing along with the ball"),
            "Relax" => ("Peaceful", "Listening to the meadow"),
            _ => ("Delighted", "Happy to see you!")
        };
        return state with { IsWalking = false, Mood = mood, Activity = activity };
    }
    public int HitTest(float normalizedX, float normalizedY, long time)
    {
        var view = Viewport(_drawWidth, _drawHeight);
        var screenX = normalizedX * _drawWidth; var screenY = normalizedY * _drawHeight;
        if (!view.Contains(screenX, screenY) || view.Width <= 0) return -1;
        var x = (screenX - view.Left) * 480 / view.Width;
        var y = (screenY - view.Top) * 270 / view.Height;
        var closest = -1; var distance = 32f * 32;
        for (var i = 0; i < Residents.Count; i++)
        {
            if (!IsAtHome(Residents[i])) continue;
            var state = GetResidentState(i, time);
            var d = MathF.Pow(state.X - x, 2) + MathF.Pow(state.Y - 15 - y, 2);
            if (d < distance) { distance = d; closest = i; }
        }
        return closest;
    }

    // A short launcher loop uses genuine Idle frames at fixed positions. It never
    // reverses time (and therefore never sends a walking sprite backwards).
    public void DrawWidgetFrame(SKCanvas c, int width, int height, int frame) =>
        Draw(c, width, height, frame * 400L, labels: false, ambient: true);

    public async Task WarmEnvironmentAsync(int environmentIndex)
    {
        var asset = ParkMap.For(environmentIndex).Asset;
        Lazy<Task> pending;
        lock (_environmentGate)
        {
            if (_environment.ContainsKey(asset)) return;
            if (!_environmentLoads.TryGetValue(asset, out var existing))
            {
                pending = new Lazy<Task>(
                    () => LoadEnvironmentAsync(asset),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _environmentLoads.Add(asset, pending);
            }
            else
                pending = existing;
        }

        try { await pending.Value.ConfigureAwait(false); }
        catch
        {
            lock (_environmentGate)
            {
                if (_environmentLoads.TryGetValue(asset, out var current) && ReferenceEquals(current, pending))
                    _environmentLoads.Remove(asset);
            }
            throw;
        }
    }

    private async Task LoadEnvironmentAsync(string asset)
    {
        await using var stream = await FileSystem.OpenAppPackageFileAsync($"pokepark/maps/{asset}").ConfigureAwait(false);
        await using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        var bitmap = await Task.Run(() => SKBitmap.Decode(bytes)).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Invalid Poképark map: {asset}");

        lock (_environmentGate)
        {
            if (!_environment.TryAdd(asset, bitmap))
                bitmap.Dispose();
        }
    }

    public void Draw(SKCanvas c, int width, int height, long time, bool labels = true, bool ambient = false)
    {
        c.Clear(SKColor.Parse("#233a39"));
        var view = Viewport(width, height);
        if (labels) { _drawWidth = width; _drawHeight = height; }
        c.Save();
        c.Translate(view.Left, view.Top);
        c.Scale(view.Width / 480f, view.Height / 270f);
        c.ClipRect(SKRect.Create(0, 0, 480, 270));
        using var p = new SKPaint { IsAntialias = false };
        DrawMap(c, p);
        var poses = Residents.Select((mon, i) => (mon, i, state: GetResidentState(i, ambient ? 0 : time)))
            .Where(v => IsAtHome(v.mon))
            .OrderBy(v => v.state.Y);
        foreach (var (mon, i, state) in poses)
        {
            var pos = new SKPoint(state.X, state.Y);
            _interactions.TryGetValue(mon.Id, out var interaction);
            var active = !ambient && interaction?.IsActive(time) == true;
            var hop = active && interaction!.Action == "Play"
                ? -MathF.Abs(MathF.Sin(interaction.Elapsed(time) / 260f)) * 5 : 0;
            var ground = pos;

            if (i == SelectedIndex && labels)
            {
                p.Color = new SKColor(255, 245, 188, 180);
                c.DrawOval(pos.X, pos.Y + 2, 13, 4, p);
            }
            p.Color = new SKColor(53, 91, 65, 45);
            c.DrawOval(pos.X, pos.Y + 2, 9, 3, p);
            pos.Y += hop;
            var moving = state.IsWalking && !ambient;
            var frame = walking.GetFrame(mon.Species, mon.Form, mon.Shiny, ambient ? 0 : state.Facing, time + i * 173, moving);
            var bitmap = frame?.Bitmap ?? sprites.GetSprite(mon.Species, mon.Form, mon.Shiny);
            if (bitmap is not null)
            {
                var source = frame?.Source ?? new SKRect(0, 0, bitmap.Width, bitmap.Height);
                p.Color = SKColors.White;
                if (frame is not null)
                {
                    // PMD animation cells share a centered origin. Cropping each
                    // frame to its alpha bounds destroys that anchor and causes jitter.
                    var scale = Math.Min(2.8f, Math.Min(42 / source.Width, 42 / source.Height)) * SizeFactor(mon.Species);
                    var w = source.Width * scale; var h = source.Height * scale;
                    c.DrawBitmap(bitmap, source, new SKRect(pos.X - w / 2, pos.Y - h, pos.X + w / 2, pos.Y), p);
                }
                else
                {
                    source = VisibleBounds(bitmap, source);
                    var scale = Math.Min(2.8f, Math.Min(42 / source.Width, 42 / source.Height)) * SizeFactor(mon.Species);
                    var w = source.Width * scale; var h = source.Height * scale;
                    var bob = moving ? (float)Math.Sin(time / 140d + i) * 1.2f : 0;
                    c.DrawBitmap(bitmap, source, new SKRect(pos.X - w / 2, pos.Y - h + bob, pos.X + w / 2, pos.Y + bob), p);
                }
            }
            else
            {
                p.Color = SKColor.Parse("#f8efcf"); c.DrawCircle(pos.X, pos.Y - 13, 9, p);
                p.Color = SKColor.Parse("#65855e"); c.DrawCircle(pos.X - 3, pos.Y - 15, 1, p); c.DrawCircle(pos.X + 3, pos.Y - 15, 1, p);
            }
            if (active) DrawActivity(c, p, ground, interaction!, time);
            if (!active && (time / 1000 + i * 3) % 13 < 3) DrawEmote(c, p, pos.X + 12, pos.Y - 33, state.Mood == "Delighted" ? 1 : (int)(time / 13000 + i) % 3);
            if (labels && i == SelectedIndex)
            {
                using var font = PixelFont.For(mon.Name, 9);
                p.Color = SKColor.Parse("#3f614f");
                var name = mon.Name.Length > 15 ? mon.Name[..14] + "…" : mon.Name;
                var labelWidth = font.MeasureText(name, p) + 10;
                var labelX = Math.Clamp(pos.X, labelWidth / 2, 480 - labelWidth / 2);
                var baseline = Math.Min(265, pos.Y + 13);
                p.Color = new SKColor(255, 250, 229, 235);
                c.DrawRoundRect(SKRect.Create(labelX - labelWidth / 2, baseline - 10, labelWidth, 13), 3, 3, p);
                p.Color = SKColor.Parse("#304739");
                c.DrawText(name, labelX, baseline, SKTextAlign.Center, font, p);
            }
        }
        c.Restore();
    }

    private bool IsAtHome(ParkPokemon mon)
    {
        if (EnvironmentIndex == 0) return true;
        var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
        return EnvironmentIndex switch
        {
            1 => types.Contains(ParkType.Water),
            2 => types.Contains(ParkType.Fire) || types.Contains(ParkType.Ground) || types.Contains(ParkType.Rock),
            3 => types.Contains(ParkType.Flying) || types.Contains(ParkType.Ice),
            _ => true
        };
    }

    private static float SizeFactor(int species) => species switch
    {
        3 or 6 or 9 or 31 or 34 or 59 or 68 or 80 or 89 or 94 or 130 or 131 or 143 or 149 or 150 or 151 or 242 or 248 or 249 or 250 or 384 or 445 or 483 or 484 or 487 or 643 or 644 or 646 or 716 or 717 or 718 or 799 or 800 or 889 or 890 or 984 or 985 or 998 or 1000 => 1.55f,
        4 or 7 or 10 or 13 or 16 or 19 or 25 or 29 or 32 or 35 or 37 or 39 or 41 or 43 or 46 or 48 or 54 or 56 or 60 or 63 or 66 or 69 or 72 or 74 or 81 or 84 or 86 or 90 or 92 or 100 or 109 or 116 or 120 or 129 or 133 or 172 or 173 or 174 or 175 or 194 or 218 or 223 or 265 or 270 or 280 or 293 or 304 or 363 or 401 or 412 or 415 or 425 or 436 or 453 or 458 or 504 or 506 or 532 or 543 or 557 or 568 or 585 or 592 or 594 or 704 or 742 or 744 or 746 or 767 or 819 or 821 or 831 or 833 or 906 or 917 => 0.72f,
        _ => 1f
    };

    private void DrawMap(SKCanvas canvas, SKPaint paint)
    {
        var asset = ParkMap.For(EnvironmentIndex).Asset;
        SKBitmap? bitmap;
        lock (_environmentGate) _environment.TryGetValue(asset, out bitmap);
        if (bitmap is null) return; // loading frame keeps paint non-blocking
        paint.Color = SKColors.White;
        canvas.DrawBitmap(bitmap, new SKRect(0, 0, 480, 270), paint);
    }

    private void DrawActivity(SKCanvas c, SKPaint p, SKPoint pos, ParkInteraction activity, long time)
    {
        var t = activity.Elapsed(time) / 1000f;
        var map = ParkMap.For(EnvironmentIndex);
        var allowed = ParkMap.TraversalFor(map.TerrainAt(pos.X, pos.Y));
        var x = pos.X + 26;
        if (!map.CanOccupy(x, pos.Y, allowed, 16)) x = pos.X - 26;
        if (!map.CanOccupy(x, pos.Y, allowed, 16))
        {
            DrawEmote(c, p, pos.X, pos.Y - 33, 1);
            return;
        }
        var y = pos.Y;
        void Rect(float rx, float ry, float w, float h, string color)
        { p.Color = SKColor.Parse(color); c.DrawRect(rx, ry, w, h, p); }
        switch (activity.Action)
        {
            case "Snack":
                // Picnic cloth, berry and little rising sparkles.
                Rect(x - 10, y - 2, 21, 8, "#fff0ce");
                Rect(x - 10, y, 21, 2, "#e7a3a0");
                p.Color = SKColor.Parse("#d87091"); c.DrawCircle(x, y - 5, 6, p);
                Rect(x - 3, y - 8, 2, 2, "#ffd2cc");
                Rect(x, y - 13, 2, 5, "#587c55"); Rect(x + 2, y - 13, 4, 2, "#80ab62");
                for (var j = 0; j < 3; j++)
                {
                    var rise = (t * 13 + j * 9) % 30;
                    Rect(x - 10 + j * 9, y - 17 - rise, 2, 5, "#fff3a5");
                    Rect(x - 11 + j * 9, y - 16 - rise, 4, 2, "#fff3a5");
                }
                DrawEmote(c, p, pos.X + 9, pos.Y - 33, 1);
                break;
            case "Play":
                var bounce = MathF.Abs(MathF.Sin(t * 3.85f)) * 23;
                p.Color = new SKColor(53, 91, 65, 45); c.DrawOval(x, y + 3, 7, 2, p);
                p.Color = SKColor.Parse("#efb568"); c.DrawCircle(x, y - 5 - bounce, 7, p);
                Rect(x - 6, y - 7 - bounce, 12, 3, "#fbebbb");
                Rect(x - 2, y - 11 - bounce, 3, 12, "#91b7ac");
                DrawEmote(c, p, pos.X + 9, pos.Y - 33, 0);
                break;
            case "Relax":
                for (var j = 0; j < 3; j++)
                {
                    var rise = (t * 8 + j * 13) % 36;
                    var nx = pos.X - 17 + j * 17;
                    var ny = pos.Y - 28 - rise;
                    Rect(nx, ny, 2, 8, "#7088a8"); Rect(nx + 2, ny, 4, 2, "#7088a8");
                    p.Color = SKColor.Parse("#7088a8"); c.DrawOval(nx - 1, ny + 8, 3, 2, p);
                }
                break;
            default:
                DrawEmote(c, p, pos.X + 12, pos.Y - 33, 1);
                break;
        }
    }

    private sealed class FrameBounds(SKColor[] pixels)
    {
        public SKColor[] Pixels { get; } = pixels;
        public Dictionary<SKRect, SKRect> Frames { get; } = new();
    }

    private SKRect VisibleBounds(SKBitmap bitmap, SKRect cell)
    {
        // Copy pixels once per sheet; avoid native GetPixel calls in the rendering loop.
        var cached = _visibleBounds.GetValue(bitmap, b => new FrameBounds(b.Pixels));
        var frames = cached.Frames;
        if (frames.TryGetValue(cell, out var bounds)) return bounds;
        var left = (int)cell.Right; var top = (int)cell.Bottom;
        var right = (int)cell.Left; var bottom = (int)cell.Top;
        for (var y = (int)cell.Top; y < (int)cell.Bottom; y++)
            for (var x = (int)cell.Left; x < (int)cell.Right; x++)
                if (cached.Pixels[y * bitmap.Width + x].Alpha > 0)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
                }
        bounds = right > left && bottom > top ? new SKRect(left, top, right, bottom) : cell;
        frames[cell] = bounds;
        return bounds;
    }

    private static void DrawEmote(SKCanvas c, SKPaint p, float x, float y, int kind)
    {
        x = Math.Clamp(x, 10, 470); y = Math.Clamp(y, 11, 258);
        p.Color = new SKColor(255, 252, 232); c.DrawRoundRect(new SKRect(x - 9, y - 10, x + 9, y + 6), 4, 4, p);
        c.DrawRect(x - 4, y + 5, 4, 4, p);
        p.Color = SKColor.Parse(kind == 1 ? "#d8778a" : "#687364");
        if (kind == 1)
        {
            c.DrawCircle(x - 3, y - 4, 3, p); c.DrawCircle(x + 3, y - 4, 3, p);
            using var heart = new SKPath(); heart.MoveTo(x - 6, y - 3); heart.LineTo(x, y + 3); heart.LineTo(x + 6, y - 3); heart.Close(); c.DrawPath(heart, p);
        }
        else
        {
            c.DrawRect(x - 4, y - 5, 2, 2, p); c.DrawRect(x + 2, y - 5, 2, 2, p);
            c.DrawRect(x - 3, y + (kind == 0 ? 1 : 0), 6, 1, p);
            c.DrawRect(x - 4, y + (kind == 0 ? 0 : 1), 1, 2, p); c.DrawRect(x + 3, y + (kind == 0 ? 0 : 1), 1, 2, p);
        }
    }
}
