using SkiaSharp;
using static PKForge.Chrome.PaintKit;
using static PKForge.Chrome.ToolPaint;

namespace PKForge.Chrome;

/// <summary>One box as it would look after the sort: its name, one icon per slot (30, or 20 in the
/// Game Boy games; null for empty),
/// which are shiny, whether it is locked (kept as is), and what it would run from and to: words,
/// or two type ids when sorting by type.</summary>
public sealed record SortPreviewBox(
    string Name,
    IReadOnlyList<SKImage?> Icons,
    IReadOnlyList<bool> Shiny,
    bool Locked,
    string Caption,
    int? FirstType = null,
    int? LastType = null);

/// <summary>Everything the Sort screen shows at one moment.</summary>
public sealed record SortScene(
    IReadOnlyList<string> Keys,
    int Key,
    IReadOnlyList<string> Directions,      // two labels worded for the key ("Strongest first", "Weakest first")
    bool Reverse,
    IReadOnlyList<string> Scopes,          // "This box", "Range", "All boxes"
    int Scope,
    int RangeFrom,                         // 1-based box numbers
    int RangeTo,
    bool RangeShown,
    IReadOnlyList<SortPreviewBox> Preview,
    float Scroll,
    int Focus,
    int Count,
    bool CanSort,
    string Status,
    IReadOnlyList<(string Key, string Label)> Prompts,
    Action<SKCanvas, SKRect, int>? DrawType = null);

/// <summary>
/// Sort, in the app's art direction: the box banner; at the left one rimmed panel that reads as
/// a sentence (order by a key, in a direction, over some boxes) made of slanted choice chips,
/// the chosen one lit, the cursor as the cyan chip, and the sort plate at its foot; at the right
/// the box well holding the result itself, live: every target box as a mini box of its icons in
/// the order they would land, each captioned with where it would run from and to (type plates
/// when sorting by type). Sizes are design pixels (a 1872 wide screen) scaled by a unit.
/// </summary>
public static class SortPaint
{
    public static float DesignWidth => UiScale.DesignWidth;
    public const int KeyBase = 100, Normal = 200, Reversed = 201, ScopeBase = 300, From = 400, To = 401, Go = 500, Preview = 600;

    private const float LeftWidth = 720f;
    private const float Gap = 18f;
    // A few boxes get room to read (icons twice as big); many get the overview.
    private static int Columns(int boxes) => boxes <= 4 ? 2 : 3;
    private const float BoxGap = 22f;
    private const float PlateHeight = 46f;
    private const float CaptionHeight = 46f;

    /// <summary>A mini box's height for a well of this width (design pixels), and the step between rows.</summary>
    private static (float Width, float Height) BoxSize(float wellWidth, int columns)
    {
        var width = (wellWidth - 2 * 26f - (columns - 1) * BoxGap) / columns;
        var cell = (width - 16f) / 6f;
        return (width, PlateHeight + 8f + cell * 0.78f * 5f + 8f + CaptionHeight);
    }

    private static SKRect WellRect(SKSizeI size)
    {
        var height = size.Height * DesignWidth / Math.Max(1, size.Width);
        return new SKRect(LeftWidth + Gap, HeaderHeight + 16f, DesignWidth, height - FooterHeight - 8f);
    }

    private static SKRect Viewport(SKRect well) => new(well.Left + 26f, well.Top + 76f, well.Right - 26f, well.Bottom - 18f);

    /// <summary>How far the preview can scroll for <paramref name="boxes"/> mini boxes (design pixels).</summary>
    public static float MaxScroll(SKSizeI size, int boxes)
    {
        var view = Viewport(WellRect(size));
        var columns = Columns(boxes);
        var (_, h) = BoxSize(WellRect(size).Width, columns);
        var rows = (boxes + columns - 1) / columns;
        return Math.Max(0f, rows * (h + BoxGap) - BoxGap - view.Height);
    }

    /// <summary>One row of mini boxes, for the d-pad's scroll step.</summary>
    public static float RowStep(SKSizeI size, int boxes) => BoxSize(WellRect(size).Width, Columns(boxes)).Height + BoxGap;

    public static ToolHits Paint(SKCanvas c, SKImageInfo info, SortScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);
        var targets = new List<(int, SKRect)>();

        using var bannerFont = new SKFont(face, 40f);
        using var tabFont = new SKFont(face, 32f);
        using var chipFont = new SKFont(face, 32f);
        using var small = new SKFont(face, 26f);
        using var big = new SKFont(face, 40f);

        StoragePaint.BoxHeader(c, new SKRect(0, 0, DesignWidth, HeaderHeight), "SORT BOXES", bannerFont, false, false, 1f);

