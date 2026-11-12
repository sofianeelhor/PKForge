using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>Who plays the save, for the trainer card. Party icons are cells of the box icon sheet.</summary>
public sealed record SaveDataCard(
    string Game,
    string Trainer,
    int Gender,
    IReadOnlyList<(string Label, string Value)> Facts,
    IReadOnlyList<(SKImage Sheet, SKRect Cell)> Party);

/// <summary>One tool: its name, what it does, its icon, and the shorter name on its tile.</summary>
public sealed record SaveDataLine(string Title, string Detail, SKImage? Icon, string? Short = null);

/// <summary>One group of tools, framed together under its name.</summary>
public sealed record SaveDataShelf(string Title, IReadOnlyList<SaveDataLine> Apps);

public sealed record SaveDataScene(SaveDataCard Card, IReadOnlyList<SaveDataShelf> Shelves, int Shelf, int App);

public sealed record SaveDataHits(IReadOnlyList<(int Shelf, int App, SKRect Rect)> Apps);

/// <summary>
/// Save data: the trainer card across the top, as the games print it (name tab, facts,
/// six party seats, an empty seat showing a Poké Ball in watermark), and under it each
/// group of tools in its own frame with its tab, the frames packed side by side so no row
/// is left half empty. Only the theme's own roles are used: every theme gets it right.
/// </summary>
public static class SaveDataPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float CardHeight = 270f;
    private const float FooterHeight = 70f;
    private const float Tile = 168f;
    private const float TileGap = 14f;
    private const float FramePad = 12f;
    private const float FrameGap = 22f;
    private const float TabHeight = 46f;

    public static SaveDataHits Paint(SKCanvas c, SKImageInfo info, SaveDataScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        // The frames, packed in rows from left to right.
        var widths = scene.Shelves.Select(s => FramePad * 2 + s.Apps.Count * Tile + (s.Apps.Count - 1) * TileGap).ToList();
        var rows = new List<List<int>> { new() };
        var used = 0f;
        for (var i = 0; i < widths.Count; i++)
        {
            if (rows[^1].Count > 0 && used + FrameGap + widths[i] > DesignWidth) { rows.Add([]); used = 0f; }
            used += (rows[^1].Count > 0 ? FrameGap : 0f) + widths[i];
            rows[^1].Add(i);
        }

        // Three rows of tools need the room: the card gets lower so the tiles keep their size.
        var cardHeight = rows.Count >= 3 ? 214f : CardHeight;
        PaintCard(c, new SKRect(0, 0, DesignWidth, cardHeight), scene.Card, face);
        var area = new SKRect(0, cardHeight + 22f, DesignWidth, height - FooterHeight - 16f);
        var rowHeight = Math.Min(TabHeight + Tile + FramePad * 2 + 30f, area.Height / rows.Count);
        var tile = Math.Min(Tile, rowHeight - TabHeight - FramePad * 2 - 30f);

        using var tabFont = new SKFont(face, 30f);
        using var label = new SKFont(face, 28f);
        var hits = new List<(int, int, SKRect)>();
        for (var r = 0; r < rows.Count; r++)
        {
            // Each row is stretched to the full width: the spare room goes to the frames evenly.
            var natural = rows[r].Sum(i => FramePad * 2 + scene.Shelves[i].Apps.Count * tile + (scene.Shelves[i].Apps.Count - 1) * TileGap) + FrameGap * (rows[r].Count - 1);
            var spare = (area.Width - natural) / Math.Max(1, rows[r].Sum(i => scene.Shelves[i].Apps.Count));
            var x = area.Left;
            var top = area.Top + r * rowHeight;
            foreach (var s in rows[r])
            {
                var shelf = scene.Shelves[s];
                var step = tile + spare + TileGap;
                var width = FramePad * 2 + shelf.Apps.Count * step - TileGap;
                var frame = new SKRect(x, top + TabHeight - 6f, x + width, top + rowHeight - 10f);
                var current = s == scene.Shelf;
                PaintFrame(c, frame, shelf.Title, current, tabFont);
                for (var a = 0; a < shelf.Apps.Count; a++)
                {
                    var cell = SKRect.Create(frame.Left + FramePad + a * step, frame.Top + FramePad, step - TileGap, frame.Height - FramePad * 2);
                    hits.Add((s, a, cell));
                    PaintTile(c, cell, shelf.Apps[a], current && a == scene.App, label);
                }
                x += width + FrameGap;
            }
        }

        // What the tool under the cursor does.
        var footer = new SKRect(0, height - FooterHeight, DesignWidth, height);
        using (var band = new SKPaint { Color = T.Band.WithAlpha(170), IsAntialias = true }) c.DrawRoundRect(footer, 16f, 16f, band);
        using (var rim = new SKPaint { Color = T.Rim.WithAlpha(120), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f })
            c.DrawRoundRect(SKRect.Inflate(footer, -1f, -1f), 16f, 16f, rim);
        if (scene.Shelf < scene.Shelves.Count && scene.App < scene.Shelves[scene.Shelf].Apps.Count)
        {
            var app = scene.Shelves[scene.Shelf].Apps[scene.App];
            using var title = new SKFont(face, 34f);
            using var detail = new SKFont(face, 30f);
            var titleWidth = title.MeasureFallback(app.Title);
            Text(c, app.Title, footer.Left + 28f, footer.MidY, title, T.Value, footer.Width - 56f);
            Text(c, app.Detail, footer.Left + 28f + titleWidth + 28f, footer.MidY, detail, T.SubInk, footer.Width - titleWidth - 90f);
        }

        c.Restore();
        return new SaveDataHits(
            [.. hits.Select(h => (h.Item1, h.Item2, new SKRect(h.Item3.Left * unit, h.Item3.Top * unit, h.Item3.Right * unit, h.Item3.Bottom * unit)))]);
    }

    /// <summary>A group's frame: the panel with its rim, and its name on a slanted tab riding the top edge.</summary>
    private static void PaintFrame(SKCanvas c, SKRect frame, string title, bool current, SKFont font)
    {
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(frame, 22f, 22f, fill);
        using (var rim = new SKPaint { Color = current ? T.Rim : T.Rim.WithAlpha(110), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(frame, -1.5f, -1.5f), 22f, 22f, rim);
        var w = font.MeasureFallback(title) + 70f;
        var tab = new SKRect(frame.Left + 18f, frame.Top - TabHeight + 8f, frame.Left + 18f + w, frame.Top + 8f);
        using var path = Parallelogram(tab, 16f);
        if (current) Gradient(c, path, T.TabActiveTop, T.TabActiveBottom);
        else Gradient(c, path, T.TabTop, T.TabBottom);
        Outline(c, path, current ? Pksm.Ink : T.Rim, 3f);
        Centered(c, title, tab.MidX, tab.MidY, font, current ? Pksm.Ink : T.Value);
    }

    /// <summary>A tool: its icon on a plate, its name under it; the picked one lit like the active tab.</summary>
    private static void PaintTile(SKCanvas c, SKRect cell, SaveDataLine app, bool selected, SKFont label)
    {
        var plate = new SKRect(cell.Left, cell.Top, cell.Right, cell.Bottom - 40f);
        using (var path = new SKPath())
        {
            path.AddRoundRect(plate, 18f, 18f);
            if (selected) Gradient(c, path, T.TabActiveTop, T.TabActiveBottom);
            else using (var fill = new SKPaint { Color = T.Band.WithAlpha(140), IsAntialias = true }) c.DrawPath(path, fill);
            Outline(c, path, selected ? Pksm.Ink : T.Rim.WithAlpha(90), selected ? 4f : 2f);
        }
        if (app.Icon is { } icon)
        {
            // The game's own item sprite, in its colours, at whole pixel steps.
            var size = MathF.Min(108f, MathF.Floor(plate.Height * 0.85f));
            c.DrawImage(icon, Fit(icon, SKRect.Create(plate.MidX - size / 2, plate.MidY - size / 2, size, size)),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }
        Centered(c, Fitted(app.Short ?? app.Title, label, cell.Width), cell.MidX, cell.Bottom - 18f, label, selected ? T.Value : T.SubInk);
    }

    /// <summary>The trainer card: the name tab and the game, the facts, and six party seats.</summary>
    private static void PaintCard(SKCanvas c, SKRect r, SaveDataCard card, SKTypeface face)
    {
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(r, 28f, 28f, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), 28f, 28f, rim);

        // The name tab off the left edge, the game under it.
        var tab = new SKRect(r.Left - 2f, r.Top + 26f, r.Left + 520f, r.Top + 104f);
        using (var path = Tab(tab, 0f, 38f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        using var big = new SKFont(face, 54f);
        using var small = new SKFont(face, 28f);
        using var value = new SKFont(face, 38f);
        Text(c, card.Trainer, tab.Left + 34f, tab.MidY, big, Pksm.Ink, tab.Width - 140f);
        var sign = card.Gender switch { 0 => "♂", 1 => "♀", _ => null };
        if (sign is not null) Right(c, sign, tab.Right - 56f, tab.MidY, big, card.Gender == 1 ? Pksm.Female : Pksm.SignalBlue);
        Text(c, card.Game, tab.Right + 20f, tab.MidY + 4f, small, T.SubInk, 300f);

        // The facts in one line under the name.
        var fx = r.Left + 34f;
        for (var i = 0; i < card.Facts.Count && i < 4; i++)
        {
            var x = fx + i * 230f;
            Text(c, card.Facts[i].Label, x, tab.Bottom + 34f, small, T.SubInk, 210f);
            Text(c, card.Facts[i].Value, x, tab.Bottom + 76f, value, T.Value, 210f);
        }

        // Six seats in a row at the right; a seat without a Pokémon keeps a Poké Ball in watermark.
        var seatsLeft = r.Left + 1000f;
        var seat = MathF.Min((r.Right - 26f - seatsLeft) / 6f, r.Height - 60f);
        var seatTop = r.MidY - seat / 2;
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        for (var i = 0; i < 6; i++)
        {
            var box = SKRect.Create(seatsLeft + i * seat + 6f, seatTop, seat - 12f, seat - 12f);
            using (var well = new SKPaint { Color = T.Well, IsAntialias = true }) c.DrawRoundRect(box, 18f, 18f, well);
            using (var edge = new SKPaint { Color = T.Rim.WithAlpha(90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f })
                c.DrawRoundRect(box, 18f, 18f, edge);
            if (i < card.Party.Count)
            {
                var (sheet, cell) = card.Party[i];
                var scale = MathF.Max(1f, MathF.Floor(MathF.Min(box.Width / cell.Width, box.Height / cell.Height)));
                var w = cell.Width * scale;
                var h = cell.Height * scale;
                c.DrawImage(sheet, cell, SKRect.Create(box.MidX - w / 2, box.MidY - h / 2, w, h), sampling);
            }
            else PaintBallMark(c, box);
        }
    }

    /// <summary>A Poké Ball drawn in two strokes of the rim colour, faint: an empty party seat.</summary>
    private static void PaintBallMark(SKCanvas c, SKRect box)
    {
        var radius = box.Width * 0.26f;
        using var stroke = new SKPaint { Color = T.Rim.WithAlpha(70), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f };
        c.DrawCircle(box.MidX, box.MidY, radius, stroke);
        c.DrawLine(box.MidX - radius, box.MidY, box.MidX - radius * 0.32f, box.MidY, stroke);
        c.DrawLine(box.MidX + radius * 0.32f, box.MidY, box.MidX + radius, box.MidY, stroke);
        c.DrawCircle(box.MidX, box.MidY, radius * 0.32f, stroke);
    }
}
