using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The Living Dex Autopilot's route map: every cartridge the plan reads, the destination
/// in front, a dashed route from each source that gives Pokémon, and the Pokémon icons
/// travelling along those routes while the dry run stages and the apply writes. The full
/// layout is the Thor's lower screen; <see cref="Compact"/> is the single-screen strip
/// above the plan list. Pure view: it only draws the <see cref="LivingDexRoute"/> it is given.
/// </summary>
public sealed class LivingDexRouteMap : SKCanvasView
{
    private const int MaxVisibleSources = 7;
    private const double FlightMs = 720;

    private readonly ISpriteService? _sprites;
    private readonly Queue<RouteTraveler> _queue = new();
    private readonly List<(RouteTraveler Traveler, double StartMs)> _flying = [];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private IDispatcherTimer? _timer;
    private LivingDexRoute? _route;
    private long _sequence = -1;
    private double _lastLaunchMs;
    private readonly Dictionary<string, int> _landed = new(StringComparer.Ordinal);

    public LivingDexRouteMap(ISpriteService? sprites, bool compact)
    {
        _sprites = sprites;
        Compact = compact;
        EnableTouchEvents = false;
        InputTransparent = true;
        PaintSurface += Paint;
        Unloaded += (_, _) => Stop();
    }

    public bool Compact { get; }

    /// <summary>Shows a route; a newer <see cref="LivingDexRoute.Sequence"/> launches its travelers.</summary>
    public void Show(LivingDexRoute? route)
    {
        if (route is null || route.Sequence < _sequence || _route?.DestinationId != route.DestinationId)
        {
            _queue.Clear();
            _flying.Clear();
            _landed.Clear();
        }
        _route = route;
        if (route is not null && route.Sequence > _sequence)
        {
            _sequence = route.Sequence;
            foreach (var traveler in route.Travelers)
            {
                _queue.Enqueue(traveler);
                _sprites?.Warm(traveler.Species, traveler.Form, traveler.Shiny, () => MainThread.BeginInvokeOnMainThread(InvalidateSurface));
            }
        }
        if (_queue.Count > 0 || _flying.Count > 0) Start();
        InvalidateSurface();
    }

    private void Start()
    {
        if (_timer is not null) return;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void Tick()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        // A long queue launches faster, so a 700-step dry run still reads as a stream, not a wait.
        var gap = Math.Max(45, 260 - _queue.Count * 12);
        if (_queue.Count > 0 && now - _lastLaunchMs >= gap && _flying.Count < 14)
        {
            _flying.Add((_queue.Dequeue(), now));
            _lastLaunchMs = now;
        }
        for (var i = _flying.Count - 1; i >= 0; i--)
        {
            if (now - _flying[i].StartMs < FlightMs) continue;
            var to = _flying[i].Traveler.ToId;
            _landed[to] = _landed.GetValueOrDefault(to) + 1;
            _flying.RemoveAt(i);
        }
        if (_queue.Count == 0 && _flying.Count == 0) Stop();
        InvalidateSurface();
    }

    // ── Painting ────────────────────────────────────────────────────────────

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        var info = args.Info;
        c.Clear(SKColors.Transparent);
        var route = _route;
        if (route is null || route.Carts.Count == 0) return;
        var unit = Compact ? info.Height / 110f : Math.Min(info.Width / 360f, info.Height / 300f);

        using var title = new SKFont(PixelFont.Face, 13 * unit) { Edging = SKFontEdging.Antialias, Embolden = true };
        using var small = new SKFont(PixelFont.Face, 10.5f * unit) { Edging = SKFontEdging.Antialias };
        using var count = new SKFont(PixelFont.Face, 12 * unit) { Edging = SKFontEdging.Antialias, Embolden = true };

        var area = new SKRect(0, 0, info.Width, info.Height);
        if (!Compact)
        {
            var bar = new SKRect(0, 0, info.Width, 30 * unit);
            PksmPaint.BoxNameBar(c, bar, route.Caption, title, false, false);
            var footer = new SKRect(0, info.Height - 26 * unit, info.Width, info.Height);
            PksmPaint.Panel(c, footer, Pksm.Paper, 4 * unit);
            PksmPaint.CenterText(c, route.Progress, footer.MidX, footer.MidY, small, Pksm.Ink, SKColors.Transparent, SKTextAlign.Center);
            area = new SKRect(8 * unit, bar.Bottom + 6 * unit, info.Width - 8 * unit, footer.Top - 6 * unit);
        }

