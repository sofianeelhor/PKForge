using PKForge.Domain;
using SkiaSharp;
using static PKForge.Chrome.PaintKit;
using static PKForge.Chrome.ToolPaint;

namespace PKForge.Chrome;

/// <summary>One operation of the batch plan: its label, its value (the EV spread's summary, the
/// expert lines' count), whether it is on, whether left/right cycle it, whether A opens a popup.</summary>
public sealed record BatchOp(string Label, string Value, bool On, bool Cycles = false, bool Opens = false);

/// <summary>One targeted Pokémon in the strip: its icon, the shown field before and after, whether
/// the plan changes it at all.</summary>
public sealed record BatchMon(SKImage? Icon, string Before, string After, bool Changes, bool Shiny);

/// <summary>The Pokémon under the cursor, large: every field the plan touches, before → after.</summary>
public sealed record BatchDetail(string Name, SKImage? Sprite, IReadOnlyList<BatchFieldChange> Lines, bool Changes);

/// <summary>Everything the batch editor shows at one moment.</summary>
public sealed record BatchScene(
    IReadOnlyList<string> Scopes,
    int Scope,
    IReadOnlyList<BatchOp> Ops,
    string Field,                 // the field the strip's captions show ("LEVEL")
    IReadOnlyList<BatchMon> Mons,
    int MonCursor,
    int Changing,
    int Targeted,
    BatchDetail? Detail,
    float Scroll,
    int Focus,
    bool CanApply,
    string Status,
    IReadOnlyList<(string Key, string Label)> Prompts);

/// <summary>
/// The batch editor, in the app's art direction: at the left, what will change (the scope as
/// choice chips, then the operations as tick rows on alternating bands, the cycling ones with
/// their arrows, the cursor as the cyan chip, the apply plate at the foot); at the right, the
/// box well with who is affected: the count that will change, every targeted Pokémon as a
/// free-standing icon with the shown field's before → after under it (the ones left untouched
/// fade back), and the Pokémon under the pointer large with every field it would get.
/// </summary>
public static class BatchPaint
{
    public static float DesignWidth => UiScale.DesignWidth;
    public const int ScopeBase = 100, OpBase = 200, Apply = 500, MonBase = 1000;

    private const float LeftWidth = 700f;
    private const float Gap = 18f;
    private const float CellWidth = 118f;
    private const float CellHeight = 108f;
    private const float DetailHeight = 238f;

    private static SKRect WellRect(SKSizeI size)
    {
        var height = size.Height * DesignWidth / Math.Max(1, size.Width);
        return new SKRect(LeftWidth + Gap, HeaderHeight + 16f, DesignWidth, height - FooterHeight - 8f);
    }

    private static SKRect GridView(SKRect well) => new(well.Left + 22f, well.Top + 84f, well.Right - 26f, well.Bottom - DetailHeight - 16f);

    /// <summary>The strip's geometry for a screen size: columns, the row step and the visible height (design pixels).</summary>
    public static (int Columns, float RowStep, float ViewHeight) Grid(SKSizeI size)
    {
        var view = GridView(WellRect(size));
        return (Math.Max(1, (int)(view.Width / CellWidth)), CellHeight, view.Height);
    }

    public static float MaxScroll(SKSizeI size, int count)
    {
        var (columns, step, view) = Grid(size);
        var rows = (count + columns - 1) / columns;
        return Math.Max(0f, rows * step - view + 12f);
    }

    public static ToolHits Paint(SKCanvas c, SKImageInfo info, BatchScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);
        var targets = new List<(int, SKRect)>();

        using var bannerFont = new SKFont(face, 40f);
        using var tabFont = new SKFont(face, 32f);
        using var rowFont = new SKFont(face, 32f);
        using var small = new SKFont(face, 26f);
        using var tiny = new SKFont(face, 22f);
        using var big = new SKFont(face, 40f);
        using var huge = new SKFont(face, 56f);

        StoragePaint.BoxHeader(c, new SKRect(0, 0, DesignWidth, HeaderHeight), "BATCH EDITOR", bannerFont, false, false, 1f);

