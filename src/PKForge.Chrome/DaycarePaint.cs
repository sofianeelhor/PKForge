using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One Day Care slot: who is in it (or nobody), large.</summary>
public sealed record DaycareSceneSlot(int Index, bool Occupied, string Name, string Species, int Level, string? Experience, SKImage? Sprite);

/// <summary>Everything the Day Care screen shows at one moment.</summary>
public sealed record DaycareScene(
    string Facility,
    bool CanPrevious,
    bool CanNext,
    bool EggReady,
    IReadOnlyList<DaycareSceneSlot> Slots,
    int Cursor,
    bool CanEdit,
    string Status,
    // The egg's current frame (the artist's 12x14 sprite); drawn as a silhouette when no egg is waiting.
    SKImage? Egg = null);

/// <summary>Where each touch target landed on the last paint, in canvas pixels.</summary>
public sealed record DaycareHits(SKRect Previous, SKRect Next, IReadOnlyList<SKRect> Slots, IReadOnlyList<SKRect> Withdraw);

/// <summary>
/// The Day Care as one painted screen: the facility in the box banner (its chevrons turn to the
/// other facility when the game has two), the two deposited Pokémon large on one long card,
/// each standing on the floor above its name plate and level, the egg between them, and under
/// each one the way out: withdraw to the PC. Design pixels of a 1872 wide screen.
/// </summary>
public static class DaycarePaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float HeaderHeight = 88f;
    private const float EggColumn = 360f;

    public static DaycareHits Paint(SKCanvas c, SKImageInfo info, DaycareScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        var header = new SKRect(240f, 0, DesignWidth - 240f, HeaderHeight);
        using (var font = new SKFont(face, 40f)) StoragePaint.BoxHeader(c, header, scene.Facility.ToUpperInvariant(), font, scene.CanPrevious, scene.CanNext, 1f);

        var card = new SKRect(0, HeaderHeight + 16f, DesignWidth, height);
        const float radius = 28f;
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(card, radius, radius, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(card, -1.5f, -1.5f), radius, radius, rim);

        using var big = new SKFont(face, 44f);
        using var mid = new SKFont(face, 32f);
        using var small = new SKFont(face, 26f);

        var half = (card.Width - EggColumn) / 2f;
        var slots = new List<SKRect>();
        var withdraw = new List<SKRect>();
        var count = Math.Min(2, scene.Slots.Count);
        for (var i = 0; i < count; i++)
        {
            var left = i == 0 ? card.Left : card.Right - half;
            var area = new SKRect(left, card.Top, left + half, card.Bottom - 70f);
            slots.Add(area);
            withdraw.Add(PaintSlot(c, area, scene.Slots[i], i == scene.Cursor, scene.CanEdit, big, mid, small));
        }
        if (count == 1) withdraw.Add(SKRect.Empty);

        // The egg in the middle, or its place kept empty.
        var eggArea = new SKRect(card.Left + half, card.Top, card.Right - half, card.Bottom - 70f);
        PaintEgg(c, eggArea, scene.EggReady, scene.Egg, mid, small);

        using (var rule = new SKPaint { Color = T.Rim.WithAlpha(70) })
            c.DrawRect(new SKRect(card.Left + 30f, card.Bottom - 70f, card.Right - 30f, card.Bottom - 68f), rule);
        var status = scene.Status.Length > 0 ? scene.Status : "Withdraw moves it to the first empty PC slot.";
        Text(c, status, card.Left + 36f, card.Bottom - 35f, small, T.SubInk, card.Width - 72f);

        c.Restore();
        SKRect Px(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new DaycareHits(
            Px(new SKRect(header.Left, 0, header.Left + 150, HeaderHeight)),
            Px(new SKRect(header.Right - 150, 0, header.Right, HeaderHeight)),
            [.. slots.Select(Px)],
            [.. withdraw.Select(Px)]);
    }

    private static SKRect PaintSlot(SKCanvas c, SKRect area, DaycareSceneSlot slot, bool selected, bool canEdit, SKFont big, SKFont mid, SKFont small)
    {
        // The slot's number on a small slanted tab at the top.
        var tab = new SKRect(area.Left + 40f, area.Top + 28f, area.Left + 250f, area.Top + 84f);
        using (var path = Tab(tab, 0f, 26f))
        {
            if (selected) Gradient(c, path, T.BannerTop, T.BannerBottom);
            else using (var band = new SKPaint { Color = EditorPaint.RowBand(true), IsAntialias = true }) c.DrawPath(path, band);
            Outline(c, path, selected ? Pksm.Ink : T.Rim.WithAlpha(110), 3f);
        }
        Text(c, $"SLOT {slot.Index + 1}", tab.Left + 24f, tab.MidY, mid, selected ? Pksm.Ink : T.Value, tab.Width - 50f);

        var plate = new SKRect(area.Left + 40f, area.Bottom - 250f, area.Right - 40f, area.Bottom - 150f);
        var ground = plate.Top - 24f;
        var stage = new SKRect(area.Left + 60f, tab.Bottom + 20f, area.Right - 60f, ground);
        using (var floor = new SKPaint { Color = T.Rim.WithAlpha(90) })
            c.DrawRect(new SKRect(area.Left + 60f, ground, area.Right - 60f, ground + 3f), floor);

        var action = new SKRect(area.Left + 40f, area.Bottom - 120f, area.Right - 40f, area.Bottom - 40f);
        if (!slot.Occupied)
        {
            Centered(c, "Empty", stage.MidX, stage.MidY, big, T.SubInk);
            Centered(c, "Leave a Pokémon here in the game.", stage.MidX, stage.MidY + 50f, small, T.SubInk);
            if (selected) Chip(c, plate, focused: true);
            else using (var band = new SKPaint { Color = EditorPaint.RowBand(false), IsAntialias = true }) c.DrawRoundRect(plate, 12f, 12f, band);
            Text(c, "Nobody", plate.Left + 34f, plate.MidY, big, selected ? T.ChipInk : T.SubInk, plate.Width - 60f);
            return SKRect.Empty;
        }

        if (slot.Sprite is { } sprite)
        {
            // Whole steps up to about half the stage: a small sprite (a box icon while the full
            // sprite is not cached, 20 px tall) used to stop at ×4 and float tiny over the floor
            // line (QA 2026-11-12). Big art still shrinks to fit.
            var target = Math.Min(stage.Width, stage.Height) * .55f;
            var steps = MathF.Max(4f, MathF.Floor(target / Math.Max(1, Math.Max(sprite.Width, sprite.Height))));
            var fit = Fit(sprite, stage, maxScale: steps);
            fit.Offset(0, ground - fit.Bottom);
            c.DrawImage(sprite, fit, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }

        // The name plate: the chip under the cursor; the level at its right end.
        if (selected) Chip(c, plate, focused: true);
        else using (var band = new SKPaint { Color = EditorPaint.RowBand(true), IsAntialias = true }) c.DrawRoundRect(plate, 12f, 12f, band);
        var ink = selected ? T.ChipInk : T.Value;
        var level = $"Lv. {slot.Level}";
        var levelWidth = big.MeasureFallback(level);
        Text(c, slot.Name, plate.Left + 34f, plate.MidY - 16f, big, ink, plate.Width - levelWidth - 100f);
        var detail = slot.Name == slot.Species ? slot.Experience ?? "" : slot.Experience is { } exp ? $"{slot.Species} · {exp}" : slot.Species;
        Text(c, detail, plate.Left + 34f, plate.MidY + 26f, small, selected ? T.ChipInk : T.SubInk, plate.Width - levelWidth - 100f);
        Right(c, level, plate.Right - 40f, plate.MidY, big, ink);

        using (var band = new SKPaint { Color = EditorPaint.RowBand(false), IsAntialias = true }) c.DrawRoundRect(action, 10f, 10f, band);
        if (selected)
            using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
                c.DrawRoundRect(SKRect.Inflate(action, -1.5f, -1.5f), 10f, 10f, rim);
        Arrow(c, action.Left + 34f, action.MidY, 1, canEdit ? T.Rim : T.SubInk);
        Text(c, selected ? "A  Withdraw to the PC" : "Withdraw to the PC", action.Left + 64f, action.MidY, mid, canEdit ? T.Value : T.SubInk, action.Width - 90f);
        return action;
    }

    /// <summary>The egg sprite standing in the middle: in colour when one is waiting, a quiet
    /// silhouette in its place otherwise.</summary>
    private static void PaintEgg(SKCanvas c, SKRect area, bool ready, SKImage? egg, SKFont mid, SKFont small)
    {
        var cx = area.MidX;
        var room = new SKRect(cx - 90f, area.MidY - 190f, cx + 90f, area.MidY + 20f);
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        if (egg is not null)
        {
            var dest = Fit(egg, room);
            dest.Offset(0, room.Bottom - dest.Bottom);
            if (ready) c.DrawImage(egg, dest, sampling);
            else
            {
                using var ghost = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(T.Rim.WithAlpha(90), SKBlendMode.SrcIn) };
                c.DrawImage(egg, dest, sampling, ghost);
            }
        }
        if (ready)
        {
            Centered(c, "Egg ready", cx, room.Bottom + 50f, mid, T.Value);
            Centered(c, "Collect it in the game", cx, room.Bottom + 90f, small, T.SubInk);
        }
        else Centered(c, "No egg yet", cx, room.Bottom + 50f, mid, T.SubInk);
    }
}