        // ── The sentence: order by, direction, which boxes, then sort ──
        var panel = new SKRect(0, HeaderHeight + 16f, LeftWidth, height - FooterHeight - 8f);
        Panel(c, panel);
        var inner = new SKRect(panel.Left + 24f, panel.Top, panel.Right - 24f, panel.Bottom);
        var y = panel.Top + 18f;
        y = SectionTab(c, panel.Left, y, 360f, "ORDER BY", tabFont).Bottom + 14f;
        const float chipHeight = 56f;
        const float chipStep = 66f;
        var half = (inner.Width - 18f) / 2f;
        for (var i = 0; i < scene.Keys.Count; i++)
        {
            var x = inner.Left + (i % 2) * (half + 18f);
            var top = y + (i / 2) * chipStep;
            var r = new SKRect(x, top, x + half, top + chipHeight);
            Choice(c, r, scene.Keys[i], chipFont, i == scene.Key, scene.Focus == KeyBase + i);
            targets.Add((KeyBase + i, r));
        }
        y += (scene.Keys.Count + 1) / 2 * chipStep + 6f;

        y = SectionTab(c, panel.Left, y, 360f, "DIRECTION", tabFont).Bottom + 14f;
        for (var i = 0; i < 2; i++)
        {
            var x = inner.Left + i * (half + 18f);
            var r = new SKRect(x, y, x + half, y + chipHeight);
            var label = i < scene.Directions.Count ? scene.Directions[i] : i == 0 ? "Normal" : "Reversed";
            Choice(c, r, label, chipFont, scene.Reverse == (i == 1), scene.Focus == Normal + i);
            targets.Add((Normal + i, r));
        }
        y += chipStep + 6f;

        y = SectionTab(c, panel.Left, y, 360f, "WHICH BOXES", tabFont).Bottom + 14f;
        var third = (inner.Width - 2 * 14f) / Math.Max(1, scene.Scopes.Count);
        for (var i = 0; i < scene.Scopes.Count; i++)
        {
            var x = inner.Left + i * (third + 14f);
            var r = new SKRect(x, y, x + third, y + chipHeight);
            Choice(c, r, scene.Scopes[i], chipFont, i == scene.Scope, scene.Focus == ScopeBase + i);
            targets.Add((ScopeBase + i, r));
        }
        y += chipStep;
        if (scene.RangeShown)
        {
            // From ◀ 03 ▶ to ◀ 07 ▶: two steppers, left/right change the focused one.
            Text(c, "From", inner.Left + 6f, y + chipHeight / 2, chipFont, T.Label, 120f);
            var from = new SKRect(inner.Left + 96f, y, inner.Left + 96f + 230f, y + chipHeight);
            Stepper(c, from, $"Box {scene.RangeFrom:00}", chipFont, scene.Focus == From);
            targets.Add((From, from));
            Text(c, "to", from.Right + 18f, y + chipHeight / 2, chipFont, T.Label, 60f);
            var to = new SKRect(from.Right + 70f, y, from.Right + 70f + 230f, y + chipHeight);
            Stepper(c, to, $"Box {scene.RangeTo:00}", chipFont, scene.Focus == To);
            targets.Add((To, to));
        }

        var go = new SKRect(inner.Left + 10f, panel.Bottom - 104f, inner.Right - 10f, panel.Bottom - 26f);
        var goLabel = scene.Count == 0 ? "NOTHING TO SORT" : scene.Count == 1 ? "SORT 1 POKÉMON" : $"SORT {scene.Count} POKÉMON";
        Action(c, go, goLabel, big, scene.Focus == Go, scene.CanSort && scene.Count > 0);
        targets.Add((Go, go));

