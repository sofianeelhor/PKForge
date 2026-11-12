using PKForge.Domain;
using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The Poképark home-screen widget as one pixel-art picture: a flat banded sky for the time of
/// day (sun, moon and stars, clouds or rain), rolling hills, grass with tufts and flowers, the
/// residents standing at their native sprite size, a slanted title tab and a cyan journal panel.
/// The scene is painted at low resolution and enlarged by a whole step with nearest sampling,
/// so every pixel stays square; only the text is drawn afterwards, at full size.
/// </summary>
public static class ParkWidgetPaint
{
    internal sealed record Palette(SKColor SkyTop, SKColor SkyLow, SKColor Far, SKColor Near, SKColor Grass, SKColor Tuft, SKColor Light);

    internal static Palette For(ParkDayPhase phase, ParkWeather weather)
    {
        var p = phase switch
        {
            ParkDayPhase.Morning => new Palette(new(0xf0, 0xa8, 0x90), new(0xf8, 0xd8, 0xa8), new(0x88, 0xb0, 0x78), new(0x60, 0xa0, 0x58), new(0x80, 0xc8, 0x58), new(0x58, 0xa0, 0x40), new(0xff, 0xf0, 0xa0)),
            ParkDayPhase.Evening => new Palette(new(0xb0, 0x60, 0x88), new(0xf0, 0x98, 0x60), new(0x70, 0x78, 0x70), new(0x50, 0x80, 0x48), new(0x68, 0xa8, 0x48), new(0x48, 0x80, 0x38), new(0xff, 0xc8, 0x60)),
            ParkDayPhase.Night => new Palette(new(0x18, 0x20, 0x48), new(0x30, 0x40, 0x78), new(0x28, 0x40, 0x48), new(0x20, 0x48, 0x38), new(0x30, 0x60, 0x40), new(0x20, 0x48, 0x30), new(0xf0, 0xf0, 0xd0)),
            _ => new Palette(new(0x60, 0xb8, 0xf0), new(0xa8, 0xe0, 0xf8), new(0x78, 0xb8, 0x80), new(0x58, 0xa8, 0x58), new(0x78, 0xc8, 0x50), new(0x50, 0xa0, 0x38), new(0xff, 0xe8, 0x50))
        };
        // Rain and storms grey the sky a step; the grass stays its own colour.
        return weather is ParkWeather.Rain or ParkWeather.Storm
            ? p with { SkyTop = Mix(p.SkyTop, new(0x60, 0x68, 0x78), .55f), SkyLow = Mix(p.SkyLow, new(0x88, 0x90, 0x98), .55f) }
            : p;
    }

