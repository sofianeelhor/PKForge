using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One setting row of the Widgets menu: its name, its current value, and a line on what it does.</summary>
public sealed record WidgetMenuRow(string Label, string Value, string Detail, bool Action = false);

/// <summary>
/// Everything the Widgets menu shows at one moment: the widgets on tabs, the rows of the one in
/// front, the cursor, the live preview (one painted frame of the real widget, at its scene size),
/// and a note under it. <paramref name="Armed"/>: the row under the cursor was tapped once, a
/// second tap acts.
/// </summary>
public sealed record WidgetMenuScene(
    IReadOnlyList<string> Tabs,
    int Tab,
    IReadOnlyList<WidgetMenuRow> Rows,
    int Row,
    SKImage? Preview,
    string Caption,
    string Note,
    bool Armed);

/// <summary>Where each touch target landed on the last paint, in canvas pixels.</summary>
public sealed record WidgetMenuHits(SKRect Panel, IReadOnlyList<SKRect> Tabs, IReadOnlyList<SKRect> Rows, SKRect Act, SKRect Back);

/// <summary>
/// The home-screen widgets menu, in the DA: the title on its banner tab, the widgets on the
/// summary's slanted tab strip, the live preview on the dark stage (the widget's own pixels,
/// enlarged by whole steps), and the settings as cyan rows, the cursor's on the chip. The keys at
/// the foot. Design pixels of a 1872 wide screen.
/// </summary>
public static class WidgetMenuPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float BannerHeight = 84f;
    private const float TabsHeight = 92f;
    private const float FootHeight = 76f;
    private const float Pad = 28f;
    private const float RowHeight = 84f;

    public static WidgetMenuHits Paint(SKCanvas c, SKImageInfo info, WidgetMenuScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        using var title = new SKFont(face, 44f);
        using var label = new SKFont(face, 34f);
        using var body = new SKFont(face, 30f);
        using var small = new SKFont(face, 26f);

        var width = MathF.Min(1720f, DesignWidth - 48f);
        var panelHeight = MathF.Min(height - 40f, 1000f);
        var panel = SKRect.Create((DesignWidth - width) / 2f, MathF.Max(20f, (height - panelHeight) / 2f), width, panelHeight);
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(panel, 26f, 26f, fill);
        TrainerCardPaint.Stripes(c, SKRect.Inflate(panel, -10f, -10f));
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f })
            c.DrawRoundRect(SKRect.Inflate(panel, -2f, -2f), 26f, 26f, rim);

        var banner = new SKRect(panel.Left + 12f, panel.Top + 12f, panel.Left + MathF.Min(900f, width - 90f), panel.Top + BannerHeight);
        using (var path = Tab(banner, 16f, 34f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        Text(c, "Home-screen widgets", banner.Left + 28f, banner.MidY, title, Pksm.Ink, banner.Width - 80f);

        var y = banner.Bottom + 14f;
        var tabs = Tabs(c, new SKRect(panel.Left + Pad, y, panel.Right - Pad, y + TabsHeight), scene, label);
        y += TabsHeight + 16f;

        var foot = new SKRect(panel.Left + Pad, panel.Bottom - FootHeight, panel.Right - Pad, panel.Bottom - 12f);
        var bottom = foot.Top - 8f;
        var leftWidth = (panel.Width - 2 * Pad) * 0.46f;
        var stage = new SKRect(panel.Left + Pad, y, panel.Left + Pad + leftWidth, bottom);
        var list = new SKRect(stage.Right + 26f, y, panel.Right - Pad, bottom);

        Stage(c, stage, scene, body, small);
        var rows = Rows(c, list, scene, label, body, small);

        var row = scene.Rows.Count == 0 ? null : scene.Rows[Math.Clamp(scene.Row, 0, scene.Rows.Count - 1)];
        var verb = row is null ? "Choose" : row.Action ? row.Label : "Change";
        var act = PadMenuPaint.Key(c, "A", verb, foot.Left, foot.MidY, small, body);
        var back = PadMenuPaint.Key(c, "B", "Back", act.Right + 40f, foot.MidY, small, body);
        PadMenuPaint.Key(c, "L/R", "Widget", back.Right + 40f, foot.MidY, small, body);

        c.Restore();
        SKRect Up(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new WidgetMenuHits(Up(panel), [.. tabs.Select(Up)], [.. rows.Select(Up)], Up(act), Up(back));
    }

    /// <summary>The widgets on the slanted tab strip, the one in front lit.</summary>
    private static List<SKRect> Tabs(SKCanvas c, SKRect r, WidgetMenuScene scene, SKFont font)
    {
        var n = Math.Max(1, scene.Tabs.Count);
        const float slant = 26f;
        var step = MathF.Min(400f, (r.Width - slant - 8f) / n);
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
        var ax = left + scene.Tab * step;
        using (var lit = Parallelogram(new SKRect(ax - 6f, top - 6f, ax + step + slant + 2f, bottom + 2f), slant))
        {
            Gradient(c, lit, T.TabActiveTop, T.TabActiveBottom);
            Outline(c, lit, Pksm.Ink, 4f);
        }
        var hits = new List<SKRect>();
        for (var i = 0; i < scene.Tabs.Count; i++)
        {
            var cellLeft = left + i * step + slant * 0.7f;
            var mid = (top + bottom) / 2;
            hits.Add(new SKRect(left + i * step, r.Top, left + (i + 1) * step + slant / 2, r.Bottom));
            var active = i == scene.Tab;
            Text(c, scene.Tabs[i], cellLeft + 8f, mid, font, active ? Pksm.Ink : T.TabIcon, step - slant - 16f);
        }
        return hits;
    }

    /// <summary>The preview on the dark stage, enlarged by whole steps, its caption and note under it.</summary>
    private static void Stage(SKCanvas c, SKRect r, WidgetMenuScene scene, SKFont body, SKFont small)
    {
        StoragePaint.WellPanel(c, r, 1f);
        var noteLines = Wrap(scene.Note, small, r.Width - 60f).Take(4).ToList();
        var textHeight = 50f + noteLines.Count * 36f;
        var box = new SKRect(r.Left + 30f, r.Top + 30f, r.Right - 30f, r.Bottom - textHeight - 20f);
        if (scene.Preview is { } preview && box.Width > 0 && box.Height > 0)
        {
            var dest = Fit(preview, box);
            dest = new SKRect(MathF.Round(dest.Left), MathF.Round(dest.Top), MathF.Round(dest.Right), MathF.Round(dest.Bottom));
            c.DrawImage(preview, dest, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
            using var frame = new SKPaint { Color = Pksm.Ink, IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 3f };
            c.DrawRect(SKRect.Inflate(dest, 1.5f, 1.5f), frame);
        }
        var y = r.Bottom - textHeight;
        Text(c, scene.Caption, r.Left + 30f, y + 20f, body, T.Value, r.Width - 60f);
        y += 56f;
        foreach (var line in noteLines)
        {
            Text(c, line, r.Left + 30f, y, small, T.SubInk, r.Width - 60f);
            y += 36f;
        }
    }

    /// <summary>The setting rows: a band each, the cursor's on the chip, the value at the right; the cursor's detail below.</summary>
    private static List<SKRect> Rows(SKCanvas c, SKRect r, WidgetMenuScene scene, SKFont label, SKFont body, SKFont small)
    {
        var hits = new List<SKRect>();
        var y = r.Top;
        for (var i = 0; i < scene.Rows.Count; i++)
        {
            var row = scene.Rows[i];
            var band = new SKRect(r.Left, y, r.Right, y + RowHeight - 12f);
            hits.Add(band);
            var focused = i == scene.Row;
            if (focused) Chip(c, band, focused: true);
            else
                using (var fill = new SKPaint { Color = EditorPaint.RowBand(false), IsAntialias = true }) c.DrawRoundRect(band, 10f, 10f, fill);
            var ink = focused ? T.ChipInk : T.Value;
            var valueWidth = row.Value.Length == 0 ? 0f : MathF.Min(body.MeasureFallback(row.Value), band.Width * 0.5f);
            Text(c, row.Label, band.Left + 34f, band.MidY, label, ink, band.Width - valueWidth - 90f);
            if (row.Value.Length > 0)
                Right(c, Fitted(row.Value, body, band.Width * 0.5f), band.Right - 34f, band.MidY, body, focused ? T.ChipInk : T.SubInk);
            if (focused && scene.Armed)
            {
                // A second tap acts: a small arrow at the chip's left says so.
                Arrow(c, band.Left + 18f, band.MidY, 1, T.ChipInk);
            }
            y += RowHeight;
        }
        if (scene.Rows.Count > 0 && scene.Row < scene.Rows.Count)
        {
            y += 10f;
            foreach (var line in Wrap(scene.Rows[scene.Row].Detail, small, r.Width - 20f).Take(Math.Max(0, (int)((r.Bottom - y) / 36f))))
            {
                Text(c, line, r.Left + 10f, y + 16f, small, T.SubInk, r.Width - 20f);
                y += 36f;
            }
        }
        return hits;
    }
}
