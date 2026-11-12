using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One line of the card: what it is, its value, and whether it can be changed.</summary>
public sealed record TrainerCardLine(string Label, string Value, bool Editable, string Hint);

public sealed record TrainerCardScene(
    string Game,
    string Trainer,
    int Gender,
    string IdLine,
    IReadOnlyList<TrainerCardLine> Lines,
    int Cursor,
    int Scroll,
    IReadOnlyList<(SKImage Sheet, SKRect Cell)> Party,
    bool ReadOnly,
    // The badge case: one entry per badge the game has, true when it was earned.
    IReadOnlyList<bool>? Badges = null,
    // The badges' art keys ("kanto/boulder"), in the same order as Badges.
    IReadOnlyList<string>? BadgeKeys = null);

public sealed record TrainerCardHits(IReadOnlyList<(int Index, SKRect Rect)> Lines, int VisibleLines);

/// <summary>
/// The trainer card, as the games print one: a card with its banner, the trainer's lines on
/// alternating bands at the left (name, ID, money, play time, every currency the game keeps),
/// and the trainer's name large with the party at the right. The line under the cursor says
/// at the bottom what A does to it.
/// </summary>
public static class TrainerCardPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float BannerHeight = 92f;
    private const float LineHeight = 84f;
    private const float FooterHeight = 70f;
    private const float LinesWidth = 1060f;
    private const float BadgeBand = 132f;

    public static int VisibleLines(SKSizeI size)
    {
        if (size.Width <= 0) return 1;
        var unit = size.Width / DesignWidth;
        var height = size.Height / unit;
        return Math.Max(1, (int)((height - FooterHeight - 16f - BannerHeight - 48f - BadgeBand) / LineHeight));
    }

    /// <summary>The art key of a badge, from the region and name the engine reports.</summary>
    public static string BadgeKey(string region, string name) => BadgeArt.KeyOf(region, name);

    /// <summary>The card's fine horizontal ruling, like the printed cards in the games.</summary>
    internal static void Stripes(SKCanvas c, SKRect area)
    {
        using var stripe = new SKPaint { Color = T.Rim.WithAlpha(22) };
        for (var y = area.Top; y < area.Bottom; y += 16f)
            c.DrawRect(new SKRect(area.Left, y, area.Right, MathF.Min(area.Bottom, y + 8f)), stripe);
    }

    public static TrainerCardHits Paint(SKCanvas c, SKImageInfo info, TrainerCardScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        // The card: a plate with a double rim, its banner across the top.
        var card = new SKRect(0, 0, DesignWidth, height - FooterHeight - 16f);
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(card, 32f, 32f, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f })
            c.DrawRoundRect(SKRect.Inflate(card, -2f, -2f), 32f, 32f, rim);
        using (var inner = new SKPaint { Color = T.Rim.WithAlpha(90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f })
            c.DrawRoundRect(SKRect.Inflate(card, -12f, -12f), 24f, 24f, inner);
        Stripes(c, SKRect.Inflate(card, -14f, -14f));

        var banner = new SKRect(card.Left + 12f, card.Top + 12f, card.Right - 12f, card.Top + 12f + BannerHeight);
        using (var path = new SKPath())
        {
            path.AddRoundRect(new SKRoundRect(banner, 24f, 24f));
            c.Save();
            c.ClipRect(new SKRect(banner.Left, banner.Top, banner.Right, banner.Bottom));
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            c.Restore();
        }
        using (var line = new SKPaint { Color = Pksm.Ink, StrokeWidth = 3f }) c.DrawLine(banner.Left, banner.Bottom, banner.Right, banner.Bottom, line);
        using var title = new SKFont(face, 46f);
        using var small = new SKFont(face, 30f);
        Text(c, "TRAINER CARD", banner.Left + 36f, banner.MidY, title, Pksm.Ink, 600f);
        Right(c, scene.Game, banner.Right - 36f, banner.MidY, small, Pksm.Ink);

        // The lines.
        var hits = new List<(int, SKRect)>();
        var visible = VisibleLines(new SKSizeI(info.Width, info.Height));
        using var label = new SKFont(face, 32f);
        using var value = new SKFont(face, 38f);
        for (var i = scene.Scroll; i < scene.Lines.Count && i < scene.Scroll + visible; i++)
        {
            var top = banner.Bottom + 24f + (i - scene.Scroll) * LineHeight;
            var row = new SKRect(card.Left + 36f, top, card.Left + LinesWidth, top + LineHeight - 10f);
            hits.Add((i, row));
            var selected = i == scene.Cursor;
            if (selected) Chip(c, row, focused: true);
            else
            {
                using var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true };
                c.DrawRoundRect(row, 12f, 12f, band);
            }
            var line = scene.Lines[i];
            Text(c, line.Label, row.Left + 30f, row.MidY, label, selected ? T.ChipInk : T.SubInk, 360f);
            var valueInk = selected ? T.ChipInk : line.Editable && !scene.ReadOnly ? T.Value : T.SubInk;
            Right(c, line.Value, row.Right - 36f, row.MidY, value, valueInk);
        }
        if (scene.Lines.Count > visible)
        {
            var rail = new SKRect(card.Left + LinesWidth + 12f, banner.Bottom + 24f, card.Left + LinesWidth + 18f, banner.Bottom + 24f + visible * LineHeight - 10f);
            using var track = new SKPaint { Color = T.Band.WithAlpha(120), IsAntialias = true };
            c.DrawRoundRect(rail, 3f, 3f, track);
            var span = rail.Height * visible / scene.Lines.Count;
            var at = rail.Top + (rail.Height - span) * scene.Scroll / Math.Max(1, scene.Lines.Count - visible);
            using var thumb = new SKPaint { Color = T.Rim, IsAntialias = true };
            c.DrawRoundRect(new SKRect(rail.Left, at, rail.Right, at + span), 3f, 3f, thumb);
        }

        // The trainer at the right: name large, ID, sign, the party in two rows of three.
        var side = new SKRect(card.Left + LinesWidth + 50f, banner.Bottom + 24f, card.Right - 36f, card.Bottom - 30f);
        var tab = new SKRect(side.Left - 10f, side.Top, side.Right, side.Top + 88f);
        using (var path = Tab(tab, 0f, 36f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        using var big = new SKFont(face, 56f);
        Text(c, scene.Trainer, tab.Left + 30f, tab.MidY, big, Pksm.Ink, tab.Width - 150f);
        var sign = scene.Gender switch { 0 => "♂", 1 => "♀", _ => null };
        if (sign is not null) Right(c, sign, tab.Right - 60f, tab.MidY, big, scene.Gender == 1 ? Pksm.Female : Pksm.SignalBlue);
        Text(c, scene.IdLine, side.Left + 20f, tab.Bottom + 36f, small, T.SubInk, side.Width - 40f);

        // The party, standing on the card as the games show it: no frames, whole-step scaled.
        var bandTop = card.Bottom - 12f - BadgeBand;
        var seatsTop = tab.Bottom + 64f;
        var seatW = side.Width / 3f;
        var seatH = (bandTop - 16f - seatsTop) / 2f;
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        for (var i = 0; i < 6 && i < scene.Party.Count; i++)
        {
            var box = SKRect.Create(side.Left + i % 3 * seatW, seatsTop + i / 3 * seatH, seatW, seatH);
            var (sheet, cellRect) = scene.Party[i];
            var scale = MathF.Max(1f, MathF.Floor(MathF.Min(box.Width / cellRect.Width, box.Height / cellRect.Height)));
            var w = cellRect.Width * scale;
            var h = cellRect.Height * scale;
            c.DrawImage(sheet, cellRect, SKRect.Create(box.MidX - w / 2, box.MidY - h / 2, w, h), sampling);
        }

        // The badge case: a band across the foot of the card, each badge in colour once earned.
        var bandRect = new SKRect(card.Left + 12f, bandTop, card.Right - 12f, card.Bottom - 12f);
        using (var bandFill = new SKPaint { Color = T.Band.WithAlpha(150) })
        {
            c.Save();
            c.ClipRoundRect(new SKRoundRect(SKRect.Inflate(card, -12f, -12f), 24f, 24f), antialias: true);
            c.DrawRect(bandRect, bandFill);
            c.Restore();
        }
        using (var edge = new SKPaint { Color = Pksm.Ink, StrokeWidth = 3f }) c.DrawLine(bandRect.Left, bandRect.Top, bandRect.Right, bandRect.Top, edge);
        Text(c, "BADGES", bandRect.Left + 36f, bandRect.MidY, label, T.Value, 220f);
        if (scene.Badges is { Count: > 0 } badges)
        {
            var spacing = MathF.Min(112f, (bandRect.Width - 300f) / badges.Count);
            for (var b = 0; b < badges.Count; b++)
            {
                var room = SKRect.Create(bandRect.Left + 280f + spacing * b, bandRect.Top + 14f, spacing, bandRect.Height - 28f);
                var key = scene.BadgeKeys is { } keys && b < keys.Count ? keys[b] : "";
                BadgeArt.Draw(c, key, SKRect.Inflate(room, -8f, 0f), badges[b], T.Rim.WithAlpha(120));
            }
        }
        else Right(c, "This game keeps no badges.", bandRect.Right - 36f, bandRect.MidY, small, T.SubInk);

        // What A does on the line under the cursor.
        var footer = new SKRect(0, height - FooterHeight, DesignWidth, height);
        using (var band = new SKPaint { Color = T.Band.WithAlpha(170), IsAntialias = true }) c.DrawRoundRect(footer, 16f, 16f, band);
        using (var frame = new SKPaint { Color = T.Rim.WithAlpha(120), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f })
            c.DrawRoundRect(SKRect.Inflate(footer, -1f, -1f), 16f, 16f, frame);
        if (scene.Cursor >= 0 && scene.Cursor < scene.Lines.Count)
        {
            var hint = scene.ReadOnly ? "Hardcore mode: the card can be read, not changed." : scene.Lines[scene.Cursor].Hint;
            Text(c, hint, footer.Left + 28f, footer.MidY, small, T.Value, footer.Width - 56f);
        }

        c.Restore();
        return new TrainerCardHits([.. hits.Select(h => (h.Item1, new SKRect(h.Item2.Left * unit, h.Item2.Top * unit, h.Item2.Right * unit, h.Item2.Bottom * unit)))], visible);
    }
}