    private static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t));

    internal static readonly SKColor PanelFill = new(0xe0, 0xf8, 0xf8), PanelRim = new(0x38, 0x98, 0xb0), PanelText = new(0x20, 0x40, 0x50);
    internal static readonly SKColor TabFill = new(0x28, 0x38, 0x68), TabRim = new(0x58, 0xd0, 0xe8), Ink = new(0x18, 0x20, 0x30);

    /// <summary>The whole step that brings a widget of that pixel size to a scene of about the wanted height.</summary>
    public static int StepFor(ParkWidgetSize size, int widthPx, int heightPx)
    {
        var target = size switch { ParkWidgetSize.Small => 80, ParkWidgetSize.Wide => 90, _ => 150 };
        return Math.Clamp((int)Math.Round(Math.Min(heightPx, widthPx * 1.5) / target), 1, 8);
    }

    /// <summary>Paints the widget into a new bitmap of <c>view.Width * step</c> by <c>view.Height * step</c>.</summary>
    public static SKBitmap Render(ParkWidgetView view, int step, Func<ParkWidgetResident, SKBitmap?> sprite, SKTypeface face)
    {
        using var scene = new SKBitmap(view.Width, view.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(scene)) Scene(c, view, sprite);
        var output = new SKBitmap(view.Width * step, view.Height * step, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        using var image = SKImage.FromBitmap(scene);
        canvas.DrawImage(image, SKRect.Create(0, 0, output.Width, output.Height), new SKSamplingOptions(SKFilterMode.Nearest), null);
        Words(canvas, view, step, face);
        return output;
    }

    private static void Rect(SKCanvas c, SKPaint p, SKColor color, int x, int y, int w, int h)
    {
        p.Color = color;
        c.DrawRect(x, y, w, h, p);
    }

    private static int PanelHeight(ParkWidgetView view) => view.Lines.Count * 9 + 4;

    private static void Scene(SKCanvas c, ParkWidgetView v, Func<ParkWidgetResident, SKBitmap?> sprite)
    {
        using var p = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        var pal = For(v.Phase, v.Weather);
        int w = v.Width, h = v.Height, horizon = v.HorizonY;

        // Sky: two flat bands meeting on a one-pixel checker row, never a gradient.
        var band = horizon / 2;
        Rect(c, p, pal.SkyTop, 0, 0, w, band);
        Rect(c, p, pal.SkyLow, 0, band, w, horizon - band);
        for (var x = 0; x < w; x += 2) Rect(c, p, pal.SkyTop, x, band, 1, 1);
        for (var x = 1; x < w; x += 4) Rect(c, p, pal.SkyTop, x, band + 1, 1, 1);

        var seed = v.Width * 31 + v.Height;
        if (v.Phase == ParkDayPhase.Night && v.Weather is not (ParkWeather.Rain or ParkWeather.Storm))
        {
            var rng = new Random(seed);
            for (var i = 0; i < w * horizon / 90; i++)
                Rect(c, p, i % 5 == 0 ? new SKColor(0xff, 0xf0, 0x90) : new SKColor(0xd8, 0xe0, 0xff), rng.Next(w), rng.Next(Math.Max(1, horizon - 6)), 1, 1);
        }
        if (v.Weather is not (ParkWeather.Rain or ParkWeather.Storm))
            Disc(c, p, pal.Light, w - 14, Math.Max(6, horizon / 3), v.Phase == ParkDayPhase.Night ? pal.SkyTop : (SKColor?)null);
        var clouds = v.Weather switch { ParkWeather.Sunny => 1, ParkWeather.Cloudy => 3, _ => 4 };
        var cloud = v.Phase == ParkDayPhase.Night ? new SKColor(0x58, 0x68, 0x98) : v.Weather is ParkWeather.Rain or ParkWeather.Storm ? new SKColor(0xb0, 0xb8, 0xc0) : SKColors.White;
        for (var i = 0; i < clouds; i++)
            Cloud(c, p, cloud, (i * 47 + 9) % Math.Max(1, w - 20) + 2, 3 + (i * 11) % Math.Max(1, horizon / 2));

        // Hills: two stepped ridges, the far one paler.
        for (var x = 0; x < w; x++)
        {
            var far = (int)(4 + 3 * Math.Sin(x / 9.0) + 2 * Math.Sin(x / 4.3 + 1));
            Rect(c, p, pal.Far, x, horizon - far, 1, far);
            var near = (int)(2 + 2 * Math.Sin(x / 13.0 + 2));
            Rect(c, p, pal.Near, x, horizon - near, 1, near);
        }
        // Grass, with tufts and a few flowers on a fixed pattern.
        Rect(c, p, pal.Grass, 0, horizon, w, h - horizon);
        var flowers = new[] { new SKColor(0xf8, 0x70, 0x80), new SKColor(0xf8, 0xf0, 0x60), SKColors.White };
        var r = new Random(seed + 7);
        for (var i = 0; i < w * (h - horizon) / 40; i++)
        {
            int x = r.Next(w), y = horizon + 2 + r.Next(Math.Max(1, h - horizon - 2));
            Rect(c, p, pal.Tuft, x, y, 1, 1); Rect(c, p, pal.Tuft, x + 2, y, 1, 1); Rect(c, p, pal.Tuft, x + 1, y - 1, 1, 1);
            if (i % 6 == 0 && v.Phase != ParkDayPhase.Night) Rect(c, p, flowers[i % 3], x + 1, y - 2, 1, 1);
        }

        // Residents, back row first, at their native size, feet on the grass.
        using var sp = new SKPaint { IsAntialias = false };
        foreach (var place in v.Placements.OrderBy(pl => pl.FeetY))
        {
            if (sprite(place.Resident) is not { } bmp) continue;
            using var img = SKImage.FromBitmap(bmp);
            var dest = SKRect.Create(place.FeetX - bmp.Width / 2, place.FeetY - bmp.Height, bmp.Width, bmp.Height);
            c.Save();
            if (place.FacingLeft) { c.Translate(dest.MidX * 2, 0); c.Scale(-1, 1); }
            c.DrawImage(img, dest, new SKSamplingOptions(SKFilterMode.Nearest), sp);
            c.Restore();
        }

        if (v.Weather is ParkWeather.Rain or ParkWeather.Storm)
        {
            var rain = new Random(seed + 3);
            for (var i = 0; i < w * h / 160; i++)
                Rect(c, p, new SKColor(0xc8, 0xe0, 0xf8, 0xc0), rain.Next(w), rain.Next(h), 1, 3);
        }

        // Night dims the scene by a flat veil, the same everywhere.
        if (v.Phase == ParkDayPhase.Night) Rect(c, p, new SKColor(0x10, 0x18, 0x40, 0x48), 0, horizon - 8, w, h - horizon + 8);

        // Journal panel: cyan with a one-pixel rim; the title tab: slanted, navy, cyan-edged.
        var ph = PanelHeight(v);
        Rect(c, p, PanelRim, 1, h - ph - 1, w - 2, ph);
        Rect(c, p, PanelFill, 2, h - ph, w - 4, ph - 2);
        Rect(c, p, Ink, 0, h - 1, w, 1);
        var tabWidth = v.Title.Length * 5 + 8;
        for (var y = 0; y < 11; y++)
        {
            var right = tabWidth - y / 2;
            Rect(c, p, y == 10 ? TabRim : TabFill, 0, y, right, 1);
            Rect(c, p, TabRim, right, y, 1, 1);
        }
    }

    internal static void Disc(SKCanvas c, SKPaint p, SKColor color, int cx, int cy, SKColor? bite)
    {
        int[] half = [2, 4, 5, 5, 5, 4, 2];
        for (var i = 0; i < half.Length; i++) Rect(c, p, color, cx - half[i], cy - 3 + i, half[i] * 2, 1);
        if (bite is { } b) // a crescent: the sky takes a bite out of the moon
            for (var i = 1; i < half.Length - 1; i++) Rect(c, p, b, cx - half[i] + 3, cy - 3 + i, half[i] * 2 - 3, 1);
    }

    internal static void Cloud(SKCanvas c, SKPaint p, SKColor color, int x, int y)
    {
        Rect(c, p, color, x + 4, y, 7, 1);
        Rect(c, p, color, x + 2, y + 1, 12, 2);
        Rect(c, p, color, x, y + 3, 18, 2);
    }

    private static void Words(SKCanvas c, ParkWidgetView v, int step, SKTypeface face)
    {
        using var font = new SKFont(face, 7 * step) { Edging = SKFontEdging.Alias, Hinting = SKFontHinting.None };
        using var paint = new SKPaint { IsAntialias = false, Color = SKColors.White };
        c.DrawTextFallback(v.Title, 3 * step, 8.5f * step, SKTextAlign.Left, font, paint);
        paint.Color = PanelText;
        var top = v.Height - PanelHeight(v) + 1;
        for (var i = 0; i < v.Lines.Count; i++)
        {
            var text = v.Lines[i];
            var max = (v.Width - 8) * step;
            while (text.Length > 2 && font.MeasureFallback(text) > max) text = text[..^2].TrimEnd() + "…";
            c.DrawTextFallback(text, 4 * step, (top + 8 + i * 9) * step, SKTextAlign.Left, font, paint);
        }
    }
}