        // ── What will change ──
        var panel = new SKRect(0, HeaderHeight + 16f, LeftWidth, height - FooterHeight - 8f);
        Panel(c, panel);
        var inner = new SKRect(panel.Left + 24f, panel.Top, panel.Right - 24f, panel.Bottom);
        var y = SectionTab(c, panel.Left, panel.Top + 18f, 330f, "APPLY TO", tabFont).Bottom + 14f;
        var n = Math.Max(1, scene.Scopes.Count);
        var w = (inner.Width - (n - 1) * 14f) / n;
        for (var i = 0; i < scene.Scopes.Count; i++)
        {
            var r = new SKRect(inner.Left + i * (w + 14f), y, inner.Left + i * (w + 14f) + w, y + 56f);
            Choice(c, r, scene.Scopes[i], small, i == scene.Scope, scene.Focus == ScopeBase + i);
            targets.Add((ScopeBase + i, r));
        }
        y += 76f;
        y = SectionTab(c, panel.Left, y, 330f, "CHANGES", tabFont).Bottom + 12f;
        var apply = new SKRect(inner.Left + 10f, panel.Bottom - 100f, inner.Right - 10f, panel.Bottom - 24f);
        var step = Math.Min(60f, (apply.Top - 16f - y) / Math.Max(1, scene.Ops.Count));
        for (var i = 0; i < scene.Ops.Count; i++)
        {
            var op = scene.Ops[i];
            var row = new SKRect(inner.Left, y + i * step, inner.Right, y + (i + 1) * step - 6f);
            var focused = scene.Focus == OpBase + i;
            Band(c, row, i, focused);
            targets.Add((OpBase + i, row));
            var ink = focused ? T.ChipInk : T.Value;
            var textLeft = row.Left + 26f;
            if (!op.Cycles && !op.Opens)
            {
                var box = new SKRect(row.Left + 24f, row.MidY - 16f, row.Left + 56f, row.MidY + 16f);
                Check(c, box, op.On, ink);
                textLeft = box.Right + 18f;
            }
            if (op.Cycles)
            {
                // The value between its arrows, lit when it does something.
                var valueWidth = 250f;
                Text(c, op.Label, textLeft, row.MidY, rowFont, ink, row.Width - valueWidth - 60f);
                var vr = new SKRect(row.Right - valueWidth - 20f, row.Top, row.Right - 20f, row.Bottom);
                Arrow(c, vr.Left + 14f, row.MidY, -1, ink);
                Centered(c, Fitted(op.Value, small, valueWidth - 60f), vr.MidX, row.MidY, small, op.On ? (focused ? T.ChipInk : T.Rim) : T.SubInk);
                Arrow(c, vr.Right - 10f, row.MidY, 1, ink);
            }
            else
            {
                var value = op.Opens ? (op.Value.Length > 0 ? op.Value : "add…") : "";
                var vw = value.Length > 0 ? small.MeasureFallback(value) + 30f : 0f;
                Text(c, op.Label, textLeft, row.MidY, rowFont, ink, row.Right - textLeft - vw - 20f);
                if (value.Length > 0) Right(c, value, row.Right - 22f, row.MidY, small, op.On ? (focused ? T.ChipInk : T.Rim) : T.SubInk);
            }
        }
        var label = scene.Changing == 0 ? "NOTHING TO APPLY" : scene.Changing == 1 ? "APPLY TO 1 POKÉMON" : $"APPLY TO {scene.Changing} POKÉMON";
        Action(c, apply, label, big, scene.Focus == Apply, scene.CanApply && scene.Changing > 0);
        targets.Add((Apply, apply));

        // ── Who is affected ──
        var well = WellRect(info.Size);
        StoragePaint.WellPanel(c, well, 1f);
        var counter = $"{scene.Changing}";
        Text(c, counter, well.Left + 30f, well.Top + 44f, huge, scene.Changing > 0 ? T.Rim : T.SubInk, 200f);
        var cw = huge.MeasureFallback(counter);
        Text(c, $"of {scene.Targeted} will change", well.Left + 46f + cw, well.Top + 48f, rowFont, T.Value, 500f);
        if (scene.Field.Length > 0)
        {
            var tag = $"SHOWING {scene.Field}";
            var tw = small.MeasureFallback(tag) + 56f;
            var plate = new SKRect(well.Right - 30f - tw, well.Top + 22f, well.Right - 30f, well.Top + 70f);
            using (var path = Parallelogram(plate, 14f))
            {
                Gradient(c, path, T.TabActiveTop, T.TabActiveBottom);
                Outline(c, path, Pksm.Ink, 3f);
            }
            Centered(c, tag, plate.MidX, plate.MidY, small, Pksm.Ink);
        }

