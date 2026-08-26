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

    private static readonly SKColor Bg = new(0x14, 0x1D, 0x3E);
    private static readonly SKColor BgLine = new(0x1D, 0x2A, 0x55);
    private static readonly SKColor Body = new(0x2A, 0x3A, 0x78, 0xC8);
    private static readonly SKColor TopEdge = new(0x5A, 0x70, 0xC8, 0xE0);
    private static readonly SKColor BotEdge = new(0x0C, 0x14, 0x30, 0xE0);
    private static readonly SKColor FaintBody = new(0x4A, 0x26, 0x20, 0xCC);
    private static readonly SKColor FaintEdge = new(0x7A, 0x44, 0x36, 0xE0);
    private static readonly SKColor FaintName = new(0xE0, 0xA9, 0x8A);
    private static readonly SKColor FaintLv = new(0xB0, 0x7A, 0x60);
    private static readonly SKColor Track = new(0x0C, 0x14, 0x30);
    private static readonly SKColor LvColor = new(0x8F, 0xA0, 0xC8);
    private static readonly SKColor HpLabel = new(0x66, 0x7A, 0xB8);
    private static readonly SKColor Selected = new(0x35, 0xB8, 0xC8);

    public static void Paint(SKCanvas canvas, SKImageInfo info, ISpriteService sprites, ISaveEngineSession? session, int selectedSlot)
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
            Slot(canvas, rect, detail, sprites, i == selectedSlot);
        }
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

    private static void Slot(SKCanvas canvas, SKRect r, EntityDetail? detail, ISpriteService sprites, bool selected)
    {
        var fainted = detail is { IsEmpty: false, CurrentHp: 0 };
        var body = detail is null or { IsEmpty: true } ? Body.WithAlpha(0x50)
            : fainted ? FaintBody : Body;
        var topEdge = fainted ? FaintEdge : TopEdge;

        var path = BevelPath(r, 0);
        using (var b = new SKPaint { Color = BotEdge, IsAntialias = true })
            canvas.DrawPath(path, b);
        using (var b = new SKPaint { Color = body, IsAntialias = true })
            canvas.DrawPath(BevelPath(SKRect.Inflate(r, -2, -2.5f), 0), b);
        using (var t = new SKPaint { Color = topEdge, IsAntialias = true, StrokeWidth = 3 })
            canvas.DrawLine(r.Left + 12, r.Top + 1.5f, r.Right - 28, r.Top + 1.5f, t);

        if (selected)
        {
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
            sprites.Warm(detail.Species, detail.Form, detail.IsShiny, () => { });
        }

        var tx = r.Left + r.Width * 0.32f;
        var textRight = r.Right - 16;
        var nameColor = fainted ? FaintName : SKColors.White;

        using var nameFont = FontFor(detail.Nickname, Math.Max(20f, r.Height * 0.175f));
        using var smallFont = FontFor("Lv.", Math.Max(15f, r.Height * 0.13f));
        using var labelFont = FontFor("HP", Math.Max(12f, r.Height * 0.105f));

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
            sprites.WarmBall(detail.Ball, () => { });
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

    /// <summary>A font that can draw the text: the pixel face when it covers every glyph, else system default (CJK nicknames).</summary>
    private static SKFont FontFor(string text, float size)
    {
        var pixel = SKTypeface.FromFamilyName("PixelUI");
        var covered = pixel is not null && text.All(c => pixel.GetGlyph(c) != 0);
        return new SKFont(covered ? pixel : SKTypeface.Default, size)
        {
            Edging = SKFontEdging.Antialias,
            Embolden = true,
        };
    }

    /// <summary>The gender glyphs as clean vectors: blue male arrow, pink female cross.</summary>
    private static void DrawGender(SKCanvas canvas, SKPoint center, float radius, bool male)
    {
        var color = male ? new SKColor(0x4A, 0x8B, 0xF0) : new SKColor(0xF0, 0x7A, 0x9B);
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
