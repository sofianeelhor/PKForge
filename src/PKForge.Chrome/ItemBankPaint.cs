using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One row of either side: the item's name, its count, its art (null while it loads),
/// a short note under the count (where it lands, why it stays), dimmed when it cannot move.</summary>
public sealed record ItemBankSceneRow(string Name, int Count, SKImage? Art, string? Note = null, bool Dimmed = false);

/// <summary>The action menu open on the focused side: its rows, the one under the cursor, the
/// amount the arrows set, and why the menu cannot act (null when it can).</summary>
public sealed record ItemBankSceneMenu(IReadOnlyList<string> Actions, int Action, int Amount, string? Refusal = null);

/// <summary>Everything the Item Bank screen shows at one moment.</summary>
public sealed record ItemBankScene(
    IReadOnlyList<BagScenePouch> Tabs,
    int Tab,
    IReadOnlyList<ItemBankSceneRow> Bank,
    int BankCursor,
    int BankScroll,
    // Null when no game is chosen yet: the right side invites the player to pick one.
    string? Game,
    IReadOnlyList<ItemBankSceneRow> Bag,
    int BagCursor,
    int BagScroll,
    bool BankFocused,
    string Search,
    string Sort,
    // The line under the focused item's name: origins for a banked item, the pouch for a bag item.
    string? Detail,
    ItemBankSceneMenu? Menu,
    bool CanEdit,
    string Status,
    // The Bank's section tabs over the right side ("Pokémon | Items"), and where the pad cursor is on them.
    IReadOnlyList<BankSection>? Sections = null,
    int? SectionCursor = null,
    // Not null while making items: the generator replaces the two lists.
    ItemMakerScene? Maker = null);

/// <summary>
/// The Item Bank's generator: the catalogue of one game family (its pouches as tabs, the search),
/// the item under the cursor large in its own panel with how many one generation makes, and the
/// menu that adds them to the bank. Count on a row is how many the bank already holds.
/// </summary>
public sealed record ItemMakerScene(
    string Family,
    IReadOnlyList<BagScenePouch> Tabs,
    int Tab,
    IReadOnlyList<ItemBankSceneRow> Items,
    int Cursor,
    int Scroll,
    string Search,
    // The lines beside the item's art: the family and generation, the stack, what is banked.
    IReadOnlyList<string> Facts,
    ItemBankSceneMenu? Menu);

/// <summary>Where each touch target landed on the last paint, in canvas pixels.</summary>
public sealed record ItemBankHits(
    SKRect Previous,
    SKRect Next,
    IReadOnlyList<SKRect> Tabs,
    SKRect BankList,
    SKRect BagList,
    IReadOnlyList<(int Index, SKRect Rect)> BankRows,
    IReadOnlyList<(int Index, SKRect Rect)> BagRows,
    SKRect Search,
    SKRect Sort,
    SKRect Game,
    IReadOnlyList<(int Index, SKRect Rect)> Actions,
    SKRect Minus,
    SKRect Plus,
    float RowHeight,
    IReadOnlyList<SKRect>? Sections = null,
    SKRect Make = default);

