using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>The park on screen: the map, its life (<see cref="ParkLife"/>) and the camera. The same
/// scene paints the page and the widget frames.</summary>
public sealed class PokeparkScene(ISpriteService sprites, PokeparkSpriteService walking)
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKBitmap, FrameBounds> _visibleBounds = new();
    private readonly object _artGate = new();
    private ParkArt? _art;
    private Lazy<Task>? _artLoad;
    private static ParkMap Map => ParkMap.Park;
    private readonly ParkLife _life = new(ParkMap.Park, []);
    private ParkCamera _camera = new(1, 0, 0);
    private float _drawWidth = 1, _drawHeight = 1;
    private long? _cameraTime;

    /// <summary>The park's life, for the page: needs, moments, commands.</summary>
    public ParkLife Life => _life;
    /// <summary>Test overrides for the time of day and the weather (Diagnostics builds); null follows the real clock.</summary>
    public ParkDayPhase? PhaseOverride { get; set; }
    public ParkWeather? WeatherOverride { get; set; }
    /// <summary>The real weather of the player's city (Options, Time and weather), when it is on.</summary>
    public ParkWeatherReading? RealWeather { get; set; }

    /// <summary>Hide and seek with the pad: the place the player is about to look.</summary>
    public ParkPoint? SeekCursor { get; set; }

    /// <summary>Screen pixels hidden at the bottom by the card: the camera keeps the selected Pokémon above them.</summary>
    public float CoveredBottom { get; set; }

    /// <summary>The page's camera: touch and the pad move it, drawing reads it.</summary>
    public ParkViewport Viewport { get; } = new(ParkMap.Park.Width, ParkMap.Park.Height);

    private IReadOnlyList<ParkPokemon> _residents = [];
    public IReadOnlyList<ParkPokemon> Residents
    {
        get => _residents;
        set { _residents = value; _life.SetResidents(LifeResidents()); }
    }

    private IEnumerable<ParkLifeResident> LifeResidents() => _residents.Select(mon =>
        new ParkLifeResident(mon.Id, mon.Name, mon.Species, HabitatCatalog.TypesFor(mon.Species, mon.Form), ParkLife.PersonalityFor(mon.Id),
            NatureOf?.Invoke(mon), HabitatCatalog.SpeedFor(mon.Species, mon.Form), mon.Shiny, mon.Form));

    /// <summary>A resident's nature, when its save or the Bank can tell (set by the page).</summary>
    public Func<ParkPokemon, int?>? NatureOf { get; set; }

    public int SelectedIndex { get; set; } = -1;
    /// <summary>Where the camera is: the place around the selected Pokémon, or the middle of the view.</summary>
    public ParkRegion Region
    {
        get
        {
            var selectedId = SelectedIndex >= 0 && SelectedIndex < Residents.Count ? Residents[SelectedIndex].Id : null;
            if (Viewport.Following && selectedId is not null && _life[selectedId] is { } actor) return Map.RegionAt(actor.X, actor.Y);
            return Map.RegionAt(Viewport.CenterX, Viewport.CenterY);
        }
    }
    public ParkEnvironmentId Habitat => Region.Habitat;

    /// <summary>The camera glides to the next place of the park (L and R), and stays there.</summary>
    public ParkRegion FlyToRegion(int delta)
    {
        var places = Map.Regions.Take(Map.Regions.Count - 1).ToArray();
        var here = Array.FindIndex(places, r => r.Id == Region.Id);
        var next = places[((here < 0 ? (delta > 0 ? -1 : 0) : here) + delta + places.Length) % places.Length];
        Viewport.FlyTo(next.Center.X, next.Center.Y);
        return next;
    }

    /// <summary>The player spends a moment with a resident (Talk, Snack, Play or Relax).</summary>
    public ParkReply Interact(int index, long time, string action)
    {
        if (index < 0 || index >= Residents.Count) return ParkReply.None;
        return _life.Command(Residents[index].Id, action.ToLowerInvariant() switch
        {
            "snack" => ParkCommand.Snack, "play" => ParkCommand.Play, "relax" => ParkCommand.Relax, _ => ParkCommand.Talk
        });
    }

    public ParkResidentState GetResidentState(int index, long time)
    {
        if (index < 0 || index >= Residents.Count || _life[Residents[index].Id] is not { } actor) return default;
        return new(actor.X, actor.Y, actor.IsWalking, actor.Facing, actor.Mood, actor.Activity);
    }

    public int HitTest(float normalizedX, float normalizedY, long time)
    {
        if (normalizedX is < 0 or > 1 || normalizedY is < 0 or > 1) return -1;
        var point = _camera.ToMap(normalizedX * _drawWidth, normalizedY * _drawHeight);
        var closest = -1; var distance = 20f * 20;
        for (var i = 0; i < Residents.Count; i++)
        {
            if (_life[Residents[i].Id] is not { } actor || actor.Indoors || actor.Hidden || actor.InShadow || actor.Submerged) continue;
            var d = MathF.Pow(actor.X - point.X, 2) + MathF.Pow(actor.Y - actor.Altitude - 10 - point.Y, 2);
            if (d < distance) { distance = d; closest = i; }
        }
        return closest;
    }

    /// <summary>Loads the park's art once; a failed load is tried again on the next call.</summary>
    public async Task LoadArtAsync()
    {
        Lazy<Task> load;
        lock (_artGate)
        {
            if (_art is not null) return;
            load = _artLoad ??= new Lazy<Task>(async () =>
            {
                // The ways across the park for walkers and swimmers are worked out here, off the
                // screen's thread, not on the first frame.
                var routes = Task.Run(() =>
                {
                    foreach (var way in new[] { ParkTraversal.Land, ParkTraversal.Land | ParkTraversal.Water, ParkTraversal.Water })
                        ParkNavigator.For(Map, way);
                });
                var art = await PokeparkMapArt.LoadAsync(Map).ConfigureAwait(false);
                await Task.Run(() => art.LitBy(ParkSky.LightColor(_life.Phase, DateTime.Now, _life.Weather))).ConfigureAwait(false);
                await routes.ConfigureAwait(false);
                lock (_artGate) _art = art;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }
        try { await load.Value.ConfigureAwait(false); }
        catch
        {
            lock (_artGate) if (ReferenceEquals(_artLoad, load)) _artLoad = null;
            throw;
        }
    }

    private static readonly SKColor Background = SKColor.Parse("#233a39");
    private readonly ParkLowRes _lowRes = new(), _lowResAmbient = new();

    /// <summary>Shows what a frame costs, top right (Options, Performance).</summary>
    public bool ShowPerformance { get; set; }
    private double _drawMs, _lifeMs, _frameGap;
    private long _lastFrame = -1;

    public void Draw(SKCanvas c, int width, int height, long time, bool labels = true, bool ambient = false)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!ambient)
        {
            if (_lastFrame >= 0) _frameGap += (time - _lastFrame - _frameGap) * .05;
            _lastFrame = time;
        }
        DrawFrame(c, width, height, time, labels, ambient);
        if (ambient) return;
        var spent = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _drawMs += (spent - _drawMs) * .05;
        TracePerformance(time, spent, width, height);
        if (!ShowPerformance) return;
        using var font = new SKFont(PixelFont.Face, MathF.Max(14, width / 80f));
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, 230), IsAntialias = false };
        using var back = new SKPaint { Color = new SKColor(0, 0, 0, 150) };
        var text = $"frame {_drawMs:0.0} ms (life {_lifeMs:0.00} ms)  ·  {(_frameGap > 0 ? 1000 / _frameGap : 0):0} fps  ·  {_life.Actors.Count} Pokémon";
        var w = font.MeasureFallback(text);
        c.DrawRect(width - w - font.Size * 1.5f, 0, w + font.Size * 1.5f, font.Size * 1.8f, back);
        c.DrawTextFallback(text, width - w - font.Size * .75f, font.Size * 1.3f, SKTextAlign.Left, font, paint);
    }

    // Diagnostic builds: a summary every five seconds and every slow frame, to read with adb logcat.
    private long _traceSince = -1;
    private int _traceFrames;
    private double _traceSum, _traceMax, _traceLifeMax, _lastLifeMs;

    [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("DIAGNOSTIC")]
    private void TracePerformance(long time, double spent, int width, int height)
    {
        if (_traceSince < 0)
        {
            _traceSince = time;
            ParkLife.SlowChoice = line => PerfTrace.Park("slow " + line);
        }
        _traceFrames++; _traceSum += spent; _traceMax = Math.Max(_traceMax, spent); _traceLifeMax = Math.Max(_traceLifeMax, _lastLifeMs);
        if (spent > 25)
            PerfTrace.Park($"slow frame {spent:0.0} ms (life {_lastLifeMs:0.0} ms, GC gen0 {GC.CollectionCount(0)} gen1 {GC.CollectionCount(1)} gen2 {GC.CollectionCount(2)}) · " +
                string.Join(" | ", _life.Actors.Take(14).Select(a => $"{a.Resident.Name}:{a.Activity}")));
        if (time - _traceSince < 5000) return;
        var seconds = (time - _traceSince) / 1000.0;
        PerfTrace.Park($"5s: {_traceFrames / seconds:0} fps · frame avg {_traceSum / _traceFrames:0.0} ms max {_traceMax:0.0} ms · " +
            $"life avg {_lifeMs:0.00} ms max {_traceLifeMax:0.0} ms · {_life.Actors.Count} Pokémon · {width}×{height} scale {Viewport.Scale:0.##} · " +
            $"{_life.Phase} {_life.Weather} · GC gen0 {GC.CollectionCount(0)} gen2 {GC.CollectionCount(2)} · heap {GC.GetTotalMemory(false) / 1024 / 1024} MB");
        _traceSince = time; _traceFrames = 0; _traceSum = _traceMax = _traceLifeMax = 0;
    }

    private void DrawFrame(SKCanvas c, int width, int height, long time, bool labels, bool ambient)
    {
        c.Clear(Background);
        var map = Map;
        ParkArt? art;
        lock (_artGate) art = _art;
        var now = DateTime.Now;
        // A forced time of day stands at the middle of that part of the day.
        if (PhaseOverride is { } forced)
            now = now.Date.AddHours(forced switch { ParkDayPhase.Morning => 7.5, ParkDayPhase.Evening => 19.5, ParkDayPhase.Night => 23, _ => 13 });
        _life.Phase = ParkCare.PhaseAt(now);
        _life.Weather = ParkRealWeather.SkyFor(WeatherOverride, RealWeather, DateTimeOffset.Now, ParkCare.WeatherAt(now));
        _life.Today = DateOnly.FromDateTime(now);
        if (!ambient)
        {
            var lifeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _life.Advance(time);
            _lastLifeMs = System.Diagnostics.Stopwatch.GetElapsedTime(lifeStart).TotalMilliseconds;
            _lifeMs += (_lastLifeMs - _lifeMs) * .05;
        }
        var actors = _life.Actors.Where(a => !a.Indoors && !a.Hidden && !a.InShadow && !a.Submerged).ToArray();
        var selectedId = SelectedIndex >= 0 && SelectedIndex < Residents.Count ? Residents[SelectedIndex].Id : null;
        var camera = ParkCamera.Fit(map, width, height, new SKPoint(map.Width / 2f, map.Height / 2f));
        if (labels)
        {
            // The first frame jumps to the selected resident; later frames glide.
            var elapsed = _cameraTime is { } last ? time - last : 1000;
            _cameraTime = time;
            Viewport.Resize(width, height);
            Viewport.Step(elapsed);
            if (_life.Game is { Kind: ParkGameKind.HideAndSeek or ParkGameKind.Race, Stage: not ParkGameStage.Over }) { }
            else if (actors.FirstOrDefault(a => a.Resident.Id == selectedId) is { } selected)
                Viewport.Track(selected.X, selected.Y - selected.Altitude - 12 + CoveredBottom / 2 / Viewport.Scale, elapsed);
            _camera = camera = ParkCamera.Of(Viewport);
            _drawWidth = width; _drawHeight = height;
        }
        // The world at its own resolution, scaled up once (see ParkLowRes).
        var screen = c;
        var lowRes = ambient ? _lowResAmbient : _lowRes;
        c = lowRes.Begin(camera, map.Width, map.Height, width, height, Background);
        using var p = new SKPaint { IsAntialias = false, Color = SKColors.White };
        // The day's light: the map's art is kept tinted (redrawn only when the light changes), the
        // sprites are tinted as they are drawn.
        var lightColor = ParkSky.LightColor(_life.Phase, now, _life.Weather);
        using var light = lightColor == SKColors.White ? null : SKColorFilter.CreateBlendMode(lightColor, SKBlendMode.Modulate);
        using var mapPaint = new SKPaint { IsAntialias = false, Color = SKColors.White };
        var visible = camera.Visible(width, height);
        var lit = art?.LitBy(lightColor);
        if (lit is { } lit0) ParkPaint.Ground(c, lit0.Ground, visible, mapPaint);
        p.ColorFilter = light;
        var heads = new float[actors.Length];
        // A flyer in the air leaves its shadow on the ground, in depth order, and is drawn over everything.
        var layers = actors.Select((actor, k) => (actor.InFlight ? float.MaxValue : actor.Y,
            (Action)(() => heads[k] = DrawResident(c, p, actor, time, labels && actor.Resident.Id == selectedId, ambient)))).ToList();
        foreach (var diver in _life.Actors.Where(a => a.Submerged))
            layers.Add((diver.Y, () => DrawUnderwater(c, p, diver, time)));
        foreach (var flyer in actors.Where(a => a.InFlight))
            layers.Add((flyer.Y, () => DrawFlightShadow(c, p, flyer, labels && flyer.Resident.Id == selectedId)));
        if (ParkSky.Window(c, p, map, _life.Phase) is { } window) layers.Add(window);
        if (ParkSky.Campfire(c, p, _life.Campfire, _life.Now, time) is { } fire) layers.Add(fire);
        if (_life.Chronicle.Dam > 0 && _life.DamSpot is { } dam)
            layers.Add((dam.Y + 10, () => ParkProps.DrawDam(c, p, dam.X + 12, dam.Y + 10, _life.Chronicle.Dam)));
        ParkPaint.InDepthOrder(c, lit?.Pieces ?? [], visible, mapPaint, layers);
        DrawGlows(c, actors, now);
        DrawBall(c, p, time);
        p.ColorFilter = null;
        foreach (var effect in _life.Effects) ParkEffects.Draw(c, p, effect, time);
        if (SeekCursor is { } look && _life.Game is { Kind: ParkGameKind.HideAndSeek })
        {
            // A small pointer over the place the pad will look.
            var bob = MathF.Round(MathF.Sin(time / 180f) * 1.5f);
            p.Color = new SKColor(0x2b, 0x2d, 0x42);
            c.DrawRect(look.X - 3, look.Y - 22 + bob, 7, 3, p); c.DrawRect(look.X - 2, look.Y - 19 + bob, 5, 2, p); c.DrawRect(look.X - 1, look.Y - 17 + bob, 3, 2, p);
            p.Color = new SKColor(0xf4, 0xf6, 0xff);
            c.DrawRect(look.X - 2, look.Y - 21 + bob, 5, 1, p); c.DrawRect(look.X - 1, look.Y - 19 + bob, 3, 1, p);
        }
        if (_life.Weather is ParkWeather.Sunny or ParkWeather.Cloudy) ParkSky.Fireflies(c, p, map, _life.Phase, time);
        ParkSky.Rain(c, p, map, _life.Weather, time);

        // Emotes and the name tag float above everything.
        ParkActor? named = null;
        for (var k = 0; k < actors.Length; k++)
        {
            var actor = actors[k];
            // A fresh emote shows even on a sleeper (a pat makes it smile); otherwise sleepers show Zs.
            var fresh = !ambient && actor.Emote != ParkEmote.None && time - actor.EmoteSince < 2600;
            if (actor.Pose == ParkPose.Sleep && !fresh)
            {
                // Zs, and now and then a dream: of food when hungry, of you when it loves you.
                var dreaming = (time - actor.PoseSince) / 4000 % 3 == 2;
                if (dreaming)
                    ParkEmotes.DrawDream(c, p, actor.Care.Hunger < 40 ? ParkEmotes.Dream.Berry : actor.Care.Bond >= 60 ? ParkEmotes.Dream.Heart
                        : actor.Resident.Temperament == ParkTemperament.Playful ? ParkEmotes.Dream.Ball : ParkEmotes.Dream.Star,
                        actor.X + 4, heads[k], (time - actor.PoseSince) % 4000);
                else ParkEmotes.Draw(c, p, ParkEmote.Sleep, actor.X + 4, heads[k], time - actor.PoseSince);
            }
            else if (fresh)
                ParkEmotes.Draw(c, p, actor.Emote, actor.X + 5, heads[k] - 1, time - actor.EmoteSince);
            if (labels && actor.Resident.Id == selectedId) named = actor;
        }
        lowRes.End(screen, camera);
        // The name tag is text: drawn at the screen's own resolution, over the scaled-up world.
        if (named is not null && !Screensaver)
            DrawLabel(screen, p, named.Resident.Name, (named.X - camera.Left) * camera.Scale, (named.Y - named.Altitude - camera.Top) * camera.Scale, width, height);
        if (labels && ShowCaptions && !Screensaver) DrawCaptions(screen, actors, heads, camera, width, height, selectedId);
        c = screen;
        if (!ambient && !Screensaver) DrawGameBanner(c, p, width, time);
        // A zap, or the storm's lightning, flashes the whole screen for an instant.
        if (!ambient && Math.Max(ParkEffects.Flash(_life.Effects, time), ParkSky.Lightning(_life.Weather, time)) is > 0 and var flash)
        {
            p.Color = new SKColor(255, 250, 220, (byte)(150 * flash));
            c.DrawRect(0, 0, width, height, p);
        }
    }

    // After dark the Fire types' flames, Ampharos's tail, Lanturn's lure and the like light up the
    // ground round them: brighter when they are happy, dimmer asleep.
    private void DrawGlows(SKCanvas c, IReadOnlyList<ParkActor> actors, DateTime now)
    {
        var dark = _life.Phase switch { ParkDayPhase.Night => 1f, ParkDayPhase.Evening => .6f, _ => 0f };
        if (dark <= 0) return;
        using var paint = new SKPaint { BlendMode = SKBlendMode.Screen, IsAntialias = false };
        foreach (var actor in actors)
        {
            if (ParkLife.GlowOf(actor.Resident) is not { } glow || actor.Camouflaged) continue;
            var mood = actor.IsAsleep ? .55f : actor.ActivityMoodIsBright ? 1.25f : 1f;
            var radius = glow.Radius * mood;
            var color = new SKColor((byte)(glow.Color >> 16), (byte)(glow.Color >> 8), (byte)glow.Color);
            var center = new SKPoint(actor.X, actor.Y - 8 - actor.Altitude);
            // A slow flicker, each its own.
            var flicker = .85f + .15f * MathF.Sin((float)(now.TimeOfDay.TotalMilliseconds / 260 + actor.Resident.Id.Length));
            using var shader = SKShader.CreateRadialGradient(center, radius,
                [color.WithAlpha((byte)(150 * dark * flicker * actor.OpacityAt(_life.Now))), color.WithAlpha(0)], SKShaderTileMode.Clamp);
            paint.Shader = shader;
            c.DrawCircle(center, radius, paint);
        }
    }

    // A giant deep under the water: its great dark shape, drifting, and a few rings where it passes.
    private void DrawUnderwater(SKCanvas c, SKPaint p, ParkActor actor, long time)
    {
        // Its own shape, dark under the water (an oval until its sprite has loaded).
        var mon = _residents.FirstOrDefault(r => r.Id == actor.Resident.Id) ?? Visitor(actor);
        if (mon is not null && walking.GetFrame(mon.Species, mon.Form, mon.Shiny, actor.Pose, actor.Facing, time - actor.PoseSince) is { } frame)
        {
            var middle = new SKPoint(actor.X - frame.Feet.X, actor.Y - frame.Feet.Y);
            ParkWater.Deep(c, p, frame.Bitmap, frame.Source, middle, new SKPoint(actor.X, actor.Y), time);
            return;
        }
        p.Color = new SKColor(14, 34, 82, 70);
        c.DrawOval(actor.X, actor.Y - 2, 20, 7, p);
    }

    // The ground under a flyer: its shadow, smaller and fainter the higher it flies.
    private void DrawFlightShadow(SKCanvas c, SKPaint p, ParkActor actor, bool selected)
    {
        var mon = _residents.FirstOrDefault(r => r.Id == actor.Resident.Id) ?? Visitor(actor);
        var size = mon is null ? 1 : walking.GetShadowSize(mon.Species, mon.Form, mon.Shiny) ?? 1;
        var shrink = 1 - Math.Clamp(actor.Altitude / 60f, 0, .5f);
        if (selected)
        {
            p.Color = new SKColor(255, 245, 188, 150);
            c.DrawOval(actor.X, actor.Y, 8 * shrink, 3 * shrink, p);
        }
        p.Color = new SKColor(0, 0, 0, (byte)(60 * shrink));
        c.DrawOval(actor.X, actor.Y, (4 + size * 2) * shrink, (1.5f + size * .5f) * shrink, p);
    }

    /// <returns>The top of the drawn sprite, for the emote above its head.</returns>
    private float DrawResident(SKCanvas c, SKPaint p, ParkActor actor, long time, bool selected, bool ambient)
    {
        var mon = _residents.FirstOrDefault(r => r.Id == actor.Resident.Id) ?? Visitor(actor);
        if (mon is null) return actor.Y;
        // Leaping out of the water (Magikarp) it shows whole, in the air.
        var swimming = !actor.InFlight && !actor.Riding && actor.Altitude == 0 && _life.IsSwimming(actor);
        var lift = MathF.Round(actor.Altitude);
        // Faded out (a Ghost unseen, a teleport) or blended in (Kecleon): barely there.
        var opacity = actor.Camouflaged ? .1f : actor.OpacityAt(_life.Now);
        if (opacity < .02f) return actor.Y - 12;
        if (selected && !actor.InFlight)
        {
            p.Color = new SKColor(255, 245, 188, 180);
            c.DrawOval(actor.X, actor.Y, 8, 3, p);
        }
        // No shadow on the ground for one lying up on top of another (Snorlax's belly).
        if (!swimming && !actor.InFlight && actor.Altitude == 0)
        {
            var size = walking.GetShadowSize(mon.Species, mon.Form, mon.Shiny) ?? 1;
            p.Color = new SKColor(0, 0, 0, (byte)(60 * opacity));
            c.DrawOval(actor.X, actor.Y, 4 + size * 2, 1.5f + size * .5f, p);
        }
        var elapsed = ambient ? time : time - actor.PoseSince;
        // Ditto in disguise wears the other species' sprite, tinted its own purple.
        var (species, form, shiny) = actor.Disguise is { } copy ? (copy, 0, false) : (mon.Species, mon.Form, mon.Shiny);
        if (actor.Disguise is not null) walking.Warm(species, 0, false, static () => { });
        var frame = walking.GetFrame(species, form, shiny, actor.Pose, actor.Facing, elapsed);
        var light = p.ColorFilter;
        using var dittoTint = actor.Disguise is null || !actor.DisguiseTinted ? null : SKColorFilter.CreateBlendMode(new SKColor(225, 170, 255), SKBlendMode.Modulate);
        using var disguised = dittoTint is null ? null : light is null ? null : SKColorFilter.CreateCompose(light, dittoTint);
        if (dittoTint is not null) p.ColorFilter = disguised ?? dittoTint;
        float head;
        p.Color = SKColors.White.WithAlpha((byte)(255 * opacity));
        // In the water: whole above the surface, a little of its body seen through it below.
        var waterline = actor.Y - 4;
        void Sprite(SKBitmap bitmap, SKRect source, SKRect dest)
        {
            if (swimming) ParkWater.Swimmer(c, p, bitmap, source, dest, waterline);
            else c.DrawBitmap(bitmap, source, dest, p);
        }
        if (frame is not null)
        {
            // Drawn on whole art pixels, the PMD shadow's middle on the feet; the big species a
            // little smaller (issue #48), nearest-sampled.
            var source = frame.Source;
            var scale = walking.GetScale(species, form, shiny);
            float middleX = actor.X - frame.Feet.X * scale, middleY = actor.Y - lift - frame.Feet.Y * scale + (swimming ? 4 : 0);
            float w = MathF.Round(source.Width * scale), h = MathF.Round(source.Height * scale);
            Sprite(frame.Bitmap, source, SKRect.Create(MathF.Round(middleX - w / 2), MathF.Round(middleY - h / 2), w, h));
            head = middleY + frame.Bounds.Top * scale;
        }
        else if (sprites.GetSprite(mon.Species, mon.Form, mon.Shiny) is { } bitmap)
        {
            var source = VisibleBounds(bitmap, new SKRect(0, 0, bitmap.Width, bitmap.Height));
            var scale = Math.Min(.5f, 32 / source.Height);
            var w = source.Width * scale; var h = source.Height * scale;
            var bob = actor.IsWalking && !ambient ? MathF.Round((float)Math.Sin(time / 140d)) : 0;
            var bottom = actor.Y - lift + (swimming ? 4 : 0);
            Sprite(bitmap, source, SKRect.Create(MathF.Round(actor.X - w / 2), MathF.Round(bottom - h + bob), w, h));
            head = bottom - h;
        }
        else
        {
            p.Color = SKColor.Parse("#f8efcf"); c.DrawCircle(actor.X, actor.Y - 7, 6, p);
            p.Color = SKColor.Parse("#65855e"); c.DrawRect(actor.X - 3, actor.Y - 9, 1, 1, p); c.DrawRect(actor.X + 2, actor.Y - 9, 1, 1, p);
            head = actor.Y - 13;
        }
        // Jigglypuff's marker: a moustache scribbled across the face.
        if (actor.DoodledUntil > _life.Now && !ambient)
        {
            var light0 = p.ColorFilter;
            p.ColorFilter = null;
            p.Color = new SKColor(28, 28, 40);
            float fx = MathF.Round(actor.X), fy = MathF.Round(head + 6);
            foreach (var (dx, dy) in new (int, int)[] { (-3, 0), (-2, 1), (-1, 0), (1, 0), (2, 1), (3, 0), (3, -4), (4, -5) })
                c.DrawRect(fx + dx, fy + dy, 1, 1, p);
            p.ColorFilter = light0;
            p.Color = SKColors.White;
        }
        if (swimming) DrawRipples(c, p, actor.X, waterline, time);
        p.ColorFilter = light;
        if (!ambient && actor.Prop != ParkProp.None) ParkProps.Draw(c, p, actor.Prop, actor.X, actor.Y, actor.Facing, time - actor.PoseSince, actor.BerryFlavor);
        return head;
    }

    // A short notice at the top of the view (a goal done), a few seconds each, one after the other.
    private readonly Queue<string> _notices = new();
    private long _noticeSince = -1;

    public void Notify(string text) { lock (_notices) _notices.Enqueue(text); }

    private string? Notice(long time)
    {
        lock (_notices)
        {
            if (_notices.Count == 0) return null;
            if (_noticeSince < 0) _noticeSince = time;
            if (time - _noticeSince < 4000) return _notices.Peek();
            _notices.Dequeue();
            _noticeSince = _notices.Count > 0 ? time : -1;
            return _notices.Count > 0 ? _notices.Peek() : null;
        }
    }

    // What the game asks of the player, at the top of the view: what to do, time left, progress.
    private void DrawGameBanner(SKCanvas c, SKPaint p, int width, long time)
    {
        if (_life.Game is not { Stage: not ParkGameStage.Over } game || _life[game.HostId] is not { } host)
        {
            if (Notice(time) is { } notice) DrawBanner(c, p, width, notice, null);
            return;
        }
        var name = host.Resident.Name;
        var seconds = Math.Max(0, (game.Deadline - _life.Now) / 1000);
        var text = (game.Kind, game.Stage) switch
        {
            (ParkGameKind.HideAndSeek, ParkGameStage.Starting) => $"Eyes closed… counting to ten while {name} hides",
            (ParkGameKind.HideAndSeek, _) => $"Find {name}! Tap where it hides  ·  {seconds / 60}:{seconds % 60:D2}  ·  {game.Tries} tries",
            (ParkGameKind.Race, ParkGameStage.Starting) => "The racers line up…",
            (ParkGameKind.Race, ParkGameStage.Countdown) => $"{3 - (_life.Now - game.StageSince) / 1000}…",
            (ParkGameKind.Race, _) => _life.Now - game.StageSince < 900 ? "GO!" : $"Tap {name} to cheer it on!  ·  {game.Finished.Count}/{game.Players.Count} at the flag",
            (ParkGameKind.Brush, _) => $"Brush {name} with your finger  ·  {(int)(game.Progress * 100)}%",
            (ParkGameKind.Spar, ParkGameStage.Countdown) => $"{name} takes its stance…",
            (ParkGameKind.Spar, _) => game.Striking ? "BLOCK!" : $"Round {Math.Min(game.Round + 1, ParkLife.SparRounds)}/{ParkLife.SparRounds}  ·  {game.Blocked} blocked  ·  tap or A as it strikes",
            _ => $"{name} is digging…"
        };
        DrawBanner(c, p, width, text, game.Kind == ParkGameKind.Brush ? game.Progress : null);
    }

    private static void DrawBanner(SKCanvas c, SKPaint p, int width, string text, float? progress)
    {
        var unit = width / 1920f;
        using var font = PixelFont.For(text, 40 * unit);
        var w = font.MeasureFallback(text, p) + 60 * unit;
        var rect = SKRect.Create((width - w) / 2, 24 * unit, w, 64 * unit);
        p.Color = new SKColor(0x10, 0x16, 0x30, 220);
        c.DrawRoundRect(rect, 18 * unit, 18 * unit, p);
        if (progress is { } done)
        {
            p.Color = new SKColor(0x6c, 0xd0, 0x7a, 180);
            c.DrawRoundRect(SKRect.Create(rect.Left, rect.Bottom - 8 * unit, rect.Width * done, 8 * unit), 4 * unit, 4 * unit, p);
        }
        p.Color = new SKColor(0xf4, 0xf6, 0xff);
        c.DrawTextFallback(text, rect.MidX, rect.MidY + 14 * unit, SKTextAlign.Center, font, p);
    }

    // Wild visitors have no roster entry: one is made up for drawing, and their sprites load on first sight.
    private readonly Dictionary<string, ParkPokemon> _visitors = new();
    private ParkPokemon? Visitor(ParkActor actor)
    {
        if (!ParkLife.IsVisitor(actor.Resident.Id)) return null;
        if (!_visitors.TryGetValue(actor.Resident.Id, out var look))
        {
            // A released Pokémon back from the Wilds keeps its form and its colors.
            var friend = _life.OldFriendOf(actor.Resident.Id);
            _visitors[actor.Resident.Id] = look = new ParkPokemon(actor.Resident.Id, actor.Resident.Species, friend?.Form ?? 0, friend?.Shiny ?? actor.Resident.Shiny,
                actor.Resident.Name, ParkLife.IsKeeper(actor.Resident.Id) ? "Daycare" : friend is null ? "Wild" : "Old friend");
            walking.Warm(look.Species, look.Form, look.Shiny, static () => { });
            sprites.Warm(look.Species, look.Form, look.Shiny, static () => { });
        }
        return look;
    }

    /// <summary>A wild visitor under a tap, if any.</summary>
    public string? VisitorAt(float normalizedX, float normalizedY)
    {
        var point = _camera.ToMap(normalizedX * _drawWidth, normalizedY * _drawHeight);
        return _life.Actors.Where(a => ParkLife.IsVisitor(a.Resident.Id))
            .Where(a => MathF.Pow(a.X - point.X, 2) + MathF.Pow(a.Y - 10 - point.Y, 2) < 20f * 20)
            .Select(a => a.Resident.Id).FirstOrDefault();
    }

    // The thrown ball: an arc through the air, then on the ground, then carried in front of its Pokémon.
    private void DrawBall(SKCanvas c, SKPaint p, long time)
    {
        if (_life.Ball is not { } ball) return;
        if (ball.HolderId is { } holder && _life[holder] is { } carrier)
        {
            var side = carrier.Facing is 1 or 2 or 3 ? 6 : carrier.Facing is 5 or 6 or 7 ? -6 : 0;
            ParkProps.DrawBall(c, p, carrier.X + side, carrier.Y + 1, 4);
            return;
        }
        var t = Math.Clamp((time - ball.ThrownAt) / (float)ParkBall.FlightMilliseconds, 0, 1);
        var x = ball.From.X + (ball.To.X - ball.From.X) * t;
        var y = ball.From.Y + (ball.To.Y - ball.From.Y) * t;
        // A high arc, then two small bounces where it lands.
        var height = t < 1 ? MathF.Sin(t * MathF.PI) * 34 : MathF.Abs(MathF.Sin((time - ball.ThrownAt - ParkBall.FlightMilliseconds) / 120f)) * 4 * MathF.Max(0, 1 - (time - ball.ThrownAt - ParkBall.FlightMilliseconds) / 500f);
        ParkProps.DrawBall(c, p, x, y, height);
    }

    /// <summary>A point on the screen (0–1 of the view) in art pixels.</summary>
    public ParkPoint ScreenToMap(float normalizedX, float normalizedY)
    {
        var point = _camera.ToMap(normalizedX * _drawWidth, normalizedY * _drawHeight);
        return new(point.X, point.Y);
    }

    // Two light rings spread from the swimmer, one after the other.
    private static void DrawRipples(SKCanvas c, SKPaint p, float x, float y, long time)
    {
        for (var i = 0; i < 2; i++)
        {
            var t = (time / 1600f + i * .5f) % 1;
            var radius = 5 + MathF.Round(t * 5);
            p.Color = new SKColor(0xd8, 0xf0, 0xff, (byte)(200 * (1 - t)));
            c.DrawRect(MathF.Round(x - radius), MathF.Round(y), radius * 2, 1, p);
            c.DrawRect(MathF.Round(x - radius + 2), MathF.Round(y + 1), radius * 2 - 4, 1, p);
        }
    }

    /// <summary>A word over a Pokémon that has just done something (Options, Captions).</summary>
    public bool ShowCaptions { get; set; } = true;

    /// <summary>The park as a screensaver: no captions, no banners, only the park.</summary>
    public bool Screensaver { get; set; }

    private const long CaptionFor = 4000;

    // What a Pokémon just did, in a few words over its head for four seconds: the one you follow
    // first, then the newest, four at most (a group doing the same thing, once), nudged up so they
    // never cover each other. Not for one that cannot be seen (a ghost faded out, Kecleon blended in).
    private void DrawCaptions(SKCanvas c, ParkActor[] actors, float[] heads, ParkCamera camera, int screenWidth, int screenHeight, string? selectedId)
    {
        var now = _life.Now;
        var shown = Enumerable.Range(0, actors.Length)
            .Where(k => actors[k].Caption is not null && now - actors[k].CaptionSince is >= 0 and < CaptionFor &&
                !actors[k].Camouflaged && actors[k].OpacityAt(now) > .5f)
            .Select(k => (Actor: actors[k], Head: heads[k], X: (actors[k].X - camera.Left) * camera.Scale, Y: (heads[k] - camera.Top) * camera.Scale))
            .Where(a => a.X > -40 && a.X < screenWidth + 40 && a.Y > 0 && a.Y < screenHeight + 40)
            .OrderByDescending(a => a.Actor.Resident.Id == selectedId).ThenByDescending(a => a.Actor.CaptionSince)
            .DistinctBy(a => a.Actor.Caption).Take(4).ToArray();
        ParkCaptionsPaint.Draw(c, shown.Select(a => new ParkCaptionsPaint.Caption(a.Actor.Caption!, a.X, a.Y - 24 * camera.Scale,
            ParkCaptionsPaint.Alpha(now - a.Actor.CaptionSince, CaptionFor))), screenWidth, PixelFont.For);
    }

    // The name under the Pokémon you follow, in screen pixels (the same size at any zoom): white
    // on a translucent black pill, like the park's other labels.
    private static void DrawLabel(SKCanvas c, SKPaint p, string text, float x, float y, int screenWidth, int screenHeight)
    {
        var name = text.Length > 15 ? text[..14] + "…" : text;
        var size = MathF.Max(13, screenWidth / 88f);
        using var font = PixelFont.For(name, size);
        var width = font.MeasureFallback(name, p) + size;
        var height = size * 1.35f;
        var labelX = Math.Clamp(x, width / 2, screenWidth - width / 2);
        var top = Math.Min(screenHeight - height - 2, y + size * .5f);
        var filter = p.ColorFilter;
        p.ColorFilter = null;
        p.IsAntialias = true;
        p.Color = new SKColor(16, 22, 30, 165);
        c.DrawRoundRect(SKRect.Create(labelX - width / 2, top, width, height), height / 2, height / 2, p);
        p.Color = SKColors.White;
        c.DrawTextFallback(name, labelX, top + height * .76f, SKTextAlign.Center, font, p);
        p.IsAntialias = false;
        p.ColorFilter = filter;
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
}