        var view = GridView(well);
        var (columns, rowStep, viewHeight) = Grid(info.Size);
        var max = MaxScroll(info.Size, scene.Mons.Count);
        var scroll = Math.Clamp(scene.Scroll, 0f, max);
        var gridLeft = view.Left + (view.Width - columns * CellWidth) / 2f;
        SKRect cursorCell = SKRect.Empty;
        c.Save();
        c.ClipRect(new SKRect(view.Left - 10f, view.Top - 6f, view.Right + 10f, view.Bottom + 4f));
        for (var i = 0; i < scene.Mons.Count; i++)
        {
            var top = view.Top + 14f + (i / columns) * rowStep - scroll;
            if (top + rowStep < view.Top || top > view.Bottom) continue;
            var cell = SKRect.Create(gridLeft + (i % columns) * CellWidth, top, CellWidth, CellHeight - 8f);
            var mon = scene.Mons[i];
            var art = new SKRect(cell.Left + 8f, cell.Top, cell.Right - 8f, cell.Top + 64f);
            Icon(c, mon.Icon, art, 2f, mon.Changes ? (byte)255 : (byte)100);
            if (mon.Shiny) Star(c, art.Right - 6f, art.Top + 8f, 8f);
            var caption = new SKRect(cell.Left + 2f, art.Bottom + 2f, cell.Right - 2f, cell.Bottom);
            if (mon.Changes && mon.After.Length == 0 && mon.Before.Length == 0) { }
            else if (mon.Changes)
            {
                var text = mon.Before.Length > 0 && mon.Before != mon.After ? $"{Short(mon.Before)}›{Short(mon.After)}" : Short(mon.After);
                Centered(c, Fitted(text, tiny, caption.Width), caption.MidX, caption.MidY, tiny, T.Rim);
            }
            else Centered(c, "same", caption.MidX, caption.MidY, tiny, T.SubInk);
            if (i == scene.MonCursor) cursorCell = cell;
            if (top >= view.Top - 4f && top + CellHeight <= view.Bottom + 20f) targets.Add((MonBase + i, cell));
        }
        if (!cursorCell.IsEmpty && scene.Focus >= MonBase)
            StoragePaint.Pointer(c, new SKRect(cursorCell.Left, cursorCell.Top + 4f, cursorCell.Right, cursorCell.Bottom), 1f);
        if (scene.Mons.Count == 0)
            Centered(c, "No Pokémon in this scope.", view.MidX, view.MidY, big, T.SubInk);
        c.Restore();
        Rail(c, new SKRect(well.Right - 16f, view.Top, well.Right - 10f, view.Bottom), scroll, max, viewHeight);

        // The Pokémon under the pointer, large, with every field it would get.
        var detail = new SKRect(well.Left + 18f, well.Bottom - DetailHeight - 8f, well.Right - 18f, well.Bottom - 14f);
        using (var rule = new SKPaint { Color = T.Rim.WithAlpha(90), StrokeWidth = 2f, IsAntialias = true })
            c.DrawLine(detail.Left + 10f, detail.Top, detail.Right - 10f, detail.Top, rule);
        if (scene.Detail is { } d)
        {
            var art = new SKRect(detail.Left + 6f, detail.Top + 10f, detail.Left + 230f, detail.Bottom);
            if (d.Sprite is { } sprite) c.DrawImage(sprite, Fit(sprite, art), Nearest);
            var left = art.Right + 20f;
            Text(c, d.Name, left, detail.Top + 38f, big, T.Value, 520f);
            if (d.Lines.Count > 0) Right(c, d.Changes ? "would change" : "already matches", detail.Right - 10f, detail.Top + 38f, small, d.Changes ? T.Rim : T.SubInk);
            var lineTop = detail.Top + 72f;
            var twoColumns = d.Lines.Count > 4;
            var colWidth = (detail.Right - left) / (twoColumns ? 2f : 1f);
            for (var i = 0; i < d.Lines.Count && i < 8; i++)
            {
                var line = d.Lines[i];
                var x = left + (twoColumns ? i / 4 : 0) * colWidth;
                var r = new SKRect(x, lineTop + (i % 4) * 40f, x + colWidth - 16f, lineTop + (i % 4) * 40f + 36f);
                using (var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true }) c.DrawRoundRect(r, 8f, 8f, band);
                Text(c, line.Field, r.Left + 14f, r.MidY, small, T.Label, 170f);
                var after = line.Known ? line.After : line.After + "?";
                var afterWidth = small.MeasureFallback(after);
                var color = line.Changes ? T.Rim : T.SubInk;
                Right(c, Fitted(after, small, r.Width - 200f), r.Right - 12f, r.MidY, small, color);
                if (line.Before.Length > 0 && line.Changes)
                {
                    var arrowX = r.Right - 24f - Math.Min(afterWidth, r.Width - 200f) - 16f;
                    Arrow(c, arrowX, r.MidY, 1, T.SubInk);
                    Right(c, Fitted(line.Before, small, Math.Max(0, arrowX - 20f - (r.Left + 190f))), arrowX - 18f, r.MidY, small, T.Value);
                }
            }
            if (d.Lines.Count == 0)
                Text(c, "Tick a change on the left to see it here.", left, lineTop + 20f, small, T.SubInk, detail.Right - left);
        }

        var prompts = Footer(c, new SKRect(0, height - FooterHeight, DesignWidth, height), scene.Prompts, scene.Status, small);
        c.Restore();
        return new ToolHits(unit, [.. targets.Select(t => (t.Item1, Px(t.Item2, unit)))], [.. prompts.Select(p => Px(p, unit))], Px(view, unit), max);
    }

    // The strip's captions are tiny: "Lv. 34" reads "34", a spread reads its total.
    private static string Short(string value)
    {
        if (value.StartsWith("Lv. ", StringComparison.Ordinal)) return value[4..];
        if (value.Count(ch => ch == '/') == 5)
            return value.Split('/').Sum(v => int.TryParse(v, out var x) ? x : 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return value;
    }
}
