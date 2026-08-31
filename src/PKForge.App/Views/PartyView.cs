using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// The party view (box -1): the retro navy deck. Three rows of two beveled angular
/// slots on a dark navy grid, translucent panels, cyan selection frame, red-brown
/// fainted state. Sprite free in the slot (no pod), name, gender, ball, level, HP bar
/// with numbers. Empty slots are dashed bevel outlines. Live state from the session.
/// </summary>
public static class PartyView
{
    public const int Count = 6;
    private const int Columns = 2;
    private const int Rows = 3;

    private static SKFont? _nameFont;
    private static SKFont? _smallFont;
    private static SKFont? _labelFont;

    private static readonly SKColor Bg = Pksm.LogoDeep;
    private static readonly SKColor BgLine = Pksm.LogoGrid.WithAlpha(0x78);
    private static readonly SKColor Body = Pksm.LogoGrid.WithAlpha(0xC8);
    private static readonly SKColor TopEdge = Pksm.LogoCyan.WithAlpha(0xE0);
    private static readonly SKColor BotEdge = Pksm.LogoVoid.WithAlpha(0xE0);
    private static readonly SKColor FaintBody = new(0x4A, 0x26, 0x20, 0xCC);
    private static readonly SKColor FaintEdge = new(0x7A, 0x44, 0x36, 0xE0);
    private static readonly SKColor FaintName = new(0xE0, 0xA9, 0x8A);
    private static readonly SKColor FaintLv = new(0xB0, 0x7A, 0x60);
    private static readonly SKColor Track = Pksm.LogoVoid;
    private static readonly SKColor LvColor = Pksm.InkSoft;
    private static readonly SKColor HpLabel = Pksm.LogoBlue;
    private static readonly SKColor Selected = Pksm.LogoCyan;

    public static void Paint(SKCanvas canvas, SKImageInfo info, ISpriteService sprites, ISaveEngineSession? session, int selectedSlot, Action invalidate, (int Box, int Slot)? carrySource = null, float pulsePhase = 0f)
    {
        // The navy world with its faint grid.
        using (var bg = new SKPaint { Color = Bg })
            canvas.DrawRect(new SKRect(0, 0, info.Width, info.Height), bg);
        using (var line = new SKPaint { Color = BgLine, StrokeWidth = 1 })
        {
            for (float x = 0; x < info.Width; x += 26) canvas.DrawLine(x, 0, x, info.Height, line);
            for (float y = 0; y < info.Height; y += 26) canvas.DrawLine(0, y, info.Width, y, line);
        }

        foreach (var i in Enumerable.Range(0, Count))
        {
            var rect = SlotRect(info, i);
            EntityDetail? detail = null;
            try { detail = session?.ReadEntity(-1, i); } catch { /* engine validates coordinates */ }
            var srcSlot = carrySource is { Box: -1, Slot: var cs } ? cs : -1;
            var aiming = srcSlot >= 0 && selectedSlot != srcSlot;
            // The held mon breathes wherever it is RENDERED: on its source slot when idle,
            // on the aimed slot while previewing the swap (it follows the cursor).
            var heldHere = srcSlot >= 0
                && (aiming ? i == selectedSlot : i == srcSlot)
                && detail is { IsEmpty: false };
            var breath = 1f;
            if (heldHere)
                breath = 1f + 0.028f * (0.5f + 0.5f * MathF.Sin(pulsePhase));
            var pulsed = breath == 1f ? rect : ScaleRect(rect, breath);

            // Swap preview: while carrying and aiming at another slot, both cards show
            // what the swap would look like, ghosted, before A confirms.
            var previewPartner = -1;
            if (carrySource is { Box: -1, Slot: var src } && selectedSlot != src)
            {
                if (i == selectedSlot) previewPartner = src;      // target shows the carried mon
                else if (i == src) previewPartner = selectedSlot; // source shows the target's mon
            }
            var drawDetail = detail;
            var isGhost = false;
            if (previewPartner >= 0)
            {
                EntityDetail? partner = null;
                try { partner = session?.ReadEntity(-1, previewPartner); } catch { }
                drawDetail = partner;
                isGhost = i == selectedSlot; // only the aimed slot wears the SWAP tag; both wear the veil
            }

            Slot(canvas, pulsed, drawDetail, sprites, i == selectedSlot, invalidate,
                lifted: heldHere, pulsePhase: pulsePhase,
                ghost: previewPartner >= 0, ghostTag: isGhost);
        }
    }

    private static SKRect ScaleRect(SKRect r, float scale)
    {
        var w = r.Width * (scale - 1f) / 2f;
        var h = r.Height * (scale - 1f) / 2f;
        return new SKRect(r.Left - w, r.Top - h, r.Right + w, r.Bottom + h);
    }

