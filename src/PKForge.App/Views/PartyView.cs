using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// The party view (box -1): six dense HGSS-style cards in two columns. Green card,
/// ball at the corner, sprite left, name, gender, level, threshold-colored HP bar with
/// numbers, shiny star. Selection is the red corner bracket, same as every other grid.
/// Cards read their state live from the session.
/// </summary>
public static class PartyView
{
    public const int Count = 6;
    private const int Columns = 2;
    private const int Rows = 3;

    // The party world green (HGSS party screen).
    private static readonly SKColor CardGreen = new(0x57, 0xB9, 0x5A);
    private static readonly SKColor CardGreenEdge = new(0x2E, 0x8F, 0x3E);
    private static readonly SKColor TextShadow = new(0x1E, 0x5A, 0x28);
    private static readonly SKColor TrackDark = new(0x1E, 0x5A, 0x28);
    private static readonly SKColor HpGood = new(0x7C, 0xE0, 0x7F);
    private static readonly SKColor HpMid = new(0xE8, 0xC8, 0x4A);
    private static readonly SKColor HpBad = new(0xE8, 0x58, 0x58);

    public static void Paint(SKCanvas canvas, SKImageInfo info, ISpriteService sprites, ISaveEngineSession? session, int selectedSlot)
    {
        const float pad = 18;
        const float gap = 12;
        var w = (info.Width - pad * 2 - gap) / Columns;
        var h = (info.Height - pad * 2 - gap * (Rows - 1)) / Rows;

        for (var i = 0; i < Count; i++)
        {
            var col = i % Columns;
            var row = i / Columns;
            var rect = new SKRect(pad + col * (w + gap), pad + row * (h + gap),
                pad + col * (w + gap) + w, pad + row * (h + gap) + h);

            EntityDetail? detail = null;
            try { detail = session?.ReadEntity(-1, i); } catch { /* party coordinates validated in the engine */ }
            Card(canvas, rect, detail, sprites);

            if (i == selectedSlot)
                PksmPaint.Selection(canvas, rect);
        }
    }

    /// <summary>Maps a touch point to a card index (2x3), -1 outside.</summary>
    public static int SlotFromTouch(SKSize canvasSize, SKPoint point)
    {
        const float pad = 18;
        const float gap = 12;
        var w = (canvasSize.Width - pad * 2 - gap) / Columns;
        var h = (canvasSize.Height - pad * 2 - gap * (Rows - 1)) / Rows;
        for (var i = 0; i < Count; i++)
        {
            var col = i % Columns;
            var row = i / Columns;
            var rect = new SKRect(pad + col * (w + gap), pad + row * (h + gap),
                pad + col * (w + gap) + w, pad + row * (h + gap) + h);
            if (rect.Contains(point.X, point.Y)) return i;
        }
        return -1;
    }

