using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>Where a pack stands on this device.</summary>
public enum SpritePackState { Missing, Partial, Installed }

/// <summary>
/// One sprite pack: its name, what it brings, its size, how much of it is here, a few of its
/// sprites to show (null while they load), and its download progress (null when idle).
/// </summary>
public sealed record SpritePackCard(
    string Name,
    string About,
    string Size,
    SpritePackState State,
    string StateText,
    IReadOnlyList<SKImage?> Samples,
    double? Progress = null,
    string? ProgressText = null);

/// <summary>The sprite pack screen at one moment.</summary>
public sealed record SpritePackScene(IReadOnlyList<SpritePackCard> Packs, int Cursor, string? Status = null);

/// <summary>Where the cards and the foot keys landed, in canvas pixels.</summary>
public sealed record SpritePackHits(IReadOnlyList<SKRect> Cards, SKRect Primary, SKRect Delete, SKRect Leave, int VisibleCards, int First);

/// <summary>
/// The sprite packs as one painted screen: the title on the banner tab, one rimmed striped
/// card per pack with its own sprites shown large and free-standing, its size, what is here
/// and an ACTIVE mark once it is in use, the download drawn as a slanted gauge, and the keys
/// for what the pack under the cursor can do at the foot.
/// </summary>
public static class SpritePackPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float BannerHeight = 96f;
    private const float FootHeight = 84f;
    private const float CardHeight = 300f;
    private const float Gap = 22f;

    /// <summary>What A does for <paramref name="card"/> ("Download", "Resume", "Stop", or null when nothing).</summary>
    public static string? PrimaryLabel(SpritePackCard card) => card.Progress is not null ? "Stop"
        : card.State switch { SpritePackState.Missing => "Download", SpritePackState.Partial => "Resume", _ => null };

    public static SpritePackHits Paint(SKCanvas c, SKImageInfo info, SpritePackScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        using var title = new SKFont(face, 48f);
        using var name = new SKFont(face, 44f);
        using var body = new SKFont(face, 30f);
        using var small = new SKFont(face, 27f);

        // The title on the banner tab, as every painted menu.
        var banner = new SKRect(24f, 16f, 900f, 16f + BannerHeight - 16f);
        using (var path = Tab(banner, 16f, 40f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        Text(c, "SPRITE PACKS", banner.Left + 30f, banner.MidY, title, Pksm.Ink, banner.Width - 90f);
        if (scene.Status is { Length: > 0 } status)
            Right(c, Fitted(status, small, 860f), DesignWidth - 30f, banner.MidY, small, T.SubInk);

        // The cards: as many as fit, the cursor's always in view.
        var top = BannerHeight + 8f;
        var room = height - top - FootHeight - 8f;
        // The cards share the screen, up to a tall card each: the sprites grow with them.
        var visible = Math.Max(1, Math.Min(Math.Max(1, scene.Packs.Count), (int)((room + Gap) / (CardHeight + Gap))));
        var cardHeight = MathF.Min(CardHeight * 1.5f, (room + Gap) / visible - Gap);
        var first = Math.Clamp(scene.Cursor - visible + 1, 0, Math.Max(0, scene.Packs.Count - visible));
        var hits = new List<SKRect>();
        for (var i = first; i < scene.Packs.Count && i < first + visible; i++)
        {
            var rect = SKRect.Create(24f, top + (i - first) * (cardHeight + Gap), DesignWidth - 48f, cardHeight);
            PaintCard(c, rect, scene.Packs[i], i == scene.Cursor, name, body, small);
            hits.Add(rect);
        }
        if (first > 0) Caret(c, DesignWidth / 2f, top - 2f, up: true);
        if (first + visible < scene.Packs.Count) Caret(c, DesignWidth / 2f, height - FootHeight - 2f, up: false);
        if (scene.Packs.Count == 0)
            Centered(c, "No sprite pack is offered in this version.", DesignWidth / 2f, top + room / 2f, body, T.Value);

        // The keys for the pack under the cursor.
        var footMid = height - FootHeight / 2f;
        var x = 40f;
        var primary = SKRect.Empty;
        var delete = SKRect.Empty;
        var current = scene.Cursor >= 0 && scene.Cursor < scene.Packs.Count ? scene.Packs[scene.Cursor] : null;
        if (current is not null && PrimaryLabel(current) is { } act)
        {
            primary = PadMenuPaint.Key(c, "A", act, x, footMid, small, body);
            x = primary.Right + 40f;
        }
        if (current is { Progress: null, State: not SpritePackState.Missing })
        {
            delete = PadMenuPaint.Key(c, "Y", "Delete", x, footMid, small, body);
            x = delete.Right + 40f;
        }
        var leave = PadMenuPaint.Key(c, "B", "Back", x, footMid, small, body);

        c.Restore();
        SKRect Px(SKRect r) => r.IsEmpty ? r : new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new SpritePackHits([.. hits.Select(Px)], Px(primary), Px(delete), Px(leave), visible, first);
    }

    private static void PaintCard(SKCanvas c, SKRect r, SpritePackCard card, bool focused, SKFont name, SKFont body, SKFont small)
    {
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(r, 24f, 24f, fill);
        TrainerCardPaint.Stripes(c, SKRect.Inflate(r, -10f, -10f));
        using (var rim = new SKPaint { Color = focused ? T.Rim : T.Rim.WithAlpha(110), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = focused ? 5f : 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -2.5f, -2.5f), 24f, 24f, rim);

        // Left: the name on its plate (the cursor's chip when focused), what it brings, size and state.
        const float textWidth = 760f;
        var plate = SKRect.Create(r.Left + 22f, r.Top + 22f, MathF.Min(textWidth, name.MeasureFallback(card.Name) + 90f), 66f);
        if (focused) Chip(c, plate, focused: true);
        else
        {
            using var band = new SKPaint { Color = EditorPaint.RowBand(true), IsAntialias = true };
            using var path = Parallelogram(plate, 18f);
            c.DrawPath(path, band);
        }
        Text(c, card.Name, plate.Left + 34f, plate.MidY, name, focused ? T.ChipInk : T.Value, plate.Width - 60f);

        var y = plate.Bottom + 34f;
        foreach (var line in Wrap(card.About, body, textWidth - 20f).Take(2))
        {
            Text(c, line, r.Left + 34f, y, body, T.Value, textWidth - 20f);
            y += 38f;
        }

        // Size and state on small slanted tags; ACTIVE when the pack is in use.
        var tagY = r.Bottom - 56f;
        var tx = Tag(c, card.Size, r.Left + 30f, tagY, small, T.ChipTop, T.ChipInk);
        // While it downloads, the state tag says how far along it is.
        tx = Tag(c, card.Progress is not null && card.ProgressText is { Length: > 0 } now ? Fitted(now, small, textWidth - (tx - r.Left) - 60f) : card.StateText,
            tx + 14f, tagY, small, card.State == SpritePackState.Installed ? T.RimFill : T.Band, T.Value);
        if (card.State == SpritePackState.Installed && card.Progress is null)
            Tag(c, "ACTIVE", tx + 14f, tagY, small, T.Rim, Pksm.Ink);

        // Right: the pack's own sprites, large, whole steps, nothing under or around them.
        var shelf = new SKRect(r.Left + textWidth + 40f, r.Top + 18f, r.Right - 30f, r.Bottom - (card.Progress is null ? 18f : 60f));
        var shown = card.Samples.Count;
        if (shown > 0)
        {
            var cell = shelf.Width / shown;
            var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
            using var faded = new SKPaint { Color = SKColors.White.WithAlpha(card.State == SpritePackState.Missing ? (byte)120 : (byte)255) };
            for (var i = 0; i < shown; i++)
            {
                if (card.Samples[i] is not { } image) continue;
                var box = SKRect.Create(shelf.Left + i * cell, shelf.Top, cell, shelf.Height);
                c.DrawImage(image, Fit(image, SKRect.Inflate(box, -6f, -4f)), sampling, faded);
            }
        }

        // The download: a slanted gauge under the sprites, filled in the rim's cyan.
        if (card.Progress is { } progress)
        {
            var track = new SKRect(shelf.Left, r.Bottom - 46f, r.Right - 30f, r.Bottom - 20f);
            using (var path = Parallelogram(track, 10f))
            {
                using var well = new SKPaint { Color = T.Well, IsAntialias = true };
                c.DrawPath(path, well);
            }
            var fill = (float)Math.Clamp(progress, 0, 1);
            if (fill > 0.005f)
            {
                using var path = Parallelogram(new SKRect(track.Left, track.Top, track.Left + MathF.Max(20f, track.Width * fill), track.Bottom), 10f);
                using var lit = new SKPaint { Color = T.Rim, IsAntialias = true };
                c.DrawPath(path, lit);
            }
            using (var path = Parallelogram(track, 10f)) Outline(c, path, Pksm.Ink, 2.5f);
        }
    }

    /// <summary>A small slanted tag; returns its right edge.</summary>
    private static float Tag(SKCanvas c, string text, float x, float mid, SKFont font, SKColor fill, SKColor ink)
    {
        var w = font.MeasureFallback(text) + 44f;
        using (var path = Parallelogram(SKRect.Create(x, mid - 22f, w, 44f), 12f))
        {
            using var paint = new SKPaint { Color = fill, IsAntialias = true };
            c.DrawPath(path, paint);
            Outline(c, path, Pksm.Ink, 2f);
        }
        Centered(c, text, x + w / 2f, mid, font, ink);
        return x + w;
    }

    private static void Caret(SKCanvas c, float cx, float cy, bool up)
    {
        using var path = new SKPath();
        var d = up ? -1f : 1f;
        path.MoveTo(cx - 14f, cy - d * 7f);
        path.LineTo(cx + 14f, cy - d * 7f);
        path.LineTo(cx, cy + d * 9f);
        path.Close();
        using var paint = new SKPaint { Color = T.Rim, IsAntialias = true };
        c.DrawPath(path, paint);
    }
}
