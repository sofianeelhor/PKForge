using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One pouch on the tab strip: its name, its icon, how many kinds it holds.</summary>
public sealed record BagScenePouch(string Title, SKImage? Icon, int Count);

/// <summary>One item row: its name, its count, its art (null while it loads).</summary>
public sealed record BagSceneItem(string Name, int Count, SKImage? Art,
    string? About = null, string? FirstSeen = null, string? HeldBy = null,
    // The Item Bank: a row this game cannot take is dimmed, and Note says why (or how many the bag holds).
    bool Dimmed = false, string? Note = null);

/// <summary>The item's menu, as in the games' bag: the count, then what can be done with it.</summary>
public enum BagAction { Count, Exact, Fill, Remove, Add, Presets, Put, Bank }

/// <summary>Everything the bag screen shows at one moment.</summary>
public sealed record BagScene(
    IReadOnlyList<BagScenePouch> Pouches,
    int Pouch,
    IReadOnlyList<BagSceneItem> Items,
    int Cursor,
    int Scroll,
    bool InMenu,
    BagAction Action,
    bool CanEdit,
    string Status,
    // Not null while adding: the search typed so far. Items are then the pouch's catalogue,
    // each with how many the bag already holds, and Amount is how many go in.
    string? Search = null,
    int Amount = 1,
    // Adding from the Item Bank instead of the catalogue: Items are the banked items, Count how many are banked.
    bool FromBank = false,
    // How many "Send to Item Bank" sends; 0 when this game has no Item Bank.
    int BankAmount = 0);

/// <summary>Where each touch target landed on the last paint, in canvas pixels.</summary>
public sealed record BagHits(
    SKRect Previous,
    SKRect Next,
    IReadOnlyList<SKRect> Tabs,
    IReadOnlyList<(int Index, SKRect Rect)> Rows,
    SKRect List,
    IReadOnlyList<(BagAction Action, SKRect Rect)> Actions,
    SKRect Minus,
    SKRect Plus,
    int VisibleRows,
    float RowHeight,
    SKRect Search,
    SKRect BankMinus = default,
    SKRect BankPlus = default);

/// <summary>
/// The bag in the app's art direction, as one painted screen: the pouch name in the slanted
/// box banner with its chevrons, the items in the box well, the pouches as the summary's
/// slanted icon tabs, and the item under the cursor in a cyan-rimmed panel with its menu.
/// Sizes are design pixels (a 1872 wide screen) scaled by a unit.
/// </summary>
public static class BagPaint
{

    /// <summary>The design width: a canvas's unit is its width over this.</summary>
    public static float DesignWidth => UiScale.DesignWidth;

    private const float Gap = 24f;
    private const float HeaderHeight = 88f;
    private const float ListWidth = 1040f;
    private const float RowHeight = 104f;
    private const float RowInset = 22f;
    private const float ActionHeight = 76f;

    /// <summary>The actions the menu lists for the scene, in order.</summary>
    public static IReadOnlyList<BagAction> Actions(BagScene scene) =>
        // Adding and presets live on Y and X (and the footer), not twice on screen.
        scene.Items.Count == 0 ? []
        : scene.Search is not null ? [BagAction.Count, BagAction.Put]
        : scene.BankAmount > 0 ? [BagAction.Count, BagAction.Fill, BagAction.Remove, BagAction.Bank]
        : [BagAction.Count, BagAction.Fill, BagAction.Remove];

    public static string Label(BagAction action) => action switch
    {
        BagAction.Count => "Count",
        BagAction.Exact => "Exact count",
        BagAction.Fill => "Fill to the max",
        BagAction.Remove => "Remove",
        BagAction.Add => "Add an item",
        BagAction.Put => "Put in the bag",
        BagAction.Bank => "Send to Item Bank",
        _ => "Presets",
    };

    /// <summary>How many rows the list shows on a canvas of this size.</summary>
    public static int VisibleRows(SKSizeI size, bool searching = false)
    {
        if (size.Width <= 0) return 1;
        var unit = size.Width / DesignWidth;
        var height = size.Height / unit;
        var well = height - (HeaderHeight + 16f) - (searching ? SearchHeight : 0f);
        return Math.Max(1, (int)((well - 2 * RowInset) / RowHeight));
    }

    private const float SearchHeight = 84f;

