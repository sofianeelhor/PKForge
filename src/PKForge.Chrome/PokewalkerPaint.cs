using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>The Pokémon paired with the Pokéwalker; null on the scene when nobody is.</summary>
public sealed record PokewalkerSceneMon(string Name, string Detail, int Level, SKImage? Sprite);

/// <summary>One course cell: its name, whether it is open, and whether the save's language ever had it.</summary>
public sealed record PokewalkerSceneCourse(string Name, bool Unlocked, bool Special, bool Available);

/// <summary>Everything the Pokéwalker screen shows at one moment. <paramref name="Cursor"/> runs
/// over Steps (0), Watts (1), Unlock all (2), then the courses in order (3…).</summary>
public sealed record PokewalkerScene(
    PokewalkerSceneMon? Mon,
    string Steps,
    string Watts,
    IReadOnlyList<PokewalkerSceneCourse> Courses,
    int Cursor,
    bool CanEdit,
    string Status);

/// <summary>Where each focusable item landed on the last paint, in canvas pixels, in cursor order.</summary>
public sealed record PokewalkerHits(IReadOnlyList<SKRect> Items);

/// <summary>
/// The Pokéwalker as one painted screen: the device's name in the box banner, the Pokémon on a
/// stroll standing large at the left over its name plate, the steps and watts under it, and at
/// the right the 27 courses in three columns, each with its unlock mark, under the way to open
/// them all. Design pixels of a 1872 wide screen.
/// </summary>
public static class PokewalkerPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    public const int StepsItem = 0;
    public const int WattsItem = 1;
    public const int UnlockAllItem = 2;
    public const int FirstCourseItem = 3;
    public const int CourseColumns = 3;

    private const float HeaderHeight = 88f;
    private const float LeftColumn = 640f;

    public static PokewalkerHits Paint(SKCanvas c, SKImageInfo info, PokewalkerScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        var header = new SKRect(240f, 0, DesignWidth - 240f, HeaderHeight);
        using (var font = new SKFont(face, 40f)) StoragePaint.BoxHeader(c, header, "POKéWALKER", font, false, false, 1f);

        var card = new SKRect(0, HeaderHeight + 16f, DesignWidth, height);
        const float radius = 28f;
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(card, radius, radius, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(card, -1.5f, -1.5f), radius, radius, rim);

        using var big = new SKFont(face, 44f);
        using var mid = new SKFont(face, 32f);
        using var small = new SKFont(face, 26f);

        var items = new SKRect[FirstCourseItem + scene.Courses.Count];
        var body = new SKRect(card.Left, card.Top, card.Right, card.Bottom - 70f);
        PaintWalker(c, new SKRect(body.Left, body.Top, body.Left + LeftColumn, body.Bottom), scene, items, big, mid, small);
        using (var split = new SKPaint { Color = T.Rim.WithAlpha(70) })
            c.DrawRect(new SKRect(body.Left + LeftColumn, body.Top + 30f, body.Left + LeftColumn + 2f, body.Bottom - 20f), split);
        PaintCourses(c, new SKRect(body.Left + LeftColumn + 2f, body.Top, body.Right, body.Bottom), scene, items, mid, small);

        using (var rule = new SKPaint { Color = T.Rim.WithAlpha(70) })
            c.DrawRect(new SKRect(card.Left + 30f, card.Bottom - 70f, card.Right - 30f, card.Bottom - 68f), rule);
        Text(c, scene.Status, card.Left + 36f, card.Bottom - 35f, small, T.SubInk, card.Width - 72f);

        c.Restore();
        SKRect Px(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new PokewalkerHits([.. items.Select(Px)]);
    }

    private static void PaintWalker(SKCanvas c, SKRect area, PokewalkerScene scene, SKRect[] items, SKFont big, SKFont mid, SKFont small)
    {
        SectionTab(c, new SKRect(area.Left + 40f, area.Top + 28f, area.Left + 330f, area.Top + 84f), "ON A STROLL", mid);

        // The two counters at the bottom, the name plate above them, the Pokémon on the floor above that.
        const float rowHeight = 76f;
        var watts = new SKRect(area.Left + 40f, area.Bottom - 30f - rowHeight, area.Right - 40f, area.Bottom - 30f);
        var steps = new SKRect(watts.Left, watts.Top - 14f - rowHeight, watts.Right, watts.Top - 14f);
        items[StepsItem] = steps;
        items[WattsItem] = watts;
        ValueRow(c, steps, "Steps", scene.Steps, scene.Cursor == StepsItem, scene.CanEdit, mid);
        ValueRow(c, watts, "Watts", scene.Watts, scene.Cursor == WattsItem, scene.CanEdit, mid);

        var plate = new SKRect(area.Left + 40f, steps.Top - 40f - 100f, area.Right - 40f, steps.Top - 40f);
        var ground = plate.Top - 24f;
        var stage = new SKRect(area.Left + 60f, area.Top + 104f, area.Right - 60f, ground);
        using (var floor = new SKPaint { Color = T.Rim.WithAlpha(90) })
            c.DrawRect(new SKRect(stage.Left, ground, stage.Right, ground + 3f), floor);
        using (var band = new SKPaint { Color = EditorPaint.RowBand(true), IsAntialias = true }) c.DrawRoundRect(plate, 12f, 12f, band);

        if (scene.Mon is not { } mon)
        {
            Centered(c, "Nobody", stage.MidX, stage.MidY, big, T.SubInk);
            Centered(c, "Send a Pokémon to the Pokéwalker in the game.", stage.MidX, stage.MidY + 50f, small, T.SubInk);
            Text(c, "No Pokémon paired", plate.Left + 34f, plate.MidY, big, T.SubInk, plate.Width - 60f);
            return;
        }

        if (mon.Sprite is { } sprite)
        {
            var fit = Fit(sprite, stage, maxScale: 4f);
            fit.Offset(0, ground - fit.Bottom);
            c.DrawImage(sprite, fit, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }
        var level = $"Lv. {mon.Level}";
        var levelWidth = big.MeasureFallback(level);
        Text(c, mon.Name, plate.Left + 34f, plate.MidY - 16f, big, T.Value, plate.Width - levelWidth - 100f);
        Text(c, mon.Detail, plate.Left + 34f, plate.MidY + 26f, small, T.SubInk, plate.Width - levelWidth - 100f);
        Right(c, level, plate.Right - 40f, plate.MidY, big, T.Value);
    }

    private static void PaintCourses(SKCanvas c, SKRect area, PokewalkerScene scene, SKRect[] items, SKFont mid, SKFont small)
    {
        var open = scene.Courses.Count(course => course.Unlocked);
        SectionTab(c, new SKRect(area.Left + 40f, area.Top + 28f, area.Left + 420f, area.Top + 84f), $"COURSES {open}/{scene.Courses.Count}", mid);

        var all = new SKRect(area.Right - 470f, area.Top + 28f, area.Right - 40f, area.Top + 84f);
        items[UnlockAllItem] = all;
        var allFocused = scene.Cursor == UnlockAllItem;
        if (allFocused) Chip(c, all, focused: true);
        else using (var band = new SKPaint { Color = EditorPaint.RowBand(false), IsAntialias = true }) c.DrawRoundRect(all, 10f, 10f, band);
        Arrow(c, all.Left + 34f, all.MidY, 1, scene.CanEdit ? T.Rim : T.SubInk);
        Text(c, "Unlock every course", all.Left + 62f, all.MidY, mid,
            allFocused ? T.ChipInk : scene.CanEdit ? T.Value : T.SubInk, all.Width - 80f);

        var rows = (int)Math.Ceiling(scene.Courses.Count / (double)CourseColumns);
        var grid = new SKRect(area.Left + 40f, all.Bottom + 26f, area.Right - 40f, area.Bottom - 20f);
        const float gap = 10f;
        var cellWidth = (grid.Width - gap * (CourseColumns - 1)) / CourseColumns;
        var cellHeight = Math.Min(70f, (grid.Height - gap * (rows - 1)) / Math.Max(1, rows));
        for (var i = 0; i < scene.Courses.Count; i++)
        {
            // Column-major, so the courses read down each column in the game's order.
            var column = i / rows;
            var row = i % rows;
            var left = grid.Left + column * (cellWidth + gap);
            var top = grid.Top + row * (cellHeight + gap);
            var cell = new SKRect(left, top, left + cellWidth, top + cellHeight);
            items[FirstCourseItem + i] = cell;
            CourseCell(c, cell, i, scene.Courses[i], scene.Cursor == FirstCourseItem + i, small);
        }
    }

    private static void CourseCell(SKCanvas c, SKRect cell, int index, PokewalkerSceneCourse course, bool focused, SKFont font)
    {
        if (focused) Chip(c, cell, focused: true);
        else using (var band = new SKPaint { Color = EditorPaint.RowBand(course.Unlocked), IsAntialias = true }) c.DrawRoundRect(cell, 10f, 10f, band);

        // The unlock mark: a filled square when open, an empty one when locked.
        var size = Math.Min(24f, cell.Height - 24f);
        var mark = new SKRect(cell.Left + 26f, cell.MidY - size / 2, cell.Left + 26f + size, cell.MidY + size / 2);
        using (var paint = new SKPaint
               {
                   Color = course.Unlocked ? (focused ? T.ChipInk : T.Rim) : T.SubInk,
                   Style = course.Unlocked ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
                   StrokeWidth = 3f,
               })
            c.DrawRect(course.Unlocked ? mark : SKRect.Inflate(mark, -1.5f, -1.5f), paint);

        var ink = focused ? T.ChipInk : course.Unlocked ? T.Value : T.SubInk;
        var label = $"{index + 1:00} {course.Name}{(course.Special ? " ★" : "")}";
        Text(c, label, mark.Right + 16f, cell.MidY, font, course.Available || focused ? ink : ink.WithAlpha(140), cell.Right - mark.Right - 40f);
    }

    private static void ValueRow(SKCanvas c, SKRect r, string label, string value, bool focused, bool canEdit, SKFont font)
    {
        if (focused) Chip(c, r, focused: true);
        else using (var band = new SKPaint { Color = EditorPaint.RowBand(false), IsAntialias = true }) c.DrawRoundRect(r, 10f, 10f, band);
        Text(c, label, r.Left + 34f, r.MidY, font, focused ? T.ChipInk : T.SubInk, r.Width / 2);
        Right(c, value, r.Right - 40f, r.MidY, font, focused ? T.ChipInk : canEdit ? T.Value : T.SubInk);
    }

    private static void SectionTab(SKCanvas c, SKRect tab, string label, SKFont font)
    {
        using (var path = Tab(tab, 0f, 26f))
        {
            using (var band = new SKPaint { Color = EditorPaint.RowBand(true), IsAntialias = true }) c.DrawPath(path, band);
            Outline(c, path, T.Rim.WithAlpha(110), 3f);
        }
        Text(c, label, tab.Left + 24f, tab.MidY, font, T.Value, tab.Width - 50f);
    }
}
