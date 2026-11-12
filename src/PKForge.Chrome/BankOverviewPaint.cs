using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One Bank box as the overview shows it: its name, how full, its 30 icons (null for an
/// empty slot or one still loading), its wallpaper art (null for a flat tone).</summary>
public sealed record BankOverviewCard(
    int Box,
    string Name,
    int Count,
    bool Locked,
    bool LivingDex,
    IReadOnlyList<SKImage?> Icons,
    SKImage? Wallpaper,
    SKColor WallpaperAverage,
    SKColor Tone);

/// <summary>A run of boxes under one heading: a group, or the boxes in none (Title null).
/// <paramref name="Heading"/> false draws the run without a heading (a bank with no groups).</summary>
public sealed record BankOverviewRun(string? Title, IReadOnlyList<int> Boxes, bool Heading = true);

/// <summary>Everything the box overview shows at one moment.</summary>
public sealed record BankOverviewScene(
    IReadOnlyList<BankOverviewCard> Cards,     // by box index
    IReadOnlyList<BankOverviewRun> Runs,
    string Title,                              // the banner: "All boxes" or a group's name
    bool CanCycleView,                         // the banner's chevrons switch the view
    int Cursor,                                // a box index
    float Scroll,                              // design pixels into the content
    int? Held,                                 // the box being carried
    bool Swap,
    string Status,
    IReadOnlyList<(string Key, string Label)> Prompts,
    StoragePaint.WallpaperStyle Style,
    SKImage? Padlock,
    string EmptyNote = "");

/// <summary>Where each card and heading sits, in design pixels; card and heading rects are
/// in content space (0 at the top of the scrolling area).</summary>
public sealed record BankOverviewLayout(
    IReadOnlyList<(int Box, int Row, int Column, SKRect Rect)> Cards,
    IReadOnlyList<(int Run, SKRect Rect)> Headings,
    float ContentHeight,
    SKRect Viewport,
    int Rows);

/// <summary>Touch targets from the last paint, in canvas pixels.</summary>
public sealed record BankOverviewHits(
    IReadOnlyList<(int Box, SKRect Rect)> Cards,
    SKRect Previous,
    SKRect Next,
    SKRect Banner,
    IReadOnlyList<SKRect> Prompts,
    SKRect Viewport,
    float Unit);

/// <summary>
/// Every Bank box at a glance, in the app's art direction: the view's name in the slanted box
/// banner between its chevrons, the boxes as rimmed cards in the box well (each a mini box of
/// its 30 icons over its own wallpaper, name plate on top), groups as slanted heading tabs, the
/// cursor as the cyan section chip on the card's plate. No springs, no glow: what moves, moves
/// in one step. Sizes are design pixels (a 1872 wide canvas) scaled by a unit.
/// </summary>
public static class BankOverviewPaint
{
    public static float DesignWidth => UiScale.DesignWidth;
    public const int Columns = 4;

    private const float HeaderHeight = 88f;
    private const float FooterHeight = 64f;
    private const float Inset = 24f;
    private const float Gap = 20f;
    private const float PlateHeight = 50f;
    private const float BodyHeight = 252f;
    private const float CardHeight = PlateHeight + BodyHeight;
    private const float HeadingHeight = 64f;

    /// <summary>How far one row of cards is from the next, in design pixels.</summary>
    public const float RowPitch = CardHeight + Gap;
    private const int SlotColumns = 6;
    private const int SlotRows = 5;

    private static SKSamplingOptions Nearest => new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>Lays the runs out for a canvas of <paramref name="size"/>: four cards a row, a
    /// heading over every run that has one; an empty group keeps its heading and no row.</summary>
    public static BankOverviewLayout Layout(IReadOnlyList<BankOverviewRun> runs, SKSizeI size)
    {
        var unit = Math.Max(0.01f, size.Width / DesignWidth);
        var height = size.Height / unit;
        var well = new SKRect(0, HeaderHeight + 16f, DesignWidth, height - FooterHeight - 8f);
        var viewport = new SKRect(well.Left + Inset, well.Top + Inset * 0.75f, well.Right - Inset, well.Bottom - Inset * 0.75f);
        var cardWidth = (viewport.Width - Gap * (Columns - 1)) / Columns;
        var cards = new List<(int, int, int, SKRect)>();
        var headings = new List<(int, SKRect)>();
        var y = 0f;
        var row = 0;
        for (var run = 0; run < runs.Count; run++)
        {
            if (runs[run].Heading)
            {
                headings.Add((run, new SKRect(viewport.Left, y, viewport.Right, y + HeadingHeight - 12f)));
                y += HeadingHeight;
            }
            var boxes = runs[run].Boxes;
            for (var i = 0; i < boxes.Count; i++)
            {
                var column = i % Columns;
                if (i > 0 && column == 0) { y += CardHeight + Gap; row++; }
                var left = viewport.Left + column * (cardWidth + Gap);
                cards.Add((boxes[i], row, column, new SKRect(left, y, left + cardWidth, y + CardHeight)));
            }
            if (boxes.Count > 0) { y += CardHeight + Gap; row++; }
        }
        return new BankOverviewLayout(cards, headings, Math.Max(0, y - Gap), viewport, row);
    }

    /// <summary>The scroll that keeps <paramref name="box"/>'s card (and its heading, when it
    /// opens its run) in view, moving as little as it can from <paramref name="scroll"/>.</summary>
    public static float ScrollFor(BankOverviewLayout layout, int box, float scroll)
    {
        var view = layout.Viewport.Height;
        var max = Math.Max(0, layout.ContentHeight - view);
        var card = layout.Cards.FirstOrDefault(c => c.Box == box);
        if (card.Rect.Height <= 0) return Math.Clamp(scroll, 0, max);
        var top = card.Rect.Top;
        // The run's heading comes along when the card is on its first row.
        var heading = layout.Headings.LastOrDefault(h => h.Rect.Bottom <= top + 1f);
        if (heading.Rect.Height > 0 && top - heading.Rect.Top <= HeadingHeight + 1f) top = heading.Rect.Top;
        if (top < scroll) scroll = top;
        else if (card.Rect.Bottom > scroll + view) scroll = card.Rect.Bottom - view;
        return Math.Clamp(scroll, 0, max);
    }

    public static BankOverviewHits Paint(SKCanvas c, SKImageInfo info, BankOverviewScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        var layout = Layout(scene.Runs, info.Size);
        c.Save();
        c.Scale(unit);

        // The banner: the view's name; the chevrons switch between all boxes and each group.
        var header = new SKRect(0, 0, DesignWidth, HeaderHeight);
        using (var font = new SKFont(face, 40f))
            StoragePaint.BoxHeader(c, header, scene.Title.ToUpperInvariant(), font, scene.CanCycleView, scene.CanCycleView, 1f);

        var well = new SKRect(0, HeaderHeight + 16f, DesignWidth, height - FooterHeight - 8f);
        StoragePaint.WellPanel(c, well, 1f);

        var cardHits = new List<(int, SKRect)>();
        var view = layout.Viewport;
        using var name = new SKFont(face, 30f);
        using var small = new SKFont(face, 26f);
        c.Save();
        c.ClipRect(SKRect.Inflate(view, 8f, Inset * 0.75f - 4f));
        c.Translate(0, view.Top - scene.Scroll);
        var visibleTop = scene.Scroll - 8f;
        var visibleBottom = scene.Scroll + view.Height + 8f;

        foreach (var (run, rect) in layout.Headings)
        {
            if (rect.Bottom < visibleTop || rect.Top > visibleBottom) continue;
            PaintHeading(c, rect, scene.Runs[run], scene.Cards, name, small);
        }

        SKRect heldAt = SKRect.Empty;
        foreach (var (box, _, _, rect) in layout.Cards)
        {
            if (rect.Bottom < visibleTop || rect.Top > visibleBottom || (uint)box >= (uint)scene.Cards.Count) continue;
            var onScreen = new SKRect(rect.Left, rect.Top - scene.Scroll + view.Top, rect.Right, rect.Bottom - scene.Scroll + view.Top);
            cardHits.Add((box, onScreen));
            if (box == scene.Held)
            {
                // Where the carried box came from: its outline, waiting.
                using var dash = new SKPaint
                {
                    Color = T.Rim.WithAlpha(150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f,
                    PathEffect = SKPathEffect.CreateDash([18f, 12f], 0),
                };
                c.DrawRoundRect(SKRect.Inflate(rect, -2f, -2f), 18f, 18f, dash);
                continue;
            }
            PaintCard(c, rect, scene.Cards[box], box == scene.Cursor && scene.Held is null, scene, name, small);
            if (box == scene.Cursor && scene.Held is not null) heldAt = rect;
        }

        if (scene.Held is { } held && (uint)held < (uint)scene.Cards.Count)
        {
            var target = heldAt;
            if (target.IsEmpty && layout.Cards.FirstOrDefault(card => card.Box == scene.Cursor) is { Rect.Height: > 0 } spot) target = spot.Rect;
            if (!target.IsEmpty)
            {
                if (scene.Swap)
                {
                    // Swapping: the box under the cursor is marked as the one to trade places.
                    using var veil = new SKPaint { Color = T.Well.WithAlpha(150), IsAntialias = true };
                    c.DrawRoundRect(target, 18f, 18f, veil);
                    var chip = new SKRect(target.MidX - 90f, target.Bottom - 64f, target.MidX + 90f, target.Bottom - 20f);
                    Chip(c, chip, true);
                    Centered(c, "SWAP", chip.MidX, chip.MidY, name, T.ChipInk);
                }
                else
                {
                    // Moving: the bar before the cursor's card is where the box lands.
                    using var bar = new SKPaint { Color = T.Rim, IsAntialias = true };
                    c.DrawRoundRect(new SKRect(target.Left - 15f, target.Top, target.Left - 5f, target.Bottom), 5f, 5f, bar);
                }
                // The carried box itself rides over the cursor's card, lifted a step up and over.
                var lifted = SKRect.Create(target.Left + 26f, target.Top - 26f, target.Width, target.Height);
                lifted.Offset(0, Math.Max(0, scene.Scroll - lifted.Top));
                PaintCard(c, lifted, scene.Cards[held], true, scene, name, small);
                using var gold = new SKPaint { Color = Pksm.ShinyGold, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5f };
                c.DrawRoundRect(SKRect.Inflate(lifted, -2.5f, -2.5f), 18f, 18f, gold);
            }
        }
        c.Restore();

        if (layout.Cards.Count == 0)
        {
            using var big = new SKFont(face, 40f);
            Centered(c, "No box in this group yet.", view.MidX, view.MidY - 26f, big, T.Value);
            Centered(c, scene.EmptyNote, view.MidX, view.MidY + 26f, small, T.SubInk);
        }

        // A thin rail on the right says where the view is when it does not all fit.
        if (layout.ContentHeight > view.Height)
        {
            var rail = new SKRect(well.Right - 14f, view.Top, well.Right - 8f, view.Bottom);
            using var track = new SKPaint { Color = T.Band.WithAlpha(120), IsAntialias = true };
            c.DrawRoundRect(rail, 3f, 3f, track);
            var span = Math.Max(30f, rail.Height * view.Height / layout.ContentHeight);
            var at = rail.Top + (rail.Height - span) * scene.Scroll / Math.Max(1f, layout.ContentHeight - view.Height);
            using var thumb = new SKPaint { Color = T.Rim, IsAntialias = true };
            c.DrawRoundRect(new SKRect(rail.Left, at, rail.Right, at + span), 3f, 3f, thumb);
        }

        var prompts = PaintFooter(c, new SKRect(0, height - FooterHeight, DesignWidth, height), scene, small);
        c.Restore();

        SKRect Px(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new BankOverviewHits(
            [.. cardHits.Select(h => (h.Item1, Px(h.Item2)))],
            Px(new SKRect(0, 0, 150, HeaderHeight)),
            Px(new SKRect(DesignWidth - 150, 0, DesignWidth, HeaderHeight)),
            Px(StoragePaint.BannerRect(header, 1f)),
            [.. prompts.Select(Px)],
            Px(view),
            unit);
    }

    // ── A group's heading: the slanted tab, then how much it holds ──

    private static void PaintHeading(SKCanvas c, SKRect r, BankOverviewRun run, IReadOnlyList<BankOverviewCard> cards, SKFont name, SKFont small)
    {
        var title = run.Title ?? "No group";
        var width = Math.Min(r.Width * 0.5f, Math.Max(260f, name.MeasureFallback(title) + 110f));
        var tab = new SKRect(r.Left - 4f, r.Top + 4f, r.Left + width, r.Bottom);
        using (var path = Tab(tab, 0f, 30f))
        {
            if (run.Title is null)
            {
                using var fill = new SKPaint { Color = T.Band, IsAntialias = true };
                c.DrawPath(path, fill);
                Outline(c, path, T.Rim.WithAlpha(140), 3f);
            }
            else
            {
                Gradient(c, path, T.BannerTop, T.BannerBottom);
                Outline(c, path, Pksm.Ink, 3f);
            }
        }
        Text(c, title, tab.Left + 26f, tab.MidY, name, run.Title is null ? T.Value : Pksm.Ink, width - 70f);
        var count = run.Boxes.Sum(b => (uint)b < (uint)cards.Count ? cards[b].Count : 0);
        var boxes = run.Boxes.Count;
        var about = boxes == 0 ? "Empty: put boxes in it from a box's menu (X)"
            : $"{boxes} box{(boxes == 1 ? "" : "es")} · {count} Pokémon";
        Text(c, about, tab.Right + 18f, tab.MidY, small, T.SubInk, r.Right - tab.Right - 30f);
        using var rule = new SKPaint { Color = T.Rim.WithAlpha(70), IsAntialias = true, StrokeWidth = 2f };
        c.DrawLine(tab.Right + 18f, tab.Bottom - 1f, r.Right, tab.Bottom - 1f, rule);
    }

    // ── A box card: the name plate (the cursor's chip when selected), the mini box under it ──

    private static void PaintCard(SKCanvas c, SKRect r, BankOverviewCard card, bool selected, BankOverviewScene scene, SKFont name, SKFont small)
    {
        const float radius = 18f;
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(r, radius, radius, fill);

        // The mini box: the wallpaper (or the box's tone) in a small well, then its 30 Pokémon.
        var body = new SKRect(r.Left + 10f, r.Top + PlateHeight + 2f, r.Right - 10f, r.Bottom - 10f);
        if (card.Wallpaper is { } art)
            StoragePaint.Wallpaper(c, body, art, card.WallpaperAverage, scene.Style, 0.45f);
        else
        {
            using var well = new SKPaint { Color = T.Well, IsAntialias = true };
            c.DrawRoundRect(body, 12f, 12f, well);
            using var tint = new SKPaint { Color = PksmPaint.Mix(card.Tone, T.Well, 0.72f), IsAntialias = true };
            c.DrawRoundRect(SKRect.Inflate(body, -3f, -3f), 10f, 10f, tint);
        }
        var cellW = (body.Width - 12f) / SlotColumns;
        var cellH = (body.Height - 10f) / SlotRows;
        for (var slot = 0; slot < card.Icons.Count && slot < SlotColumns * SlotRows; slot++)
        {
            if (card.Icons[slot] is not { } icon) continue;
            var cx = body.Left + 6f + (slot % SlotColumns + 0.5f) * cellW;
            var cy = body.Top + 5f + (slot / SlotColumns + 0.5f) * cellH;
            // Whole steps of the icon's own pixels, standing free: no frame, no spot under it.
            // Box icons (40×30) at ×2, as large as the storage grid's pixels allow here; bigger art fits the same room.
            var box = new SKRect(cx - 40f, cy - 30f, cx + 40f, cy + 30f);
            c.DrawImage(icon, Fit(icon, box, 2f), Nearest);
        }

        // The plate: name on the left, how full on the right.
        var plate = new SKRect(r.Left + 8f, r.Top + 6f, r.Right - 8f, r.Top + PlateHeight - 4f);
        if (selected) Chip(c, plate, focused: true);
        else
        {
            using var band = new SKPaint { Color = EditorPaint.RowBand(true), IsAntialias = true };
            c.DrawRoundRect(plate, 10f, 10f, band);
        }
        var ink = selected ? T.ChipInk : T.Value;
        var full = card.Count >= SlotColumns * SlotRows;
        var countText = full ? "FULL" : $"{card.Count}/30";
        var countWidth = small.MeasureFallback(countText);
        var lockRoom = card.Locked ? 40f : 0f;
        Text(c, card.Name, plate.Left + 22f, plate.MidY, name, ink, plate.Width - countWidth - lockRoom - 60f);
        Right(c, countText, plate.Right - 22f, plate.MidY, small, full && !selected ? Pksm.ShinyGold : selected ? T.ChipInk : T.SubInk);
        if (card.Locked && scene.Padlock is { } padlock)
        {
            // The lock mark, on its gold plate: this box is a finished shelf.
            var size = 26f;
            var at = new SKRect(plate.Right - 30f - countWidth - size - 8f, plate.MidY - size / 2, plate.Right - 30f - countWidth - 8f, plate.MidY + size / 2);
            using var gold = new SKPaint { Color = Pksm.ShinyGold, IsAntialias = true };
            c.DrawRoundRect(SKRect.Inflate(at, 4f, 4f), 6f, 6f, gold);
            c.DrawImage(padlock, at, Nearest);
        }
        using var rim = new SKPaint
        {
            Color = selected ? T.Rim : T.Rim.WithAlpha(90), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = selected ? 5f : 3f,
        };
        c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), radius, radius, rim);
        if (card.LivingDex)
        {
            // Where the living dex starts: a tag sitting on the card's lower rim, clear of the Pokémon.
            var tag = new SKRect(r.Right - 150f, r.Bottom - 16f, r.Right - 24f, r.Bottom + 14f);
            using var plateFill = new SKPaint { Color = T.Well, IsAntialias = true };
            c.DrawRoundRect(tag, 8f, 8f, plateFill);
            using var tagRim = new SKPaint { Color = Pksm.ShinyGold, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f };
            c.DrawRoundRect(tag, 8f, 8f, tagRim);
            Centered(c, "DEX ▸", tag.MidX, tag.MidY, small, Pksm.ShinyGold);
        }
    }

    // ── The footer: each prompt a keycap and its words, each one a touch button too ──

    private static List<SKRect> PaintFooter(SKCanvas c, SKRect r, BankOverviewScene scene, SKFont font)
    {
        var hits = new List<SKRect>();
        var x = r.Left + 8f;
        foreach (var (key, label) in scene.Prompts)
        {
            var keyWidth = Math.Max(44f, font.MeasureFallback(key) + 24f);
            var width = keyWidth + 12f + font.MeasureFallback(label) + 26f;
            var plate = new SKRect(x, r.Top + 8f, x + width, r.Bottom - 8f);
            using (var fill = new SKPaint { Color = T.Band.WithAlpha(150), IsAntialias = true }) c.DrawRoundRect(plate, 12f, 12f, fill);
            var cap = new SKRect(plate.Left + 6f, plate.Top + 6f, plate.Left + 6f + keyWidth, plate.Bottom - 6f);
            using (var capFill = new SKPaint { Color = T.Rim, IsAntialias = true }) c.DrawRoundRect(cap, 8f, 8f, capFill);
            Centered(c, key, cap.MidX, cap.MidY, font, T.ChipInk);
            Text(c, label, cap.Right + 12f, plate.MidY, font, T.Value, width);
            hits.Add(plate);
            x = plate.Right + 12f;
        }
        if (scene.Status.Length > 0)
            Right(c, Fitted(scene.Status, font, Math.Max(0, r.Right - x - 30f)), r.Right - 16f, r.MidY, font, T.SubInk);
        return hits;
    }
}
