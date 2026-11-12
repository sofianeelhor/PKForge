using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// The party view (box -1), after Black and White's party screen: two staggered columns
/// of octagonal panels with a bright cyan rim and one light diagonal band, joined by a
/// trace, over a grid with a node at every crossing. Each panel carries the Pokémon's box
/// sprite, its nickname and gender, "HP" in green and the bar, the level and the HP
/// numbers; a status shows as the games' tag under the sprite. The selected panel turns a
/// brighter blue, a fainted one rust. Live state from the session.
/// </summary>
public static class PartyView
{
    public const int Count = 6;
    private const int Columns = 2;
    private const int Rows = 3;

    private static SKFont? _nameFontFallback;
    private static SKFont? _nameFontPixel;
    private static SKFont? _smallFont;
    private static SKFont? _labelFont;

    private static SKColor GridTop => ColorTheme.Current.PartyGridTop;
    private static SKColor GridLine => StoragePaint.FrameEdge.WithAlpha(0x78);
    private static SKColor GridNode => StoragePaint.FrameEdge.WithAlpha(0xC8);
    private static SKColor Rim => ColorTheme.Current.PartyRim;
    private static SKColor RimDark => ColorTheme.Current.PartyRimDark;
    private static SKColor Trace => Rim.WithAlpha(0x8C);
    private static SKColor Body => ColorTheme.Current.PartyBody;
    private static SKColor BodyBand => ColorTheme.Current.PartyBand;
    private static SKColor SelectedBody => ColorTheme.Current.PartySelected;
    private static SKColor SelectedBand => ColorTheme.Current.PartySelectedBand;
    private static readonly SKColor FaintBody = new(0x5A, 0x2A, 0x2E);
    private static readonly SKColor FaintBand = new(0x6A, 0x36, 0x38);
    private static SKColor EmptyBody => RimDark.WithAlpha(0xB0);
    private static SKColor EmptyRim => StoragePaint.FrameEdge;
    private static readonly SKColor HpLabel = new(0x6C, 0xE8, 0x5C);
    private static readonly SKColor Track = new(0x30, 0x34, 0x40);
    private static SKColor TextShadow => ColorTheme.Current.PartyShadow;
    private static SKColor Ghost => Rim.WithAlpha(0x70);

    public static void Paint(SKCanvas canvas, SKImageInfo info, ISpriteService sprites, ISaveEngineSession? session, int selectedSlot, Action invalidate, (int Box, int Slot)? carrySource = null, float pulsePhase = 0f,
        Func<int, bool>? isMarked = null, Func<int, bool?>? rangeMark = null)
    {
        NodeGrid(canvas, info);
        DrawTrace(canvas, info);

        var swapping = SwapProgress();
        var slidingCards = new List<(int Slot, EntityDetail? Detail)>();
        foreach (var i in Enumerable.Range(0, Count))
        {
            var rect = SlotRect(info, i);
            EntityDetail? detail = null;
            try { detail = session?.ReadEntity(-1, i); } catch { /* engine validates coordinates */ }
            // The carried Pokémon stays on its own card, lifted and breathing, until the
            // second A drops it; the cursor alone shows where it will go.
            var heldHere = carrySource is { Box: -1, Slot: var src } && src == i && detail is { IsEmpty: false };
            var breath = heldHere ? 1f + 0.028f * (0.5f + 0.5f * MathF.Sin(pulsePhase)) : 1f;
            var pulsed = breath == 1f ? rect : ScaleRect(rect, breath);

            if (swapping is { } sw && (i == sw.A || i == sw.B))
            {
                // Drawn last, over the other cards, while the two slide past each other.
                slidingCards.Add((i, detail));
                continue;
            }

            Slot(canvas, pulsed, detail, sprites, i == selectedSlot && isMarked is null && !heldHere, invalidate, lifted: heldHere);

            // Multi-select: the same wash, green hand and check badge as the box grid.
            if (isMarked is null) continue;
            if (rangeMark?.Invoke(i) is { } mark) PksmPaint.RangeWash(canvas, rect, mark);
            if (i == selectedSlot) PksmPaint.Selection(canvas, rect, Pksm.CursorGreen);
            if (detail is { IsEmpty: false } && isMarked(i)) PksmPaint.MarkBadge(canvas, rect);
        }

        if (swapping is { } swap)
        {
            // Each card glides from the slot it left to the one it now holds.
            var eased = 1f - MathF.Pow(1f - swap.T, 3f);
            foreach (var (slot, detail) in slidingCards)
            {
                var from = SlotRect(info, slot == swap.A ? swap.B : swap.A);
                var to = SlotRect(info, slot);
                var at = new SKRect(Lerp(from.Left, to.Left, eased), Lerp(from.Top, to.Top, eased),
                    Lerp(from.Right, to.Right, eased), Lerp(from.Bottom, to.Bottom, eased));
                Slot(canvas, at, detail, sprites, slot == selectedSlot && swap.T >= 1f, invalidate);
            }
            if (swap.T < 1f) invalidate();
        }
    }

