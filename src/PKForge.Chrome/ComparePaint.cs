using PKForge.Domain;
using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>Everything the compare screen shows at one moment: the model, each side's big
/// sprite (null while it loads), which side is focused (0 left, 1 right), the first section shown.</summary>
public sealed record CompareScene(MonComparison Model, SKImage? LeftSprite, SKImage? RightSprite, int Focus, int Section);

/// <summary>Where each side's header landed on the last paint, in canvas pixels, and how far it can scroll.</summary>
public sealed record CompareHits(SKRect Left, SKRect Right, SKRect Body, bool CanScrollDown);

/// <summary>
/// Two Pokémon side by side, in the app's art direction: the box banner across the top, each
/// Pokémon free-standing at its outer edge with its name plate (the focused one on the cursor
/// chip), then one rimmed panel where every value is mirrored around a centre label column.
/// Numbers are paired bars growing outward; the higher side is lit and pointed at, and a value
/// a format does not keep is a dash. Sizes are design pixels (a 1872 wide screen) scaled by a unit.
/// </summary>
public static class ComparePaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float BannerHeight = 84f;
    private const float HeadTop = BannerHeight + 14f;
    private const float HeadHeight = 232f;
    private const float BodyTop = HeadTop + HeadHeight + 14f;
    private static float Mid => DesignWidth / 2f;
    private const float LabelHalf = 140f;
    private const float NumberZone = 120f;
    private const float TitleHeight = 62f;
    private const float RowHeight = 50f;

    public static CompareHits Paint(SKCanvas c, SKImageInfo info, CompareScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        var model = scene.Model;
        c.Save();
        c.Scale(unit);

        using var title = new SKFont(face, 46f);
        using var name = new SKFont(face, 40f);
        using var small = new SKFont(face, 30f);
        using var row = new SKFont(face, 32f);

        // The banner.
        var banner = new SKRect(0, 0, DesignWidth, BannerHeight);
        using (var path = Tab(new SKRect(banner.Left, banner.Top, banner.Right - 60f, banner.Bottom), 0f, 40f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        Text(c, "COMPARE", banner.Left + 36f, banner.MidY, title, Pksm.Ink, 600f);
        var count = model.Differences;
        Right(c, count == 0 ? "No differences" : count == 1 ? "1 difference" : $"{count} differences", banner.Right - 120f, banner.MidY, small, Pksm.Ink);

        // The two Pokémon.
        var left = new SKRect(0, HeadTop, Mid - 40f, HeadTop + HeadHeight);
        var right = new SKRect(Mid + 40f, HeadTop, DesignWidth, HeadTop + HeadHeight);
        Head(c, left, model.Left, scene.LeftSprite, scene.Focus == 0, mirrored: false, name, small);
        Head(c, right, model.Right, scene.RightSprite, scene.Focus == 1, mirrored: true, name, small);
        Centered(c, "VS", Mid, left.MidY, title, T.SubInk);

        // The values.
        var body = new SKRect(0, BodyTop, DesignWidth, height);
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(body, 28f, 28f, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(body, -1.5f, -1.5f), 28f, 28f, rim);
        TrainerCardPaint.Stripes(c, SKRect.Inflate(body, -6f, -6f));

        var inner = new SKRect(body.Left + 24f, body.Top + 18f, body.Right - 24f, body.Bottom - 18f);
        c.Save();
        c.ClipRect(inner);
        var y = inner.Top;
        var overflow = false;
        for (var s = Math.Clamp(scene.Section, 0, Math.Max(0, model.Sections.Count - 1)); s < model.Sections.Count; s++)
        {
            if (y >= inner.Bottom) { overflow = true; break; }
            var section = model.Sections[s];
            SectionTitle(c, new SKRect(inner.Left, y + 6f, inner.Right, y + TitleHeight - 6f), section, row, small);
            y += TitleHeight;
            var band = 0;
            foreach (var r in section.Rows)
            {
                TextRow(c, new SKRect(inner.Left, y, inner.Right, y + RowHeight - 4f), r, band++ % 2 == 0, row);
                y += RowHeight;
            }
            foreach (var b in section.Bars)
            {
                BarRow(c, new SKRect(inner.Left, y, inner.Right, y + RowHeight - 4f), b, band++ % 2 == 0, row, small);
                y += RowHeight;
            }
            y += 10f;
            if (y > inner.Bottom) overflow = true;
        }
        c.Restore();
        if (overflow)
        {
            // More below: a small down arrow at the panel's foot.
            using var more = new SKPath();
            var (ax, ay) = (body.Right - 48f, body.Bottom - 22f);
            more.MoveTo(ax - 14f, ay - 12f);
            more.LineTo(ax + 14f, ay - 12f);
            more.LineTo(ax, ay + 4f);
            more.Close();
            using var ink = new SKPaint { Color = T.Rim, IsAntialias = true };
            c.DrawPath(more, ink);
        }

        c.Restore();
        static SKRect Scale(SKRect r, float u) => new(r.Left * u, r.Top * u, r.Right * u, r.Bottom * u);
        return new CompareHits(Scale(left, unit), Scale(right, unit), Scale(body, unit), overflow);
    }

    /// <summary>One side's header: the sprite free-standing at the outer edge, the name plate,
    /// and the lines under it. The right side mirrors the left.</summary>
    private static void Head(SKCanvas c, SKRect r, CompareHead head, SKImage? sprite, bool focused, bool mirrored, SKFont name, SKFont small)
    {
        var art = mirrored ? new SKRect(r.Right - 236f, r.Top, r.Right - 16f, r.Bottom) : new SKRect(r.Left + 16f, r.Top, r.Left + 236f, r.Bottom);
        if (sprite is not null)
            c.DrawImage(sprite, Fit(sprite, art), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));

        var textLeft = mirrored ? r.Left : art.Right + 16f;
        var textRight = mirrored ? art.Left - 16f : r.Right;
        var plate = new SKRect(textLeft, r.Top + 10f, textRight, r.Top + 74f);
        if (focused) Chip(c, plate, focused: true);
        else
        {
            using var path = Parallelogram(plate, 18f);
            using var fill = new SKPaint { Color = T.Band.WithAlpha(170), IsAntialias = true };
            c.DrawPath(path, fill);
            Outline(c, path, T.Rim.WithAlpha(90), 2f);
        }
        var label = head.Shiny ? head.Name + " ★" : head.Name;
        var ink = focused ? T.ChipInk : T.Value;
        var width = plate.Width - 64f;
        if (mirrored) Right(c, Fitted(label, name, width), plate.Right - 40f, plate.MidY, name, head.Shiny ? Pksm.ShinyGold : ink);
        else Text(c, label, plate.Left + 36f, plate.MidY, name, head.Shiny ? Pksm.ShinyGold : ink, width);

        var species = head.Form is { Length: > 0 } form && form != head.Species ? $"{head.Species} · {form}" : head.Species;
        string[] lines = [$"{species} · Lv. {head.Level}", $"{head.Format} · Gen {head.Generation}", head.Where];
        for (var i = 0; i < lines.Length; i++)
        {
            var mid = plate.Bottom + 28f + i * 37f;
            var color = i == 0 ? T.Value : T.SubInk;
            if (mirrored) Right(c, Fitted(lines[i], small, plate.Width - 24f), plate.Right - 20f, mid, small, color);
            else Text(c, lines[i], plate.Left + 20f, mid, small, color, plate.Width - 24f);
        }
        if (head.Legal is { } legal)
            Verdict(c, legal, mirrored ? plate.Right - 20f : plate.Left + 20f, plate.Bottom + 28f + 3 * 37f, mirrored, small);
    }

    /// <summary>
    /// The legality verdict on a slanted tag of its signal colour. The signal colours never change
    /// with the theme, and as bare text the red one sank into the red themes (Fire, Fighting): on
    /// its own plate, outlined and lettered in a deep shade of the same colour, it reads the same
    /// in every theme. <paramref name="x"/> is the tag's outer edge (its right edge when mirrored).
    /// </summary>
    private static void Verdict(SKCanvas c, bool legal, float x, float mid, bool mirrored, SKFont font)
    {
        var (text, fill) = legal ? ("Legal", Pksm.Legal) : ("Illegal", Pksm.Illegal);
        var ink = PksmPaint.Darker(fill, 0.75f); // 5.1:1 on the red, 7.2:1 on the green (WCAG AA)
        const float slant = 12f;
        var width = font.MeasureFallback(text) + 2 * slant + 24f;
        var left = mirrored ? x - width : x;
        var tag = new SKRect(left, mid - 19f, left + width, mid + 19f);
        using var path = Parallelogram(tag, slant);
        using (var paint = new SKPaint { Color = fill, IsAntialias = true }) c.DrawPath(path, paint);
        Outline(c, path, ink, 3f);
        Centered(c, text, tag.MidX, tag.MidY, font, ink);
    }

    /// <summary>A section's title on a slanted tab at the centre, its difference count beside it.</summary>
    private static void SectionTitle(SKCanvas c, SKRect r, CompareSection section, SKFont font, SKFont small)
    {
        using (var line = new SKPaint { Color = T.Rim.WithAlpha(110), StrokeWidth = 2f, IsAntialias = true })
            c.DrawLine(r.Left + 8f, r.MidY, r.Right - 8f, r.MidY, line);
        var tab = new SKRect(Mid - 200f, r.Top, Mid + 200f, r.Bottom);
        using (var path = Parallelogram(tab, 18f))
        {
            Gradient(c, path, T.TabActiveTop, T.TabActiveBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        Centered(c, section.Title.ToUpperInvariant(), tab.MidX, tab.MidY, font, Pksm.Ink);
        var n = section.Differences;
        if (n > 0)
        {
            var text = n == 1 ? "1 differs" : $"{n} differ";
            var w = small.MeasureFallback(text) + 32f;
            var plate = new SKRect(tab.Right + 18f, r.Top + 6f, tab.Right + 18f + w, r.Bottom - 6f);
            using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(plate, 10f, 10f, fill);
            Centered(c, text, plate.MidX, plate.MidY, small, T.Rim);
        }
    }

    private static void Band(SKCanvas c, SKRect r, bool dark, bool differs)
    {
        using var band = new SKPaint { Color = differs ? T.RimFill.WithAlpha(220) : EditorPaint.RowBand(dark), IsAntialias = true };
        c.DrawRoundRect(r, 10f, 10f, band);
        using var label = new SKPaint { Color = T.LabelColumn.WithAlpha(200), IsAntialias = true };
        c.DrawRect(new SKRect(Mid - LabelHalf, r.Top, Mid + LabelHalf, r.Bottom), label);
    }

    /// <summary>The marker that points from the label column toward the higher side.</summary>
    private static void Winner(SKCanvas c, SKRect r, CompareEdge edge)
    {
        if (edge == CompareEdge.Left) Arrow(c, Mid - LabelHalf + 22f, r.MidY, -1, T.Rim);
        else if (edge == CompareEdge.Right) Arrow(c, Mid + LabelHalf - 22f, r.MidY, 1, T.Rim);
    }

    // The higher side is lit; a value both share steps back so the differences carry the eye.
    private static SKColor Ink(string value, CompareEdge edge, CompareEdge side) =>
        value == MonComparison.Missing ? T.SubInk : edge == side ? T.Rim : edge == CompareEdge.Same ? T.SubInk : T.Value;

    private static void TextRow(SKCanvas c, SKRect r, CompareRow row, bool dark, SKFont font)
    {
        Band(c, r, dark, row.Differs);
        Centered(c, row.Label, Mid, r.MidY, font, T.Label);
        Winner(c, r, row.Edge);
        var max = Mid - LabelHalf - 24f - r.Left - 16f;
        Right(c, Fitted(row.Left, font, max), Mid - LabelHalf - 24f, r.MidY, font, Ink(row.Left, row.Edge, CompareEdge.Left));
        Text(c, row.Right, Mid + LabelHalf + 24f, r.MidY, font, Ink(row.Right, row.Edge, CompareEdge.Right), max);
    }

    private static void BarRow(SKCanvas c, SKRect r, CompareBar bar, bool dark, SKFont font, SKFont small)
    {
        Band(c, r, dark, bar.Edge is CompareEdge.Left or CompareEdge.Right);
        Centered(c, bar.Label, Mid, r.MidY, font, T.Label);
        Winner(c, r, bar.Edge);

        var length = Mid - LabelHalf - NumberZone - r.Left - 24f;
        var track = r.Height - 22f;
        // Left grows leftward from the number zone; right mirrors it.
        var leftEnd = Mid - LabelHalf - NumberZone;
        var rightStart = Mid + LabelHalf + NumberZone;
        Fill(c, new SKRect(leftEnd - length, r.MidY - track / 2, leftEnd, r.MidY + track / 2), bar.Left, bar.LeftFraction, grow: -1, lit: bar.Edge == CompareEdge.Left);
        Fill(c, new SKRect(rightStart, r.MidY - track / 2, rightStart + length, r.MidY + track / 2), bar.Right, bar.RightFraction, grow: 1, lit: bar.Edge == CompareEdge.Right);

        var l = Number(bar.Left, bar.LeftNature);
        var rt = Number(bar.Right, bar.RightNature);
        Right(c, l, Mid - LabelHalf - 18f, r.MidY, font, Ink(l, bar.Edge, CompareEdge.Left));
        Text(c, rt, Mid + LabelHalf + 18f, r.MidY, font, Ink(rt, bar.Edge, CompareEdge.Right), NumberZone);
        _ = small;
    }

    // The nature's raised stat carries a +, the lowered one a -, as in the games' summary.
    private static string Number(int? value, int nature) =>
        value is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) + (nature > 0 ? "+" : nature < 0 ? "-" : "") : MonComparison.Missing;

    private static void Fill(SKCanvas c, SKRect track, int? value, float fraction, int grow, bool lit)
    {
        using (var back = new SKPaint { Color = T.Well.WithAlpha(200), IsAntialias = true }) c.DrawRoundRect(track, 6f, 6f, back);
        if (value is null || fraction <= 0f) return;
        var w = MathF.Max(8f, track.Width * fraction);
        var fill = grow < 0 ? new SKRect(track.Right - w, track.Top, track.Right, track.Bottom) : new SKRect(track.Left, track.Top, track.Left + w, track.Bottom);
        using var paint = new SKPaint { Color = lit ? T.Rim : T.Label.WithAlpha(190), IsAntialias = true };
        c.DrawRoundRect(fill, 6f, 6f, paint);
    }
}