/// <summary>
/// The Item Bank screen in the app's art direction, built from the bag's parts (<see cref="BagPaint"/>):
/// the bank on the left under the box banner, the Bank's section tabs on the right where the
/// Pokémon Bank paints them, the pouches as the summary's slanted icon tabs across both sides,
/// a game's bag on the right, each list in a box well with the section chip as cursor, and the
/// focused item in a cyan-rimmed panel with its menu. The generator takes the same frame: its
/// catalogue in the left well, the item large in a panel on the right. Sizes are design pixels
/// scaled by a unit.
/// </summary>
public static class ItemBankPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float Gap = 24f;
    private const float HeaderHeight = 88f;
    private const float PlateTop = HeaderHeight + 12f;
    private const float PlateHeight = 62f;
    private const float PouchTop = PlateTop + PlateHeight + 8f;
    private const float PouchHeight = 74f;
    private const float RowHeight = 78f;
    private const float RowInset = 16f;
    private const float PanelHeight = 248f;
    private const float ActionHeight = 58f;
    private const float MakeWidth = 262f;
    private const float HintHeight = 62f;
    private const float SortWidth = 252f;

    private static float Column => (DesignWidth - Gap) / 2f;

    /// <summary>The section tabs, over the same pixels as on the Pokémon Bank (its side panel's
    /// column, level with the box banner), so turning to "Items" changes only what is under them.</summary>
    private static SKRect SectionsRect => new(1126f, 16f, DesignWidth - 14f, HeaderHeight);

    /// <summary>How many rows each list shows on a canvas of this size; the generator's
    /// catalogue, which runs down to the hint strip, shows more.</summary>
    public static int VisibleRows(SKSizeI size, bool making = false)
    {
        if (size.Width <= 0) return 1;
        var height = size.Height / (size.Width / DesignWidth);
        var bottom = making ? height - HintHeight - 12f : WellBottom(height);
        return VisibleIn(new SKRect(0, WellTop, 1, bottom));
    }

    private static int VisibleIn(SKRect well) => Math.Max(1, (int)((well.Height - 2 * RowInset) / RowHeight));

    private const float WellTop = PouchTop + PouchHeight + 8f;
    private static float WellBottom(float height) => height - PanelHeight - 12f;

    public static ItemBankHits Paint(SKCanvas c, SKImageInfo info, ItemBankScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);
        var left = new SKRect(0, 0, Column, height);
        var right = new SKRect(Column + Gap, 0, DesignWidth, height);
        var maker = scene.Maker;
        var pouches = maker?.Tabs ?? scene.Tabs;
        var pouch = maker?.Tab ?? scene.Tab;

        // The bank: its banner names the tab, the chevrons turn tabs like the bag's.
        var tabTitle = pouches.Count > 0 ? pouches[Math.Clamp(pouch, 0, pouches.Count - 1)].Title : "All";
        var banner = maker is null ? $"ITEM BANK · {tabTitle}" : $"MAKE · {tabTitle}";
        using (var font = new SKFont(face, 38f))
            StoragePaint.BoxHeader(c, new SKRect(left.Left, 0, left.Right, HeaderHeight), banner.ToUpperInvariant(),
                font, pouches.Count > 1, pouches.Count > 1, 1f);
        // The Bank's sections sit where the Pokémon Bank paints them: this is its other tab.
        var sections = scene.Sections is { Count: > 0 } named
            ? BankSectionPaint.Strip(c, SectionsRect, named, 1, scene.SectionCursor, face)
            : [];

        // The pouches as the summary's slanted icon tabs, across both sides: a tab narrows both lists.
        var width = Math.Min(DesignWidth - 40f, Math.Max(1, pouches.Count) * 150f + 40f);
        var strip = new SKRect(DesignWidth / 2 - width / 2, PouchTop, DesignWidth / 2 + width / 2, PouchTop + PouchHeight);
        var tabs = IconTabs(c, strip, [.. pouches.Select(t => t.Icon)], pouch);

        // The plates: search and sort (and the generator) over the bank, the chosen game over the bag.
        var search = maker is null
            ? new SKRect(left.Left + 8f, PlateTop, left.Right - MakeWidth - SortWidth - 24f, PlateTop + PlateHeight)
            : new SKRect(left.Left + 8f, PlateTop, left.Right - 8f, PlateTop + PlateHeight);
        var sort = maker is null ? new SKRect(search.Right + 12f, PlateTop, left.Right - MakeWidth - 14f, PlateTop + PlateHeight) : SKRect.Empty;
        var make = maker is null ? new SKRect(left.Right - MakeWidth, PlateTop, left.Right - 8f, PlateTop + PlateHeight) : SKRect.Empty;
        var game = new SKRect(right.Left + 8f, PlateTop, right.Right - 8f, PlateTop + PlateHeight);
        var query = maker?.Search ?? scene.Search;
        using (var plateFont = new SKFont(face, 32f))
        {
            Plate(c, search);
            Glass(c, search.Left + 34f, search.MidY);
            if (query.Length > 0) Text(c, query, search.Left + 68f, search.MidY, plateFont, T.Value, search.Width - 90f);
            else Text(c, maker is null ? "Search: Y" : "Search every item: Y or a tap", search.Left + 68f, search.MidY, plateFont, T.SubInk, search.Width - 90f);
            if (maker is null)
            {
                Plate(c, sort);
                Text(c, $"Sort: {scene.Sort}", sort.Left + 22f, sort.MidY, plateFont, T.Value, sort.Width - 40f);
                // The generator's way in, as the section chip: it reads as the bank's own button.
                Chip(c, make, focused: true);
                Cross(c, make.Left + 42f, make.MidY, T.ChipInk);
                Text(c, "Make items", make.Left + 70f, make.MidY, plateFont, T.ChipInk, make.Width - 86f);
            }
            Plate(c, game);
            if (maker is { } m) Text(c, $"For {m.Family} · X: another game", game.Left + 26f, game.MidY, plateFont, T.Value, game.Width - 52f);
            else if (scene.Game is { } title) Text(c, $"{title} · X: another game", game.Left + 26f, game.MidY, plateFont, T.Value, game.Width - 52f);
            else Text(c, "No game chosen · X picks one from the shelf", game.Left + 26f, game.MidY, plateFont, T.SubInk, game.Width - 52f);
        }

        var bottom = WellBottom(height);
        var bankWell = new SKRect(left.Left, WellTop, left.Right, bottom);
        var bagWell = new SKRect(right.Left, WellTop, right.Right, bottom);
        var bankPanel = new SKRect(left.Left, bottom + 12f, left.Right, height);
        var bagPanel = new SKRect(right.Left, bottom + 12f, right.Right, height);
        List<(int Index, SKRect Rect)> bankRows, bagRows = [];
        List<(int Index, SKRect Rect)> actions;
        SKRect minus, plus;
        using var empty = new SKFont(face, 36f);
        using var small = new SKFont(face, 26f);
        if (maker is not null)
        {
            // The catalogue takes the whole left side, a slim strip of hints under it.
            bankWell = new SKRect(left.Left, WellTop, left.Right, height - HintHeight - 12f);
            bankPanel = new SKRect(left.Left, height - HintHeight, left.Right, height);
            StoragePaint.WellPanel(c, bankWell, 1f);
            bankRows = Rows(c, bankWell, maker.Items, maker.Cursor, maker.Scroll, maker.Menu is null, true, face, banked: true);
            if (maker.Items.Count == 0)
            {
                Centered(c, query.Length > 0 ? $"No item matches “{query}”." : $"Nothing to make in {tabTitle}.", bankWell.MidX, bankWell.MidY - 20f, empty, T.Value);
                Centered(c, "Y changes the search, X the game family.", bankWell.MidX, bankWell.MidY + 26f, small, T.SubInk);
            }
            Frame(c, bankPanel, lit: false);
            using (var hint = new SKFont(face, 28f))
                Text(c, maker.Menu is null
                    ? "A: how many · L R: a page · Y: search · X: game family · B: back"
                    : "◀ ▶: one more or less · L R: ten · A: make them · B: not now",
                    bankPanel.Left + 28f, bankPanel.MidY, hint, T.SubInk, bankPanel.Width - 56f);
            (actions, minus, plus) = MakerPanel(c, new SKRect(right.Left, WellTop, right.Right, height), scene, maker, face);
        }
        else
        {
            StoragePaint.WellPanel(c, bankWell, 1f);
            StoragePaint.WellPanel(c, bagWell, 1f);
            bankRows = Rows(c, bankWell, scene.Bank, scene.BankCursor, scene.BankScroll, scene.BankFocused && scene.Menu is null,
                scene.BankFocused, face);
            bagRows = Rows(c, bagWell, scene.Bag, scene.BagCursor, scene.BagScroll, !scene.BankFocused && scene.Menu is null,
                !scene.BankFocused, face);
            if (scene.Bank.Count == 0)
            {
                Centered(c, scene.Search.Length > 0 ? $"Nothing banked matches “{scene.Search}”." : $"Nothing banked in {tabTitle}.",
                    bankWell.MidX, bankWell.MidY - 20f, empty, T.Value);
                Centered(c, "Send items here from a game, or make some with −.", bankWell.MidX, bankWell.MidY + 26f, small, T.SubInk);
            }
            if (scene.Game is null)
            {
                Centered(c, "Choose a game to trade items with.", bagWell.MidX, bagWell.MidY - 20f, empty, T.Value);
                Centered(c, "X or a tap on the plate above: any game on the shelf.", bagWell.MidX, bagWell.MidY + 26f, small, T.SubInk);
            }
            else if (scene.Bag.Count == 0)
            {
                Centered(c, $"Nothing in {tabTitle} in this game.", bagWell.MidX, bagWell.MidY - 20f, empty, T.Value);
                Centered(c, "Banked items that fit come in with A on the left.", bagWell.MidX, bagWell.MidY + 26f, small, T.SubInk);
            }

            // The focused item's panel sits under its own side; the other side keeps a quiet one.
            (actions, minus, plus) = Panel(c, scene.BankFocused ? bankPanel : bagPanel, scene, face);
            Quiet(c, scene.BankFocused ? bagPanel : bankPanel, scene, face,
                scene.BankFocused ? "L / R: over to the bag" : "L / R: over to the Item Bank");
        }

        c.Restore();
        SKRect Px(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new ItemBankHits(
            Px(new SKRect(left.Left, 0, left.Left + 150f, HeaderHeight)),
            Px(new SKRect(left.Right - 150f, 0, left.Right, HeaderHeight)),
            [.. tabs.Select(Px)],
            Px(bankWell), maker is null ? Px(bagWell) : SKRect.Empty,
            [.. bankRows.Select(r => (r.Index, Px(r.Rect)))],
            [.. bagRows.Select(r => (r.Index, Px(r.Rect)))],
            Px(search), Px(sort), Px(game),
            [.. actions.Select(a => (a.Index, Px(a.Rect)))],
            Px(minus), Px(plus), RowHeight * unit,
            [.. sections.Select(Px)], Px(make));
    }

    private static void Plate(SKCanvas c, SKRect r)
    {
        if (r.IsEmpty) return;
        using (var fill = new SKPaint { Color = T.Band.WithAlpha(170), IsAntialias = true }) c.DrawRoundRect(r, 12f, 12f, fill);
        using var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f };
        c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), 12f, 12f, rim);
    }

    private static void Glass(SKCanvas c, float cx, float cy)
    {
        using var glass = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f, StrokeCap = SKStrokeCap.Round };
        c.DrawCircle(cx, cy - 3f, 11f, glass);
        c.DrawLine(cx + 8f, cy + 5f, cx + 17f, cy + 14f, glass);
    }

    /// <summary>A square-ended plus in the app's line weight: the generator's mark.</summary>
    private static void Cross(SKCanvas c, float cx, float cy, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = false };
        c.DrawRect(new SKRect(cx - 14f, cy - 4f, cx + 14f, cy + 4f), paint);
        c.DrawRect(new SKRect(cx - 4f, cy - 14f, cx + 4f, cy + 14f), paint);
    }

    /// <param name="banked">The generator's catalogue: a count is how many the bank holds, shown only when some are.</param>
    private static List<(int Index, SKRect Rect)> Rows(SKCanvas c, SKRect well, IReadOnlyList<ItemBankSceneRow> rows, int cursor, int scroll,
        bool focused, bool sideFocused, SKTypeface face, bool banked = false)
    {
        var hits = new List<(int Index, SKRect Rect)>();
        if (rows.Count == 0) return hits;
        using var name = new SKFont(face, 38f);
        using var count = new SKFont(face, 38f);
        using var note = new SKFont(face, 24f);
        using var held = new SKFont(face, 28f);
        var visible = VisibleIn(well);
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        c.Save();
        c.ClipRect(SKRect.Inflate(well, -4f, -4f));
        for (var i = scroll; i < rows.Count && i < scroll + visible; i++)
        {
            var top = well.Top + RowInset + (i - scroll) * RowHeight;
            var rect = new SKRect(well.Left + RowInset + 8f, top, well.Right - RowInset - 24f, top + RowHeight - 6f);
            hits.Add((i, rect));
            var row = rows[i];
            // The other side keeps its cursor, unlit, so the player sees where each side stands.
            var selected = i == cursor;
            if (selected && sideFocused) Chip(c, rect, focused);
            else if (selected)
            {
                using var keep = new SKPaint { Color = T.Rim.WithAlpha(110), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f };
                using var band = new SKPaint { Color = EditorPaint.RowBand(true), IsAntialias = true };
                c.DrawRoundRect(rect, 12f, 12f, band);
                c.DrawRoundRect(SKRect.Inflate(rect, -1.5f, -1.5f), 12f, 12f, keep);
            }
            else
            {
                using var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true };
                c.DrawRoundRect(rect, 12f, 12f, band);
            }
            var art = new SKRect(rect.Left + 14f, rect.MidY - 34f, rect.Left + 82f, rect.MidY + 34f);
            if (row.Art is { } image)
            {
                using var fade = new SKPaint { Color = SKColors.White.WithAlpha(row.Dimmed ? (byte)90 : (byte)255) };
                c.DrawImage(image, Fit(image, art), sampling, fade);
            }
            var lit = selected && sideFocused;
            var ink = lit ? T.ChipInk : row.Dimmed ? T.SubInk : T.Value;
            var countText = banked ? row.Count > 0 ? $"×{row.Count} banked" : "" : $"×{row.Count}";
            var countFont = banked ? held : count;
            var countWidth = countText.Length > 0 ? countFont.MeasureFallback(countText) : 0f;
            if (row.Note is { } line)
            {
                Text(c, row.Name, rect.Left + 96f, rect.MidY - 14f, name, ink, rect.Width - 136f - countWidth);
                Text(c, line, rect.Left + 96f, rect.MidY + 20f, note, lit ? T.ChipInk : T.SubInk, rect.Width - 136f - countWidth);
            }
            else Text(c, row.Name, rect.Left + 96f, rect.MidY, name, ink, rect.Width - 136f - countWidth);
            if (countText.Length > 0) Right(c, countText, rect.Right - 26f, rect.MidY, countFont, banked && !lit ? T.SubInk : ink);
        }
        c.Restore();
        if (rows.Count > visible)
        {
            var rail = new SKRect(well.Right - RowInset - 8f, well.Top + RowInset, well.Right - RowInset - 2f, well.Bottom - RowInset);
            using var track = new SKPaint { Color = T.Band.WithAlpha(120), IsAntialias = true };
            c.DrawRoundRect(rail, 3f, 3f, track);
            var span = Math.Max(24f, rail.Height * visible / rows.Count);
            var at = rail.Top + (rail.Height - span) * scroll / Math.Max(1, rows.Count - visible);
            using var thumb = new SKPaint { Color = T.Rim, IsAntialias = true };
            c.DrawRoundRect(new SKRect(rail.Left, at, rail.Right, at + span), 3f, 3f, thumb);
        }
        return hits;
    }

    private static void Frame(SKCanvas c, SKRect r, bool lit)
    {
        const float radius = 24f;
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(r, radius, radius, fill);
        using var rim = new SKPaint { Color = lit ? T.Rim : T.Rim.WithAlpha(110), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f };
        c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), radius, radius, rim);
    }

    /// <summary>The unfocused side's panel: a quiet frame saying how to get there, and the status.</summary>
    private static void Quiet(SKCanvas c, SKRect r, ItemBankScene scene, SKTypeface face, string hint)
    {
        Frame(c, r, lit: false);
        using var small = new SKFont(face, 28f);
        Text(c, hint, r.Left + 28f, r.Top + 40f, small, T.SubInk, r.Width - 56f);
        if (scene.Status.Length > 0)
            foreach (var (line, i) in Wrap(scene.Status, small, r.Width - 56f).Take(4).Select((l, i) => (l, i)))
                Text(c, line, r.Left + 28f, r.Top + 92f + i * 36f, small, T.Value, r.Width - 56f);
    }

    /// <summary>The focused item: its name tab, the detail line, then its menu when open.</summary>
    private static (List<(int Index, SKRect Rect)> Actions, SKRect Minus, SKRect Plus) Panel(SKCanvas c, SKRect r, ItemBankScene scene, SKTypeface face)
    {
        Frame(c, r, lit: true);
        var rows = scene.BankFocused ? scene.Bank : scene.Bag;
        var cursor = scene.BankFocused ? scene.BankCursor : scene.BagCursor;
        var item = rows.Count > 0 ? rows[Math.Clamp(cursor, 0, rows.Count - 1)] : null;
        using var big = new SKFont(face, 36f);
        using var mid = new SKFont(face, 30f);
        using var small = new SKFont(face, 26f);
        if (item is null)
        {
            Text(c, scene.BankFocused ? "The Item Bank" : scene.Game ?? "No game", r.Left + 28f, r.Top + 44f, big, T.Value, r.Width - 56f);
            if (scene.Status.Length > 0) Text(c, scene.Status, r.Left + 28f, r.Top + 96f, small, T.SubInk, r.Width - 56f);
            return ([], SKRect.Empty, SKRect.Empty);
        }

        var tab = new SKRect(r.Left - 2f, r.Top + 16f, r.Left + r.Width * 0.7f, r.Top + 72f);
        using (var path = Tab(tab, 0f, 30f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        Text(c, item.Name, tab.Left + 28f, tab.MidY, big, Pksm.Ink, tab.Width - 70f);
        if (item.Art is { } art)
        {
            var box = new SKRect(r.Right - 116f, r.Top + 10f, r.Right - 24f, r.Top + 102f);
            c.DrawImage(art, Fit(art, box), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }
        var line = r.Top + 96f;
        // With the menu open, a refusal or the last report takes the detail line: it says why A does nothing.
        var said = scene.Menu is { } open ? open.Refusal ?? (scene.Status.Length > 0 ? scene.Status : null) : null;
        if (said is not null) Text(c, said, r.Left + 28f, line, small, T.Value, r.Width - 56f);
        else if (scene.Detail is { } detail) Text(c, detail, r.Left + 28f, line, small, T.SubInk, r.Width - 160f);

        if (scene.Menu is not { } menu)
        {
            var hint = scene.Status.Length > 0 ? scene.Status
                : !scene.CanEdit ? "Hardcore mode keeps the games' bags as they are."
                : item.Dimmed ? item.Note ?? "It cannot move from here."
                : scene.BankFocused ? "A: send it to the game or throw it away." : "A: send it to the Item Bank.";
            foreach (var (text, i) in Wrap(hint, mid, r.Width - 56f).Take(3).Select((l, i) => (l, i)))
                Text(c, text, r.Left + 28f, line + 44f + i * 36f, mid, scene.Status.Length > 0 ? T.Value : T.SubInk, r.Width - 56f);
            return ([], SKRect.Empty, SKRect.Empty);
        }
        return MenuRows(c, r, menu, r.Bottom - 16f - menu.Actions.Count * ActionHeight, mid, big);
    }

    /// <summary>The menu: one banded row per action; the amount's arrows live on the focused row.</summary>
    private static (List<(int Index, SKRect Rect)> Actions, SKRect Minus, SKRect Plus) MenuRows(SKCanvas c, SKRect r, ItemBankSceneMenu menu,
        float top, SKFont mid, SKFont big)
    {
        var actions = new List<(int Index, SKRect Rect)>();
        SKRect minus = SKRect.Empty, plus = SKRect.Empty;
        for (var i = 0; i < menu.Actions.Count; i++)
        {
            var row = new SKRect(r.Left + 20f, top + i * ActionHeight, r.Right - 20f, top + (i + 1) * ActionHeight - 6f);
            actions.Add((i, row));
            var focused = i == menu.Action;
            if (focused) Chip(c, row, focused: true);
            else
            {
                using var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true };
                c.DrawRoundRect(row, 10f, 10f, band);
            }
            // The refusal is the focused action's: that row reads dimmed, the others stay lit.
            var enabled = !focused || menu.Refusal is null;
            var ink = !enabled ? T.SubInk : focused ? T.ChipInk : T.Value;
            Text(c, menu.Actions[i], row.Left + 26f, row.MidY, mid, ink, row.Width - 300f);
            if (!focused) continue;
            minus = new SKRect(row.Right - 250f, row.Top, row.Right - 180f, row.Bottom);
            plus = new SKRect(row.Right - 70f, row.Top, row.Right, row.Bottom);
            Arrow(c, minus.MidX, row.MidY, -1, ink);
            Centered(c, $"×{menu.Amount}", (minus.Right + plus.Left) / 2, row.MidY, big, ink);
            Arrow(c, plus.MidX, row.MidY, 1, ink);
        }
        return (actions, minus, plus);
    }

    /// <summary>The generator's item: its name tab, its art large, what one generation makes, then the menu.</summary>
    private static (List<(int Index, SKRect Rect)> Actions, SKRect Minus, SKRect Plus) MakerPanel(SKCanvas c, SKRect r, ItemBankScene scene,
        ItemMakerScene maker, SKTypeface face)
    {
        Frame(c, r, lit: true);
        using var big = new SKFont(face, 38f);
        using var mid = new SKFont(face, 30f);
        using var small = new SKFont(face, 26f);
        var item = maker.Items.Count > 0 ? maker.Items[Math.Clamp(maker.Cursor, 0, maker.Items.Count - 1)] : null;
        if (item is null)
        {
            Centered(c, $"Making items for {maker.Family}", r.MidX, r.MidY - 20f, mid, T.Value);
            Centered(c, "Pick an item on the left.", r.MidX, r.MidY + 24f, small, T.SubInk);
            return ([], SKRect.Empty, SKRect.Empty);
        }
        var tab = new SKRect(r.Left - 2f, r.Top + 20f, r.Left + r.Width * 0.8f, r.Top + 86f);
        using (var path = Tab(tab, 0f, 32f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        Text(c, item.Name, tab.Left + 30f, tab.MidY, big, Pksm.Ink, tab.Width - 76f);

        // The menu sits at the bottom; the art and the facts share what is above it.
        var menu = maker.Menu;
        var menuTop = r.Bottom - 22f - (menu?.Actions.Count ?? 1) * ActionHeight;
        var side = Math.Min(232f, menuTop - 12f - (tab.Bottom + 22f));
        var artBox = new SKRect(r.Left + 34f, tab.Bottom + 22f, r.Left + 34f + side, tab.Bottom + 22f + side);
        if (item.Art is { } art && side > 24f)
            c.DrawImage(art, Fit(art, artBox), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        var textLeft = artBox.Right + 30f;
        var textWidth = r.Right - textLeft - 28f;
        var line = artBox.Top + 20f;
        for (var i = 0; i < maker.Facts.Count; i++)
        {
            if (line + 16f > menuTop - 8f) break;
            Text(c, maker.Facts[i], textLeft, line, i == 0 ? mid : small, i == 0 ? T.Value : T.SubInk, textWidth);
            line += i == 0 ? 44f : 38f;
        }
        // A refusal belongs to the item; with none, the report of the last make.
        if ((menu?.Refusal ?? (scene.Status.Length > 0 ? scene.Status : null)) is { } said)
            foreach (var text in Wrap(said, small, textWidth).Take(2))
            {
                if (line + 16f > menuTop - 8f) break;
                Text(c, text, textLeft, line + 6f, small, T.Value, textWidth);
                line += 34f;
            }

        if (menu is null)
        {
            var row = new SKRect(r.Left + 20f, menuTop, r.Right - 20f, menuTop + ActionHeight - 6f);
            using var band = new SKPaint { Color = EditorPaint.RowBand(true), IsAntialias = true };
            c.DrawRoundRect(row, 10f, 10f, band);
            Text(c, scene.CanEdit ? "A or a second tap on the item: how many" : "Hardcore mode makes no items.",
                row.Left + 26f, row.MidY, mid, T.SubInk, row.Width - 52f);
            return ([], SKRect.Empty, SKRect.Empty);
        }
        return MenuRows(c, r, menu, menuTop, mid, big);
    }
}
