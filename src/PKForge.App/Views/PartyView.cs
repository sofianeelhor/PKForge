using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// The party view (box -1): six HGSS-style cards in two columns. Ball, sprite, name,
/// gender, level, HP bar with numbers, shiny star. Selection is the red corner bracket,
/// same as every other grid. Cards read their state live from the session.
/// </summary>
public static class PartyView
{
    public const int Count = 6;
    private const int Columns = 2;
    private const int Rows = 3;

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
            Card(canvas, rect, detail, sprites, session);

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

    private static void Card(SKCanvas canvas, SKRect r, EntityDetail? detail, ISpriteService sprites, ISaveEngineSession? session)
    {
        if (detail is null || detail.IsEmpty)
        {
            using var dashed = new SKPaint { Color = Pksm.PaperEdge.WithAlpha(0x90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            dashed.PathEffect = SKPathEffect.CreateDash([7, 6], 0);
            canvas.DrawRoundRect(r, 6, 6, dashed);
            return;
        }

        // The white card.
        using (var fill = new SKPaint { Color = Pksm.Paper, IsAntialias = true })
            canvas.DrawRoundRect(r, 6, 6, fill);
        using (var edge = new SKPaint { Color = Pksm.PaperEdge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
            canvas.DrawRoundRect(r, 6, 6, edge);

        using var nameFont = new SKFont { Size = Math.Max(15, r.Height * 0.19f), Edging = SKFontEdging.Antialias, Embolden = true };
        using var smallFont = new SKFont { Size = Math.Max(12, r.Height * 0.155f), Edging = SKFontEdging.Antialias, Embolden = true };

        // Ball icon, top-left.
        var ballSize = r.Height * 0.3f;
        var ball = sprites.GetBall(detail.Ball);
        if (ball is not null)
            canvas.DrawBitmap(ball, new SKRect(r.Left + 8, r.Top + 7, r.Left + 8 + ballSize, r.Top + 7 + ballSize), new SKPaint());
        else
            sprites.WarmBall(detail.Ball, () => { });

        // Sprite, left half, vertically centered.
        var bitmap = sprites.GetSprite(detail.Species, detail.Form, detail.IsShiny);
        var spriteArea = new SKRect(r.Left + 6, r.Top + 6, r.Left + r.Width * 0.42f, r.Bottom - 6);
        if (bitmap is not null)
        {
            var scale = Math.Min(spriteArea.Width / bitmap.Width, spriteArea.Height / bitmap.Height);
            var w = bitmap.Width * scale;
            var h = bitmap.Height * scale;
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, new SKRect(spriteArea.MidX - w / 2, spriteArea.MidY - h / 2, spriteArea.MidX + w / 2, spriteArea.MidY + h / 2),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }
        else
        {
            sprites.Warm(detail.Species, detail.Form, detail.IsShiny, () => { });
        }

        var textX = r.Left + r.Width * 0.46f;

        // Name + gender.
        using (var ink = new SKPaint { Color = Pksm.Ink, IsAntialias = true })
            canvas.DrawText(detail.Nickname, textX, r.Top + r.Height * 0.3f, SKTextAlign.Left, nameFont, ink);
        if (detail.Gender is 0 or 1)
            DrawGender(canvas, new SKPoint(r.Right - r.Height * 0.28f, r.Top + r.Height * 0.24f), r.Height * 0.11f, detail.Gender == 0);

        // Level.
        using (var soft = new SKPaint { Color = Pksm.InkSoft, IsAntialias = true })
            canvas.DrawText($"Lv.{detail.Level}", textX, r.Top + r.Height * 0.52f, SKTextAlign.Left, smallFont, soft);

        // HP bar: track, fill by threshold, numbers on the right.
        var maxHp = detail.Stats is { Count: 6 } ? detail.Stats[0] : 0;
        if (maxHp > 0)
        {
            var ratio = Math.Clamp(detail.CurrentHp / (float)maxHp, 0f, 1f);
            var bar = new SKRect(textX, r.Top + r.Height * 0.62f, r.Right - 10, r.Top + r.Height * 0.72f);
            using (var track = new SKPaint { Color = Pksm.PaperEdge, IsAntialias = true })
                canvas.DrawRoundRect(bar, 3, 3, track);
            var fillColor = ratio > 0.5f ? Pksm.Legal : ratio > 0.2f ? new SKColor(0xD9, 0x9A, 0x17) : Pksm.Illegal;
            if (ratio > 0f)
                using (var fill = new SKPaint { Color = fillColor, IsAntialias = true })
                    canvas.DrawRoundRect(new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * ratio, bar.Bottom), 3, 3, fill);

            using var ink = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
            var hp = $"{detail.CurrentHp}/{maxHp}";
            canvas.DrawText(hp, r.Right - 10, r.Bottom - r.Height * 0.1f, SKTextAlign.Right, smallFont, ink);
        }

        // Shiny star, top-right corner.
        if (detail.IsShiny)
            PksmPaint.Sparkle(canvas, new SKPoint(r.Right - 14, r.Top + 14), 7);
    }

    /// <summary>The gender glyphs as clean vectors: blue male arrow, pink female cross.</summary>
    private static void DrawGender(SKCanvas canvas, SKPoint center, float radius, bool male)
    {
        var color = male ? Pksm.ButtonBlue : new SKColor(0xE2, 0x63, 0x8B);
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, radius * 0.3f) };
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