    private const float SwapMilliseconds = 220f;
    private static (int A, int B, long Start)? _swap;

    /// <summary>Starts the card swap animation after two party slots traded Pokémon.</summary>
    public static void BeginSwap(int a, int b) => _swap = (a, b, Environment.TickCount64);

    private static (int A, int B, float T)? SwapProgress()
    {
        if (_swap is not { } swap) return null;
        var t = (Environment.TickCount64 - swap.Start) / SwapMilliseconds;
        if (t >= 1f) { _swap = null; return null; }
        return (swap.A, swap.B, Math.Clamp(t, 0f, 1f));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static SKRect ScaleRect(SKRect r, float scale)
    {
        var w = r.Width * (scale - 1f) / 2f;
        var h = r.Height * (scale - 1f) / 2f;
        return new SKRect(r.Left - w, r.Top - h, r.Right + w, r.Bottom + h);
    }

    /// <summary>Maps a touch point to a slot index (two staggered columns), -1 outside.</summary>
    public static int SlotFromTouch(SKSize canvasSize, SKPoint point)
    {
        for (var i = 0; i < Count; i++)
            if (SlotRect(new SKImageInfo((int)canvasSize.Width, (int)canvasSize.Height), i).Contains(point.X, point.Y))
                return i;
        return -1;
    }

    private static (float Margin, float GapX, float GapY, float Stagger, float Width, float Height) Grid(SKImageInfo info)
    {
        var margin = MathF.Round(info.Width * 0.022f);
        var gapX = MathF.Round(info.Width * 0.026f);
        var stagger = MathF.Round(info.Height * 0.055f);
        var gapY = MathF.Round(info.Height * 0.03f);
        var width = (info.Width - margin * 2 - gapX) / Columns;
        var height = (info.Height - margin * 2 - stagger - gapY * (Rows - 1)) / Rows;
        return (margin, gapX, gapY, stagger, width, height);
    }

    /// <summary>Slots 0, 2, 4 run down the left column; 1, 3, 5 down the right one, a
    /// half-step lower, like the games' party screen.</summary>
    private static SKRect SlotRect(SKImageInfo info, int index)
    {
        var g = Grid(info);
        var col = index % Columns;
        var row = index / Columns;
        var x = g.Margin + col * (g.Width + g.GapX);
        var y = g.Margin + row * (g.Height + g.GapY) + (col == 1 ? g.Stagger : 0);
        return new SKRect(x, y, x + g.Width, y + g.Height);
    }

    /// <summary>The DS's grid behind the party: thin lines with a node at every crossing,
    /// over a dark fall from the top.</summary>
    private static void NodeGrid(SKCanvas canvas, SKImageInfo info)
    {
        using (var bg = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, info.Height), [GridTop, StoragePaint.Well], SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(new SKRect(0, 0, info.Width, info.Height), bg);
        var step = MathF.Round(info.Height / 8f);
        var start = MathF.Round(step * 0.25f);
        using var line = new SKPaint { Color = GridLine, StrokeWidth = MathF.Max(1, step / 44) };
        using var node = new SKPaint { Color = GridNode, IsAntialias = true };
        for (var x = start; x < info.Width; x += step) canvas.DrawLine(x, 0, x, info.Height, line);
        for (var y = start; y < info.Height; y += step) canvas.DrawLine(0, y, info.Width, y, line);
        for (var x = start; x < info.Width; x += step)
            for (var y = start; y < info.Height; y += step)
                canvas.DrawCircle(x, y, step / 18, node);
    }

    /// <summary>The circuit trace behind the cards: a spine in the column gap that jogs
    /// between the two columns, with a stub into every card.</summary>
    private static void DrawTrace(SKCanvas canvas, SKImageInfo info)
    {
        var g = Grid(info);
        var spine = g.Margin + g.Width + g.GapX / 2;
        using var paint = new SKPaint { Color = Trace, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, StrokeJoin = SKStrokeJoin.Round };
        using var path = new SKPath();
        for (var row = 0; row < Rows; row++)
        {
            var left = SlotRect(info, row * 2);
            var right = SlotRect(info, row * 2 + 1);
            var yLeft = left.MidY - left.Height * 0.12f;
            var yRight = right.MidY + right.Height * 0.12f;
            path.MoveTo(left.Right - 6, yLeft);
            path.LineTo(spine, yLeft);
            path.LineTo(spine, yRight);
            path.LineTo(right.Left + 6, yRight);
            if (row < Rows - 1)
            {
                var nextLeft = SlotRect(info, row * 2 + 2);
                path.MoveTo(spine, yRight);
                path.LineTo(spine, nextLeft.MidY - nextLeft.Height * 0.12f);
            }
        }
        canvas.DrawPath(path, paint);
    }

    private static void Slot(SKCanvas canvas, SKRect r, EntityDetail? detail, ISpriteService sprites, bool selected, Action invalidate, bool lifted = false)
    {
        var empty = detail is null or { IsEmpty: true };
        var fainted = detail is { IsEmpty: false, CurrentHp: 0 };
        var unit = r.Height / 150f;

        if (lifted)
        {
            // The carried Pokémon's panel stays, outlined, a little inset.
            using var outline = Octagon(r);
            using var ghost = new SKPaint { Color = Ghost, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, PathEffect = SKPathEffect.CreateDash([7, 6], 0) };
            canvas.DrawPath(outline, ghost);
            r = SKRect.Inflate(r, 2, -4);
        }

        if (empty)
        {
            if (detail is null) return;
            EmptySlot(canvas, r, selected);
            return;
        }

        Panel(canvas, r, fainted ? FaintBody : selected ? SelectedBody : Body, fainted ? FaintBand : selected ? SelectedBand : BodyBand);

        // The box sprite in the panel's top-left corner, the status tag under it.
        var spriteArea = SKRect.Create(r.Left + 14 * unit, r.Top + 4 * unit, 100 * unit, 88 * unit);
        var bitmap = sprites.GetSprite(detail!.Look);
        if (bitmap is not null)
        {
            var scale = MathF.Max(1f, MathF.Floor(MathF.Min(spriteArea.Width / bitmap.Width, spriteArea.Height / bitmap.Height) * 2f) / 2f);
            var w = bitmap.Width * scale;
            var h = bitmap.Height * scale;
            using var paint = new SKPaint();
            if (fainted) paint.ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(0x70, 0x50, 0x48), SKBlendMode.SrcIn);
            var image = Services.SpriteImages.Of(bitmap);
            canvas.DrawImage(image, new SKRect(spriteArea.MidX - w / 2, spriteArea.Bottom - h, spriteArea.MidX + w / 2, spriteArea.Bottom),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), paint);
        }
        else
        {
            sprites.Warm(detail.Look, invalidate);
        }
        if (detail.IsShiny)
            DrawShinyStar(canvas, new SKPoint(spriteArea.Right - 6 * unit, r.Top + 22 * unit), 11 * unit);
        if (StatusTag(detail) is { } status)
            Tag(canvas, SKRect.Create(r.Left + 34 * unit, r.Top + 78 * unit, 56 * unit, 20 * unit), status.Label, status.Color);

