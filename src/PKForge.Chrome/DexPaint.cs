using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One species in the grid: its number, its box icon (null while it loads) and its marks.</summary>
public sealed record DexSceneCell(int Number, SKImage? Sheet, SKRect Source, bool Seen, bool Caught,
    bool Staged = false, bool Dimmed = false, bool Ticked = false);

/// <summary>The species under the cursor, as its page in the sheet.</summary>
public sealed record DexSceneSheet(
    int Number,
    string Name,
    string Region,
    SKImage? Sprite,
    SKImage? Shiny,
    // 0 unseen, 1 seen, 2 caught.
    int State,
    bool Staged,
    string? Forms,
    IReadOnlyList<string> Actions,
    // Not null in the living dex gaps: whether the species is picked for generation.
    bool? Picked = null);

/// <summary>Everything the Pokédex screen shows at one moment.</summary>
public sealed record DexScene(
    string Title,
    bool CanPrevious,
    bool CanNext,
    IReadOnlyList<(string Label, int Count)> Filters,
    int Filter,
    string Search,
    IReadOnlyList<DexSceneCell> Cells,
    int Cursor,
    int ScrollRow,
    DexSceneSheet? Sheet,
    string Progress,
    string Status,
    string Empty,
    bool CanEdit = true);

/// <summary>Where each touch target landed on the last paint, in canvas pixels.</summary>
public sealed record DexHits(
    SKRect Previous,
    SKRect Next,
    IReadOnlyList<SKRect> Filters,
    SKRect Search,
    SKRect Grid,
    IReadOnlyList<(int Index, SKRect Rect)> Cells,
    IReadOnlyList<SKRect> States,
    IReadOnlyList<(int Index, SKRect Rect)> Actions,
    int Columns,
    int VisibleRows,
    float CellHeight);

