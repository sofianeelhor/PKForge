using PKForge.Domain;
using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>The park's own art the living widget stands on: a tree and a grass tuft cut from the park's map.</summary>
public sealed record LiveParkArt(SKBitmap? Tree, SKBitmap? Bush);

/// <summary>
/// The living Poképark widget's film (see <see cref="LivePark"/>): the sky of the hour above a line
/// of the park's own trees, the park's grass, and the residents walking, looking about, sitting and
/// sleeping in their PMD frames. Each frame is painted in scene pixels and enlarged by a whole step
/// with nearest sampling; only the words are drawn afterwards, at full size. Flat colours only.
/// </summary>
public static class LiveParkPaint
{
    /// <summary>The park map's grass.</summary>
    private static readonly SKColor Grass = new(42, 129, 75);
    private static readonly SKColor SnoozeInk = new(0xe8, 0xf0, 0xff);

    /// <summary>Paints the whole film: <paramref name="frames"/> bitmaps of <c>view.Width * step</c> by <c>view.Height * step</c>.</summary>
    /// <param name="sheet">A resident's live sheet (its PMD frames), or null.</param>
    /// <param name="standing">A resident's standing sprite, for one with no sheet.</param>
    public static List<SKBitmap> Render(LiveParkView view, int frames, int step, LiveParkArt art,
        Func<ParkWidgetResident, SKBitmap?> sheet, Func<ParkWidgetResident, SKBitmap?> standing, SKTypeface face, DateTime local)
    {
        frames = Math.Max(1, frames);
        step = Math.Max(1, step);
        var light = ParkSky.LightColor(view.Phase, local, view.Weather);
        using var backdrop = new SKBitmap(view.Width, view.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(backdrop)) Backdrop(c, view, art, light);
        using var backdropImage = SKImage.FromBitmap(backdrop);

        var output = new List<SKBitmap>(frames);
        using var scene = new SKBitmap(view.Width, view.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var lit = light == SKColors.White ? null : SKColorFilter.CreateBlendMode(light, SKBlendMode.Modulate);
        using var font = new SKFont(face, 7 * step) { Edging = SKFontEdging.Alias, Hinting = SKFontHinting.None };
        for (var f = 0; f < frames; f++)
        {
            using (var c = new SKCanvas(scene))
            {
                c.Clear(SKColors.Transparent);
                c.DrawImage(backdropImage, 0, 0);
                Actors(c, view, f, frames, sheet, standing, lit);
                Weather(c, view, f, frames, backdrop);
                Plates(c, view);
            }
            var bitmap = new SKBitmap(view.Width * step, view.Height * step, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            using (var image = SKImage.FromBitmap(scene))
            {
                canvas.DrawImage(image, SKRect.Create(0, 0, bitmap.Width, bitmap.Height), new SKSamplingOptions(SKFilterMode.Nearest), null);
                Words(canvas, view, step, font);
            }
            output.Add(bitmap);
        }
        return output;
    }

    private static void Rect(SKCanvas c, SKPaint p, SKColor color, int x, int y, int w, int h)
    {
        p.Color = color;
        c.DrawRect(x, y, w, h, p);
    }

    /// <summary>The trees' feet, left to right: a gap of sky between most of them, the same for a widget size.</summary>
    internal static List<(int X, bool Mirror)> Trees(LiveParkView v, int treeWidth)
    {
        var list = new List<(int, bool)>();
        var rng = new Random(v.Seed);
        var x = -treeWidth / 3 + rng.Next(8);
        while (x < v.Width + treeWidth / 2)
        {
            list.Add((x, rng.Next(2) == 0));
            x += treeWidth + 10 + rng.Next(26);
        }
        return list;
    }

    private static void Backdrop(SKCanvas c, LiveParkView v, LiveParkArt art, SKColor light)
    {
        using var p = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        var pal = ParkWidgetPaint.For(v.Phase, v.Weather);
        int w = v.Width, h = v.Height, horizon = v.HorizonY;
        var rain = v.Weather is ParkWeather.Rain or ParkWeather.Storm;

        // The sky of the hour: two flat bands on a one-pixel checker row, never a gradient.
        var band = Math.Max(4, horizon / 2);
        Rect(c, p, pal.SkyTop, 0, 0, w, band);
        Rect(c, p, pal.SkyLow, 0, band, w, horizon - band + 2);
        for (var x = 0; x < w; x += 2) Rect(c, p, pal.SkyTop, x, band, 1, 1);
        for (var x = 1; x < w; x += 4) Rect(c, p, pal.SkyTop, x, band + 1, 1, 1);

        var trees = art.Tree is { } tree ? Trees(v, tree.Width) : [];
        // The sun or the moon in the widest gap between two crowns, so a tree never hides it.
        var sunX = w - 14;
        if (art.Tree is { } t && trees.Count > 1)
        {
            var best = -1;
            for (var i = 0; i + 1 < trees.Count; i++)
            {
                var gap = trees[i + 1].X - trees[i].X - t.Width;
                var mid = trees[i].X + t.Width + gap / 2;
                if (gap > best && mid > 8 && mid < w - 8) { best = gap; sunX = mid; }
            }
        }
        if (v.Phase == ParkDayPhase.Night && !rain)
        {
            var stars = new Random(v.Seed + 1);
            for (var i = 0; i < w * horizon / 70; i++)
                Rect(c, p, i % 5 == 0 ? new SKColor(0xff, 0xf0, 0x90) : new SKColor(0xd8, 0xe0, 0xff), stars.Next(w), stars.Next(Math.Max(1, horizon - 4)), 1, 1);
        }
        if (!rain) ParkWidgetPaint.Disc(c, p, pal.Light, sunX, Math.Clamp(horizon / 3, 5, 14), v.Phase == ParkDayPhase.Night ? pal.SkyTop : null);
        var clouds = v.Weather switch { ParkWeather.Sunny => 1, ParkWeather.Cloudy => 3, _ => 4 };
        var cloud = v.Phase == ParkDayPhase.Night ? new SKColor(0x58, 0x68, 0x98) : rain ? new SKColor(0xb0, 0xb8, 0xc0) : SKColors.White;
        for (var i = 0; i < clouds; i++)
            ParkWidgetPaint.Cloud(c, p, cloud, (i * 53 + 11) % Math.Max(1, w - 20) + 2, 2 + (i * 7) % Math.Max(1, horizon / 3));

        // The park's ground and trees, in the light of the hour, as the park itself tints them.
        using var lit = light == SKColors.White ? null : SKColorFilter.CreateBlendMode(light, SKBlendMode.Modulate);
        using var art2 = new SKPaint { IsAntialias = false, ColorFilter = lit };
        Rect(c, art2, Grass, 0, horizon, w, h - horizon);
        art2.Color = SKColors.White;
        if (art.Bush is { } bush)
        {
            using var bushImage = SKImage.FromBitmap(bush);
            // A few tufts, spread along the back of the lawn, so the residents stand clear in front of them.
            var tufts = new Random(v.Seed + 7);
            var count = Math.Max(2, w / 40);
            var share = w / count;
            var depth = Math.Max(1, (h - LivePark.PanelHeight(v.Lines.Count) - horizon) / 3);
            for (var i = 0; i < count; i++)
            {
                var x = i * share + tufts.Next(Math.Max(1, share - bush.Width));
                var y = horizon + 4 + tufts.Next(depth);
                c.DrawImage(bushImage, x, y, new SKSamplingOptions(SKFilterMode.Nearest), art2);
            }
        }
        if (art.Tree is { } treeArt)
        {
            using var image = SKImage.FromBitmap(treeArt);
            foreach (var (x, mirror) in trees)
            {
                var dest = SKRect.Create(x, horizon + 3 - treeArt.Height, treeArt.Width, treeArt.Height);
                c.Save();
                if (mirror) { c.Translate(dest.MidX * 2, 0); c.Scale(-1, 1); }
                c.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Nearest), art2);
                c.Restore();
            }
        }
    }