        // ── The result, live ──
        var well = WellRect(info.Size);
        StoragePaint.WellPanel(c, well, 1f);
        if (scene.Focus == Preview)
            using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5f })
                c.DrawRoundRect(SKRect.Inflate(well, -2.5f, -2.5f), 28f, 28f, rim);
        targets.Add((Preview, well));
        var boxesWord = scene.Preview.Count == 1 ? "1 box" : $"{scene.Preview.Count} boxes";
        Text(c, "AFTER SORTING", well.Left + 30f, well.Top + 40f, tabFont, T.Value, 400f);
        Right(c, $"{scene.Count} Pokémon · {boxesWord}", well.Right - 30f, well.Top + 40f, small, T.SubInk);

        var view = Viewport(well);
        var columns = Columns(scene.Preview.Count);
        var (bw, bh) = BoxSize(well.Width, columns);
        var max = MaxScroll(info.Size, scene.Preview.Count);
        var scroll = Math.Clamp(scene.Scroll, 0f, max);
        c.Save();
        c.ClipRect(SKRect.Inflate(view, 8f, 8f));
        for (var i = 0; i < scene.Preview.Count; i++)
        {
            var top = view.Top + (i / columns) * (bh + BoxGap) - scroll;
            if (top > view.Bottom || top + bh < view.Top) continue;
            var left = view.Left + (i % columns) * (bw + BoxGap);
            MiniBox(c, new SKRect(left, top, left + bw, top + bh), scene.Preview[i], scene.DrawType, tabFont, small);
        }
        if (scene.Preview.Count == 0)
            Centered(c, "No box to sort.", view.MidX, view.MidY, big, T.SubInk);
        c.Restore();
        Rail(c, new SKRect(well.Right - 16f, view.Top, well.Right - 10f, view.Bottom), scroll, max, view.Height);

        var prompts = Footer(c, new SKRect(0, height - FooterHeight, DesignWidth, height), scene.Prompts, scene.Status, small);
        c.Restore();
        return new ToolHits(unit, [.. targets.Select(t => (t.Item1, Px(t.Item2, unit)))], [.. prompts.Select(p => Px(p, unit))], Px(well, unit), max);
    }

    private static void Stepper(SKCanvas c, SKRect r, string label, SKFont font, bool focused)
    {
        if (focused) Chip(c, r, focused: true);
        else
        {
            using var fill = new SKPaint { Color = T.Band.WithAlpha(150), IsAntialias = true };
            c.DrawRoundRect(r, 10f, 10f, fill);
        }
        var ink = focused ? T.ChipInk : T.Value;
        Arrow(c, r.Left + 30f, r.MidY, -1, ink);
        Centered(c, label, r.MidX, r.MidY, font, ink);
        Arrow(c, r.Right - 30f, r.MidY, 1, ink);
    }

    /// <summary>A box as it would land: the name plate, the icons on the well, the caption under.</summary>
    private static void MiniBox(SKCanvas c, SKRect r, SortPreviewBox box, Action<SKCanvas, SKRect, int>? drawType, SKFont name, SKFont small)
    {
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(r, 16f, 16f, fill);
        var plate = new SKRect(r.Left + 8f, r.Top + 6f, r.Right - 8f, r.Top + PlateHeight);
        using (var band = new SKPaint { Color = EditorPaint.RowBand(true), IsAntialias = true }) c.DrawRoundRect(plate, 10f, 10f, band);
        var count = box.Icons.Count(i => i is not null);
        // The box's own size: 30, or 20 in the Game Boy games (one icon entry per slot).
        var capacity = box.Icons.Count;
        Text(c, box.Name, plate.Left + 16f, plate.MidY, small, T.Value, plate.Width - 130f);
        Right(c, box.Locked ? "LOCKED" : count >= capacity ? "FULL" : $"{count}/{capacity}", plate.Right - 16f, plate.MidY, small,
            box.Locked || count >= capacity ? Pksm.ShinyGold : T.SubInk);

        var body = new SKRect(r.Left + 8f, plate.Bottom + 6f, r.Right - 8f, r.Bottom - CaptionHeight - 4f);
        using (var well = new SKPaint { Color = T.Well, IsAntialias = true }) c.DrawRoundRect(body, 10f, 10f, well);
        var cw = body.Width / 6f;
        var ch = body.Height / 5f;
        for (var slot = 0; slot < box.Icons.Count && slot < 30; slot++)
        {
            var cell = SKRect.Create(body.Left + (slot % 6) * cw, body.Top + (slot / 6) * ch, cw, ch);
            Icon(c, box.Icons[slot], cell, 2f, box.Locked ? (byte)110 : (byte)255);
            if (slot < box.Shiny.Count && box.Shiny[slot]) Star(c, cell.Right - 9f, cell.Top + 9f, 7f);
        }
        using (var rim = new SKPaint { Color = T.Rim.WithAlpha(box.Locked ? (byte)50 : (byte)90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), 16f, 16f, rim);

        var caption = new SKRect(r.Left + 12f, body.Bottom + 4f, r.Right - 12f, r.Bottom - 4f);
        if (box.FirstType is { } first && drawType is not null)
        {
            var w = Math.Min(130f, (caption.Width - 50f) / 2f);
            var a = new SKRect(caption.MidX - 25f - w, caption.MidY - 16f, caption.MidX - 25f, caption.MidY + 16f);
            drawType(c, a, first);
            if (box.LastType is { } last && last != first)
            {
                Arrow(c, caption.MidX, caption.MidY, 1, T.SubInk);
                drawType(c, new SKRect(caption.MidX + 25f, a.Top, caption.MidX + 25f + w, a.Bottom), last);
            }
        }
        else Centered(c, Fitted(box.Caption, small, caption.Width), caption.MidX, caption.MidY, small, box.Locked ? T.SubInk : T.Label);
        _ = name;
    }
}