/// <summary>
/// The Pokédex as one painted screen: the generation in the box banner with its chevrons, the
/// filters as a strip of slanted tabs, every species of the list as box icons standing on the
/// well's banded rows, and the species under the cursor on a sheet at the right, as its page
/// in a games' Pokédex: number and name on the tab, the Pokémon large with its shiny beside it,
/// the unseen / seen / caught switch, then what can be done with it. Design pixels of a 1872
/// wide screen, scaled by a unit.
/// </summary>
public static class DexPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float HeaderHeight = 88f;
    private const float StripHeight = 70f;
    private const float GridWidth = 1180f;
    private const float CellWidth = 112f;
    private const float CellHeight = 112f;
    private const float Inset = 20f;
    private const float Gap = 24f;

    private static float GridTop => HeaderHeight + 14f + StripHeight + 12f;

    public static int Columns => (int)((GridWidth - 2 * Inset - 16f) / CellWidth);

    /// <summary>How many whole rows of species the grid shows on a canvas of this size.</summary>
    public static int VisibleRows(SKSizeI size)
    {
        if (size.Width <= 0) return 1;
        var unit = size.Width / DesignWidth;
        var height = size.Height / unit;
        return Math.Max(1, (int)((height - GridTop - 2 * Inset) / CellHeight));
    }

    public static DexHits Paint(SKCanvas c, SKImageInfo info, DexScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        var header = new SKRect(0, 0, GridWidth, HeaderHeight);
        using (var font = new SKFont(face, 40f)) StoragePaint.BoxHeader(c, header, scene.Title.ToUpperInvariant(), font, scene.CanPrevious, scene.CanNext, 1f);

        var strip = new SKRect(0, HeaderHeight + 14f, GridWidth, HeaderHeight + 14f + StripHeight);
        var (filters, search) = PaintStrip(c, strip, scene, face);

        var well = new SKRect(0, GridTop, GridWidth, height);
        StoragePaint.WellPanel(c, well, 1f);
        var cells = PaintGrid(c, well, scene, face);

        var sheet = new SKRect(GridWidth + Gap, 0, DesignWidth, height);
        var (states, actions) = PaintSheet(c, sheet, scene, face);

        c.Restore();
        SKRect Px(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new DexHits(
            Px(new SKRect(0, 0, 150, HeaderHeight)),
            Px(new SKRect(GridWidth - 150, 0, GridWidth, HeaderHeight)),
            [.. filters.Select(Px)],
            Px(search),
            Px(well),
            [.. cells.Select(cell => (cell.Index, Px(cell.Rect)))],
            [.. states.Select(Px)],
            [.. actions.Select(a => (a.Index, Px(a.Rect)))],
            Columns,
            Math.Max(1, (int)((well.Height - 2 * Inset) / CellHeight)),
            CellHeight * unit);
    }

    // ── The strip: the filters as slanted tabs, the search plate at the end ──

    private static (List<SKRect> Filters, SKRect Search) PaintStrip(SKCanvas c, SKRect r, DexScene scene, SKTypeface face)
    {
        var hits = new List<SKRect>();
        const float slant = 22f;
        const float searchWidth = 300f;
        using var font = new SKFont(face, 30f);
        using var count = new SKFont(face, 24f);
        var n = scene.Filters.Count;
        var room = r.Width - searchWidth - 20f;
        var step = n == 0 ? 0 : (room - slant) / n;
        for (var i = 0; i < n; i++)
        {
            var tab = new SKRect(r.Left + i * step, r.Top + 4f, r.Left + (i + 1) * step + slant - 8f, r.Bottom - 4f);
            hits.Add(tab);
            var active = i == scene.Filter;
            using (var path = Parallelogram(tab, slant))
            {
                if (active) Gradient(c, path, T.ChipTop, T.ChipBottom);
                else using (var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true }) c.DrawPath(path, band);
                Outline(c, path, active ? T.Rim : T.Rim.WithAlpha(90), active ? 4f : 2f);
            }
            var (label, total) = scene.Filters[i];
            var ink = active ? T.ChipInk : T.Value;
            var mid = tab.Left + slant / 2 + (tab.Width - slant) / 2;
            var labelWidth = font.MeasureFallback(label);
            var countText = $"{total}";
            var countWidth = count.MeasureFallback(countText);
            var x = mid - (labelWidth + 14f + countWidth) / 2;
            Text(c, label, x, tab.MidY, font, ink, labelWidth + 2f);
            Text(c, countText, x + labelWidth + 14f, tab.MidY + 2f, count, active ? T.ChipInk : T.SubInk, countWidth + 2f);
        }

        var search = new SKRect(r.Right - searchWidth, r.Top + 4f, r.Right, r.Bottom - 4f);
        using (var fill = new SKPaint { Color = T.Band.WithAlpha(170), IsAntialias = true }) c.DrawRoundRect(search, 12f, 12f, fill);
        using (var rim = new SKPaint { Color = scene.Search.Length > 0 ? T.Rim : T.Rim.WithAlpha(110), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(search, -1.5f, -1.5f), 12f, 12f, rim);
        using (var glass = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f, StrokeCap = SKStrokeCap.Round })
        {
            var cx = search.Left + 32f;
            c.DrawCircle(cx, search.MidY - 3f, 10f, glass);
            c.DrawLine(cx + 7f, search.MidY + 4f, cx + 15f, search.MidY + 12f, glass);
        }
        if (scene.Search.Length > 0) Text(c, scene.Search, search.Left + 60f, search.MidY, font, T.Value, search.Width - 76f);
        else Text(c, "Name or No.", search.Left + 60f, search.MidY, font, T.SubInk, search.Width - 76f);
        return (hits, search);
    }

    // ── The grid: box icons on banded rows, the cursor as the slanted chip ──

    private static List<(int Index, SKRect Rect)> PaintGrid(SKCanvas c, SKRect well, DexScene scene, SKTypeface face)
    {
        var hits = new List<(int, SKRect)>();
        if (scene.Cells.Count == 0)
        {
            using var big = new SKFont(face, 40f);
            Centered(c, scene.Empty, well.MidX, well.MidY, big, T.SubInk);
            return hits;
        }
        var columns = Columns;
        var rows = Math.Max(1, (int)((well.Height - 2 * Inset) / CellHeight));
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

        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        using var number = new SKFont(face, 22f);
        using var silhouette = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(T.Silhouette, SKBlendMode.SrcIn) };
        using var dim = new SKPaint { Color = SKColors.White.WithAlpha(0x58) };
        var first = scene.ScrollRow * columns;
        for (var i = first; i < scene.Cells.Count && i < first + rows * columns; i++)
        {
            var at = i - first;
            var cell = SKRect.Create(left + at % columns * CellWidth, top + at / columns * CellHeight, CellWidth, CellHeight);
            hits.Add((i, cell));
            var entry = scene.Cells[i];
            var selected = i == scene.Cursor;
            if (selected) Chip(c, SKRect.Inflate(cell, 4f, -2f), focused: true);

            if (entry.Sheet is { } sheet)
            {
                // 40×30 icons at ×2, standing on the row; a hop up under the cursor.
                var lift = selected ? 6f : 0f;
                var dest = SKRect.Create(cell.MidX - 40f, cell.Top + 18f - lift, 80f, 60f);
                if (!entry.Seen) c.DrawImage(sheet, entry.Source, dest, sampling, silhouette);
                else if (entry.Dimmed)
                {
                    c.SaveLayer(dim);
                    c.DrawImage(sheet, entry.Source, dest, sampling);
                    c.Restore();
                }
                else c.DrawImage(sheet, entry.Source, dest, sampling);
            }
            var ink = selected ? T.ChipInk : entry.Staged ? T.Rim : entry.Seen ? T.DexNumber : T.SubInk;
            Centered(c, $"{entry.Number:000}", cell.MidX, cell.Bottom - 18f, number, ink);
            if (entry.Staged && !selected) StagedMark(c, cell.Left + 16f, cell.Bottom - 18f);
            if (entry.Caught) Ball(c, cell.Right - 18f, cell.Top + 20f, 9f);
            if (entry.Ticked) Tick(c, cell.Left + 20f, cell.Top + 20f, 11f);
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
        return hits;
    }

    // ── The sheet: the species' page ──

    private static readonly string[] StateNames = ["Unseen", "Seen", "Caught"];

    private static (List<SKRect> States, List<(int Index, SKRect Rect)> Actions) PaintSheet(SKCanvas c, SKRect r, DexScene scene, SKTypeface face)
    {
        const float radius = 28f;
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(r, radius, radius, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), radius, radius, rim);

        var states = new List<SKRect>();
        var actions = new List<(int, SKRect)>();
        using var big = new SKFont(face, 40f);
        using var mid = new SKFont(face, 30f);
        using var small = new SKFont(face, 26f);

        if (scene.Sheet is not { } page)
        {
            Centered(c, "No species here.", r.MidX, r.MidY, mid, T.SubInk);
            Footnotes(c, r, scene, small);
            return (states, actions);
        }

        // The tab: the number in its own block, then the name, run off the sheet's left edge.
        var tab = new SKRect(r.Left - 2f, r.Top + 26f, r.Left + r.Width * 0.86f, r.Top + 96f);
        using (var path = Tab(tab, 0f, 34f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        var numberText = $"No.{page.Number:000}";
        Text(c, numberText, tab.Left + 30f, tab.MidY, big, Pksm.Ink, 220f);
        var nameLeft = tab.Left + 40f + big.MeasureFallback(numberText) + 18f;
        Text(c, page.State == 0 && page.Picked is null ? "?????" : page.Name, nameLeft, tab.MidY, big, Pksm.Ink, tab.Right - 40f - nameLeft);
        var line = page.Forms is { } forms ? $"{page.Region} · {forms}" : page.Region;
        Text(c, line, r.Left + 30f, tab.Bottom + 30f, small, T.SubInk, r.Width - 60f);

        // The Pokémon, large, standing on the sheet; its shiny smaller at its side.
        var stage = new SKRect(r.Left + 24f, tab.Bottom + 56f, r.Right - 24f, tab.Bottom + 56f + 300f);
        var ground = stage.Bottom - 24f;
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        using var silhouette = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(T.Silhouette, SKBlendMode.SrcIn) };
        var hidden = page.State == 0 && page.Picked is null;
        if (page.Sprite is { } sprite)
        {
            var box = new SKRect(stage.Left + 20f, stage.Top, stage.Left + stage.Width * 0.62f, ground);
            var fit = Fit(sprite, box);
            fit.Offset(0, ground - fit.Bottom);
            c.DrawImage(sprite, fit, sampling, hidden ? silhouette : null);
        }
        if (page.Shiny is { } shiny && !hidden)
        {
            var box = new SKRect(stage.Left + stage.Width * 0.62f, stage.Top + 90f, stage.Right - 10f, ground);
            var fit = Fit(shiny, box);
            fit.Offset(0, ground - fit.Bottom);
            c.DrawImage(shiny, fit, sampling);
            Centered(c, "★ Shiny", fit.MidX, stage.Top + 70f, small, Pksm.ShinyGold);
        }
        using (var floor = new SKPaint { Color = T.Rim.WithAlpha(90), IsAntialias = false })
            c.DrawRect(new SKRect(stage.Left + 20f, ground, stage.Right - 20f, ground + 3f), floor);

        var y = stage.Bottom + 10f;
        if (page.Picked is { } picked)
        {
            // Gaps: one switch, picked for generation or not.
            var row = new SKRect(r.Left + 24f, y, r.Right - 24f, y + 70f);
            states.Add(row);
            Chip(c, row, focused: picked);
            if (!picked) using (var band = new SKPaint { Color = T.PanelFill.WithAlpha(160), IsAntialias = true }) c.DrawRoundRect(SKRect.Inflate(row, -4f, -4f), 10f, 10f, band);
            Text(c, picked ? "Picked: A leaves it out" : "Not picked: A picks it", row.Left + 30f, row.MidY, mid, picked ? T.ChipInk : T.Value, row.Width - 60f);
            y = row.Bottom + 16f;
        }
        else
        {
            // Unseen / seen / caught as three slanted plates; the current one is the chip.
            const float slant = 20f;
            var width = (r.Width - 48f - slant) / 3f;
            for (var i = 0; i < 3; i++)
            {
                var plate = new SKRect(r.Left + 24f + i * width, y, r.Left + 24f + (i + 1) * width + slant - 8f, y + 70f);
                states.Add(plate);
                var on = i == page.State;
                using (var path = Parallelogram(plate, slant))
                {
                    if (on) Gradient(c, path, T.ChipTop, T.ChipBottom);
                    else using (var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true }) c.DrawPath(path, band);
                    Outline(c, path, on ? T.Rim : T.Rim.WithAlpha(90), on ? 4f : 2f);
                }
                var ink = !scene.CanEdit ? T.SubInk : on ? T.ChipInk : T.Value;
                var label = StateNames[i];
                var cx = plate.Left + (plate.Width + slant / 2) / 2;
                if (i == 2)
                {
                    var w = mid.MeasureFallback(label);
                    Ball(c, cx - w / 2 - 6f, plate.MidY, 10f);
                    Centered(c, label, cx + 12f, plate.MidY, mid, ink);
                }
                else Centered(c, label, cx, plate.MidY, mid, ink);
            }
            y += 70f + 12f;
            Text(c, page.Staged ? "Changed: saved when you leave." : "As in the save.", r.Left + 30f, y + 14f, small, page.Staged ? T.Rim : T.SubInk, r.Width - 60f);
            y += 40f;
        }

        // What can be done with it: banded rows, tapped to run.
        var bottom = r.Bottom - 90f;
        for (var i = 0; i < page.Actions.Count; i++)
        {
            var row = new SKRect(r.Left + 24f, y + i * 72f, r.Right - 24f, y + (i + 1) * 72f - 6f);
            if (row.Bottom > bottom) break;
            actions.Add((i, row));
            using (var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true }) c.DrawRoundRect(row, 10f, 10f, band);
            Arrow(c, row.Left + 30f, row.MidY, 1, T.Rim);
            Text(c, page.Actions[i], row.Left + 58f, row.MidY, mid, T.Value, row.Width - 80f);
        }

        Footnotes(c, r, scene, small);
        return (states, actions);
    }

    private static void Footnotes(SKCanvas c, SKRect r, DexScene scene, SKFont small)
    {
        using (var rule = new SKPaint { Color = T.Rim.WithAlpha(70) })
            c.DrawRect(new SKRect(r.Left + 24f, r.Bottom - 84f, r.Right - 24f, r.Bottom - 82f), rule);
        Text(c, scene.Progress, r.Left + 30f, r.Bottom - 58f, small, T.Value, r.Width - 60f);
        if (scene.Status.Length > 0) Text(c, scene.Status, r.Left + 30f, r.Bottom - 24f, small, T.SubInk, r.Width - 60f);
    }

    /// <summary>The caught mark: a small Poké Ball, red top and white base (game colors, not theme roles).</summary>
    public static void Ball(SKCanvas c, float cx, float cy, float radius)
    {
        using var red = new SKPaint { Color = new SKColor(0xE8, 0x48, 0x3C), IsAntialias = true };
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var rim = new SKPaint { Color = Pksm.Ink, Style = SKPaintStyle.Stroke, StrokeWidth = radius * 0.24f, IsAntialias = true };
        c.DrawCircle(cx, cy, radius, white);
        c.Save();
        c.ClipRect(new SKRect(cx - radius, cy - radius, cx + radius, cy));
        c.DrawCircle(cx, cy, radius, red);
        c.Restore();
        c.DrawLine(cx - radius, cy, cx + radius, cy, rim);
        c.DrawCircle(cx, cy, radius, rim);
        c.DrawCircle(cx, cy, radius * 0.34f, white);
        c.DrawCircle(cx, cy, radius * 0.34f, rim);
    }

    private static void Tick(SKCanvas c, float cx, float cy, float radius)
    {
        using var disc = new SKPaint { Color = T.Rim, IsAntialias = true };
        using var check = new SKPaint { Color = Pksm.Ink, Style = SKPaintStyle.Stroke, StrokeWidth = radius * 0.3f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        c.DrawCircle(cx, cy, radius, disc);
        c.DrawLine(cx - radius * 0.45f, cy, cx - radius * 0.1f, cy + radius * 0.4f, check);
        c.DrawLine(cx - radius * 0.1f, cy + radius * 0.4f, cx + radius * 0.5f, cy - radius * 0.35f, check);
    }

    /// <summary>A changed species: a small square diamond in the rim's colour.</summary>
    private static void StagedMark(SKCanvas c, float cx, float cy)
    {
        using var paint = new SKPaint { Color = T.Rim, IsAntialias = true };
        using var path = new SKPath();
        path.MoveTo(cx, cy - 6f);
        path.LineTo(cx + 6f, cy);
        path.LineTo(cx, cy + 6f);
        path.LineTo(cx - 6f, cy);
        path.Close();
        c.DrawPath(path, paint);
    }
}