    public static BagHits Paint(SKCanvas c, SKImageInfo info, BagScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        var header = new SKRect(0, 0, ListWidth, HeaderHeight);
        var pouchTitle = scene.Pouches.Count > 0 ? scene.Pouches[scene.Pouch].Title : "Bag";
        var title = (scene.Search is null ? pouchTitle : scene.FromBank ? $"Bank to {pouchTitle}" : $"Add to {pouchTitle}").ToUpperInvariant();
        using (var font = new SKFont(face, 40f)) StoragePaint.BoxHeader(c, header, title, font, scene.Pouches.Count > 1, scene.Pouches.Count > 1, 1f);

        var well = new SKRect(0, HeaderHeight + 16f, ListWidth, height);
        StoragePaint.WellPanel(c, well, 1f);
        var search = SKRect.Empty;
        var listArea = well;
        if (scene.Search is { } query)
        {
            search = new SKRect(well.Left + RowInset + 12f, well.Top + RowInset, well.Right - RowInset - 28f, well.Top + RowInset + SearchHeight - 18f);
            if (scene.FromBank) PaintSource(c, search, face);
            else PaintSearch(c, search, query, face);
            listArea = new SKRect(well.Left, well.Top + SearchHeight, well.Right, well.Bottom);
        }
        var rows = PaintRows(c, listArea, scene, face);

        var side = new SKRect(ListWidth + Gap, 0, DesignWidth, height);
        var tabs = PaintTabs(c, new SKRect(side.Left, 0, side.Right, HeaderHeight), scene);
        var panel = new SKRect(side.Left, HeaderHeight + 16f, side.Right, height);
        var (actions, minus, plus, bankMinus, bankPlus) = PaintPanel(c, panel, scene, face);

        c.Restore();
        SKRect Px(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        var visible = Math.Max(1, (int)((listArea.Height - 2 * RowInset) / RowHeight));
        return new BagHits(
            Px(new SKRect(0, 0, 150, HeaderHeight)),
            Px(new SKRect(ListWidth - 150, 0, ListWidth, HeaderHeight)),
            [.. tabs.Select(Px)],
            [.. rows.Select(row => (row.Index, Px(row.Rect)))],
            Px(well),
            [.. actions.Select(a => (a.Action, Px(a.Rect)))],
            Px(minus), Px(plus), visible, RowHeight * unit, Px(search), Px(bankMinus), Px(bankPlus));
    }

    // ── The list: the box well, item rows on alternating bands, the cursor as the section chip ──

    private static List<(int Index, SKRect Rect)> PaintRows(SKCanvas c, SKRect well, BagScene scene, SKTypeface face)
    {
        var hits = new List<(int Index, SKRect Rect)>();
        using var name = new SKFont(face, 44f);
        using var count = new SKFont(face, 42f);
        if (scene.Items.Count == 0)
        {
            var pouch = scene.Pouches.Count > 0 ? scene.Pouches[scene.Pouch].Title : "This pouch";
            using var small = new SKFont(face, 28f);
            if (scene.FromBank)
            {
                Centered(c, $"The Item Bank holds nothing for {pouch}.", well.MidX, well.MidY - 24f, name, T.Value);
                Centered(c, "\u201cSend to Item Bank\u201d in an item's menu fills it. X goes back to the catalogue.", well.MidX, well.MidY + 28f, small, T.SubInk);
                return hits;
            }
            if (scene.Search is { } query)
            {
                Centered(c, query.Length > 0 ? $"No item matches \u201c{query}\u201d." : $"Nothing can go in {pouch} in this game.", well.MidX, well.MidY - 24f, name, T.Value);
                Centered(c, "Y changes the search, B goes back to the bag.", well.MidX, well.MidY + 28f, small, T.SubInk);
                return hits;
            }
            Centered(c, $"Nothing in {pouch} yet.", well.MidX, well.MidY - 24f, name, T.Value);
            Centered(c, scene.CanEdit ? "Y adds an item." : "Hardcore mode keeps the bag as it is.", well.MidX, well.MidY + 28f, small, T.SubInk);
            return hits;
        }

        var visible = Math.Max(1, (int)((well.Height - 2 * RowInset) / RowHeight));
        c.Save();
        c.ClipRect(SKRect.Inflate(well, -4f, -4f));
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        for (var i = scene.Scroll; i < scene.Items.Count && i < scene.Scroll + visible; i++)
        {
            var top = well.Top + RowInset + (i - scene.Scroll) * RowHeight;
            var row = new SKRect(well.Left + RowInset + 12f, top, well.Right - RowInset - 28f, top + RowHeight - 6f);
            hits.Add((i, row));
            var item = scene.Items[i];
            var selected = i == scene.Cursor;
            if (selected) Chip(c, row, focused: !scene.InMenu);
            else
            {
                using var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true };
                c.DrawRoundRect(row, 12f, 12f, band);
            }
            var art = new SKRect(row.Left + 18f, row.MidY - 46f, row.Left + 110f, row.MidY + 46f);
            if (item.Art is { } image)
            {
                // A dimmed row's art sits faded, the way the games grey out what cannot be used.
                using var fade = new SKPaint { Color = SKColors.White.WithAlpha(item.Dimmed ? (byte)90 : (byte)255) };
                c.DrawImage(image, Fit(image, art), sampling, fade);
            }
            var ink = selected ? T.ChipInk : item.Dimmed ? T.SubInk : T.Value;
            Text(c, item.Name, row.Left + 128f, row.MidY, name, ink, row.Width - 300f);
            if (scene.FromBank)
            {
                using var banked = new SKFont(face, 30f);
                Right(c, item.Dimmed ? "can't go here" : $"×{item.Count} banked", row.Right - 34f, row.MidY, banked, selected ? T.ChipInk : T.SubInk);
            }
            else if (scene.Search is null) Right(c, $"×{item.Count}", row.Right - 34f, row.MidY, count, ink);
            else if (item.Count > 0)
            {
                using var held = new SKFont(face, 30f);
                Right(c, $"×{item.Count} in bag", row.Right - 34f, row.MidY, held, selected ? T.ChipInk : T.SubInk);
            }
        }
        c.Restore();

        // A thin rail on the right says where the list is when it does not fit.
        if (scene.Items.Count > visible)
        {
            var rail = new SKRect(well.Right - RowInset - 10f, well.Top + RowInset, well.Right - RowInset - 4f, well.Bottom - RowInset);
            using var track = new SKPaint { Color = T.Band.WithAlpha(120), IsAntialias = true };
            c.DrawRoundRect(rail, 3f, 3f, track);
            var span = rail.Height * visible / scene.Items.Count;
            var at = rail.Top + (rail.Height - span) * scene.Scroll / Math.Max(1, scene.Items.Count - visible);
            using var thumb = new SKPaint { Color = T.Rim, IsAntialias = true };
            c.DrawRoundRect(new SKRect(rail.Left, at, rail.Right, at + span), 3f, 3f, thumb);
        }
        return hits;
    }

