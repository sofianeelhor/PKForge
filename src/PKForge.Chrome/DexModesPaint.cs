using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One entry of the grid: its box icon (null while it loads) and its marks.</summary>
/// <param name="Owned">Counts: drawn in colour with the caught ball.</param>
/// <param name="Elsewhere">Owned, but not in a way that counts (Origin Dex: from another era): drawn faded.</param>
/// <param name="Empty">A box plan's empty slot: nothing drawn but the slot.</param>
public sealed record DexModesCell(int Number, SKImage? Sheet, SKRect Source, bool Owned, bool Elsewhere = false,
    bool Shiny = false, bool Variant = false, bool Empty = false);

/// <summary>A line of the sheet: a fact (label and value) or, with <paramref name="Toggle"/>, a switch.</summary>
public sealed record DexModesRow(string Label, string Value, bool? Toggle = null);

/// <summary>Everything the dex modes screen shows at one moment.</summary>
/// <param name="BoxView">The grid is one planned box, six by five, instead of the scrolling dex.</param>
public sealed record DexModesScene(
    string Title,
    bool CanPrevious,
    bool CanNext,
    IReadOnlyList<(string Label, string Count)> Tabs,
    int Tab,
    IReadOnlyList<DexModesCell> Cells,
    int Cursor,
    int ScrollRow,
    bool BoxView,
    int Number,
    string Name,
    string Line,
    SKImage? Sprite,
    bool SpriteOwned,
    IReadOnlyList<DexModesRow> Rows,
    IReadOnlyList<string> Actions,
    string Progress,
    string Status,
    string Empty);

/// <summary>Where each touch target landed on the last paint, in canvas pixels.</summary>
public sealed record DexModesHits(
    SKRect Previous,
    SKRect Next,
    IReadOnlyList<SKRect> Tabs,
    SKRect Grid,
    IReadOnlyList<(int Index, SKRect Rect)> Cells,
    IReadOnlyList<(int Index, SKRect Rect)> Rows,
    IReadOnlyList<(int Index, SKRect Rect)> Actions,
    int Columns,
    int VisibleRows,
    float CellHeight);