        var dest = route.Carts.FirstOrDefault(cart => cart.Id == route.DestinationId) ?? route.Carts[0];
        var sources = route.Carts.Where(cart => cart.Id != dest.Id).ToList();
        var shown = sources.Take(MaxVisibleSources).ToList();
        var hiddenCount = sources.Count - shown.Count;

        // Layout: sources along the top (full) or the left (compact), the destination facing them.
        var positions = new Dictionary<string, SKRect>(StringComparer.Ordinal);
        SKRect destRect;
        if (Compact)
        {
            var cartH = area.Height * 0.62f;
            var cartW = cartH * 0.92f;
            destRect = new SKRect(area.Right - cartW * 1.25f, area.MidY - cartH * 0.62f, area.Right - 6 * unit, area.MidY + cartH * 0.62f);
            var span = destRect.Left - area.Left - 30 * unit;
            var step = shown.Count == 0 ? 0 : Math.Min(cartW * 1.25f, span / shown.Count);
            for (var i = 0; i < shown.Count; i++)
            {
                var left = area.Left + 4 * unit + i * step;
                positions[shown[i].Id] = new SKRect(left, area.MidY - cartH / 2, left + Math.Min(cartW, step - 4 * unit), area.MidY + cartH / 2);
            }
        }
        else
        {
            var cartW = Math.Min(64 * unit, area.Width / Math.Max(4, shown.Count + 0.6f));
            var cartH = cartW * 1.1f;
            // The destination sits above its own name line, clear of the footer.
            var destW = 100 * unit;
            var destBottom = area.Bottom - small.Size - 8 * unit;
            destRect = new SKRect(area.MidX - destW / 2, destBottom - destW * 1.05f, area.MidX + destW / 2, destBottom);
            for (var i = 0; i < shown.Count; i++)
            {
                // A gentle arc: the outer cartridges sit lower, all of them face the destination.
                var t = shown.Count == 1 ? 0.5f : i / (float)(shown.Count - 1);
                var x = area.Left + cartW * 0.6f + t * (area.Width - cartW * 1.2f);
                var y = area.Top + 10 * unit + MathF.Pow(2 * t - 1, 2) * cartH * 0.22f;
                positions[shown[i].Id] = new SKRect(x - cartW / 2, y, x + cartW / 2, y + cartH);
            }
        }
        positions[dest.Id] = destRect;

        // Routes first, under the cartridges.
        foreach (var cart in shown.Where(s => s.Out > 0 && !s.Excluded))
            DrawRoute(c, positions[cart.Id], destRect, unit, route.Running);

        foreach (var cart in shown)
            DrawCart(c, positions[cart.Id], cart, unit, small, count, focus: false,
                cart.Out > 0 ? $"-{cart.Out}" : null);
        var landed = _landed.GetValueOrDefault(dest.Id);
        DrawCart(c, destRect, dest, unit, small, count, focus: true,
            dest.In > 0 ? route.Running || route.Done ? $"+{Math.Min(landed, dest.In)}/{dest.In}" : $"+{dest.In}" : null);
        if (hiddenCount > 0)
        {
            var anchor = Compact ? new SKPoint(destRect.Left - 22 * unit, area.Bottom - 6 * unit) : new SKPoint(area.Right - 4 * unit, area.Top + 4 * unit);
            PksmPaint.ShadowText(c, $"+{hiddenCount} more", anchor.X, anchor.Y + small.Size, small, Pksm.InkSoft, SKColors.Transparent, SKTextAlign.Right);
        }
        if (route.Done)
            DrawDone(c, destRect, unit);