    private static void Actors(SKCanvas c, LiveParkView v, int frame, int frames, Func<ParkWidgetResident, SKBitmap?> sheet,
        Func<ParkWidgetResident, SKBitmap?> standing, SKColorFilter? lit)
    {
        using var p = new SKPaint { IsAntialias = false, ColorFilter = lit };
        using var zz = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        var loopMs = frames * LivePark.IntervalMs;
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest);
        var poses = v.Actors.Select(a => (Actor: a, Pose: LivePark.Pose(a, frame, frames))).OrderBy(x => x.Pose.FeetY).ThenBy(x => x.Pose.X);
        foreach (var (actor, pose) in poses)
        {
            var scale = Math.Clamp(actor.Resident.Scale, .25f, 1f);
            int top;
            if (LivePark.Resolve(actor.Resident.Clips, pose.Clip) is { } clip && sheet(actor.Resident) is { } bitmap)
            {
                var f = LivePark.FrameAt(clip, pose.Phase, loopMs);
                int dw = Math.Max(1, (int)MathF.Round(f.Width * scale)), dh = Math.Max(1, (int)MathF.Round(f.Height * scale));
                var left = pose.X - (int)MathF.Round(f.FeetX * scale);
                top = pose.FeetY - (int)MathF.Round(f.FeetY * scale);
                using var image = SKImage.FromBitmap(bitmap);
                c.DrawImage(image, SKRect.Create(f.X, f.Y, f.Width, f.Height), SKRect.Create(left, top, dw, dh), sampling, p);
            }
            else if (standing(actor.Resident) is { } still)
            {
                int dw = Math.Max(1, (int)MathF.Round(still.Width * scale)), dh = Math.Max(1, (int)MathF.Round(still.Height * scale));
                top = pose.FeetY - dh;
                using var image = SKImage.FromBitmap(still);
                c.Save();
                var dest = SKRect.Create(pose.X - dw / 2, top, dw, dh);
                if (!actor.Rightward) { c.Translate(dest.MidX * 2, 0); c.Scale(-1, 1); }
                c.DrawImage(image, dest, sampling, p);
                c.Restore();
            }
            else continue;
            if (pose.Asleep) Snooze(c, zz, pose.X + 5, top, frame, frames);
        }
    }

    /// <summary>Two little z's rising over a sleeper, one after the other, looping with the film.</summary>
    private static void Snooze(SKCanvas c, SKPaint p, int x, int top, int frame, int frames)
    {
        for (var k = 0; k < 2; k++)
        {
            var rise = (frame * 8 / frames + k * 4) % 8;
            int zx = x + k * 5, zy = top - 3 - rise;
            Rect(c, p, SnoozeInk, zx, zy, 4, 1);
            Rect(c, p, SnoozeInk, zx + 2, zy + 1, 1, 1);
            Rect(c, p, SnoozeInk, zx + 1, zy + 2, 1, 1);
            Rect(c, p, SnoozeInk, zx, zy + 3, 4, 1);
        }
    }

    /// <summary>Rain falls a whole sky per loop, so the last frame meets the first; stars twinkle at night.</summary>
    private static void Weather(SKCanvas c, LiveParkView v, int frame, int frames, SKBitmap backdrop)
    {
        using var p = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        int w = v.Width, h = v.Height;
        if (v.Weather is ParkWeather.Rain or ParkWeather.Storm)
        {
            var drops = new Random(v.Seed + 3);
            var fall = frame * h / frames;
            for (var i = 0; i < w * h / 150; i++)
            {
                int x = drops.Next(w), y = (drops.Next(h) + fall) % h;
                Rect(c, p, new SKColor(0xc8, 0xe0, 0xf8, 0xc0), x, y, 1, 3);
            }
        }
        else if (v.Phase == ParkDayPhase.Night)
        {
            // A few stars blink in turn, in the open sky only.
            var pal = ParkWidgetPaint.For(v.Phase, v.Weather);
            var stars = new Random(v.Seed + 11);
            for (var i = 0; i < Math.Max(3, w / 24); i++)
            {
                int x = stars.Next(w), y = stars.Next(Math.Max(1, v.HorizonY - 12));
                var under = backdrop.GetPixel(x, y);
                if ((frame + i) % 4 == 0 && (under == pal.SkyTop || under == pal.SkyLow)) Rect(c, p, new SKColor(0xff, 0xf8, 0xc0), x, y, 1, 1);
            }
        }
    }

    /// <summary>The slanted title tab, and on the 4x4 the cyan journal panel.</summary>
    private static void Plates(SKCanvas c, LiveParkView v)
    {
        using var p = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        int w = v.Width, h = v.Height;
        var ph = LivePark.PanelHeight(v.Lines.Count);
        if (ph > 0)
        {
            Rect(c, p, ParkWidgetPaint.PanelRim, 1, h - ph - 1, w - 2, ph);
            Rect(c, p, ParkWidgetPaint.PanelFill, 2, h - ph, w - 4, ph - 2);
            Rect(c, p, ParkWidgetPaint.Ink, 0, h - 1, w, 1);
        }
        var tabWidth = v.Title.Length * 5 + 8;
        for (var y = 0; y < 11; y++)
        {
            var right = tabWidth - y / 2;
            Rect(c, p, y == 10 ? ParkWidgetPaint.TabRim : ParkWidgetPaint.TabFill, 0, y, right, 1);
            Rect(c, p, ParkWidgetPaint.TabRim, right, y, 1, 1);
        }
    }

    private static void Words(SKCanvas c, LiveParkView v, int step, SKFont font)
    {
        using var paint = new SKPaint { IsAntialias = false, Color = SKColors.White };
        c.DrawTextFallback(v.Title, 3 * step, 8.5f * step, SKTextAlign.Left, font, paint);
        paint.Color = ParkWidgetPaint.PanelText;
        var top = v.Height - LivePark.PanelHeight(v.Lines.Count) + 1;
        for (var i = 0; i < v.Lines.Count; i++)
        {
            var text = v.Lines[i];
            var max = (v.Width - 8) * step;
            while (text.Length > 2 && font.MeasureFallback(text) > max) text = text[..^2].TrimEnd() + "…";
            c.DrawTextFallback(text, 4 * step, (top + 8 + i * 9) * step, SKTextAlign.Left, font, paint);
        }
    }
}