    /// <summary>Maps a touch point to a slot index (3 rows x 2 columns), -1 outside.</summary>
    public static int SlotFromTouch(SKSize canvasSize, SKPoint point)
    {
        for (var i = 0; i < Count; i++)
            if (SlotRect(new SKImageInfo((int)canvasSize.Width, (int)canvasSize.Height), i).Contains(point.X, point.Y))
                return i;
        return -1;
    }

    private static SKRect SlotRect(SKImageInfo info, int index)
    {
        const float pad = 52;
        const float gap = 12;
        const float top = 16;
        var w = (info.Width - pad - gap - pad * 0.2f) / Columns;
        var h = (info.Height - top - gap * (Rows - 1) - 14) / Rows;
        var col = index % Columns;
        var row = index / Columns;
        var x = pad + col * (w + gap);
        var y = top + row * (h + gap);
        return new SKRect(x, y, x + w, y + h);
    }

    private static void Slot(SKCanvas canvas, SKRect r, EntityDetail? detail, ISpriteService sprites, bool selected, Action invalidate, bool lifted = false, float pulsePhase = 0f, bool ghost = false, bool ghostTag = false)
    {
        var fainted = detail is { IsEmpty: false, CurrentHp: 0 };
        var body = detail is null or { IsEmpty: true } ? Body.WithAlpha(0x50)
            : fainted ? FaintBody : Body;
        var topEdge = fainted ? FaintEdge : TopEdge;

        var path = BevelPath(lifted ? SKRect.Inflate(r, 2, -4) : r, 0);
        if (lifted)
        {
            using var carryGhost = new SKPaint { Color = Selected.WithAlpha(0x70), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            carryGhost.PathEffect = SKPathEffect.CreateDash([7, 6], 0);
            canvas.DrawPath(BevelPath(r, 0), carryGhost);
        }
        using (var b = new SKPaint { Color = BotEdge, IsAntialias = true })
            canvas.DrawPath(path, b);
        using (var b = new SKPaint { Color = body, IsAntialias = true })
            canvas.DrawPath(BevelPath(SKRect.Inflate(r, -2, -2.5f), 0), b);
        using (var t = new SKPaint { Color = topEdge, IsAntialias = true, StrokeWidth = 3 })
            canvas.DrawLine(r.Left + 12, r.Top + 1.5f, r.Right - 28, r.Top + 1.5f, t);

        if (ghost)
        {
            using var veil = new SKPaint { Color = Pksm.LogoDeep.WithAlpha(0xB4), IsAntialias = true };
            canvas.DrawPath(BevelPath(SKRect.Inflate(r, -2, -2.5f), 0), veil);
        }
        if (ghostTag)
        {
            using var previewTag = new SKPaint { Color = Selected, IsAntialias = true };
            var tag = new SKRect(r.Right - 62, r.Top + 6, r.Right - 8, r.Top + 24);
            canvas.DrawRoundRect(tag, 4, 4, previewTag);
            using var tagFont = new SKFont(PixelFont.Face, 13) { Edging = SKFontEdging.Antialias, Embolden = true };
            canvas.DrawText("A SWAP", tag.MidX, tag.Bottom - 5, SKTextAlign.Center, tagFont, new SKPaint { Color = SKColors.White, IsAntialias = true });
        }
        if (selected && !ghost && !lifted)
        {
            // The cursor frame is STATIC: only the carried card breathes, never the cursor.
            using var sel = new SKPaint { Color = Selected, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
            canvas.DrawPath(path, sel);
        }

        if (detail is null or { IsEmpty: true })
        {
            if (detail is not null) // empty slot: dashed bevel outline
            {
                using var dashed = new SKPaint { Color = topEdge.WithAlpha(0x70), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                dashed.PathEffect = SKPathEffect.CreateDash([7, 6], 0);
                canvas.DrawPath(BevelPath(SKRect.Inflate(r, -6, -6), 0), dashed);
            }
            return;
        }

        // Sprite free in the slot, vertically a touch above center.
        var bitmap = sprites.GetSprite(detail.Species, detail.Form, detail.IsShiny);
        if (bitmap is not null)
        {
            var max = r.Height - 12;
            var scale = Math.Min((r.Width * 0.26f) / bitmap.Width, max / bitmap.Height);
            var w = bitmap.Width * scale;
            var h = bitmap.Height * scale;
            using var paint = new SKPaint();
            if (fainted) paint.ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(0x70, 0x50, 0x48), SKBlendMode.SrcIn);
            using var image = SKImage.FromBitmap(bitmap);
            var bottom = r.Bottom - r.Height * 0.14f;
            canvas.DrawImage(image, new SKRect(r.Left + 14, bottom - h, r.Left + 14 + w, bottom),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), paint);
        }
        else
        {
            sprites.Warm(detail.Species, detail.Form, detail.IsShiny, invalidate);
        }

        var tx = r.Left + r.Width * 0.32f;
        var textRight = r.Right - 16;
        var nameColor = fainted ? FaintName : SKColors.White;

        var nameFont = _nameFont ??= FontFor(detail.Nickname, Math.Max(20f, r.Height * 0.175f));
        var smallFont = _smallFont ??= FontFor("Lv.", Math.Max(15f, r.Height * 0.13f));
        var labelFont = _labelFont ??= FontFor("HP", Math.Max(12f, r.Height * 0.105f));

        using (var fg = new SKPaint { Color = nameColor, IsAntialias = true })
            canvas.DrawText(detail.Nickname, tx, r.Top + r.Height * 0.3f, SKTextAlign.Left, nameFont, fg);
        if (detail.Gender is 0 or 1)
        {
            var nameWidth = nameFont.MeasureText(detail.Nickname);
            DrawGender(canvas, new SKPoint(tx + nameWidth + 16, r.Top + r.Height * 0.235f), r.Height * 0.055f, detail.Gender == 0);
        }

        // Ball + level row.
        var ball = sprites.GetBall(detail.Ball);
        var ballSize = r.Height * 0.16f;
        var ballY = r.Top + r.Height * 0.4f;
        if (ball is not null)
            canvas.DrawBitmap(ball, new SKRect(tx, ballY, tx + ballSize, ballY + ballSize), new SKPaint());
        else
            sprites.WarmBall(detail.Ball, invalidate);
        using (var fg = new SKPaint { Color = fainted ? FaintLv : LvColor, IsAntialias = true })
            canvas.DrawText($"Lv.{detail.Level}", tx + ballSize + 8, r.Top + r.Height * 0.53f, SKTextAlign.Left, smallFont, fg);

        // HP: label, thin track, threshold fill, numbers right of the bar.
        var maxHp = detail.Stats is { Count: 6 } ? detail.Stats[0] : 0;
        if (maxHp > 0)
        {
            using (var fg = new SKPaint { Color = HpLabel, IsAntialias = true })
                canvas.DrawText("HP", tx, r.Top + r.Height * 0.79f, SKTextAlign.Left, labelFont, fg);

            var bar = new SKRect(tx + labelFont.MeasureText("HP") + 12, r.Top + r.Height * 0.7f, textRight - 90, r.Top + r.Height * 0.8f);
            using (var track = new SKPaint { Color = Track, IsAntialias = true })
                canvas.DrawRoundRect(bar, 3, 3, track);
            var ratio = Math.Clamp(detail.CurrentHp / (float)maxHp, 0f, 1f);
            if (ratio > 0f)
            {
                var fill = ratio > 0.5f ? new SKColor(0x3F, 0xE0, 0x7F) : ratio > 0.2f ? new SKColor(0xE8, 0xC8, 0x4A) : new SKColor(0xE8, 0x58, 0x58);
                using var f = new SKPaint { Color = fill, IsAntialias = true };
                canvas.DrawRoundRect(new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * ratio, bar.Bottom), 3, 3, f);
            }

            using (var fg = new SKPaint { Color = nameColor, IsAntialias = true })
                canvas.DrawText($"{detail.CurrentHp}/{maxHp}", textRight, r.Top + r.Height * 0.79f, SKTextAlign.Right, smallFont, fg);
        }
    }

    /// <summary>Beveled angular panel path: chamfered corners, big cut top-right.</summary>
    private static SKPath BevelPath(SKRect r, float _)
    {
        var path = new SKPath();
        path.MoveTo(r.Left + 10, r.Top);
        path.LineTo(r.Right - 26, r.Top);
        path.LineTo(r.Right, r.Top + 26);
        path.LineTo(r.Right, r.Bottom - 10);
        path.LineTo(r.Right - 10, r.Bottom);
        path.LineTo(r.Left + 10, r.Bottom);
        path.LineTo(r.Left, r.Bottom - 10);
        path.LineTo(r.Left, r.Top + 10);
        path.Close();
        return path;
    }

    private static SKFont FontFor(string text, float size) => PixelFont.For(text, size);

    /// <summary>The gender glyphs as clean vectors: blue male arrow, pink female cross.</summary>
    private static void DrawGender(SKCanvas canvas, SKPoint center, float radius, bool male)
    {
        var color = male ? Pksm.LogoCyan : new SKColor(0xF0, 0x7A, 0x9B);
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