/// <summary>
/// The Origin Dex, the Extended Living Dex and its box planner as one painted screen, in the
/// Pokédex's language: the mode and scope in the box banner with its chevrons, generations as
/// slanted tabs, entries as box icons on the well's banded rows (a planned box as the game's six
/// by five), and the entry under the cursor on a sheet at the right: its page, the facts that
/// decide whether it counts, the switches of the mode, and what can be done. Design pixels of a
/// 1872 wide screen, scaled by a unit.
/// </summary>
public static class DexModesPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float HeaderHeight = 88f;
    private const float StripHeight = 70f;
    private const float GridWidth = 1180f;
    private const float CellWidth = 112f;
    private const float CellHeight = 112f;
    private const float BoxCellWidth = 180f;
    private const float BoxCellHeight = 136f;
    private const float Inset = 20f;
    private const float Gap = 24f;

    private static float GridTop => HeaderHeight + 14f + StripHeight + 12f;

    public static int Columns => (int)((GridWidth - 2 * Inset - 16f) / CellWidth);
    public const int BoxColumns = 6;
    public const int BoxRows = 5;

    /// <summary>How many whole rows of entries the grid shows on a canvas of this size.</summary>
    public static int VisibleRows(SKSizeI size)
    {
        if (size.Width <= 0) return 1;
        var unit = size.Width / DesignWidth;
        var height = size.Height / unit;
        return Math.Max(1, (int)((height - GridTop - 2 * Inset) / CellHeight));
    }

    public static DexModesHits Paint(SKCanvas c, SKImageInfo info, DexModesScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        var header = new SKRect(0, 0, GridWidth, HeaderHeight);
        using (var font = new SKFont(face, 40f)) StoragePaint.BoxHeader(c, header, scene.Title.ToUpperInvariant(), font, scene.CanPrevious, scene.CanNext, 1f);

        var strip = new SKRect(0, HeaderHeight + 14f, GridWidth, HeaderHeight + 14f + StripHeight);
        var tabs = PaintTabs(c, strip, scene, face);

        var well = new SKRect(0, GridTop, GridWidth, height);
        StoragePaint.WellPanel(c, well, 1f);
        var (cells, columns, rows, cellHeight) = scene.BoxView ? PaintBox(c, well, scene, face) : PaintGrid(c, well, scene, face);

        var sheet = new SKRect(GridWidth + Gap, 0, DesignWidth, height);
        var (rowHits, actions) = PaintSheet(c, sheet, scene, face);

        c.Restore();
        SKRect Px(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new DexModesHits(
            Px(new SKRect(0, 0, 150, HeaderHeight)),
            Px(new SKRect(GridWidth - 150, 0, GridWidth, HeaderHeight)),
            [.. tabs.Select(Px)],
            Px(well),
            [.. cells.Select(cell => (cell.Index, Px(cell.Rect)))],
            [.. rowHits.Select(r => (r.Index, Px(r.Rect)))],
            [.. actions.Select(a => (a.Index, Px(a.Rect)))],
            columns, rows, cellHeight * unit);
    }

    // ── The strip: scopes as slanted tabs, label and count ──

    private static List<SKRect> PaintTabs(SKCanvas c, SKRect r, DexModesScene scene, SKTypeface face)
    {
        var hits = new List<SKRect>();
        const float slant = 22f;
        using var font = new SKFont(face, 28f);
        using var count = new SKFont(face, 22f);
        var n = scene.Tabs.Count;
        var step = n == 0 ? 0 : (r.Width - slant) / n;
        for (var i = 0; i < n; i++)
        {
            var tab = new SKRect(r.Left + i * step, r.Top + 4f, r.Left + (i + 1) * step + slant - 8f, r.Bottom - 4f);
            hits.Add(tab);
            var active = i == scene.Tab;
            using (var path = Parallelogram(tab, slant))
            {
                if (active) Gradient(c, path, T.ChipTop, T.ChipBottom);
                else using (var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true }) c.DrawPath(path, band);
                Outline(c, path, active ? T.Rim : T.Rim.WithAlpha(90), active ? 4f : 2f);
            }
            var (label, total) = scene.Tabs[i];
            var mid = tab.Left + slant / 2 + (tab.Width - slant) / 2;
            // Label above, count below: nine generations fit the strip at any width.
            Centered(c, label, mid, tab.MidY - 12f, font, active ? T.ChipInk : T.Value);
            Centered(c, total, mid, tab.MidY + 16f, count, active ? T.ChipInk : T.SubInk);
        }
        return hits;
    }

    // ── The dex grid ──

    private static (List<(int Index, SKRect Rect)>, int, int, float) PaintGrid(SKCanvas c, SKRect well, DexModesScene scene, SKTypeface face)
    {
        var hits = new List<(int, SKRect)>();
        var columns = Columns;
        var rows = Math.Max(1, (int)((well.Height - 2 * Inset) / CellHeight));
        if (scene.Cells.Count == 0)
        {
            using var big = new SKFont(face, 40f);
            Centered(c, scene.Empty, well.MidX, well.MidY, big, T.SubInk);
            return (hits, columns, rows, CellHeight);
        }
        var totalRows = (scene.Cells.Count + columns - 1) / columns;
        var left = well.Left + Inset + 8f;
        var top = well.Top + Inset;
        var rowWidth = columns * CellWidth;

        c.Save();
        c.ClipRect(SKRect.Inflate(well, -4f, -4f));
        for (var row = 0; row < rows && scene.ScrollRow + row < totalRows; row++)
        {
            var band = new SKRect(left - 6f, top + row * CellHeight + 4f, left + rowWidth + 6f, top + (row + 1) * CellHeight - 4f);
            using var fill = new SKPaint { Color = EditorPaint.RowBand((scene.ScrollRow + row) % 2 == 0), IsAntialias = true };
            c.DrawRoundRect(band, 12f, 12f, fill);
        }
        using var number = new SKFont(face, 22f);
        var first = scene.ScrollRow * columns;
        for (var i = first; i < scene.Cells.Count && i < first + rows * columns; i++)
        {
            var at = i - first;
            var cell = SKRect.Create(left + at % columns * CellWidth, top + at / columns * CellHeight, CellWidth, CellHeight);
            hits.Add((i, cell));
            PaintCell(c, cell, scene.Cells[i], i == scene.Cursor, 2, number);
        }
        c.Restore();

        if (totalRows > rows)
        {
            var rail = new SKRect(well.Right - 16f, well.Top + Inset, well.Right - 10f, well.Bottom - Inset);
            using var track = new SKPaint { Color = T.Band.WithAlpha(120), IsAntialias = true };
            c.DrawRoundRect(rail, 3f, 3f, track);
            var span = Math.Max(24f, rail.Height * rows / totalRows);
            var y = rail.Top + (rail.Height - span) * scene.ScrollRow / Math.Max(1, totalRows - rows);
            using var thumb = new SKPaint { Color = T.Rim, IsAntialias = true };
            c.DrawRoundRect(new SKRect(rail.Left, y, rail.Right, y + span), 3f, 3f, thumb);
        }
        return (hits, columns, rows, CellHeight);
    }

    // ── One planned box: the game's six by five ──

    private static (List<(int Index, SKRect Rect)>, int, int, float) PaintBox(SKCanvas c, SKRect well, DexModesScene scene, SKTypeface face)
    {
        var hits = new List<(int, SKRect)>();
        var width = BoxColumns * BoxCellWidth;
        var height = BoxRows * BoxCellHeight;
        var left = well.MidX - width / 2;
        var top = well.Top + Math.Max(Inset, (well.Height - height) / 2);
        for (var row = 0; row < BoxRows; row++)
        {
            var band = new SKRect(left - 8f, top + row * BoxCellHeight + 4f, left + width + 8f, top + (row + 1) * BoxCellHeight - 4f);
            using var fill = new SKPaint { Color = EditorPaint.RowBand(row % 2 == 0), IsAntialias = true };
            c.DrawRoundRect(band, 12f, 12f, fill);
        }
        using var number = new SKFont(face, 24f);
        for (var i = 0; i < scene.Cells.Count && i < BoxColumns * BoxRows; i++)
        {
            var cell = SKRect.Create(left + i % BoxColumns * BoxCellWidth, top + i / BoxColumns * BoxCellHeight, BoxCellWidth, BoxCellHeight);
            hits.Add((i, cell));
            PaintCell(c, cell, scene.Cells[i], i == scene.Cursor, 3, number);
        }
        return (hits, BoxColumns, BoxRows, BoxCellHeight);
    }

    /// <summary>A cell: the 40×30 icon at a whole-step scale standing on its row, the number
    /// under it, the ball when it counts, a star for a shiny, a diamond for a variant entry.</summary>
    private static void PaintCell(SKCanvas c, SKRect cell, DexModesCell entry, bool selected, int scale, SKFont number)
    {
        if (selected) Chip(c, SKRect.Inflate(cell, 4f, -2f), focused: true);
        if (entry.Empty)
        {
            using var slot = new SKPaint { Color = T.Rim.WithAlpha(60), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
            c.DrawRoundRect(SKRect.Inflate(cell, -24f, -24f), 10f, 10f, slot);
            return;
        }
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        if (entry.Sheet is { } sheet)
        {
            var lift = selected ? 6f : 0f;
            var w = 40f * scale;
            var h = 30f * scale;
            var dest = SKRect.Create(cell.MidX - w / 2, cell.Bottom - 34f - h - lift, w, h);
            if (entry.Owned) c.DrawImage(sheet, entry.Source, dest, sampling);
            else if (entry.Elsewhere)
            {
                using var dim = new SKPaint { Color = SKColors.White.WithAlpha(0x58) };
                c.SaveLayer(dim);
                c.DrawImage(sheet, entry.Source, dest, sampling);
                c.Restore();
            }
            else
            {
                using var silhouette = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(T.Silhouette, SKBlendMode.SrcIn) };
                c.DrawImage(sheet, entry.Source, dest, sampling, silhouette);
            }
        }
        var ink = selected ? T.ChipInk : entry.Owned ? T.DexNumber : T.SubInk;
        Centered(c, $"{entry.Number:000}", cell.MidX, cell.Bottom - 18f, number, ink);
        if (entry.Owned) DexPaint.Ball(c, cell.Right - 18f, cell.Top + 20f, 9f);
        if (entry.Shiny) Star(c, cell.Left + 20f, cell.Top + 20f, 10f);
        else if (entry.Variant) Diamond(c, cell.Left + 18f, cell.Top + 20f, 7f);
    }

    // ── The sheet: the entry's page, its facts and switches, the actions ──

    private static (List<(int Index, SKRect Rect)> Rows, List<(int Index, SKRect Rect)> Actions) PaintSheet(
        SKCanvas c, SKRect r, DexModesScene scene, SKTypeface face)
    {
        const float radius = 28f;
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(r, radius, radius, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), radius, radius, rim);

        var rows = new List<(int, SKRect)>();
        var actions = new List<(int, SKRect)>();
        using var big = new SKFont(face, 40f);
        using var mid = new SKFont(face, 28f);
        using var small = new SKFont(face, 24f);

        // The tab: number block then the name, run off the sheet's left edge.
        var tab = new SKRect(r.Left - 2f, r.Top + 26f, r.Left + r.Width * 0.86f, r.Top + 96f);
        using (var path = Tab(tab, 0f, 34f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        var numberText = scene.Number > 0 ? $"No.{scene.Number:000}" : "";
        Text(c, numberText, tab.Left + 30f, tab.MidY, big, Pksm.Ink, 220f);
        var nameLeft = tab.Left + 40f + (numberText.Length > 0 ? big.MeasureFallback(numberText) + 18f : 0f);
        Text(c, scene.Name, nameLeft, tab.MidY, big, Pksm.Ink, tab.Right - 40f - nameLeft);
        Text(c, scene.Line, r.Left + 30f, tab.Bottom + 28f, small, T.SubInk, r.Width - 60f);

        // The Pokémon, at a whole-step scale, standing on the sheet's floor.
        var stage = new SKRect(r.Left + 24f, tab.Bottom + 50f, r.Right - 24f, tab.Bottom + 50f + 230f);
        var ground = stage.Bottom - 16f;
        if (scene.Sprite is { } sprite)
        {
            var scale = Math.Max(1, (int)Math.Min((stage.Width - 40f) / sprite.Width, (ground - stage.Top) / sprite.Height));
            var w = sprite.Width * scale;
            var h = sprite.Height * scale;
            var dest = SKRect.Create(MathF.Round(stage.MidX - w / 2), ground - h, w, h);
            var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
            if (scene.SpriteOwned) c.DrawImage(sprite, dest, sampling);
            else
            {
                using var silhouette = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(T.Silhouette, SKBlendMode.SrcIn) };
                c.DrawImage(sprite, dest, sampling, silhouette);
            }
        }
        using (var floor = new SKPaint { Color = T.Rim.WithAlpha(90) })
            c.DrawRect(new SKRect(stage.Left + 20f, ground, stage.Right - 20f, ground + 3f), floor);

        // Facts and switches: banded rows; a switch is the slanted chip when on.
        var y = stage.Bottom + 12f;
        var bottom = r.Bottom - 92f;
        const float rowHeight = 50f;
        for (var i = 0; i < scene.Rows.Count; i++)
        {
            var row = new SKRect(r.Left + 24f, y, r.Right - 24f, y + rowHeight - 6f);
            if (row.Bottom > bottom) break;
            var line = scene.Rows[i];
            if (line.Toggle is { } on)
            {
                rows.Add((i, row));
                using (var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true }) c.DrawRoundRect(row, 10f, 10f, band);
                var plate = new SKRect(row.Right - 130f, row.Top + 4f, row.Right - 6f, row.Bottom - 4f);
                using (var path = Parallelogram(plate, 14f))
                {
                    if (on) Gradient(c, path, T.ChipTop, T.ChipBottom);
                    else using (var off = new SKPaint { Color = T.Band.WithAlpha(170), IsAntialias = true }) c.DrawPath(path, off);
                    Outline(c, path, on ? T.Rim : T.Rim.WithAlpha(90), on ? 3f : 2f);
                }
                Centered(c, on ? "ON" : "OFF", plate.MidX, plate.MidY, small, on ? T.ChipInk : T.SubInk);
                Text(c, line.Label, row.Left + 20f, row.MidY, mid, T.Value, row.Width - 170f);
                if (line.Value.Length > 0) Right(c, line.Value, plate.Left - 16f, row.MidY, small, T.SubInk);
            }
            else
            {
                using (var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true }) c.DrawRoundRect(row, 10f, 10f, band);
                var valueWidth = line.Value.Length > 0 ? Math.Min(small.MeasureFallback(line.Value), row.Width * 0.62f) : 0f;
                Text(c, line.Label, row.Left + 20f, row.MidY, small, T.SubInk, row.Width - valueWidth - 50f);
                if (line.Value.Length > 0) Text(c, Fitted(line.Value, small, row.Width * 0.62f), row.Right - 18f - valueWidth, row.MidY, small, T.Value, valueWidth + 2f);
            }
            y += rowHeight;
        }

        y += 8f;
        for (var i = 0; i < scene.Actions.Count; i++)
        {
            var row = new SKRect(r.Left + 24f, y, r.Right - 24f, y + 60f);
            if (row.Bottom > bottom) break;
            actions.Add((i, row));
            using (var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true }) c.DrawRoundRect(row, 10f, 10f, band);
            Arrow(c, row.Left + 30f, row.MidY, 1, T.Rim);
            Text(c, scene.Actions[i], row.Left + 58f, row.MidY, mid, T.Value, row.Width - 80f);
            y += 66f;
        }

        using (var rule = new SKPaint { Color = T.Rim.WithAlpha(70) })
            c.DrawRect(new SKRect(r.Left + 24f, r.Bottom - 84f, r.Right - 24f, r.Bottom - 82f), rule);
        Text(c, scene.Progress, r.Left + 30f, r.Bottom - 58f, small, T.Value, r.Width - 60f);
        if (scene.Status.Length > 0) Text(c, scene.Status, r.Left + 30f, r.Bottom - 24f, small, T.SubInk, r.Width - 60f);
        return (rows, actions);
    }

    /// <summary>The shiny mark: a five-point star in the shiny gold.</summary>
    private static void Star(SKCanvas c, float cx, float cy, float radius)
    {
        using var paint = new SKPaint { Color = Pksm.ShinyGold, IsAntialias = true };
        using var rim = new SKPaint { Color = Pksm.Ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        using var path = new SKPath();
        for (var k = 0; k < 10; k++)
        {
            var angle = -MathF.PI / 2 + k * MathF.PI / 5;
            var d = k % 2 == 0 ? radius : radius * 0.45f;
            var (x, y) = (cx + d * MathF.Cos(angle), cy + d * MathF.Sin(angle));
            if (k == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }
        path.Close();
        c.DrawPath(path, paint);
        c.DrawPath(path, rim);
    }

    /// <summary>A variant entry (a form, ♀, Gigantamax, a sweet): a small diamond in the rim's colour.</summary>
    private static void Diamond(SKCanvas c, float cx, float cy, float size)
    {
        using var paint = new SKPaint { Color = T.Rim, IsAntialias = true };
        using var path = new SKPath();
        path.MoveTo(cx, cy - size);
        path.LineTo(cx + size, cy);
        path.LineTo(cx, cy + size);
        path.LineTo(cx - size, cy);
        path.Close();
        c.DrawPath(path, paint);
    }
}
