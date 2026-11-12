using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>
/// Where a storage tool screen (Sort, Batch editor, Presets) put its focusable things on the
/// last paint, in canvas pixels: each target's id, the painted key prompts at the foot, and the
/// area a drag scrolls with how far it can go (design pixels).
/// </summary>
public sealed record ToolHits(
    float Unit,
    IReadOnlyList<(int Id, SKRect Rect)> Targets,
    IReadOnlyList<SKRect> Prompts,
    SKRect ScrollArea,
    float MaxScroll);

/// <summary>The pieces the three storage tool screens share: the rimmed striped panel, the
/// slanted section tab, choice chips, the tick box and the painted key prompts.</summary>
internal static class ToolPaint
{
    internal const float HeaderHeight = 88f;
    internal const float FooterHeight = 64f;
    internal static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    internal static SKRect Px(SKRect r, float unit) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);

    /// <summary>The rimmed panel with the summary's faint stripes.</summary>
    internal static void Panel(SKCanvas c, SKRect r)
    {
        const float radius = 28f;
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(r, radius, radius, fill);
        c.Save();
        using (var clip = new SKPath())
        {
            clip.AddRoundRect(SKRect.Inflate(r, -6f, -6f), radius - 6f, radius - 6f);
            c.ClipPath(clip, antialias: true);
            TrainerCardPaint.Stripes(c, r);
        }
        c.Restore();
        using var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f };
        c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), radius, radius, rim);
    }

    /// <summary>A section's title on the banner tab run off the panel's left edge; returns its rect.</summary>
    internal static SKRect SectionTab(SKCanvas c, float left, float top, float width, string title, SKFont font)
    {
        var tab = new SKRect(left - 2f, top, left + width, top + 54f);
        using (var path = Tab(tab, 0f, 30f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        Text(c, title, tab.Left + 26f, tab.MidY, font, Pksm.Ink, width - 60f);
        return tab;
    }

    /// <summary>A choice: a dim slanted plate; the chosen one lit like the active tab; the cursor
    /// on it as the cyan section chip (lit and rimmed when it is also the chosen one).</summary>
    internal static void Choice(SKCanvas c, SKRect r, string label, SKFont font, bool chosen, bool focused, bool enabled = true)
    {
        using var path = Parallelogram(r, 16f);
        if (chosen)
        {
            Gradient(c, path, T.TabActiveTop, T.TabActiveBottom);
            Outline(c, path, focused ? T.Rim : Pksm.Ink, focused ? 5f : 3f);
        }
        else if (focused) Chip(c, r, focused: true);
        else
        {
            using var fill = new SKPaint { Color = T.Band.WithAlpha(150), IsAntialias = true };
            c.DrawPath(path, fill);
            Outline(c, path, T.Rim.WithAlpha(80), 2f);
        }
        var ink = !enabled ? T.SubInk : chosen ? Pksm.Ink : focused ? T.ChipInk : T.Value;
        Centered(c, Fitted(label, font, r.Width - 44f), r.MidX, r.MidY, font, ink);
    }

    /// <summary>A row band (alternating), or the cursor chip.</summary>
    internal static void Band(SKCanvas c, SKRect r, int index, bool focused)
    {
        if (focused) { Chip(c, r, focused: true); return; }
        using var band = new SKPaint { Color = EditorPaint.RowBand(index % 2 == 0), IsAntialias = true };
        c.DrawRoundRect(r, 10f, 10f, band);
    }

    /// <summary>The tick box: a square well, ticked in the rim colour when on.</summary>
    internal static void Check(SKCanvas c, SKRect r, bool on, SKColor ink)
    {
        using (var well = new SKPaint { Color = T.Well, IsAntialias = true }) c.DrawRoundRect(r, 6f, 6f, well);
        using (var edge = new SKPaint { Color = on ? T.Rim : ink.WithAlpha(140), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), 6f, 6f, edge);
        if (!on) return;
        using var tick = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6f, StrokeCap = SKStrokeCap.Square, StrokeJoin = SKStrokeJoin.Miter };
        using var path = new SKPath();
        path.MoveTo(r.Left + r.Width * 0.22f, r.MidY);
        path.LineTo(r.Left + r.Width * 0.43f, r.Bottom - r.Height * 0.24f);
        path.LineTo(r.Right - r.Width * 0.2f, r.Top + r.Height * 0.24f);
        c.DrawPath(path, tick);
    }

    /// <summary>The big action plate at a panel's foot ("SORT 42 POKÉMON").</summary>
    internal static void Action(SKCanvas c, SKRect r, string label, SKFont font, bool focused, bool enabled)
    {
        using var path = Parallelogram(r, 22f);
        if (enabled) Gradient(c, path, T.BannerTop, T.BannerBottom);
        else
        {
            using var fill = new SKPaint { Color = T.Band.WithAlpha(150), IsAntialias = true };
            c.DrawPath(path, fill);
        }
        Outline(c, path, focused ? T.Rim : Pksm.Ink, focused ? 6f : 3f);
        Centered(c, Fitted(label, font, r.Width - 60f), r.MidX, r.MidY, font, enabled ? Pksm.Ink : T.SubInk);
    }

    /// <summary>A thin rail at the right of a scrolling area.</summary>
    internal static void Rail(SKCanvas c, SKRect rail, float scroll, float max, float view)
    {
        if (max <= 0) return;
        using var track = new SKPaint { Color = T.Band.WithAlpha(120), IsAntialias = true };
        c.DrawRoundRect(rail, 3f, 3f, track);
        var span = Math.Max(30f, rail.Height * view / (view + max));
        var at = rail.Top + (rail.Height - span) * Math.Clamp(scroll / max, 0f, 1f);
        using var thumb = new SKPaint { Color = T.Rim, IsAntialias = true };
        c.DrawRoundRect(new SKRect(rail.Left, at, rail.Right, at + span), 3f, 3f, thumb);
    }

    /// <summary>The key prompts at the foot (tappable), the status at the right.</summary>
    internal static List<SKRect> Footer(SKCanvas c, SKRect r, IReadOnlyList<(string Key, string Label)> prompts, string status, SKFont font)
    {
        var hits = new List<SKRect>();
        var x = r.Left + 8f;
        foreach (var (key, label) in prompts)
        {
            var keyWidth = Math.Max(44f, font.MeasureFallback(key) + 24f);
            var width = keyWidth + 12f + font.MeasureFallback(label) + 26f;
            var plate = new SKRect(x, r.Top + 8f, x + width, r.Bottom - 8f);
            using (var fill = new SKPaint { Color = T.Band.WithAlpha(150), IsAntialias = true }) c.DrawRoundRect(plate, 12f, 12f, fill);
            var cap = new SKRect(plate.Left + 6f, plate.Top + 6f, plate.Left + 6f + keyWidth, plate.Bottom - 6f);
            using (var capFill = new SKPaint { Color = T.Rim, IsAntialias = true }) c.DrawRoundRect(cap, 8f, 8f, capFill);
            Centered(c, key, cap.MidX, cap.MidY, font, T.ChipInk);
            Text(c, label, cap.Right + 12f, plate.MidY, font, T.Value, width);
            hits.Add(plate);
            x = plate.Right + 12f;
        }
        if (status.Length > 0)
            Right(c, Fitted(status, font, Math.Max(0, r.Right - x - 30f)), r.Right - 16f, r.MidY, font, T.SubInk);
        return hits;
    }

    /// <summary>A small box icon, whole-step nearest-sampled, never larger than <paramref name="maxScale"/>.</summary>
    internal static void Icon(SKCanvas c, SKImage? icon, SKRect box, float maxScale, byte alpha = 255)
    {
        if (icon is null) return;
        if (alpha == 255) { c.DrawImage(icon, Fit(icon, box, maxScale), Nearest); return; }
        using var fade = new SKPaint { Color = SKColors.White.WithAlpha(alpha) };
        c.DrawImage(icon, Fit(icon, box, maxScale), Nearest, fade);
    }

    /// <summary>The shiny star: a small gold four-point sparkle.</summary>
    internal static void Star(SKCanvas c, float cx, float cy, float size)
    {
        using var path = new SKPath();
        path.MoveTo(cx, cy - size);
        path.LineTo(cx + size * 0.28f, cy - size * 0.28f);
        path.LineTo(cx + size, cy);
        path.LineTo(cx + size * 0.28f, cy + size * 0.28f);
        path.LineTo(cx, cy + size);
        path.LineTo(cx - size * 0.28f, cy + size * 0.28f);
        path.LineTo(cx - size, cy);
        path.LineTo(cx - size * 0.28f, cy - size * 0.28f);
        path.Close();
        using var gold = new SKPaint { Color = Pksm.ShinyGold, IsAntialias = true };
        c.DrawPath(path, gold);
        Outline(c, path, Pksm.Ink, 1.5f);
    }
}
