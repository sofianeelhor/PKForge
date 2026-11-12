using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One section of the Bank: its name, its icon, and a short count beside the name.</summary>
public sealed record BankSection(string Label, SKImage? Icon, string? Badge = null);

/// <summary>
/// The Bank's section tabs ("Pokémon | Items"): the summary's slanted tab strip with a name on
/// each tab, the open section lit. Both halves of the Bank paint it at the same place, so moving
/// between them reads as turning a tab. The pad cursor, when it is on the strip, is the chip.
/// </summary>
public static class BankSectionPaint
{
    /// <summary>The strip's design height: its unit is its height over this.</summary>
    public const float DesignHeight = 88f;

    /// <summary>Paints the strip filling <paramref name="r"/> (canvas pixels) and returns each
    /// tab's touch target, in the same pixels.</summary>
    public static IReadOnlyList<SKRect> Paint(SKCanvas c, SKRect r, IReadOnlyList<BankSection> sections, int active, int? cursor, SKTypeface face)
    {
        var unit = r.Height / DesignHeight;
        c.Save();
        c.Translate(r.Left, r.Top);
        c.Scale(unit);
        var hits = Strip(c, new SKRect(0, 0, r.Width / unit, DesignHeight), sections, active, cursor, face);
        c.Restore();
        return [.. hits.Select(h => new SKRect(r.Left + h.Left * unit, r.Top + h.Top * unit, r.Left + h.Right * unit, r.Top + h.Bottom * unit))];
    }

    /// <summary>The strip in design pixels, for painters that already work in them.</summary>
    internal static List<SKRect> Strip(SKCanvas c, SKRect r, IReadOnlyList<BankSection> sections, int active, int? cursor, SKTypeface face)
    {
        var hits = new List<SKRect>();
        var n = Math.Max(1, sections.Count);
        const float slant = 30f;
        var step = (r.Width - slant - 12f) / n;
        var left = r.Left + 6f;
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

        var ax = left + Math.Clamp(active, 0, n - 1) * step;
        using (var lit = Parallelogram(new SKRect(ax - 6f, top - 8f, ax + step + slant + 4f, bottom + 2f), slant))
        {
            Gradient(c, lit, T.TabActiveTop, T.TabActiveBottom);
            Outline(c, lit, Pksm.Ink, 4f);
        }
        if (cursor is { } at && at != active && at >= 0 && at < n)
        {
            // The pad cursor: the tab washed in the rim's cyan and ringed by it, flat, so it reads at a glance.
            var cx = left + at * step;
            using var cursorPath = Parallelogram(new SKRect(cx + 4f, top + 4f, cx + step + slant - 4f, bottom - 4f), slant);
            using (var wash = new SKPaint { Color = T.Rim.WithAlpha(80), IsAntialias = true }) c.DrawPath(cursorPath, wash);
            Outline(c, cursorPath, T.Rim, 6f);
        }

        using var font = new SKFont(face, 38f);
        using var small = new SKFont(face, 26f);
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var x0 = left + i * step + slant;
            var mid = (top + bottom) / 2;
            hits.Add(new SKRect(left + i * step, r.Top, left + (i + 1) * step + slant / 2, r.Bottom));
            var ink = cursor == i && i != active ? T.ChipInk : T.TabIcon;
            var labelWidth = font.MeasureFallback(section.Label);
            var badgeWidth = section.Badge is { } badge ? small.MeasureFallback(badge) + 14f : 0f;
            const float iconSize = 44f;
            var content = iconSize + 14f + labelWidth + badgeWidth;
            var x = x0 + Math.Max(8f, (step - slant - content) / 2);
            if (section.Icon is { } icon)
            {
                using var tint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(ink, SKBlendMode.SrcIn) };
                c.DrawImage(icon, Fit(icon, new SKRect(x, mid - iconSize / 2, x + iconSize, mid + iconSize / 2)), sampling, tint);
            }
            x += iconSize + 14f;
            Text(c, section.Label, x, mid, font, ink, step - (x - left - i * step) - 8f);
            if (section.Badge is { } count)
                Text(c, count, x + labelWidth + 14f, mid + 4f, small, ink.WithAlpha(200), badgeWidth);
        }
        return hits;
    }
}
