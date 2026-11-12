using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>
/// One row of a picker: its name, an optional effect under it, the item art, a type badge,
/// the move category (an id the host draws), a right-hand tag, and a muted look for
/// entries outside the legal set.
/// </summary>
public sealed record PickerLine(
    string Name,
    string? Detail = null,
    SKImage? Icon = null,
    string? TypeName = null,
    SKColor? TypeColor = null,
    int? Category = null,
    string? Tag = null,
    SKColor? TagColor = null,
    bool Muted = false,
    int? TypeId = null);

/// <summary>A toggle beside the search plate ("✓ Holdable" / "Show all", "Type: All").</summary>
public sealed record PickerChip(string Label, bool On, SKColor? Accent = null);

/// <summary>
/// Everything a picker shows at one moment. Rows come through <see cref="Line"/> so only the
/// visible ones are built (their icons decoded); <see cref="Offset"/> is the list's scroll in
/// design pixels, so a drag moves it smoothly.
/// </summary>
public sealed record PickerScene(
    string Title,
    string Query,
    int Count,
    Func<int, PickerLine> Line,
    int Cursor,
    float Offset,
    IReadOnlyList<PickerChip> Chips,
    string Status,
    IReadOnlyList<(string Key, string Label)> Keys,
    bool DetailRows,
    // True when the host lays a live card (the move or nature preview) over the space it is given.
    bool Preview = false,
    Action<SKCanvas, SKRect, int>? DrawCategory = null,
    // The app's own pixel type plate (TypePlates), drawn for each row's type id.
    Action<SKCanvas, SKRect, int>? DrawType = null);

/// <summary>Where everything landed, in canvas pixels, plus the list's geometry in design pixels.</summary>
public sealed record PickerHits(
    float Unit,
    SKRect Panel,
    SKRect List,
    IReadOnlyList<(int Index, SKRect Rect)> Rows,
    SKRect Search,
    IReadOnlyList<SKRect> Chips,
    IReadOnlyList<(string Key, SKRect Rect)> Keys,
    SKRect Side,
    float RowHeight,
    float ListHeight,
    float MaxOffset);

