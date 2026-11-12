using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One Pal Park place: the migrated Pokémon waiting for the Catching Show, or nobody.</summary>
public sealed record PalParkSceneSlot(int Index, bool Occupied, string Name, string Species, int Level, string Origin,
    bool Legal, SKImage? Sprite);

/// <summary>Everything the Pal Park screen shows at one moment.</summary>
public sealed record PalParkScene(
    IReadOnlyList<PalParkSceneSlot> Slots,
    int Cursor,
    bool LockArmed,
    int TrainersRecorded,
    bool CanEdit,
    string Status);

/// <summary>Where each touch target landed on the last paint, in canvas pixels.</summary>
public sealed record PalParkHits(IReadOnlyList<SKRect> Slots, IReadOnlyList<SKRect> Actions);

/// <summary>
/// The Pal Park as one painted screen: the six places of the Catching Show side by side on one
/// long card, each Pokémon standing on the floor above its name plate (level, origin game and
/// a legality mark), then the transfer lock and the four actions: put in, clear, clear the
/// lock, fill from a box. Design pixels of a 1872 wide screen.
/// </summary>
public static class PalParkPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    public static readonly string[] ActionLabels = ["Put in", "Clear", "Clear lock", "Fill from a box"];

    private const float HeaderHeight = 88f;
    private const float FooterHeight = 220f;

    public static PalParkHits Paint(SKCanvas c, SKImageInfo info, PalParkScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        var header = new SKRect(240f, 0, DesignWidth - 240f, HeaderHeight);
        using (var font = new SKFont(face, 40f)) StoragePaint.BoxHeader(c, header, "PAL PARK", font, false, false, 1f);

        var card = new SKRect(0, HeaderHeight + 16f, DesignWidth, height);
        const float radius = 28f;
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(card, radius, radius, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(card, -1.5f, -1.5f), radius, radius, rim);

        using var big = new SKFont(face, 36f);
        using var mid = new SKFont(face, 30f);
        using var small = new SKFont(face, 24f);

        var slots = new List<SKRect>();
        var count = Math.Min(6, scene.Slots.Count);
        var inner = new SKRect(card.Left + 24f, card.Top + 20f, card.Right - 24f, card.Bottom - FooterHeight);
        var width = inner.Width / 6f;
        for (var i = 0; i < count; i++)
        {
            var area = new SKRect(inner.Left + (i * width), inner.Top, inner.Left + ((i + 1) * width), inner.Bottom);
            slots.Add(area);
            PaintSlot(c, area, scene.Slots[i], i == scene.Cursor, big, mid, small);
        }

        using (var rule = new SKPaint { Color = T.Rim.WithAlpha(70) })
            c.DrawRect(new SKRect(card.Left + 30f, inner.Bottom + 6f, card.Right - 30f, inner.Bottom + 8f), rule);

        // The lock, then the actions.
        var lockLine = scene.LockArmed
            ? $"Transfer lock on: the game waits a day between migrations from the same GBA game ({scene.TrainersRecorded} recorded)."
            : scene.TrainersRecorded > 0
                ? $"No transfer lock: the next migration is allowed at once ({scene.TrainersRecorded} GBA trainers recorded)."
                : "No transfer lock: the next migration is allowed at once.";
        Text(c, lockLine, card.Left + 36f, inner.Bottom + 44f, small, scene.LockArmed ? T.Value : T.SubInk, card.Width - 72f);

        var actions = new List<SKRect>();
        var row = new SKRect(card.Left + 30f, inner.Bottom + 76f, card.Right - 30f, inner.Bottom + 146f);
        var gap = 20f;
        var each = (row.Width - (gap * (ActionLabels.Length - 1))) / ActionLabels.Length;
        for (var i = 0; i < ActionLabels.Length; i++)
        {
            var button = new SKRect(row.Left + (i * (each + gap)), row.Top, row.Left + (i * (each + gap)) + each, row.Bottom);
            actions.Add(button);
            using (var band = new SKPaint { Color = EditorPaint.RowBand(false), IsAntialias = true }) c.DrawRoundRect(button, 10f, 10f, band);
            using (var rim = new SKPaint { Color = scene.CanEdit ? T.Rim : T.Rim.WithAlpha(90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
                c.DrawRoundRect(SKRect.Inflate(button, -1.5f, -1.5f), 10f, 10f, rim);
            Text(c, $"{Keys[i]}  {ActionLabels[i]}", button.Left + 26f, button.MidY, mid, scene.CanEdit ? T.Value : T.SubInk, button.Width - 40f);
        }

        var status = scene.Status.Length > 0 ? scene.Status : "The Catching Show takes all six.";
        Text(c, status, card.Left + 36f, card.Bottom - 34f, small, T.SubInk, card.Width - 72f);

        c.Restore();
        SKRect Px(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new PalParkHits([.. slots.Select(Px)], [.. actions.Select(Px)]);
    }

    private static readonly string[] Keys = ["A", "X", "Y", "+"];

    private static void PaintSlot(SKCanvas c, SKRect area, PalParkSceneSlot slot, bool selected, SKFont big, SKFont mid, SKFont small)
    {
        var tab = new SKRect(area.Left + 16f, area.Top + 8f, area.Left + 120f, area.Top + 58f);
        using (var path = Tab(tab, 0f, 20f))
        {
            using (var band = new SKPaint { Color = selected ? T.Rim : EditorPaint.RowBand(true), IsAntialias = true }) c.DrawPath(path, band);
            Outline(c, path, selected ? T.Rim : T.Rim.WithAlpha(110), 3f);
        }
        Text(c, $"{slot.Index + 1}", tab.Left + 26f, tab.MidY, mid, selected ? T.ChipInk : T.Value, tab.Width - 40f);

        var plate = new SKRect(area.Left + 10f, area.Bottom - 150f, area.Right - 10f, area.Bottom - 20f);
        var ground = plate.Top - 20f;
        var stage = new SKRect(area.Left + 20f, tab.Bottom + 12f, area.Right - 20f, ground);
        using (var floor = new SKPaint { Color = T.Rim.WithAlpha(90) })
            c.DrawRect(new SKRect(area.Left + 20f, ground, area.Right - 20f, ground + 3f), floor);

        if (selected) Chip(c, plate, focused: true);
        else using (var band = new SKPaint { Color = EditorPaint.RowBand(slot.Occupied), IsAntialias = true }) c.DrawRoundRect(plate, 12f, 12f, band);
        var ink = selected ? T.ChipInk : T.Value;
        var soft = selected ? T.ChipInk : T.SubInk;
        var max = plate.Width - 44f;

        if (!slot.Occupied)
        {
            Centered(c, "Empty", stage.MidX, stage.MidY, big, T.SubInk);
            Text(c, "Nobody", plate.Left + 26f, plate.MidY, big, soft, max);
            return;
        }

        if (slot.Sprite is { } sprite)
        {
            var fit = Fit(sprite, stage, maxScale: 3f);
            fit.Offset(0, ground - fit.Bottom);
            c.DrawImage(sprite, fit, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }

        Text(c, slot.Name, plate.Left + 26f, plate.Top + 34f, big, ink, max);
        Text(c, $"Lv. {slot.Level}  {slot.Origin}", plate.Left + 26f, plate.Top + 74f, small, soft, max);
        using (var dot = new SKPaint { Color = slot.Legal ? Pksm.Legal : Pksm.Illegal, IsAntialias = true })
            c.DrawCircle(plate.Left + 36f, plate.Top + 108f, 8f, dot);
        Text(c, slot.Legal ? "Legal" : "Not legal", plate.Left + 54f, plate.Top + 108f, small, soft, max - 30f);
    }
}