        // Name with the gender at the right end; "HP" in green and the bar under it.
        var tx = r.Left + 128 * unit;
        var textRight = r.Right - 26 * unit;
        var nameFont = NameFontFor(detail.Nickname, 34 * unit);
        var nameBaseline = r.Top + 54 * unit;
        var name = Fit(detail.Nickname, nameFont, textRight - tx - 40 * unit);
        Shadowed(canvas, name, tx, nameBaseline, SKTextAlign.Left, nameFont, Pksm.Ink, unit);
        if (detail.Gender is 0 or 1)
            DrawGender(canvas, new SKPoint(textRight - 14 * unit, nameBaseline - 14 * unit), 9 * unit, detail.Gender == 0);

        var labelFont = LabelFont(22 * unit);
        var barTop = r.Top + 73 * unit;
        Shadowed(canvas, "HP", tx + 4 * unit, barTop + 11 * unit, SKTextAlign.Left, labelFont, HpLabel, unit);
        var bar = new SKRect(tx + 48 * unit, barTop, r.Right - 70 * unit, barTop + 12 * unit);
        using (var track = new SKPaint { Color = Track })
            canvas.DrawRect(bar, track);
        var maxHp = detail.Stats is { Count: 6 } ? detail.Stats[0] : 0;
        if (maxHp > 0)
        {
            var ratio = Math.Clamp(detail.CurrentHp / (float)maxHp, 0f, 1f);
            if (ratio > 0f)
            {
                var color = ratio > 0.5f ? new SKColor(0x58, 0xD8, 0x48) : ratio > 0.2f ? new SKColor(0xF0, 0xC0, 0x28) : new SKColor(0xE8, 0x48, 0x38);
                var fill = new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * ratio, bar.Bottom);
                using var f = new SKPaint { Color = color };
                canvas.DrawRect(fill, f);
                using var shade = new SKPaint { Color = SKColors.Black.WithAlpha(0x32) };
                canvas.DrawRect(new SKRect(fill.Left, fill.Bottom - bar.Height * 0.35f, fill.Right, fill.Bottom), shade);
            }
        }

        // Level at the bottom left, HP numbers at the bottom right.
        var smallFont = SmallFont(32 * unit);
        var bottomBaseline = r.Bottom - 18 * unit;
        Shadowed(canvas, $"Lv.{detail.Level}", r.Left + 30 * unit, bottomBaseline, SKTextAlign.Left, smallFont, Pksm.Ink, unit);
        if (maxHp > 0)
            Shadowed(canvas, $"{detail.CurrentHp} / {maxHp}", r.Right - 52 * unit, bottomBaseline, SKTextAlign.Right, smallFont, Pksm.Ink, unit);
    }

    /// <summary>The games' status tags, by PKHeX's status bits; null when the Pokémon is fine.</summary>
    private static (string Label, SKColor Color)? StatusTag(EntityDetail detail)
    {
        if (detail.CurrentHp == 0) return ("FNT", new SKColor(0xC8, 0x30, 0x40));
        var status = detail.StatusCondition;
        if ((status & 0x07) != 0) return ("SLP", new SKColor(0x88, 0x88, 0x98));
        if ((status & 0x88) != 0) return ("PSN", new SKColor(0xA0, 0x48, 0xB8));
        if ((status & 0x10) != 0) return ("BRN", new SKColor(0xE0, 0x6A, 0x30));
        if ((status & 0x20) != 0) return ("FRZ", new SKColor(0x58, 0xB8, 0xE0));
        if ((status & 0x40) != 0) return ("PAR", new SKColor(0xD8, 0xB0, 0x20));
        return null;
    }

    private static void Tag(SKCanvas canvas, SKRect r, string text, SKColor color)
    {
        using (var fill = new SKPaint { Color = color })
            canvas.DrawRect(r, fill);
        var font = LabelFont(r.Height * 0.9f);
        using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawTextFallback(text, r.MidX, r.MidY + font.Size * 0.36f, SKTextAlign.Center, font, ink);
    }

    /// <summary>The panel's octagon: corners cut at a fifth of its height.</summary>
    private static SKPath Octagon(SKRect r, float shrink = 0)
    {
        var o = SKRect.Inflate(r, -shrink, -shrink);
        var k = r.Height * 0.2f - shrink * 0.4f;
        var path = new SKPath();
        path.MoveTo(o.Left + k, o.Top);
        path.LineTo(o.Right - k, o.Top);
        path.LineTo(o.Right, o.Top + k);
        path.LineTo(o.Right, o.Bottom - k);
        path.LineTo(o.Right - k, o.Bottom);
        path.LineTo(o.Left + k, o.Bottom);
        path.LineTo(o.Left, o.Bottom - k);
        path.LineTo(o.Left, o.Top + k);
        path.Close();
        return path;
    }

    /// <summary>A dark outer line, the bright cyan rim, then the body with its one light
    /// band rising to the right, as on the DS.</summary>
    private static void Panel(SKCanvas canvas, SKRect r, SKColor body, SKColor band)
    {
        using (var outer = Octagon(r))
        using (var dark = new SKPaint { Color = RimDark, IsAntialias = true })
            canvas.DrawPath(outer, dark);
        using (var rimPath = Octagon(r, 2))
        using (var rim = new SKPaint { Color = Rim, IsAntialias = true })
            canvas.DrawPath(rimPath, rim);
        using var inner = Octagon(r, 6);
        using (var fill = new SKPaint { Color = body, IsAntialias = true })
            canvas.DrawPath(inner, fill);
        canvas.Save();
        canvas.ClipPath(inner, antialias: true);
        using (var light = new SKPaint { Color = band, IsAntialias = true })
        using (var stripe = new SKPath())
        {
            var x0 = r.Left + r.Width * 0.55f;
            stripe.MoveTo(x0, r.Bottom);
            stripe.LineTo(x0 + r.Height * 0.9f, r.Top);
            stripe.LineTo(x0 + r.Height * 0.9f + r.Width * 0.16f, r.Top);
            stripe.LineTo(x0 + r.Width * 0.16f, r.Bottom);
            stripe.Close();
            canvas.DrawPath(stripe, light);
        }
        canvas.Restore();
        using var edge = new SKPaint { Color = RimDark.WithAlpha(0x96), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        canvas.DrawPath(inner, edge);
    }

    /// <summary>An empty party slot: a dark octagon with a dim rim, no text.</summary>
    private static void EmptySlot(SKCanvas canvas, SKRect r, bool selected)
    {
        using var outline = Octagon(r, 2);
        using (var body = new SKPaint { Color = EmptyBody, IsAntialias = true })
            canvas.DrawPath(outline, body);
        using var edge = new SKPaint { Color = selected ? Rim : EmptyRim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = selected ? 3 : 2 };
        canvas.DrawPath(outline, edge);
    }

    private static void DrawShinyStar(SKCanvas canvas, SKPoint c, float r)
    {
        using var path = new SKPath();
        for (var k = 0; k < 10; k++)
        {
            var a = MathF.PI / 5 * k - MathF.PI / 2;
            var rr = k % 2 == 0 ? r : r * 0.45f;
            var p = new SKPoint(c.X + rr * MathF.Cos(a), c.Y + rr * MathF.Sin(a));
            if (k == 0) path.MoveTo(p); else path.LineTo(p);
        }
        path.Close();
        using var fill = new SKPaint { Color = Pksm.ShinyGold, IsAntialias = true };
        canvas.DrawPath(path, fill);
    }

    /// <summary>DS text: the glyphs over a dark drop shadow one step down and right.</summary>
    private static void Shadowed(SKCanvas canvas, string text, float x, float y, SKTextAlign align, SKFont font, SKColor color, float unit)
    {
        var step = MathF.Max(2, 2.4f * unit);
        using (var shadow = new SKPaint { Color = TextShadow, IsAntialias = true })
            canvas.DrawTextFallback(text, x + step, y + step, align, font, shadow);
        using var fg = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawTextFallback(text, x, y, align, font, fg);
    }

    /// <summary>Trims a nickname with an ellipsis so it never runs into the gender mark.</summary>
    private static string Fit(string text, SKFont font, float width)
    {
        if (font.MeasureFallback(text) <= width) return text;
        for (var n = text.Length - 1; n > 0; n--)
        {
            var candidate = text[..n] + "…";
            if (font.MeasureFallback(candidate) <= width) return candidate;
        }
        return text;
    }

    /// <summary>Constant-label fonts that re-resolve until the async pixel face
    /// lands — a cached SKTypeface.Default pins Roboto (wider) for the process and
    /// the HP numbers outgrow their reserve.</summary>
    private static SKFont SmallFont(float size) =>
        _smallFont is null || _smallFont.Typeface != PixelFont.Face || _smallFont.Size != size
            ? _smallFont = new SKFont(PixelFont.Face, size) { Edging = SKFontEdging.Antialias, Embolden = true }
            : _smallFont;

    private static SKFont LabelFont(float size) =>
        _labelFont is null || _labelFont.Typeface != PixelFont.Face || _labelFont.Size != size
            ? _labelFont = new SKFont(PixelFont.Face, size) { Edging = SKFontEdging.Antialias, Embolden = true }
            : _labelFont;

    /// <summary>Nicknames use the pixel face like every other label when they are plain
    /// Latin text, and the bundled M PLUS Rounded face otherwise (kana, CJK, symbols). The
    /// pixel subset's cmap claims glyphs it cannot draw, so coverage is decided by code
    /// range, not by probing the font.</summary>
    private static SKFont NameFontFor(string text, float size)
    {
        if (text.All(c => c < 0x180))
        {
            var pixel = PixelFont.Face;
            if (_nameFontPixel is null || _nameFontPixel.Typeface != pixel || _nameFontPixel.Size != size)
                _nameFontPixel = new SKFont(pixel, size) { Edging = SKFontEdging.Antialias, Embolden = true };
            return _nameFontPixel;
        }
        // Re-resolve every paint until the async face load lands: caching on the first
        // call pins SKTypeface.Default (no kana) for the whole process.
        var face = PixelFont.FallbackFace;
        if (_nameFontFallback is null || _nameFontFallback.Typeface != face || _nameFontFallback.Size != size)
            _nameFontFallback = new SKFont(face, size) { Edging = SKFontEdging.Antialias };
        return _nameFontFallback;
    }

    /// <summary>The gender glyphs as clean vectors: blue male arrow, pink female cross.</summary>
    private static void DrawGender(SKCanvas canvas, SKPoint center, float radius, bool male)
    {
        var color = male ? Pksm.SignalBlue : Pksm.Female;
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2f, radius * 0.32f) };
        if (male)
        {
            canvas.DrawCircle(center.X, center.Y + 1, radius, paint);
            canvas.DrawLine(center.X + radius * 0.7f, center.Y - radius * 0.5f, center.X + radius * 1.5f, center.Y - radius * 1.3f, paint);
            canvas.DrawLine(center.X + radius * 1.5f, center.Y - radius * 1.3f, center.X + radius * 0.75f, center.Y - radius * 1.3f, paint);
            canvas.DrawLine(center.X + radius * 1.5f, center.Y - radius * 1.3f, center.X + radius * 1.5f, center.Y - radius * 0.55f, paint);
        }
        else
        {
            canvas.DrawCircle(center.X, center.Y - 1, radius, paint);
            canvas.DrawLine(center.X, center.Y + radius * 0.75f, center.X, center.Y + radius * 1.9f, paint);
            canvas.DrawLine(center.X - radius * 0.55f, center.Y + radius * 1.3f, center.X + radius * 0.55f, center.Y + radius * 1.3f, paint);
        }
    }
}