/// <summary>
/// The searchable picker for big lists (held items, moves, abilities, balls, locations…) in
/// the DA: the panel with its title on the banner tab, the search plate with its toggles, the
/// rows on alternating bands with item art in whole pixel steps, the slanted cyan chip as the
/// cursor, what A picks, and the keys at the foot. Only the visible rows are painted.
/// </summary>
public static class PickerPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float BannerHeight = 84f;
    private const float SearchHeight = 72f;
    private const float StatusHeight = 58f;
    private const float FootHeight = 76f;
    private const float Pad = 28f;
    private const float RowInset = 14f;
    private const float SideWidth = 600f;
    private const float StackedSide = 380f;

    public static float RowHeight(bool detailRows) => detailRows ? 132f : 88f;

    public static PickerHits Paint(SKCanvas c, SKImageInfo info, PickerScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        using var title = new SKFont(face, 44f);
        using var name = new SKFont(face, 36f);
        using var detail = new SKFont(face, 26f);
        using var small = new SKFont(face, 24f);
        using var body = new SKFont(face, 31f);

        var side = scene.Preview && height <= 1300f;
        var width = side ? DesignWidth - 48f : MathF.Min(1400f, DesignWidth - 48f);
        var panel = new SKRect((DesignWidth - width) / 2f, 20f, (DesignWidth + width) / 2f, height - 20f);

        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(panel, 26f, 26f, fill);
        TrainerCardPaint.Stripes(c, SKRect.Inflate(panel, -10f, -10f));
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f })
            c.DrawRoundRect(SKRect.Inflate(panel, -2f, -2f), 26f, 26f, rim);

        var banner = new SKRect(panel.Left + 12f, panel.Top + 12f, panel.Left + MathF.Min(panel.Width - 90f, 1100f), panel.Top + BannerHeight);
        using (var path = Tab(banner, 16f, 34f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        Text(c, scene.Title, banner.Left + 28f, banner.MidY, title, Pksm.Ink, banner.Width - 80f);

        // The column the list takes, and the room the live card gets.
        var listLeft = panel.Left + Pad;
        var listRight = side ? panel.Right - Pad - SideWidth - 20f : panel.Right - Pad;
        var footTop = panel.Bottom - FootHeight;
        var sideRect = SKRect.Empty;
        if (side) sideRect = new SKRect(listRight + 20f, panel.Top + BannerHeight + 16f, panel.Right - Pad, footTop - 8f);
        else if (scene.Preview)
        {
            sideRect = new SKRect(listLeft, footTop - StackedSide, listRight, footTop - 8f);
            footTop -= StackedSide;
        }

        // The search plate and its toggles.
        var top = panel.Top + BannerHeight + 16f;
        var chipRects = new List<SKRect>();
        var chipRight = listRight;
        for (var i = scene.Chips.Count - 1; i >= 0; i--)
        {
            var chip = scene.Chips[i];
            var w = CheckedWidth(chip.Label, body) + 70f;
            var r = new SKRect(chipRight - w, top, chipRight, top + SearchHeight - 8f);
            chipRects.Insert(0, r);
            PaintChip(c, r, chip, body);
            chipRight = r.Left - 14f;
        }
        var search = new SKRect(listLeft, top, chipRight, top + SearchHeight - 8f);
        PaintSearch(c, search, scene.Query, body);

        // The status plate: what A picks, so pad aim can be misread but never mispicked.
        var status = new SKRect(listLeft, footTop - StatusHeight, listRight, footTop - 6f);
        using (var fill = new SKPaint { Color = T.Band.WithAlpha(120), IsAntialias = true }) c.DrawRoundRect(status, 12f, 12f, fill);
        Text(c, scene.Status, status.Left + 24f, status.MidY, body, T.Value, status.Width - 48f);

        // The list well.
        var well = new SKRect(listLeft, search.Bottom + 16f, listRight, status.Top - 12f);
        StoragePaint.WellPanel(c, well, 1f);
        var rowHeight = RowHeight(scene.DetailRows);
        var inner = new SKRect(well.Left + RowInset, well.Top + RowInset, well.Right - RowInset - 18f, well.Bottom - RowInset);
        var listHeight = MathF.Max(rowHeight, inner.Height);
        var maxOffset = MathF.Max(0f, scene.Count * rowHeight - listHeight);
        var offset = Math.Clamp(scene.Offset, 0f, maxOffset);
        var rows = new List<(int, SKRect)>();
        if (scene.Count == 0)
        {
            Centered(c, scene.Query.Length > 0 ? $"Nothing matches “{scene.Query}”." : "Nothing to pick here.", well.MidX, well.MidY - 22f, name, T.Value);
            Centered(c, scene.Chips.Count > 0 ? "Change the search, or a toggle beside it." : "Change the search, or B to leave.", well.MidX, well.MidY + 26f, detail, T.SubInk);
        }
        else
        {
            c.Save();
            c.ClipRect(SKRect.Inflate(well, -4f, -4f));
            var first = Math.Max(0, (int)(offset / rowHeight));
            var last = Math.Min(scene.Count - 1, (int)((offset + listHeight) / rowHeight));
            for (var i = first; i <= last; i++)
            {
                var y = inner.Top + i * rowHeight - offset;
                var row = new SKRect(inner.Left, y, inner.Right, y + rowHeight - 8f);
                rows.Add((i, row));
                PaintRow(c, row, scene.Line(i), i, i == scene.Cursor, scene, name, detail, small);
            }
            c.Restore();

            if (maxOffset > 0f)
            {
                var rail = new SKRect(well.Right - RowInset - 10f, inner.Top, well.Right - RowInset - 4f, inner.Bottom);
                using var track = new SKPaint { Color = T.Band.WithAlpha(120), IsAntialias = true };
                c.DrawRoundRect(rail, 3f, 3f, track);
                var span = MathF.Max(28f, rail.Height * listHeight / (scene.Count * rowHeight));
                var at = rail.Top + (rail.Height - span) * offset / maxOffset;
                using var thumb = new SKPaint { Color = T.Rim, IsAntialias = true };
                c.DrawRoundRect(new SKRect(rail.Left, at, rail.Right, at + span), 3f, 3f, thumb);
            }
        }

        // The live card's frame; the host lays its card inside.
        if (!sideRect.IsEmpty)
        {
            using var fill = new SKPaint { Color = T.Band.WithAlpha(90), IsAntialias = true };
            c.DrawRoundRect(sideRect, 18f, 18f, fill);
            using var rim = new SKPaint { Color = T.Rim.WithAlpha(150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f };
            c.DrawRoundRect(SKRect.Inflate(sideRect, -1.5f, -1.5f), 18f, 18f, rim);
        }

        // The keys at the foot.
        var keys = new List<(string, SKRect)>();
        var x = panel.Left + Pad;
        var mid = panel.Bottom - FootHeight / 2f - 4f;
        foreach (var (key, label) in scene.Keys)
        {
            var r = PadMenuPaint.Key(c, key, label, x, mid, detail, body);
            keys.Add((key, r));
            x = r.Right + 32f;
        }

        c.Restore();
        SKRect Up(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new PickerHits(unit, Up(panel), Up(well), [.. rows.Select(r => (r.Item1, Up(r.Item2)))], Up(search),
            [.. chipRects.Select(Up)], [.. keys.Select(k => (k.Item1, Up(k.Item2)))], sideRect.IsEmpty ? SKRect.Empty : Up(sideRect),
            rowHeight, listHeight, maxOffset);
    }

    private static void PaintRow(SKCanvas c, SKRect row, PickerLine line, int index, bool focused, PickerScene scene,
        SKFont name, SKFont detail, SKFont small)
    {
        if (focused) Chip(c, row, focused: true);
        else
        {
            using var band = new SKPaint { Color = EditorPaint.RowBand(index % 2 == 0), IsAntialias = true };
            c.DrawRoundRect(row, 12f, 12f, band);
        }
        var ink = focused ? T.ChipInk : line.Muted ? T.SubInk : T.Value;
        var soft = focused ? T.ChipInk.WithAlpha(210) : T.SubInk;
        var left = row.Left + 30f;

        if (line.Icon is { } icon)
        {
            var box = SKRect.Create(left, row.MidY - 36f, 72f, 72f);
            using var fade = new SKPaint { Color = SKColors.White.WithAlpha(line.Muted && !focused ? (byte)110 : (byte)255) };
            c.DrawImage(icon, Fit(icon, box), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), fade);
            left += 90f;
        }

        // The type badge over the category icon, the games' move-list column.
        if (line.TypeName is not null || line.Category is not null)
        {
            var hasBoth = line.TypeName is not null && line.Category is not null;
            var badgeTop = hasBoth ? row.MidY - 38f : row.MidY - 20f;
            if (line.TypeName is { } type)
            {
                var badge = SKRect.Create(left, badgeTop, 136f, 40f);
                if (line.TypeId is { } typeId && scene.DrawType is { } plate)
                {
                    if (line.Muted && !focused)
                    {
                        c.SaveLayer(new SKPaint { Color = SKColors.White.WithAlpha(150) });
                        plate(c, badge, typeId);
                        c.Restore();
                    }
                    else plate(c, badge, typeId);
                }
                else
                using (var path = Parallelogram(badge, 10f))
                {
                    var color = line.TypeColor ?? T.Rim;
                    using var paint = new SKPaint { Color = line.Muted && !focused ? color.WithAlpha(150) : color, IsAntialias = true };
                    c.DrawPath(path, paint);
                    Outline(c, path, Pksm.Ink, 2f);
                    Centered(c, Fitted(type.ToUpperInvariant(), small, 110f), badge.MidX, badge.MidY, small, Readable(color));
                }
            }
            if (line.Category is { } category && scene.DrawCategory is { } draw)
            {
                var r = SKRect.Create(left + 44f, hasBoth ? row.MidY + 8f : row.MidY - 16f, 48f, 32f);
                draw(c, r, category);
            }
            left += 156f;
        }

        var right = row.Right - 30f;
        if (line.Tag is { Length: > 0 } tag)
        {
            var w = CheckedWidth(tag, small) + 32f;
            var plate = new SKRect(right - w, row.MidY - 20f, right, row.MidY + 20f);
            var color = line.TagColor ?? T.Rim;
            using (var fill = new SKPaint { Color = focused ? Pksm.Ink.WithAlpha(60) : color.WithAlpha(46), IsAntialias = true })
                c.DrawRoundRect(plate, 8f, 8f, fill);
            using (var rim = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f })
                c.DrawRoundRect(SKRect.Inflate(plate, -1.25f, -1.25f), 8f, 8f, rim);
            Checked(c, tag, plate.MidX, plate.MidY, small, focused ? T.ChipInk : color);
            right = plate.Left - 18f;
        }

        var max = right - left;
        if (line.Detail is { Length: > 0 } more)
        {
            var lines = Wrap(more, detail, max).ToList();
            if (lines.Count > 2) lines = [lines[0], Fitted(string.Join(' ', lines.Skip(1)), detail, max)];
            var nameMid = lines.Count > 1 ? row.MidY - 30f : row.MidY - 17f;
            Text(c, line.Name, left, nameMid, name, ink, max);
            for (var i = 0; i < lines.Count; i++)
                Text(c, lines[i], left, nameMid + 40f + i * 30f, detail, soft, max);
        }
        else Text(c, line.Name, left, row.MidY, name, ink, max);
    }

    // The pixel font has no check mark: a leading "✓ " is drawn as two strokes instead.
    private const string Check = "\u2713 ";

    private static float CheckedWidth(string text, SKFont font) =>
        text.StartsWith(Check, StringComparison.Ordinal) ? font.Size * 0.8f + font.MeasureFallback(text[Check.Length..]) : font.MeasureFallback(text);

    /// <summary>Centred text, its leading check mark drawn in strokes.</summary>
    private static void Checked(SKCanvas c, string text, float cx, float mid, SKFont font, SKColor color)
    {
        if (!text.StartsWith(Check, StringComparison.Ordinal)) { Centered(c, text, cx, mid, font, color); return; }
        var rest = text[Check.Length..];
        var mark = font.Size * 0.8f;
        var left = cx - (mark + font.MeasureFallback(rest)) / 2f;
        var s = font.Size * 0.5f;
        using (var pen = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = MathF.Max(2.5f, font.Size / 9f), StrokeCap = SKStrokeCap.Square, StrokeJoin = SKStrokeJoin.Miter })
        using (var path = new SKPath())
        {
            path.MoveTo(left + s * 0.05f, mid);
            path.LineTo(left + s * 0.45f, mid + s * 0.4f);
            path.LineTo(left + s * 1.1f, mid - s * 0.5f);
            c.DrawPath(path, pen);
        }
        Text(c, rest, left + mark, mid, font, color, float.MaxValue);
    }

    private static SKColor Readable(SKColor fill) =>
        fill.Red * 0.299f + fill.Green * 0.587f + fill.Blue * 0.114f > 150f ? Pksm.Ink : SKColors.White;

    private static void PaintChip(SKCanvas c, SKRect r, PickerChip chip, SKFont font)
    {
        using var path = Parallelogram(r, 16f);
        if (chip.On)
        {
            if (chip.Accent is { } accent)
            {
                using var paint = new SKPaint { Color = accent, IsAntialias = true };
                c.DrawPath(path, paint);
            }
            else Gradient(c, path, T.ChipTop, T.ChipBottom);
            Outline(c, path, T.Rim, 4f);
            Checked(c, chip.Label, r.MidX, r.MidY, font, chip.Accent is { } a ? Readable(a) : T.ChipInk);
        }
        else
        {
            using var paint = new SKPaint { Color = T.Band.WithAlpha(150), IsAntialias = true };
            c.DrawPath(path, paint);
            Outline(c, path, T.Rim.WithAlpha(150), 3f);
            Checked(c, chip.Label, r.MidX, r.MidY, font, T.Value);
        }
    }

    /// <summary>The search plate, as the bag's: a magnifier and what is typed, or the invitation to type.</summary>
    private static void PaintSearch(SKCanvas c, SKRect r, string query, SKFont font)
    {
        using (var fill = new SKPaint { Color = T.Band.WithAlpha(170), IsAntialias = true }) c.DrawRoundRect(r, 12f, 12f, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), 12f, 12f, rim);
        using (var glass = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f, StrokeCap = SKStrokeCap.Round })
        {
            var cx = r.Left + 36f;
            c.DrawCircle(cx, r.MidY - 3f, 11f, glass);
            c.DrawLine(cx + 8f, r.MidY + 5f, cx + 17f, r.MidY + 14f, glass);
        }
        if (query.Length > 0) Text(c, query, r.Left + 70f, r.MidY, font, T.Value, r.Width - 100f);
        else Text(c, "Search… (tap, or Select)", r.Left + 70f, r.MidY, font, T.SubInk, r.Width - 100f);
    }
}