    private static void Card(SKCanvas canvas, SKRect r, EntityDetail? detail, ISpriteService sprites)
    {
        if (detail is null || detail.IsEmpty)
        {
            using var dashed = new SKPaint { Color = Pksm.PaperEdge.WithAlpha(0x90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            dashed.PathEffect = SKPathEffect.CreateDash([7, 6], 0);
            canvas.DrawRoundRect(r, 6, 6, dashed);
            return;
        }

        using (var fill = new SKPaint { Color = CardGreen, IsAntialias = true })
            canvas.DrawRoundRect(r, 6, 6, fill);
        using (var edge = new SKPaint { Color = CardGreenEdge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f })
            canvas.DrawRoundRect(r, 6, 6, edge);

        // Sprite in a fixed cell on the left; never overflows.
        var spriteCell = new SKRect(r.Left + 6, r.Top + 6, r.Left + r.Width * 0.34f, r.Bottom - 6);
        var bitmap = sprites.GetSprite(detail.Species, detail.Form, detail.IsShiny);
        if (bitmap is not null)
        {
            var scale = Math.Min(spriteCell.Width / bitmap.Width, spriteCell.Height / bitmap.Height);
            var w = bitmap.Width * scale;
            var h = bitmap.Height * scale;
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, new SKRect(spriteCell.MidX - w / 2, spriteCell.MidY - h / 2, spriteCell.MidX + w / 2, spriteCell.MidY + h / 2),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }
        else
        {
            sprites.Warm(detail.Species, detail.Form, detail.IsShiny, () => { });
        }

        // Ball badge at the card's top-left corner (over the sprite cell, like HGSS).
        var ballSize = r.Height * 0.26f;
        var ball = sprites.GetBall(detail.Ball);
        if (ball is not null)
            canvas.DrawBitmap(ball, new SKRect(r.Left + 4, r.Top + 4, r.Left + 4 + ballSize, r.Top + 4 + ballSize), new SKPaint());
        else
            sprites.WarmBall(detail.Ball, () => { });

        var textX = r.Left + r.Width * 0.38f;
        var textRight = r.Right - 12;

        using var nameFont = FontFor(detail.Nickname, Math.Max(15f, r.Height * 0.21f));
        using var smallFont = FontFor($"Lv.{detail.Level}", Math.Max(12f, r.Height * 0.17f));

        // Name with the dark green offset shadow.
        var nameY = r.Top + r.Height * 0.34f;
        using (var shadow = new SKPaint { Color = TextShadow, IsAntialias = true })
            canvas.DrawText(detail.Nickname, textX + 2, nameY + 2, SKTextAlign.Left, nameFont, shadow);
        using (var white = new SKPaint { Color = SKColors.White, IsAntialias = true })
            canvas.DrawText(detail.Nickname, textX, nameY, SKTextAlign.Left, nameFont, white);

        // Gender, small, top-right.
        if (detail.Gender is 0 or 1)
            DrawGender(canvas, new SKPoint(textRight - r.Height * 0.1f, r.Top + r.Height * 0.22f), r.Height * 0.09f, detail.Gender == 0);

        // Level under the name.
        using (var shadow = new SKPaint { Color = TextShadow, IsAntialias = true })
            canvas.DrawText($"Lv.{detail.Level}", textX + 2, r.Top + r.Height * 0.62f + 2, SKTextAlign.Left, smallFont, shadow);
        using (var white = new SKPaint { Color = SKColors.White, IsAntialias = true })
            canvas.DrawText($"Lv.{detail.Level}", textX, r.Top + r.Height * 0.62f, SKTextAlign.Left, smallFont, white);

        // HP bar along the card bottom with numbers on the right.
        var maxHp = detail.Stats is { Count: 6 } ? detail.Stats[0] : 0;
        if (maxHp > 0)
        {
            var ratio = Math.Clamp(detail.CurrentHp / (float)maxHp, 0f, 1f);
            var bar = new SKRect(textX, r.Top + r.Height * 0.74f, textRight, r.Top + r.Height * 0.86f);
            using (var track = new SKPaint { Color = TrackDark, IsAntialias = true })
                canvas.DrawRoundRect(bar, 3, 3, track);
            var fillColor = ratio > 0.5f ? HpGood : ratio > 0.2f ? HpMid : HpBad;
            if (ratio > 0f)
                using (var fill = new SKPaint { Color = fillColor, IsAntialias = true })
                    canvas.DrawRoundRect(new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * ratio, bar.Bottom), 3, 3, fill);

            using var shadow = new SKPaint { Color = TextShadow, IsAntialias = true };
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            var hp = $"{detail.CurrentHp}/{maxHp}";
            canvas.DrawText(hp, textRight + 2, r.Top + r.Height * 0.99f + 2, SKTextAlign.Right, smallFont, shadow);
            canvas.DrawText(hp, textRight, r.Top + r.Height * 0.99f, SKTextAlign.Right, smallFont, white);
        }

        if (detail.IsShiny)
            PksmPaint.Sparkle(canvas, new SKPoint(r.Right - 13, r.Top + r.Height * 0.42f), 7);
    }

    /// <summary>A font that can actually draw the text: the pixel face when it covers every glyph, else the system default (CJK nicknames).</summary>
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
        var color = male ? new SKColor(0x2A, 0x6B, 0xD8) : new SKColor(0xE2, 0x63, 0x8B);
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2f, radius * 0.32f) };
        if (male)
        {
            canvas.DrawCircle(center.X, center.Y + radius * 0.25f, radius, paint);
            canvas.DrawLine(center.X + radius * 0.7f, center.Y - radius * 0.45f, center.X + radius * 1.55f, center.Y - radius * 1.3f, paint);
            canvas.DrawLine(center.X + radius * 1.55f, center.Y - radius * 1.3f, center.X + radius * 0.8f, center.Y - radius * 1.3f, paint);
            canvas.DrawLine(center.X + radius * 1.55f, center.Y - radius * 1.3f, center.X + radius * 1.55f, center.Y - radius * 0.55f, paint);
        }
        else
        {
            canvas.DrawCircle(center.X, center.Y - radius * 0.25f, radius, paint);
            canvas.DrawLine(center.X, center.Y + radius * 0.75f, center.X, center.Y + radius * 1.9f, paint);
            canvas.DrawLine(center.X - radius * 0.55f, center.Y + radius * 1.3f, center.X + radius * 0.55f, center.Y + radius * 1.3f, paint);
        }
    }
}
