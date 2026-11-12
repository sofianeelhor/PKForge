using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The shapes and ink the painted menus share (the bag, Save data): slanted plates and tabs,
/// the cursor chip, pixel text that fits, wraps and centres. Sizes are design pixels.
/// </summary>
internal static class PaintKit
{
    internal static ColorTheme T => ColorTheme.Current;

    /// <summary>The summary's slanted icon tab strip; <paramref name="active"/> stands out.</summary>
    internal static List<SKRect> IconTabs(SKCanvas c, SKRect r, IReadOnlyList<SKImage?> icons, int active)
    {
        var hits = new List<SKRect>();
        var n = Math.Max(1, icons.Count);
        const float slant = 26f;
        var step = Math.Min(140f, (r.Width - slant - 8f) / n);
        var left = r.Left + 4f;
        var top = r.Top + 10f;
        var bottom = r.Bottom - 4f;
        using (var strip = Parallelogram(new SKRect(left, top, left + step * n + slant, bottom), slant))
        {
            Gradient(c, strip, T.TabTop, T.TabBottom);
            Outline(c, strip, T.Rim, 4f);
        }
        using (var edge = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f })
            for (var i = 1; i < n; i++)
            {
                var x = left + i * step;
                c.DrawLine(x + slant, top, x, bottom, edge);
            }
        var ax = left + active * step;
        using (var lit = Parallelogram(new SKRect(ax - 6f, top - 6f, ax + step + slant + 2f, bottom + 2f), slant))
        {
            Gradient(c, lit, T.TabActiveTop, T.TabActiveBottom);
            Outline(c, lit, Pksm.Ink, 4f);
        }
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        using var tint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(T.TabIcon, SKBlendMode.SrcIn) };
        for (var i = 0; i < icons.Count; i++)
        {
            var cx = left + i * step + (step + slant) / 2;
            var cy = (top + bottom) / 2;
            hits.Add(new SKRect(left + i * step, r.Top, left + (i + 1) * step + slant / 2, r.Bottom));
            if (icons[i] is not { } icon) continue;
            var size = Math.Min(48f, step - 40f);
            c.DrawImage(icon, Fit(icon, new SKRect(cx - size / 2, cy - size / 2, cx + size / 2, cy + size / 2)), sampling, tint);
        }
        return hits;
    }


    /// <summary>The cursor: the section chip's slanted plate with its cyan rim.</summary>
    internal static void Chip(SKCanvas c, SKRect r, bool focused)
    {
        using var path = Parallelogram(r, 18f);
        Gradient(c, path, T.ChipTop, T.ChipBottom);
        Outline(c, path, focused ? T.Rim : T.Rim.WithAlpha(110), focused ? 4f : 3f);
    }

    internal static void Arrow(SKCanvas c, float cx, float cy, int direction, SKColor color)
    {
        using var path = new SKPath();
        const float s = 13f;
        path.MoveTo(cx - direction * s * 0.7f, cy - s);
        path.LineTo(cx + direction * s * 0.8f, cy);
        path.LineTo(cx - direction * s * 0.7f, cy + s);
        path.Close();
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawPath(path, paint);
    }

    internal static SKRect Fit(SKImage image, SKRect box, float maxScale = float.MaxValue)
    {
        // Proportions kept, and whole steps either way (×2, ×3 or ÷2, ÷3): every source pixel
        // lands on the same number of screen pixels, so the pixel art stays even and crisp.
        var scale = Math.Min(box.Width / image.Width, box.Height / image.Height);
        scale = scale >= 1f ? MathF.Floor(MathF.Min(scale, maxScale)) : 1f / MathF.Ceiling(1f / scale);
        var w = image.Width * scale;
        var h = image.Height * scale;
        return new SKRect(box.MidX - w / 2, box.MidY - h / 2, box.MidX + w / 2, box.MidY + h / 2);
    }

    internal static SKPath Parallelogram(SKRect r, float slant)
    {
        var path = new SKPath();
        path.MoveTo(r.Left + slant, r.Top);
        path.LineTo(r.Right, r.Top);
        path.LineTo(r.Right - slant, r.Bottom);
        path.LineTo(r.Left, r.Bottom);
        path.Close();
        return path;
    }

    /// <summary>A tab: square on the left, its right edge slanting out toward the bottom.</summary>
    internal static SKPath Tab(SKRect r, float radius, float slant)
    {
        var path = new SKPath();
        path.MoveTo(r.Left + radius, r.Top);
        path.LineTo(r.Right - slant, r.Top);
        path.LineTo(r.Right, r.Bottom);
        path.LineTo(r.Left, r.Bottom);
        path.Close();
        return path;
    }

    internal static void Gradient(SKCanvas c, SKPath path, SKColor top, SKColor bottom)
    {
        var bounds = path.Bounds;
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, bounds.Top), new SKPoint(0, bounds.Bottom), [top, bottom], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        c.DrawPath(path, paint);
    }

    internal static void Outline(SKCanvas c, SKPath path, SKColor color, float width)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeJoin = SKStrokeJoin.Round };
        c.DrawPath(path, paint);
    }

    internal static float Baseline(float mid, SKFont font)
    {
        font.GetFontMetrics(out var m);
        return mid - (m.Ascent + m.Descent) / 2;
    }

    internal static string Fitted(string text, SKFont font, float max)
    {
        if (max <= 0 || font.MeasureFallback(text) <= max) return text;
        while (text.Length > 1 && font.MeasureFallback(text + "…") > max) text = text[..^1];
        return text.TrimEnd() + "…";
    }

    /// <summary>Breaks <paramref name="text"/> into lines no wider than <paramref name="max"/>.</summary>
    internal static IEnumerable<string> Wrap(string text, SKFont font, float max)
    {
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && font.MeasureFallback(next) > max)
            {
                yield return line;
                line = word;
            }
            else line = next;
        }
        if (line.Length > 0) yield return line;
    }

    internal static void Text(SKCanvas c, string text, float x, float mid, SKFont font, SKColor color, float max)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = false };
        c.DrawTextFallback(Fitted(text, font, max), x, Baseline(mid, font), SKTextAlign.Left, font, paint);
    }

    internal static void Right(SKCanvas c, string text, float x, float mid, SKFont font, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = false };
        c.DrawTextFallback(text, x, Baseline(mid, font), SKTextAlign.Right, font, paint);
    }

    internal static void Centered(SKCanvas c, string text, float x, float mid, SKFont font, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = false };
        c.DrawTextFallback(text, x, Baseline(mid, font), SKTextAlign.Center, font, paint);
    }
}