        // Travelers on top of everything.
        var now = _clock.Elapsed.TotalMilliseconds;
        foreach (var (traveler, start) in _flying)
        {
            if (!positions.TryGetValue(traveler.FromId, out var from)) from = Compact
                ? new SKRect(area.Left, area.MidY - 10, area.Left + 20, area.MidY + 10)
                : new SKRect(area.Right - 20, area.Top, area.Right, area.Top + 20);
            var t = (float)Math.Clamp((now - start) / FlightMs, 0, 1);
            var eased = t * t * (3 - 2 * t);
            var point = RoutePoint(from, destRect, eased);
            var size = (Compact ? 30 : 40) * unit * (1 + 0.18f * MathF.Sin(t * MathF.PI));
            DrawSprite(c, traveler, point, size);
            if (traveler.Evolving && t is > 0.4f and < 0.75f)
                PksmPaint.Sparkle(c, new SKPoint(point.X + size * 0.35f, point.Y - size * 0.35f), 7 * unit);
        }
    }

    /// <summary>The route from a source's bottom (or right) edge into the destination's top: a quadratic curve.</summary>
    private SKPoint RoutePoint(SKRect from, SKRect to, float t)
    {
        SKPoint a, b, control;
        if (Compact)
        {
            a = new SKPoint(from.MidX, from.MidY);
            b = new SKPoint(to.Left + to.Width * 0.3f, to.MidY);
            control = new SKPoint((a.X + b.X) / 2, Math.Min(a.Y, b.Y) - to.Height * 0.45f);
        }
        else
        {
            a = new SKPoint(from.MidX, from.Bottom);
            b = new SKPoint(to.MidX, to.Top + to.Height * 0.2f);
            control = new SKPoint((a.X + b.X) / 2, a.Y + (b.Y - a.Y) * 0.15f);
        }
        var u = 1 - t;
        return new SKPoint(u * u * a.X + 2 * u * t * control.X + t * t * b.X, u * u * a.Y + 2 * u * t * control.Y + t * t * b.Y);
    }

    private void DrawRoute(SKCanvas c, SKRect from, SKRect to, float unit, bool running)
    {
        using var path = new SKPath();
        var first = RoutePoint(from, to, 0);
        path.MoveTo(first);
        for (var i = 1; i <= 24; i++) path.LineTo(RoutePoint(from, to, i / 24f));
        // The dashes march while the autopilot runs: the route is live.
        var phase = running ? (float)(_clock.Elapsed.TotalMilliseconds / 40 % 16) : 0;
        using var under = new SKPaint { Color = Pksm.Paper, Style = SKPaintStyle.Stroke, StrokeWidth = 6 * unit, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        using var dash = new SKPaint
        {
            Color = Pksm.SelectBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 3 * unit, IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([8 * unit, 8 * unit], -phase * unit),
        };
        c.DrawPath(path, under);
        c.DrawPath(path, dash);
        if (running) InvalidateLater();
    }

    private bool _repaintQueued;

    /// <summary>Keeps the marching dashes moving while running, even between travelers.</summary>
    private void InvalidateLater()
    {
        if (_repaintQueued || _timer is not null) return;
        _repaintQueued = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
        {
            _repaintQueued = false;
            InvalidateSurface();
        });
    }

    /// <summary>The PKForge cartridge mark: logo deck, era stripe, the save's name and its count badge.</summary>
    private void DrawCart(SKCanvas c, SKRect r, RouteCart cart, float unit, SKFont small, SKFont count, bool focus, string? badge)
    {
        var alpha = (byte)(cart.Excluded ? 0x70 : 0xFF);
        using var shadow = new SKPaint { Color = Pksm.LogoVoid.WithAlpha((byte)(alpha * 0.8f)), IsAntialias = true };
        using var deck = new SKPaint { Color = (cart.IsBank ? Pksm.StorageMenuBlueDeep : Pksm.LogoDeck).WithAlpha(alpha), IsAntialias = true };
        using var edge = new SKPaint
        {
            Color = (focus ? Pksm.ShinyGold : Pksm.LogoGrid).WithAlpha(alpha), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = (focus ? 3.5f : 2.5f) * unit,
        };
        var radius = 6 * unit;
        c.DrawRoundRect(new SKRect(r.Left + 3 * unit, r.Top + 3 * unit, r.Right + 3 * unit, r.Bottom + 3 * unit), radius, radius, shadow);
        c.DrawRoundRect(r, radius, radius, deck);
        c.DrawRoundRect(r, radius, radius, edge);

        var era = cart.IsBank ? Pksm.ButtonBlue : SaveColors.For(cart.ColorKey, cart.Generation).ToSKColor();
        using var stripe = new SKPaint { Color = era.WithAlpha(alpha), IsAntialias = true };
        var label = new SKRect(r.Left + r.Width * 0.12f, r.Top + r.Height * 0.12f, r.Right - r.Width * 0.12f, r.Top + r.Height * 0.3f);
        c.DrawRoundRect(label, 2 * unit, 2 * unit, stripe);

        // The game's own cartridge art under the sticker (the Home shelf's icons), else the
        // generation-colored mark; the Bank shows its vault.
        var face = Math.Min(r.Width * 0.72f, r.Bottom - label.Bottom - r.Height * 0.1f);
        var art = new SKRect(r.MidX - face / 2, label.Bottom + r.Height * 0.05f, r.MidX + face / 2, label.Bottom + r.Height * 0.05f + face);
        AutopilotArt.DrawGame(c, art, cart.ArtLabel, cart.Generation, cart.ColorKey, cart.IsBank, InvalidateSurface, alpha);

        // The era on the label stripe, like a cartridge sticker.
        var generation = cart.IsBank ? "BANK" : cart.Generation is >= 1 and <= 9 ? $"GEN {cart.Generation}" : "SAVE";
        using (var sticker = new SKFont(small.Typeface, Math.Min(small.Size, label.Height * 0.8f)) { Edging = SKFontEdging.Antialias, Embolden = true })
        using (var ink = new SKPaint { Color = SKColors.White.WithAlpha(alpha), IsAntialias = true })
        {
            var text = Trim(generation, sticker, label.Width - 4 * unit);
            sticker.MeasureText(text, out var bounds);
            c.DrawText(text, label.MidX, label.MidY - bounds.MidY, SKTextAlign.Center, sticker, ink);
        }

        // The save's name under its cartridge, trimmed to the cartridge's width (neighbours never overlap).
        var name = Trim(cart.Label, small, r.Width * (focus ? 2.2f : 1.12f));
        PksmPaint.ShadowText(c, name, r.MidX, r.Bottom + small.Size + 3 * unit, small, Pksm.Ink.WithAlpha(alpha), SKColors.Transparent, SKTextAlign.Center);

        if (cart.Excluded)
        {
            using var strike = new SKPaint { Color = Pksm.Illegal, StrokeWidth = 2.5f * unit, IsAntialias = true, Style = SKPaintStyle.Stroke };
            c.DrawLine(r.Left + 4 * unit, r.Bottom - 4 * unit, r.Right - 4 * unit, r.Top + 4 * unit, strike);
        }
        if (badge is not null)
        {
            var width = count.MeasureText(badge) + 10 * unit;
            var pill = new SKRect(r.MidX - width / 2, r.Top - count.Size * 0.9f, r.MidX + width / 2, r.Top + count.Size * 0.45f);
            using var fill = new SKPaint { Color = focus ? Pksm.Legal : Pksm.Paper, IsAntialias = true };
            using var rim = new SKPaint { Color = Pksm.PaperEdge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * unit };
            c.DrawRoundRect(pill, pill.Height / 2, pill.Height / 2, fill);
            c.DrawRoundRect(pill, pill.Height / 2, pill.Height / 2, rim);
            PksmPaint.CenterText(c, badge, pill.MidX, pill.MidY, count, focus ? Pksm.Paper : Pksm.Ink, SKColors.Transparent, SKTextAlign.Center);
        }
    }

    private static void DrawDone(SKCanvas c, SKRect r, float unit)
    {
        var center = new SKPoint(r.Right - 4 * unit, r.Top + 4 * unit);
        using var fill = new SKPaint { Color = Pksm.Legal, IsAntialias = true };
        using var mark = new SKPaint { Color = Pksm.Paper, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 * unit, StrokeCap = SKStrokeCap.Round };
        var radius = 11 * unit;
        c.DrawCircle(center, radius, fill);
        using var path = new SKPath();
        path.MoveTo(center.X - radius * 0.45f, center.Y);
        path.LineTo(center.X - radius * 0.1f, center.Y + radius * 0.38f);
        path.LineTo(center.X + radius * 0.5f, center.Y - radius * 0.35f);
        c.DrawPath(path, mark);
    }

    private void DrawSprite(SKCanvas c, RouteTraveler traveler, SKPoint center, float size)
    {
        var sprite = _sprites?.GetSprite(traveler.Species, traveler.Form, traveler.Shiny);
        if (sprite is null)
        {
            GameCartridgeMark.DrawBall(c, center, size * 0.22f, Pksm.CursorRed, Pksm.Paper);
            return;
        }
        var scale = size / Math.Max(sprite.Width, sprite.Height);
        var w = sprite.Width * scale;
        var h = sprite.Height * scale;
        using var image = SKImage.FromBitmap(sprite);
        c.DrawImage(image, new SKRect(center.X - w / 2, center.Y - h / 2, center.X + w / 2, center.Y + h / 2), BoxGridRenderer.SpriteSampling);
        if (traveler.Shiny) PksmPaint.Sparkle(c, new SKPoint(center.X + w * 0.38f, center.Y - h * 0.3f), size * 0.12f);
    }

    private static string Trim(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width) return text;
        while (text.Length > 1 && font.MeasureText(text + "…") > width) text = text[..^1];
        return text + "…";
    }
}