    /// <summary>The search plate: what is typed so far, or the invitation to type.</summary>
    private static void PaintSearch(SKCanvas c, SKRect r, string query, SKTypeface face)
    {
        using (var fill = new SKPaint { Color = T.Band.WithAlpha(170), IsAntialias = true }) c.DrawRoundRect(r, 12f, 12f, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), 12f, 12f, rim);
        // A magnifier drawn in two strokes, the app's line weight.
        using (var glass = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f, StrokeCap = SKStrokeCap.Round })
        {
            var cx = r.Left + 36f;
            c.DrawCircle(cx, r.MidY - 3f, 11f, glass);
            c.DrawLine(cx + 8f, r.MidY + 5f, cx + 17f, r.MidY + 14f, glass);
        }
        using var font = new SKFont(face, 34f);
        if (query.Length > 0) Text(c, query, r.Left + 70f, r.MidY, font, T.Value, r.Width - 100f);
        else Text(c, "Search: Y or a tap", r.Left + 70f, r.MidY, font, T.SubInk, r.Width - 100f);
    }

    /// <summary>The source plate while adding from the Item Bank: the same plate as the search, the bank's words.</summary>
    private static void PaintSource(SKCanvas c, SKRect r, SKTypeface face)
    {
        using (var fill = new SKPaint { Color = T.Band.WithAlpha(170), IsAntialias = true }) c.DrawRoundRect(r, 12f, 12f, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), 12f, 12f, rim);
        using var font = new SKFont(face, 34f);
        Text(c, "From the Item Bank \u00b7 X: the catalogue", r.Left + 30f, r.MidY, font, T.Value, r.Width - 60f);
    }

    // ── The pouches: the summary's slanted icon tabs ──

    private static List<SKRect> PaintTabs(SKCanvas c, SKRect r, BagScene scene) =>
        IconTabs(c, r, [.. scene.Pouches.Select(p => p.Icon)], scene.Pouch);

    // ── The item: the cyan-rimmed panel, the name tab, the item, then its menu ──

    private static (List<(BagAction Action, SKRect Rect)> Actions, SKRect Minus, SKRect Plus, SKRect BankMinus, SKRect BankPlus) PaintPanel(SKCanvas c, SKRect r, BagScene scene, SKTypeface face)
    {
        const float radius = 28f;
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(r, radius, radius, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), radius, radius, rim);

        var actions = new List<(BagAction Action, SKRect Rect)>();
        SKRect minus = SKRect.Empty, plus = SKRect.Empty, bankMinus = SKRect.Empty, bankPlus = SKRect.Empty;
        var item = scene.Items.Count > 0 ? scene.Items[Math.Clamp(scene.Cursor, 0, scene.Items.Count - 1)] : null;
        var y = r.Top + 26f;
        using var big = new SKFont(face, 40f);
        using var mid = new SKFont(face, 30f);
        using var small = new SKFont(face, 26f);

        if (item is not null)
        {
            // The name tab, run off the panel's left edge like the summary's name banner.
            var tab = new SKRect(r.Left - 2f, y, r.Left + r.Width * 0.82f, y + 70f);
            using (var path = Tab(tab, 0f, 34f))
            {
                Gradient(c, path, T.BannerTop, T.BannerBottom);
                Outline(c, path, Pksm.Ink, 3f);
            }
            Text(c, item.Name, tab.Left + 30f, tab.MidY, big, Pksm.Ink, tab.Width - 80f);
            var pouch = scene.Pouches.Count > 0 ? scene.Pouches[scene.Pouch].Title : "";
            var place = scene.Search is null ? $"In {pouch}" : item.Count > 0 ? $"×{item.Count} in {pouch}" : $"Not in {pouch} yet";
            if (item.Note is { } note) place = note;
            var where = item.FirstSeen is { } first ? $"{place} · first seen in {first}" : place;
            Text(c, where, r.Left + 30f, tab.Bottom + 30f, small, T.SubInk, r.Width - 60f);

            // The item itself, large, in crisp whole pixel steps.
            var cy = tab.Bottom + 125f;
            var drawn = SKRect.Empty;
            if (item.Art is { } art) drawn = Fit(art, new SKRect(r.MidX - 100f, cy - 100f, r.MidX + 100f, cy + 100f));
            if (item.Art is { } image)
                c.DrawImage(image, drawn, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
            y = cy + 108f;
        }
        else
        {
            Centered(c, "This pouch is empty.", r.MidX, y + 160f, mid, T.Value);
            if (scene.CanEdit) Centered(c, "Y adds an item, X fills it from a preset.", r.MidX, y + 210f, small, T.SubInk);
            y += 260f;
        }

        // The menu: banded rows, the cursor as the chip; the count row has its arrows.
        var list = Actions(scene);
        var rowHeight = ActionHeight;
        var menuTop = r.Bottom - 70f - list.Count * rowHeight;

        // What the item is: the games' own words, then where it began and who carries it.
        if (item is not null)
        {
            var textLeft = r.Left + 30f;
            var width = r.Width - 60f;
            var line = y;
            if (item.About is { } about)
                foreach (var text in Wrap(about, mid, width).Take(4))
                {
                    if (line + 34f > menuTop - 12f) break;
                    Text(c, text, textLeft, line + 17f, mid, T.Value, width);
                    line += 38f;
                }
            line += 8f;
            if (item.HeldBy is { } held && line + 30f <= menuTop - 12f)
                Text(c, $"Wild {held} may hold one.", textLeft, line + 15f, small, T.SubInk, width);
        }

        for (var i = 0; i < list.Count; i++)
        {
            var action = list[i];
            var row = new SKRect(r.Left + 24f, menuTop + i * rowHeight, r.Right - 24f, menuTop + (i + 1) * rowHeight - 6f);
            actions.Add((action, row));
            var focused = scene.InMenu && scene.Action == action;
            var enabled = scene.CanEdit && !(item?.Dimmed ?? false);
            if (focused) Chip(c, row, focused: true);
            else
            {
                using var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true };
                c.DrawRoundRect(row, 10f, 10f, band);
            }
            var ink = !enabled ? T.SubInk : focused ? T.ChipInk : T.Value;
            if (action == BagAction.Count && item is not null)
            {
                Text(c, scene.Search is null ? "Count" : "How many", row.Left + 24f, row.MidY, mid, ink, 260f);
                minus = new SKRect(row.Right - 260f, row.Top, row.Right - 190f, row.Bottom);
                plus = new SKRect(row.Right - 70f, row.Top, row.Right, row.Bottom);
                Arrow(c, minus.MidX, row.MidY, -1, ink);
                Centered(c, $"×{(scene.Search is null ? item.Count : scene.Amount)}", (minus.Right + plus.Left) / 2, row.MidY, big, ink);
                Arrow(c, plus.MidX, row.MidY, 1, ink);
            }
            else if (action == BagAction.Bank && item is not null)
            {
                Text(c, "To the Item Bank", row.Left + 24f, row.MidY, mid, ink, row.Width - 330f);
                bankMinus = new SKRect(row.Right - 260f, row.Top, row.Right - 190f, row.Bottom);
                bankPlus = new SKRect(row.Right - 70f, row.Top, row.Right, row.Bottom);
                Arrow(c, bankMinus.MidX, row.MidY, -1, ink);
                Centered(c, $"×{Math.Min(scene.BankAmount, Math.Max(1, item.Count))}", (bankMinus.Right + bankPlus.Left) / 2, row.MidY, big, ink);
                Arrow(c, bankPlus.MidX, row.MidY, 1, ink);
            }
            else Text(c, Label(action), row.Left + 24f, row.MidY, mid, ink, row.Width - 48f);
        }

        if (scene.Status.Length > 0)
            Text(c, scene.Status, r.Left + 30f, r.Bottom - 36f, small, T.SubInk, r.Width - 60f);
        return (actions, minus, plus, bankMinus, bankPlus);
    }
}
