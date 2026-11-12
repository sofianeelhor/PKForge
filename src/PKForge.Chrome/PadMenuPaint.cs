using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One choice: its label, an optional quiet line under it, and a sprite icon or a coloured glyph.</summary>
public sealed record PadMenuLine(string Label, string? Detail = null, SKImage? Icon = null, string? Glyph = null, SKColor? Accent = null);

public sealed record PadMenuScene(
    string Title,
    string? Message,
    IReadOnlyList<PadMenuLine> Lines,
    int Columns,
    int Cursor,
    int Scroll,
    string Leave,
    // A message longer than the screen is read in pages; this is the one shown.
    int MessagePage = 0);

public sealed record PadMenuHits(SKRect Panel, IReadOnlyList<(int Index, SKRect Rect)> Lines, int VisibleRows, SKRect Choose, SKRect Leave,
    int MessagePages = 1, SKRect Message = default);

/// <summary>
/// Every small menu and question of the app, painted in the DA: a dimmed screen, one panel with
/// the title on a banner tab, the question in plain words, the choices on alternating bands
/// with the slanted cyan chip as the cursor, and the two buttons at the foot.
/// </summary>
public static class PadMenuPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float BannerHeight = 84f;
    private const float FootHeight = 76f;
    private const float Pad = 28f;

    private static float RowHeight(PadMenuScene s) =>
        s.Columns > 1 ? (s.Lines.Any(l => l.Detail is not null) ? 92f : 74f) : s.Lines.Any(l => l.Detail is not null) ? 100f : 82f;

    private static float PanelWidth(int columns) => columns switch { 1 => 980f, 2 => 1360f, _ => 1640f };

    public static PadMenuHits Paint(SKCanvas c, SKImageInfo info, PadMenuScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        using var title = new SKFont(face, 44f);
        using var label = new SKFont(face, 36f);
        using var detail = new SKFont(face, 27f);
        using var body = new SKFont(face, 31f);

        var width = PanelWidth(scene.Columns);
        List<string> WrapAt(float w) => string.IsNullOrEmpty(scene.Message) ? [] : [.. scene.Message.Split('\n').SelectMany(p => p.Length == 0 ? [""] : Wrap(p, body, w - 2 * Pad - 12f))];
        var message = WrapAt(width);
        // A long explanation over a short list gets the wide panel: fewer lines, nothing cut.
        if (message.Count > 5 && width < PanelWidth(2))
        {
            width = PanelWidth(2);
            message = WrapAt(width);
        }
        var inner = width - 2 * Pad;
        var rowHeight = RowHeight(scene);
        var rows = (scene.Lines.Count + scene.Columns - 1) / scene.Columns;
        // The message never pushes the choices off the screen: past what fits beside at least
        // two rows of choices, it is read in pages (a tap on it or L/R turns them).
        var frame = 48f + BannerHeight + Pad + FootHeight + Pad;
        var fitLines = Math.Max(2, (int)((height - frame - Math.Min(rows, 2) * rowHeight - 18f - 44f) / 40f));
        var pages = message.Count > fitLines ? (message.Count + fitLines - 1) / fitLines : 1;
        var page = Math.Clamp(scene.MessagePage, 0, pages - 1);
        if (pages > 1) message = [.. message.Skip(page * fitLines).Take(fitLines)];
        var messageHeight = message.Count == 0 ? 0f : (pages > 1 ? fitLines : message.Count) * 40f + 18f + (pages > 1 ? 44f : 0f);
        var room = height - 48f - BannerHeight - Pad - messageHeight - FootHeight - Pad;
        var visible = Math.Max(1, Math.Min(rows, (int)(room / rowHeight)));
        var panelHeight = BannerHeight + Pad + messageHeight + visible * rowHeight + FootHeight + 12f;
        var panel = SKRect.Create((DesignWidth - width) / 2f, MathF.Max(24f, (height - panelHeight) / 2f), width, panelHeight);

        // The panel: fill and a double rim, as the cards.
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(panel, 26f, 26f, fill);
        TrainerCardPaint.Stripes(c, SKRect.Inflate(panel, -10f, -10f));
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f })
            c.DrawRoundRect(SKRect.Inflate(panel, -2f, -2f), 26f, 26f, rim);

        // The title on a banner tab across the top.
        var banner = new SKRect(panel.Left + 12f, panel.Top + 12f, panel.Right - 90f, panel.Top + 12f + BannerHeight - 12f);
        using (var path = Tab(banner, 16f, 34f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        Text(c, scene.Title, banner.Left + 28f, banner.MidY, title, Pksm.Ink, banner.Width - 80f);

        var y = panel.Top + BannerHeight + Pad - 6f;
        var messageRect = new SKRect(panel.Left + Pad, y, panel.Right - Pad, y + messageHeight);
        foreach (var line in message)
        {
            Text(c, line, panel.Left + Pad + 6f, y + 18f, body, T.Value, inner - 12f);
            y += 40f;
        }
        if (pages > 1)
        {
            // Which page, and that there is more: a slanted tag with carets, as the list's own.
            y = panel.Top + BannerHeight + Pad - 6f + fitLines * 40f + 4f;
            var tag = $"Page {page + 1} / {pages}";
            var tw = detail.MeasureFallback(tag) + 120f;
            var plate = SKRect.Create(panel.Right - Pad - tw, y, tw, 38f);
            using (var path = Parallelogram(plate, 10f))
            {
                using var paint = new SKPaint { Color = T.RimFill, IsAntialias = true };
                c.DrawPath(path, paint);
                Outline(c, path, T.Rim, 2.5f);
            }
            Centered(c, tag, plate.MidX, plate.MidY, detail, T.Value);
            if (page > 0) Arrow(c, plate.Left + 30f, plate.MidY, -1, T.Rim);
            if (page < pages - 1) Arrow(c, plate.Right - 30f, plate.MidY, 1, T.Rim);
            Text(c, "L / R or tap the text", panel.Left + Pad + 6f, plate.MidY, detail, T.SubInk, inner - tw - 30f);
            y += 44f;
        }
        if (message.Count > 0) y += 18f;

        // The choices.
        var hits = new List<(int, SKRect)>();
        var colWidth = (inner - (scene.Columns - 1) * 16f) / scene.Columns;
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        for (var i = scene.Scroll * scene.Columns; i < scene.Lines.Count && i < (scene.Scroll + visible) * scene.Columns; i++)
        {
            var r = i / scene.Columns - scene.Scroll;
            var col = i % scene.Columns;
            var row = SKRect.Create(panel.Left + Pad + col * (colWidth + 16f), y + r * rowHeight, colWidth, rowHeight - 10f);
            hits.Add((i, row));
            var focused = i == scene.Cursor;
            if (focused) Chip(c, row, focused: true);
            else
            {
                using var band = new SKPaint { Color = EditorPaint.RowBand((r + col) % 2 == 0), IsAntialias = true };
                c.DrawRoundRect(row, 10f, 10f, band);
            }
            var item = scene.Lines[i];
            var ink = focused ? T.ChipInk : T.Value;
            var left = row.Left + 26f;
            var icon = SKRect.Create(left, row.MidY - 30f, 60f, 60f);
            if (item.Icon is { } image)
            {
                c.DrawImage(image, Fit(image, icon), sampling);
                left += 78f;
            }
            else if (item.Glyph is { Length: > 0 } glyph)
            {
                using (var badge = Parallelogram(SKRect.Create(left, row.MidY - 22f, 52f, 44f), 10f))
                {
                    using var paint = new SKPaint { Color = (item.Accent ?? T.Rim).WithAlpha(focused ? (byte)255 : (byte)200), IsAntialias = true };
                    c.DrawPath(badge, paint);
                    Outline(c, badge, Pksm.Ink, 2f);
                }
                Centered(c, glyph, left + 26f, row.MidY, detail, Pksm.Ink);
                left += 70f;
            }
            var max = row.Right - 26f - left;
            if (item.Detail is { Length: > 0 } more)
            {
                Text(c, item.Label, left, row.MidY - 17f, label, ink, max);
                Text(c, more, left, row.MidY + 21f, detail, focused ? T.ChipInk.WithAlpha(210) : T.SubInk, max);
            }
            else Text(c, item.Label, left, row.MidY, label, ink, max);
        }

        // More above or below: a small arrow at the edge of the list.
        if (scene.Scroll > 0) Caret(c, panel.Right - 40f, y - 8f, up: true);
        if (scene.Scroll + visible < rows) Caret(c, panel.Right - 40f, y + visible * rowHeight + 2f, up: false);

        // The two buttons at the foot.
        var foot = new SKRect(panel.Left + Pad, panel.Bottom - FootHeight, panel.Right - Pad, panel.Bottom - 16f);
        var choose = Key(c, "A", "Choose", foot.Left, foot.MidY, detail, body);
        var leave = Key(c, "B", scene.Leave, choose.Right + 40f, foot.MidY, detail, body);

        c.Restore();
        SKRect Up(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new PadMenuHits(Up(panel), [.. hits.Select(h => (h.Item1, Up(h.Item2)))], visible, Up(choose), Up(leave), pages, Up(messageRect));
    }

    private static void Caret(SKCanvas c, float cx, float cy, bool up)
    {
        using var path = new SKPath();
        var d = up ? -1f : 1f;
        path.MoveTo(cx - 12f, cy - d * 6f);
        path.LineTo(cx + 12f, cy - d * 6f);
        path.LineTo(cx, cy + d * 8f);
        path.Close();
        using var paint = new SKPaint { Color = T.Rim, IsAntialias = true };
        c.DrawPath(path, paint);
    }

    /// <summary>A button key (a slanted cap with its letter) and what it does; returns the whole hit area.</summary>
    internal static SKRect Key(SKCanvas c, string key, string text, float x, float mid, SKFont keyFont, SKFont font)
    {
        // A wider cap for a key name longer than a letter ("L/R").
        var cap = SKRect.Create(x, mid - 22f, MathF.Max(50f, keyFont.MeasureFallback(key) + 30f), 44f);
        using (var path = Parallelogram(cap, 10f))
        {
            Gradient(c, path, T.ChipTop, T.ChipBottom);
            Outline(c, path, T.Rim, 2.5f);
        }
        Centered(c, key, cap.MidX, mid, keyFont, T.ChipInk);
        var w = font.MeasureFallback(text);
        Text(c, text, cap.Right + 14f, mid, font, T.Value, w + 4f);
        return new SKRect(cap.Left - 8f, cap.Top - 8f, cap.Right + 14f + w + 8f, cap.Bottom + 8f);
    }
}
